using System.Security.Claims;
using RagChat.Api.Eval;
using RagChat.Api.GoodMem;

namespace RagChat.Api;

public sealed record ChatRequest(string Question, bool UseReranker = false);

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        // Registers the providers, creates the space and ingests /Documents. Safe to run again.
        app.MapPost("/api/setup", async (GoodMemSetup setup, CancellationToken ct) =>
            Results.Ok(await setup.RunAsync(ct)));

        // Runs the golden set with and without the reranker. Add withAnswer=true to time the LLM step too.
        app.MapPost("/api/eval", async (EvalService eval, CancellationToken ct, int runs = 3, bool withAnswer = false) =>
            Results.Ok(await eval.RunAsync(runs, withAnswer, ct)));

        var chat = app.MapGroup("/api").RequireAuthorization();

        chat.MapPost("/chat", async (ChatRequest request, ClaimsPrincipal user, ChatService chatService, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Question))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(ChatRequest.Question)] = ["A question is required."]
                });
            }

            var answer = await chatService.AskAsync(UserId(user), request.Question, request.UseReranker, ct);
            return Results.Ok(answer);
        });

        // Deletes everything this user has said. Documents are untouched.
        chat.MapDelete("/memory", async (ClaimsPrincipal user, ChatService chatService, CancellationToken ct) =>
            Results.Ok(new { deleted = await chatService.ForgetAsync(UserId(user), ct) }));
    }

    private static string UserId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)!;
}
