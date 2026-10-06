using System.Security.Cryptography;
using System.Text;
using Goodmem.Client;
using Goodmem.Client.Models;
using Microsoft.Extensions.Options;

namespace RagChat.Api.GoodMem;

public sealed record SetupResult(int DocumentsIngested, int DocumentsSkipped, bool RerankerEnabled);

public sealed class GoodMemSetup(
    GoodmemClient client,
    IOptions<GoodMemOptions> options,
    IWebHostEnvironment environment,
    ILogger<GoodMemSetup> logger)
{
    private readonly GoodMemOptions _options = options.Value;

    public async Task<SetupResult> RunAsync(CancellationToken cancellationToken)
    {
        await CreateIfMissingAsync("embedder", () => client.Embedders.CreateAsync(
            new EmbedderCreationRequest
            {
                EmbedderId = GoodMemIds.Embedder,
                DisplayName = "RagChat embedder",
                ModelIdentifier = _options.EmbeddingModel
            },
            _options.OpenAiApiKey,
            cancellationToken));

        await CreateIfMissingAsync("LLM", () => client.Llms.CreateAsync(
            new LlmCreationRequest
            {
                LlmId = GoodMemIds.Llm,
                DisplayName = "RagChat LLM",
                ModelIdentifier = _options.ChatModel
            },
            _options.OpenAiApiKey,
            cancellationToken));

        var rerankerEnabled = !string.IsNullOrWhiteSpace(_options.VoyageApiKey);
        if (rerankerEnabled)
        {
            await CreateIfMissingAsync("reranker", () => client.Rerankers.CreateAsync(
                new RerankerCreationRequest
                {
                    RerankerId = GoodMemIds.Reranker,
                    DisplayName = "RagChat reranker",
                    ModelIdentifier = _options.RerankModel
                },
                _options.VoyageApiKey,
                cancellationToken));
        }

        await CreateIfMissingAsync("space", () => client.Spaces.CreateAsync(
            new SpaceCreationRequest
            {
                SpaceId = GoodMemIds.Space,
                Name = "ragchat",
                SpaceEmbedders = [new SpaceEmbedderConfig { EmbedderId = GoodMemIds.Embedder, DefaultRetrievalWeight = 1 }]
            },
            cancellationToken));

        var (ingested, skipped) = await IngestDocumentsAsync(cancellationToken);
        return new SetupResult(ingested, skipped, rerankerEnabled);
    }

    private async Task<(int Ingested, int Skipped)> IngestDocumentsAsync(CancellationToken cancellationToken)
    {
        var folder = Path.Combine(environment.ContentRootPath, "Documents");
        var pending = new List<string>();
        var existing = new List<string>();

        foreach (var path in Directory.EnumerateFiles(folder, "*.md"))
        {
            var slug = Path.GetFileNameWithoutExtension(path);
            var title = File.ReadLines(path).First().TrimStart('#', ' ');

            try
            {
                var memory = await client.Memories.CreateFromFileAsync(
                    GoodMemIds.Space,
                    path,
                    new JsonMemoryCreationRequest
                    {
                        SpaceId = GoodMemIds.Space,
                        MemoryId = DocumentId(slug),
                        Metadata = new Dictionary<string, object>
                        {
                            ["kind"] = "document",
                            ["title"] = title,
                            ["url"] = $"https://codewithmukesh.com/blog/{slug}/"
                        }
                    },
                    cancellationToken);

                pending.Add(memory.MemoryId);
            }
            catch (ConflictException)
            {
                // The ID is already taken. That alone does not say the earlier ingestion finished,
                // so the existing memory is checked below before it counts as skipped.
                existing.Add(DocumentId(slug));
            }
        }

        // Ingestion is asynchronous. GoodMem accepts the file right away, then chunks
        // and embeds it in the background, so wait until every document is searchable.
        // This throws if any document, new or existing, ended in the FAILED state.
        await Task.WhenAll(pending.Concat(existing).Select(id => WaitUntilProcessedAsync(id, cancellationToken)));
        return (pending.Count, existing.Count);
    }

    public async Task WaitUntilProcessedAsync(string memoryId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        while (true)
        {
            var memory = await client.Memories.GetAsync(memoryId, ct: timeout.Token);
            switch (memory.ProcessingStatus)
            {
                case "COMPLETED":
                    return;
                case "FAILED":
                    throw new InvalidOperationException($"GoodMem could not process memory {memoryId}.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
        }
    }

    private async Task CreateIfMissingAsync<T>(string resource, Func<Task<T>> create)
    {
        try
        {
            await create();
            logger.LogInformation("Created the GoodMem {Resource}", resource);
        }
        catch (ConflictException)
        {
            logger.LogInformation("The GoodMem {Resource} already exists", resource);
        }
    }

    // The same file always maps to the same memory ID, so re-running setup skips it.
    public static string DocumentId(string slug) =>
        new Guid(MD5.HashData(Encoding.UTF8.GetBytes(slug))).ToString();
}
