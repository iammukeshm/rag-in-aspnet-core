namespace RagChat.Api.GoodMem;

// GoodMem lets you choose the ID of every resource you create. Fixing them here means
// setup can run any number of times: a second create returns 409 Conflict, and the
// setup treats that as "already exists" instead of creating a duplicate.
public static class GoodMemIds
{
    public const string Embedder = "002101f5-5811-4dac-9245-f653486e41cf";
    public const string Llm = "37ec67af-edcb-485d-86e2-dcbde9a675e4";
    public const string Reranker = "e69097f3-e919-4b4e-84a8-787da4006f60";
    public const string Space = "72b51942-f891-42de-a35d-daf3f08d46ef";
}
