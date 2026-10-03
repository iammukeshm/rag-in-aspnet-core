# Build Your First MCP Server in C# with the Official SDK

To build an MCP server in C#, create a console app, add the `ModelContextProtocol` package, mark a class with `[McpServerToolType]`, mark its methods with `[McpServerTool]`, and start the host with `WithStdioServerTransport()`. That is the whole shape. I built and ran everything here on **.NET 10 with `ModelContextProtocol` 2.1.0**.

The part most guides get wrong right now is the version. The official C# SDK went to **2.0.0 on 28 July 2026** and **2.1.0 on 5 August 2026**, and 2.0 changed defaults you hit on your first run. Nearly every MCP tutorial you will find, including the answers you get from AI assistants, describes 1.x behaviour that is no longer true.

So this walkthrough builds a working server from an empty folder, and flags exactly where 2.x diverges from what you have read elsewhere. The finished code is on [GitHub](https://github.com/codewithmukesh/claude-code-for-dotnet-developers/tree/main/modules/04-mcp-plugins/build-mcp-server-csharp).

Let's get into it.

## What Is an MCP Server?

**An MCP server is a small program that exposes typed tools to an AI client over the Model Context Protocol.** The client asks the server what tools it has, reads the JSON schema for each one, and calls them when a prompt needs work the model cannot do on its own. MCP is an [open specification](https://modelcontextprotocol.io/) originally built by Anthropic, and the [C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) is now co-maintained with Microsoft.

The useful mental model: MCP is to AI tooling what OpenAPI is to HTTP APIs. It is a contract that says "here is what I can do, here are the arguments, here is what comes back." Any MCP-compatible client reads that contract, so one server works in VS Code, Visual Studio, Claude Code, and Cursor with no per-client adapter.

The C# SDK does the JSON-RPC plumbing. You write C# methods and add attributes. If you have used Claude Code or Copilot agent mode, you have already consumed MCP servers without writing one - [getting started with Claude Code](/blog/claude-code-for-beginners/) covers that side.

## Why Would a .NET Developer Build One?

Because the model does not know about your systems. It has no access to your internal APIs, your ticketing system, your database, or the current state of anything. A tool closes that gap and does it in a way any client can consume.

The stronger reason is that you already have the logic. Your domain services, your EF Core queries, your integrations all exist in C#. An MCP server exposes a curated slice of that to an agent without rebuilding it in Python.

One from my own setup: the [Roslyn-based server in my .NET Claude Code kit](/blog/dotnet-claude-kit-guide/) lets an agent ask the compiler where a symbol is used instead of grepping and guessing. Same idea, different data source.

### When you should not build one

Here is the part nobody says out loud: **if an agent can already call your REST API with a bearer token and an OpenAPI document, an MCP server buys you very little.** Another process to run, another thing to version, another failure mode.

Build an MCP server when at least one of these is true:

- You need **local access** the agent cannot get over HTTP: the filesystem, a running process, a local database, a CLI.
- Your API surface is **too large to hand to a model**. A 300-endpoint API is noise. Eight well-named tools are useful.
- You want **typed discovery**, where the client learns the arguments at startup and validates before calling.
- You need to run something **inside a trust boundary** that will not be exposed to the network.

If none of those apply, write the OpenAPI document instead and move on.

## Which ModelContextProtocol Package Do You Need?

The SDK ships as five packages. Most guides say three, which was true until 2.0.

| Package | Use it when |
|---|---|
| `ModelContextProtocol.Core` | You only need the client or the low-level server API, with minimum dependencies |
| `ModelContextProtocol` | **The default.** Stdio servers, hosting, dependency injection, attribute discovery |
| `ModelContextProtocol.AspNetCore` | The server is reached over HTTP |
| `ModelContextProtocol.Extensions.Tasks` | Long-running tools the client polls for completion |
| `ModelContextProtocol.Extensions.Apps` | Server-delivered interactive UI (experimental) |

Start with `ModelContextProtocol`. It pulls in `Core`, so you are not choosing between them.

## Creating the Project

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Check it:

```bash
dotnet --version
```

There is a template (`dotnet new mcpserver`, from the preview `Microsoft.McpServer.ProjectTemplates` package) covered in the [Microsoft Learn quickstart](https://learn.microsoft.com/en-us/dotnet/ai/quickstarts/build-mcp-server). Good for shipping, bad for learning: it hands you a finished project and you never see which piece does what. Start from an empty console app instead:

```bash
dotnet new console -n NuGetMcpServer
cd NuGetMcpServer
dotnet add package ModelContextProtocol --version 2.1.0
dotnet add package Microsoft.Extensions.Hosting --version 10.0.11
dotnet add package Microsoft.Extensions.Http --version 10.0.11
```

A console app, not a web app. An stdio MCP server talks over standard input and output, so there is no web server involved at all.

By the end you will have this:

## Writing Your First Tool

A tool is a public method on a class. Two attributes make it visible: `[McpServerToolType]` on the class, `[McpServerTool]` on the method.

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace NuGetMcpServer.Tools;

[McpServerToolType]
public sealed class EchoTools
{
    [McpServerTool(Name = "echo")]
    [Description("Repeats back whatever message it is given.")]
    public static string Echo(
        [Description("The message to repeat.")] string message) => $"You said: {message}";
}
```

`[Description]` is not documentation. It is the only thing the model reads when deciding whether to call your tool, and it is how the model learns what each argument means. A tool with a vague description gets ignored, or worse, gets called with nonsense arguments. Write it like you are explaining the tool to a new teammate over chat.

That is the toy version. Now let's build something worth keeping.

## Building Something You Would Actually Keep

Every MCP tutorial builds an echo tool or a random number generator, and you finish it without knowing what a real server looks like. So here is one that solves a problem you already have.

Coding agents hallucinate NuGet package versions. They will confidently write `<PackageReference Include="Serilog.AspNetCore" Version="8.0.1" />` because that version existed when the model was trained, and your build breaks. The fix is to give the agent a way to look up the real answer.

First, a thin client over the public NuGet feed. The [Package Content API](https://learn.microsoft.com/en-us/nuget/api/package-base-address-resource) lists every published version of a package:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using NuGet.Versioning;

namespace NuGetMcpServer.Services;

public sealed class NuGetCatalogClient(HttpClient httpClient)
{
    public async Task<IReadOnlyList<NuGetVersion>> GetVersionsAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        // The feed requires the package ID lowercased with ToLowerInvariant.
        var id = packageId.Trim().ToLowerInvariant();

        using var response = await httpClient.GetAsync(
            $"v3-flatcontainer/{id}/index.json",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();

        var index = await response.Content.ReadFromJsonAsync<VersionIndex>(cancellationToken);

        if (index?.Versions is not { Count: > 0 } versions)
        {
            return [];
        }

        return [.. versions
            .Select(v => NuGetVersion.TryParse(v, out var parsed) ? parsed : null)
            .Where(v => v is not null)
            .Select(v => v!)
            .OrderByDescending(v => v, VersionComparer.VersionRelease)];
    }

    private sealed record VersionIndex(
        [property: JsonPropertyName("versions")] IReadOnlyList<string>? Versions);
}
```

Two details worth flagging. The feed returns a 404 for an unknown package, so that is handled before `EnsureSuccessStatusCode` turns it into an exception. And the NuGet docs never promise the `versions` array is sorted, so I sort it explicitly with `NuGetVersion` rather than trusting the order. `System.Version` cannot do this, because it does not understand prerelease ordering. One more package:

```bash
dotnet add package NuGet.Versioning --version 7.9.0
```

Now the tools. Note the constructor parameter: **tool classes go through dependency injection like anything else in .NET.**

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using NuGetMcpServer.Services;

namespace NuGetMcpServer.Tools;

[McpServerToolType]
public sealed class NuGetTools(NuGetCatalogClient catalog)
{
    [McpServerTool(Name = "get_latest_package_version")]
    [Description("Gets the latest stable version of a NuGet package. Call this before writing any code that references a package, so the version number is real and current.")]
    public async Task<string> GetLatestPackageVersionAsync(
        [Description("The exact NuGet package ID, for example ModelContextProtocol or Serilog.AspNetCore.")]
        string packageId,
        CancellationToken cancellationToken = default)
    {
        var versions = await catalog.GetVersionsAsync(packageId, cancellationToken);

        if (versions.Count == 0)
        {
            return $"No NuGet package was found with the ID '{packageId}'. Check the spelling.";
        }

        var latestStable = versions.FirstOrDefault(v => !v.IsPrerelease);

        return latestStable is null
            ? $"'{packageId}' has no stable release yet. The latest prerelease is {versions[0]}."
            : $"The latest stable version of {packageId} is {latestStable}.";
    }

    [McpServerTool(Name = "list_package_versions")]
    [Description("Lists the most recent versions of a NuGet package, newest first, including prereleases. Use this to check whether a specific version exists.")]
    public async Task<string> ListPackageVersionsAsync(
        [Description("The exact NuGet package ID.")] string packageId,
        [Description("How many versions to return. Defaults to 10, maximum 50.")] int count = 10,
        CancellationToken cancellationToken = default)
    {
        var versions = await catalog.GetVersionsAsync(packageId, cancellationToken);

        if (versions.Count == 0)
        {
            return $"No NuGet package was found with the ID '{packageId}'. Check the spelling.";
        }

        var take = Math.Clamp(count, 1, 50);
        var selected = versions.Take(take).Select(v => v.ToNormalizedString());

        return $"{packageId} versions (newest first): {string.Join(", ", selected)}";
    }
}
```

The `Math.Clamp` matters more than it looks. The model picks the arguments, and a model that asks for 5,000 versions will flood the context window with version strings and crash the conversation. **Treat every tool argument as untrusted input, because a language model chose it.** Same instinct you already apply to a public API endpoint.

Bad package IDs return a sentence, not an exception. The model reads it and corrects itself. An unhandled exception just fails the call.

## Wiring Up the Stdio Transport

Three registrations in the host, plus one line that matters more than the rest:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NuGetMcpServer.Services;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the JSON-RPC stream. Anything else written there corrupts the
// protocol, so every log goes to stderr instead.
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddHttpClient<NuGetCatalogClient>(client =>
{
    client.BaseAddress = new Uri("https://api.nuget.org/");
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
```

`LogToStandardErrorThreshold` is the single most important line in the file. **In stdio mode, stdout is the protocol channel.** One `Console.WriteLine`, one startup banner, one logger writing to stdout, and the client sees garbage where JSON-RPC should be. It disconnects with no useful error. This is the number one reason a first MCP server does not work.

`WithToolsFromAssembly()` scans the current assembly for `[McpServerToolType]` classes. `AddHttpClient<NuGetCatalogClient>` registers the typed client, which is why the constructor injection in `NuGetTools` resolves.

Prefer Serilog? Same rule: the sink has to write to stderr. My guide to [structured logging with Serilog](/blog/structured-logging-with-serilog-in-aspnet-core/) covers sink setup, and here you point it at `Console.Error`.

Build it:

```bash
dotnet build
```

## How Do You Test an MCP Server Without an IDE?

Use MCP Inspector. It launches your server, lists what it exposes, and lets you call tools by hand:

```bash
npx @modelcontextprotocol/inspector dotnet run
```

It opens a browser UI where you can see the tools and invoke them. Test here first, always: if Inspector cannot see your tools, no IDE will, and it tells you why faster.

You can also drive it straight from a terminal. Save three JSON-RPC messages to `probe.jsonl`:

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"probe","version":"1.0"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_latest_package_version","arguments":{"packageId":"ModelContextProtocol"}}}
```

Then feed the file in, holding the pipe open for a few seconds after the last line:

```bash
{ cat probe.jsonl; sleep 4; } | dotnet run
```

That `sleep` is doing real work. If stdin closes the instant the last message is written, the host starts shutting down before the tool call finishes its HTTP request, and you get **no output at all**. It looks identical to a server that is broken, and it is not.

Which comes back as:

```json
{"result":{"content":[{"type":"text","text":"The latest stable version of ModelContextProtocol is 2.1.0."}]},"id":2,"jsonrpc":"2.0"}
```

That is the real response from the code above, and how I confirmed the versions here.

One detail in that handshake is worth pausing on. `2025-11-25` is the newest revision `initialize` accepts. Ask for the revision this SDK actually aligns with and the server turns you down:

```json
{"error":{"code":-32022,"message":"Protocol version '2026-07-28' is not available through the initialize handshake.","data":{"supported":["2024-11-05","2025-03-26","2025-06-18","2025-11-25"],"requested":"2026-07-28"}},"id":1,"jsonrpc":"2.0"}
```

That error is the 2.0 change made concrete. `2026-07-28` dropped the `initialize` handshake, so it is reachable through discovery instead. The handshake stays available purely so older clients keep working.

Worth noticing in the generated schema: `CancellationToken` is not a tool argument. The SDK strips it and supplies it from the request, so you get cancellation without exposing it to the model.

## Connecting the Server to VS Code and Claude Code

For VS Code, create `.vscode/mcp.json` in your workspace:

```json
{
  "servers": {
    "nuget": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "NuGetMcpServer/NuGetMcpServer.csproj"]
    }
  }
}
```

VS Code runs MCP servers from the workspace root, so that path is relative to the root, not to the config file.

For Claude Code, the same server goes in `.mcp.json` at the project root. The shape is nearly identical, with one difference that will cost you time if you miss it: **the top-level key is `mcpServers`, not `servers`.**

```json
{
  "mcpServers": {
    "nuget": {
      "command": "dotnet",
      "args": ["run", "--project", "NuGetMcpServer/NuGetMcpServer.csproj"]
    }
  }
}
```

Claude Code treats an entry with no `type` as stdio, so you can omit it. Or let the CLI write the file: `claude mcp add nuget -- dotnet run --project NuGetMcpServer/NuGetMcpServer.csproj`.

Restart the client, and ask it something the tool answers:

> What is the latest stable version of Serilog.AspNetCore?

If the agent answers without calling the tool, reference it by name in the prompt. Models skip tools when they think they already know the answer, which is exactly the failure this server exists to fix.

## Stdio or HTTP: Which Transport Should You Use?

Stdio runs the server as a child process of the client. HTTP runs it as a service that clients reach over the network. Most of the decision is about who runs it.

| | Stdio | Streamable HTTP |
|---|---|---|
| **Who starts it** | The client, as a child process | You, as a hosted service |
| **Users** | One, on this machine | Many, across machines |
| **Auth** | Process boundary is the boundary | Needed, and it is your job |
| **Network exposure** | None | Real, and needs treating as such |
| **Local file and process access** | Yes | Only if the host allows it |
| **Scaling** | One process per client | Horizontal, no sticky sessions in 2.x |
| **Best for** | Developer tooling | Shared internal services |

**My default is stdio.** It has no network surface, no auth to get wrong, and no deployment. Reach for HTTP when several people or several machines need the same server, and accept that you have just taken on an authentication problem.

## Running the Same Tools Over HTTP

The HTTP version is an ASP.NET Core app that reuses the same tool classes:

```bash
dotnet add package ModelContextProtocol.AspNetCore --version 2.1.0
```

```csharp
using NuGetMcpServer.Services;
using NuGetMcpServer.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient<NuGetCatalogClient>(client =>
{
    client.BaseAddress = new Uri("https://api.nuget.org/");
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<NuGetTools>();

var app = builder.Build();

app.MapMcp("/mcp");

app.Run();
```

`WithTools<NuGetTools>()` registers a specific class, which is what you want when the tools live in a referenced project rather than this assembly. Everything else is a normal [minimal API host](/blog/minimal-apis-aspnet-core/), so the middleware, configuration, and hosting you already know all apply. Hardcoding the feed URL is fine for a sample, but in anything real move it to configuration with the [options pattern](/blog/options-pattern-in-aspnet-core/).

Here is where 2.x diverges sharply from what other guides describe. **`WithHttpTransport()` is stateless by default in SDK 2.x.** I verified this by POSTing `tools/list` to the running server with no `initialize` handshake at all, and it answered. Under 1.x that request would have been rejected for having no session.

Stateless means no `Mcp-Session-Id` header, no server-side session state, and no sticky sessions in front of it. Two replicas behind a plain load balancer just work. If you need the old behaviour, ask for it:

```csharp
.WithHttpTransport(options => options.Stateless = false)
```

Stateless also makes this trivial to [containerize and ship](/blog/docker-guide-for-dotnet-developers/): no session affinity to preserve between replicas.

A server on the network needs authentication before it goes anywhere real.

## What Changed in SDK 2.0?

Version 2.0.0 aligned the C# SDK with the MCP specification revision `2026-07-28`. If you are reading older tutorials, or getting answers from an AI assistant trained before August 2026, this table is what they are getting wrong. The full detail is in the [v2.0.0 release notes](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.0.0) and the [.NET Blog announcement](https://devblogs.microsoft.com/dotnet/announcing-v20-of-the-official-mcp-csharp-sdk/).

| Area | 1.x | 2.x |
|---|---|---|
| HTTP state | Sessions with `Mcp-Session-Id` | **Stateless by default** |
| Handshake | `initialize` / `initialized` | Discovery-first, falls back for older peers |
| Roots, Sampling, Logging | Supported | **Deprecated** (`MCP9005`) |
| Session-scoped state | Supported | Warns (`MCP9006`) |
| Legacy SSE endpoints | Supported | Warns (`MCP9004`) |
| Tasks | In the main package | Moved to `Extensions.Tasks`, wire-incompatible |
| Non-object tool results | Wrapped as `{ "result": value }` | Raw value returned directly |
| OAuth callback | `AuthorizationRedirectDelegate` | `AuthorizationCallbackHandler` (`MCP9007`) |
| `Tool.inputSchema` | Optional | **Required**, throws `JsonException` if missing |

Those `MCP90xx` codes are compiler diagnostics. If your build treats warnings as errors, upgrading to 2.x will break it until you migrate or suppress them.

The good news for anything in this article: **the basics did not change.** `AddMcpServer()`, the attributes, and `WithStdioServerTransport()` all work the same in 2.x as in 1.x. Stable, non-deprecated 1.x APIs still compile. The breakage is concentrated in HTTP sessions, the deprecated capabilities, and the experimental Tasks preview.

## My Take: When an MCP Server Is Worth It

After building this one, my honest position is that **the tool description is the actual engineering work, and the protocol is the easy part.** Getting a server running takes twenty minutes. Getting a model to call the right tool with the right arguments takes longer, and the lever is almost always the wording of `[Description]`, not the code.

The second thing I would tell anyone starting: build one tool that you personally need, not five that seem useful. This NuGet server exists because I got tired of fixing package versions in generated code. That is a real, recurring annoyance with a clear signal for whether the tool works. A tool nobody calls teaches you nothing.

And keep tool count low. Every tool you expose is another option the model weighs on every turn. Eight sharp tools beat thirty vague ones. Same discipline I apply to running servers in my [advanced Claude Code tips](/blog/claude-code-tips-advanced/): audit what is connected, switch off what you never call.

Worth knowing where the boundary sits. A tool is for work the model cannot do itself. To give an agent instructions and context, [skills](/blog/skills-claude-code/) are lighter and need no server.

## When Your Server Does Not Work

**Tools do not appear in the client.** Check both attributes. `[McpServerToolType]` on the class and `[McpServerTool]` on the method, and the class has to be in the assembly `WithToolsFromAssembly()` scans. If the tools live elsewhere, use `WithTools<T>()`.

**The client connects, then immediately disconnects.** Something wrote to stdout. Look for `Console.WriteLine`, a logger without `LogToStandardErrorThreshold`, or a wrapper script echoing anything. Run the server by hand and confirm the first bytes on stdout are JSON.

**`The command "dnx" needed to run ... was not found`.** `dnx` ships with the .NET SDK from version 10. Install the .NET 10 SDK.

**The agent answers without using your tool.** Name the tool in the prompt. If it still refuses, the `[Description]` is too vague, or the model thinks its own knowledge is good enough.

**Build breaks after upgrading to 2.x with `MCP9005` or `MCP9007`.** You are using a deprecated capability. Check the migration table above.

**Tool calls hang.** Give the `HttpClient` a timeout. Without one, a slow dependency stalls the whole conversation with no feedback.

## Key Takeaways

- **An MCP server is a console app with attributes.** Add `ModelContextProtocol`, mark a class `[McpServerToolType]`, mark methods `[McpServerTool]`, run with `WithStdioServerTransport()`.
- **In stdio mode, stdout belongs to the protocol.** Route every log to stderr with `LogToStandardErrorThreshold`, or nothing works.
- **`[Description]` is the interface.** It is the only thing the model reads when choosing a tool and interpreting its arguments.
- **SDK 2.x is stateless by default over HTTP.** No `initialize` handshake, no session header, no sticky sessions. Older tutorials describe the opposite.
- **Default to stdio.** No network surface, no auth, no deployment. Move to HTTP when several machines need the same server.
- **Do not build one out of habit.** A documented REST API is often enough. MCP earns its place with local access, typed discovery, or a curated slice of a large API.

## FAQ

## Summary

An MCP server in C# is a small amount of code: a console app, one package, two attributes, and a host. The official SDK handles JSON-RPC, schema generation, and cancellation, which leaves you writing ordinary C# and thinking about tool design.

The two things that decide whether it works are unglamorous. Keep stdout clean, and write `[Description]` like the model is the only reader, because it is.

If you are on SDK 2.x, remember that the HTTP defaults changed under you. Stateless is the new normal, and a lot of the material online has not caught up yet.

The full working code, both stdio and HTTP versions, is on [GitHub](https://github.com/codewithmukesh/claude-code-for-dotnet-developers/tree/main/modules/04-mcp-plugins/build-mcp-server-csharp). Clone it, point your client at it, and ask it for a package version.

What would you expose to an agent first? Let me know in the comments below.

Happy Coding :)
