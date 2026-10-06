using System.ComponentModel.DataAnnotations;

namespace RagChat.Api.GoodMem;

public sealed class GoodMemOptions
{
    public const string SectionName = "GoodMem";

    [Required, Url]
    public string BaseUrl { get; set; } = string.Empty;

    [Required]
    public string ApiKey { get; set; } = string.Empty;

    [Required]
    public string OpenAiApiKey { get; set; } = string.Empty;

    // Optional. Without it the API still works, just without reranking.
    public string? VoyageApiKey { get; set; }

    public string EmbeddingModel { get; set; } = "text-embedding-3-small";

    public string ChatModel { get; set; } = "gpt-5-mini";

    public string RerankModel { get; set; } = "rerank-2.5";

    // The SDK waits forever by default, so always set a timeout. On a streamed call this only covers
    // the wait for the response headers, so ChatService also uses it as the deadline for a whole chat request.
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
}
