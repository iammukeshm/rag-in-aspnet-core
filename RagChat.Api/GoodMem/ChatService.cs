using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Goodmem.Client;
using Goodmem.Client.Api;
using Goodmem.Client.Models;

namespace RagChat.Api.GoodMem;

public sealed record Citation(string Title, string? Url, string Excerpt);

public sealed record ChatAnswer(string Answer, bool Reranked, IReadOnlyList<Citation> Citations, long ElapsedMs);

public sealed class ChatService(GoodmemClient client, ILogger<ChatService> logger)
{
    // The simple RetrieveAsync overload has no Filter property, so a multi-user app has to
    // build the raw request. The raw request names its post-processor by this class name.
    private const string ChatPostProcessor = "com.goodmem.retrieval.postprocess.ChatPostProcessorFactory";
    private const int RecentTurns = 6;

    // GoodMem's default system prompt tells the model to stick strictly to the retrieved data,
    // so it ignores the conversation passed in Context. These two prompts let it use both.
    private const string SystemPrompt = """
        You answer questions for a .NET developer using two sources: the retrieved documents and the previous conversation with this user.
        - Use the previous conversation for anything the user said or asked earlier.
        - The retrieved documents are articles, never things the user said. If the user asks about something they told you and it is not in the previous conversation, say that you have no record of it.
        - Use the retrieved documents for technical facts, and keep specific details, numbers and code exact.
        - If neither source answers the question, say that you don't know.
        - Keep the answer short and direct.
        """;

    // A Pebble template. The default renders each context item as a protobuf string
    // (text: "..."), so this template reads the text property instead.
    private const string UserPrompt = """
        {% if context|length > 0 -%}
        Previous conversation, oldest first:
        {%- for contextItem in context %}
        {{ contextItem.text }}
        {%- endfor %}

        {% endif -%}
        Question: "{{ userQuery }}"

        Retrieved documents:
        {{ dataSection }}
        """;

    public async Task<ChatAnswer> AskAsync(string userId, string question, bool useReranker, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var recentTurns = await GetRecentTurnsAsync(userId, cancellationToken);
        logger.LogDebug("Loaded {Count} recent turns for {UserId}", recentTurns.Count, userId);

        var request = BuildRequest(userId, question, recentTurns, useReranker);
        var answer = await ReadAnswerAsync(client.Memories.RetrieveRawAsync(request, cancellationToken), useReranker);

        await RememberTurnAsync(userId, question, answer.Answer, cancellationToken);
        return answer with { ElapsedMs = stopwatch.ElapsedMilliseconds };
    }

    private static RetrieveMemoryRequest BuildRequest(string userId, string question, List<string> recentTurns, bool useReranker)
    {
        var config = new Dictionary<string, object>
        {
            ["llm_id"] = GoodMemIds.Llm,
            ["sys_prompt"] = SystemPrompt,
            ["prompt"] = UserPrompt,
            ["gen_token_budget"] = 2048,
            ["max_results"] = 5
        };
        if (useReranker)
        {
            config["reranker_id"] = GoodMemIds.Reranker;
        }

        var request = new RetrieveMemoryRequest
        {
            Message = question,
            // The last few turns go in as context, so "what did I just ask?" always works.
            Context = recentTurns.Select(ContextItem.OfText).ToList(),
            SpaceKeys = [new SpaceKey { SpaceId = GoodMemIds.Space, Filter = DocumentsAndOwnHistory(userId) }],
            RequestedSize = 20,
            PostProcessor = new PostProcessor { Name = ChatPostProcessor, Config = config }
        };

        return request;
    }

    public async Task<long> ForgetAsync(string userId, CancellationToken cancellationToken)
    {
        var result = await client.Memories.BatchDeleteAsync(new BatchMemoryDeletionRequest
        {
            Requests =
            [
                new BatchDeleteMemorySelectorRequest
                {
                    FilterSelector = new FilteredDeleteMemorySelectorRequest
                    {
                        SpaceId = GoodMemIds.Space,
                        Filter = OwnHistory(userId)
                    }
                }
            ]
        }, cancellationToken);

        return result.TotalDeleted ?? 0;
    }

