using System.Diagnostics;
using System.Text.Json;
using Goodmem.Client;
using Goodmem.Client.Models;
using RagChat.Api.GoodMem;

namespace RagChat.Api.Eval;

public sealed record GoldenQuestion(string Question, string Slug);

// Rank is the position of the expected article among the distinct articles returned. 0 means it was not returned.
public sealed record QuestionResult(string Question, string Expected, int BaselineRank, int RerankedRank);

public sealed record ModeSummary(string Mode, int Questions, double HitAt1, double HitAt3, double Mrr, int Samples, long P50Ms, long P95Ms);

public sealed record EvalReport(IReadOnlyList<ModeSummary> Summary, IReadOnlyList<QuestionResult> Questions);

public sealed class EvalService(GoodmemClient client, IWebHostEnvironment environment)
{
    private const string ChatPostProcessor = "com.goodmem.retrieval.postprocess.ChatPostProcessorFactory";
    private const string DocumentsOnly = "CAST(val('$.kind') AS TEXT) = 'document'";
    private const int Candidates = 20;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EvalReport> RunAsync(int runs, bool withAnswer, CancellationToken cancellationToken)
    {
        var path = Path.Combine(environment.ContentRootPath, "Eval", "golden-set.json");
        var golden = JsonSerializer.Deserialize<List<GoldenQuestion>>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions)!;

        // One unmeasured call per mode, so the first question does not pay for cold connections.
        await RetrieveAsync(golden[0].Question, rerank: false, answer: false, cancellationToken);
        await RetrieveAsync(golden[0].Question, rerank: true, answer: false, cancellationToken);

        var baselineMs = new List<long>();
        var rerankedMs = new List<long>();
        var answerMs = new List<long>();
        var results = new List<QuestionResult>();

        foreach (var item in golden)
        {
            var expected = GoodMemSetup.DocumentId(item.Slug);
            int baselineRank = 0, rerankedRank = 0;

            for (var run = 0; run < runs; run++)
            {
                var baseline = await RetrieveAsync(item.Question, rerank: false, answer: false, cancellationToken);
                baselineMs.Add(baseline.ElapsedMs);
                baselineRank = baseline.MemoryIds.IndexOf(expected) + 1;

                var reranked = await RetrieveAsync(item.Question, rerank: true, answer: false, cancellationToken);
                rerankedMs.Add(reranked.ElapsedMs);
                rerankedRank = reranked.MemoryIds.IndexOf(expected) + 1;
            }

            if (withAnswer)
            {
                var answered = await RetrieveAsync(item.Question, rerank: true, answer: true, cancellationToken);
                answerMs.Add(answered.ElapsedMs);
            }

            results.Add(new QuestionResult(item.Question, item.Slug, baselineRank, rerankedRank));
        }

        var summary = new List<ModeSummary>
        {
            Summarize("retrieve", results.Select(r => r.BaselineRank).ToList(), baselineMs),
            Summarize("retrieve + rerank", results.Select(r => r.RerankedRank).ToList(), rerankedMs)
        };
        if (withAnswer)
        {
            summary.Add(Summarize("retrieve + rerank + answer", results.Select(r => r.RerankedRank).ToList(), answerMs));
        }

        return new EvalReport(summary, results);
    }

    private async Task<(List<string> MemoryIds, long ElapsedMs)> RetrieveAsync(string question, bool rerank, bool answer, CancellationToken cancellationToken)
    {
        PostProcessor? postProcessor = null;
        if (rerank)
        {
            // max_results equals the candidate count, so the reranker reorders and never trims.
            var config = new Dictionary<string, object> { ["reranker_id"] = GoodMemIds.Reranker, ["max_results"] = Candidates };
            if (answer)
            {
                config["llm_id"] = GoodMemIds.Llm;
                config["gen_token_budget"] = 2048;
            }

            postProcessor = new PostProcessor { Name = ChatPostProcessor, Config = config };
        }

        var request = new RetrieveMemoryRequest
        {
            Message = question,
            SpaceKeys = [new SpaceKey { SpaceId = GoodMemIds.Space, Filter = DocumentsOnly }],
            RequestedSize = Candidates,
            FetchMemory = false,
            PostProcessor = postProcessor
        };

        var memoryIds = new List<string>();
        var stage = string.Empty;
        var stopwatch = Stopwatch.StartNew();

        await foreach (var evt in client.Memories.RetrieveRawAsync(request, cancellationToken))
        {
            if (evt.Status is { Code: "RERANKING_FAILED" or "SUMMARIZATION_FAILED" } status)
            {
                throw new InvalidOperationException($"GoodMem retrieval failed: {status.Code}");
            }

            if (evt.ResultSetBoundary is { Kind: "BEGIN" } boundary)
            {
                stage = boundary.StageName;
            }

            // Chunks arrive best first. The first chunk of each article decides that article's rank.
            if (evt.RetrievedItem?.Chunk is { } chunk && !memoryIds.Contains(chunk.Chunk.MemoryId))
            {
                memoryIds.Add(chunk.Chunk.MemoryId);
            }
        }

        stopwatch.Stop();

        if (rerank && stage != "rerank")
        {
            throw new InvalidOperationException("The reranker was requested but did not run.");
        }

        return (memoryIds, stopwatch.ElapsedMilliseconds);
    }

    private static ModeSummary Summarize(string mode, List<int> ranks, List<long> latencies)
    {
        var sorted = latencies.Order().ToList();
        return new ModeSummary(
            mode,
            ranks.Count,
            Math.Round(ranks.Count(r => r == 1) / (double)ranks.Count, 3),
            Math.Round(ranks.Count(r => r is >= 1 and <= 3) / (double)ranks.Count, 3),
            Math.Round(ranks.Sum(r => r == 0 ? 0 : 1.0 / r) / ranks.Count, 3),
            sorted.Count,
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95));
    }

    // Nearest-rank percentile.
    private static long Percentile(List<long> sorted, double percentile) =>
        sorted[Math.Max(0, (int)Math.Ceiling(percentile * sorted.Count) - 1)];
}