    // Each turn is stored as a memory owned by the user. GoodMem embeds it in the background,
    // so this method does not wait for it. It shows up in "recent turns" right away and in search shortly after.
    private Task RememberTurnAsync(string userId, string question, string answer, CancellationToken cancellationToken) =>
        client.Memories.CreateAsync(new JsonMemoryCreationRequest
        {
            SpaceId = GoodMemIds.Space,
            OriginalContent = $"User asked: {question}\nAssistant answered: {answer}",
            Metadata = new Dictionary<string, object>
            {
                ["kind"] = "conversation",
                ["user_id"] = userId,
                ["title"] = "Earlier conversation"
            }
        }, cancellationToken);

    private async Task<List<string>> GetRecentTurnsAsync(string userId, CancellationToken cancellationToken)
    {
        var turns = new List<string>();
        var options = new MemoriesListOptions
        {
            Filter = OwnHistory(userId),
            SortBy = "created_at",
            SortOrder = SortOrder.Descending,
            MaxResults = RecentTurns,
            IncludeContent = true
        };

        await foreach (var memory in client.Memories.ListAsync(GoodMemIds.Space, options, cancellationToken))
        {
            if (memory.OriginalContent is { Length: > 0 } content)
            {
                turns.Add(Encoding.UTF8.GetString(content));
            }

            if (turns.Count == RecentTurns)
            {
                break;
            }
        }

        turns.Reverse(); // oldest first, the way a conversation reads
        return turns;
    }

    private static async Task<ChatAnswer> ReadAnswerAsync(IAsyncEnumerable<RetrieveMemoryEvent> events, bool useReranker)
    {
        var stages = new Dictionary<string, string>();
        var memories = new Dictionary<string, Memory>();
        var chunks = new List<ChunkReference>();
        AbstractReply? reply = null;

        await foreach (var evt in events)
        {
            if (evt.Status is { Code: "RERANKING_FAILED" or "SUMMARIZATION_FAILED" } status)
            {
                throw new InvalidOperationException($"GoodMem retrieval failed: {status.Code}");
            }

            if (evt.ResultSetBoundary is { Kind: "BEGIN" } boundary)
            {
                stages[boundary.ResultSetId] = boundary.StageName;
            }

            if (evt.MemoryDefinition is { } memory)
            {
                memories[memory.MemoryId] = memory;
            }

            if (evt.RetrievedItem?.Chunk is { } chunk)
            {
                chunks.Add(chunk);
            }

            if (evt.AbstractReply is { Text.Length: > 0 } abstractReply)
            {
                reply = abstractReply;
            }
        }

        if (reply?.ResultSetId is not { } resultSetId)
        {
            throw new InvalidOperationException("GoodMem did not return an answer.");
        }

        // If the reranker fails, GoodMem falls back to plain vector results without failing the request.
        // The status check above catches that. The stage name confirms the rerank stage produced the results.
        var reranked = stages.GetValueOrDefault(resultSetId) == "rerank";
        if (useReranker && !reranked)
        {
            throw new InvalidOperationException("The reranker was requested but did not run.");
        }

        var citations = chunks
            .Where(c => c.ResultSetId == resultSetId)
            .DistinctBy(c => c.Chunk.MemoryId)
            .Select(c =>
            {
                memories.TryGetValue(c.Chunk.MemoryId, out var memory);
                return new Citation(
                    Metadata(memory, "title") ?? "Untitled",
                    Metadata(memory, "url"),
                    c.Chunk.ChunkText.Length > 200 ? c.Chunk.ChunkText[..200] + "..." : c.Chunk.ChunkText);
            })
            .ToList();

        return new ChatAnswer(reply.Text, reranked, citations, 0);
    }

    // Metadata values come back as JsonElement, not string.
    private static string? Metadata(Memory? memory, string key) =>
        memory?.Metadata?.GetValueOrDefault(key) switch
        {
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            string text => text,
            _ => null
        };

    // Filters are plain strings with no parameter binding, so escape anything you put in them.
    private static string Quote(string value) => $"'{value.Replace("'", "''")}'";

    private static string OwnHistory(string userId) =>
        $"CAST(val('$.kind') AS TEXT) = 'conversation' AND CAST(val('$.user_id') AS TEXT) = {Quote(userId)}";

    private static string DocumentsAndOwnHistory(string userId) =>
        $"CAST(val('$.kind') AS TEXT) = 'document' OR ({OwnHistory(userId)})";
}
