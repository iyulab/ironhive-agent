# IronHive.Agent

Reusable agent engine for AI-powered CLI tools. Provides the core agent loop, context management, mode system, MCP plugin integration, and built-in tools.

## Features

- **Agent Loop**: Single-threaded master loop with streaming support; `RunAsync`/`RunStreamingAsync` accept an optional per-turn `ChatOptions` override (merged onto the loop's configured defaults) for callers that need to adjust temperature, tools, or reasoning flags on a single turn
- **Context Management**: Auto-compaction (92% threshold), goal reminders, prompt caching
- **Mode System**: Plan/Work/HITL mode transitions with tool filtering
- **MCP Plugins**: Model Context Protocol server connections, hot reload; supports Stdio and HTTP/SSE transports; `IsHealthyAsync` for liveness checks
- **Built-in Tools**: Read, Write, Shell, Glob, Grep, Todo
- **Sub-Agent System**: Explore/General sub-agent spawning with depth and concurrency limits
- **Permission System**: Rule-based access control for files, commands, and tools; ships with sensible defaults
- **Planning System**: `DefaultTaskPlanner`, `DefaultPlanExecutor`, `HeuristicPlanEvaluator`, `PlannerTriggerDetector`, `PlanAndExecuteOrchestrator`
- **Checkpoint Service**: `ICheckpointService` abstraction for pre-destructive-operation state snapshots and rollback
- **Usage Tracking**: Token/cost tracking and session limits
- **Error Recovery**: Categorized error handling with recovery strategies
- **Webhook System**: Event notifications with HMAC signing

## Installation

```bash
dotnet add package IronHive.Agent
```

## Quick Start

```csharp
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;
using OpenAI;

// Construct IChatClient from any Microsoft.Extensions.AI-compatible provider
IChatClient chatClient = new OpenAIClient("YOUR_API_KEY")
    .GetChatClient("gpt-4o")
    .AsIChatClient();

var agentLoop = new AgentLoop(chatClient, new AgentOptions
{
    SystemPrompt = "You are a helpful assistant."
});

await foreach (var chunk in agentLoop.RunStreamingAsync("Hello!"))
{
    Console.Write(chunk.TextDelta);
}
```

`AgentLoop`'s constructor only requires `chatClient` — `AgentOptions`, `IUsageTracker`,
`ContextManager`, `IErrorRecoveryService`, and `IToolRetriever` are all optional and can be added
incrementally as your application needs them.

### Multi-Provider Setup (Advanced)

`AddIronHiveAgent()` registers `IUsageTracker`, `ContextManager`, and `IErrorRecoveryService`, but
deliberately does **not** register `IChatClientProvider`/`IChatClientFactory`/`IAgentLoopFactory` —
picking an `IChatClient` for a specific backend (OpenAI, a local server, ...) is an application
decision, not something this library can default to. If your app needs to select between multiple
backends at runtime (e.g. a CLI that switches between a cloud and a local model), implement
`IChatClientProvider` per backend and compose them:

```csharp
using IronHive.Agent.Context;
using IronHive.Agent.Providers;

// One IChatClientProvider per backend
public class OpenAiChatClientProvider : IChatClientProvider
{
    public string ProviderName => "openai";
    public bool IsAvailable => true;

    public Task<IChatClient> GetChatClientAsync(string? modelOverride = null, CancellationToken ct = default)
        => Task.FromResult(new OpenAIClient("YOUR_API_KEY")
            .GetChatClient(modelOverride ?? "gpt-4o")
            .AsIChatClient());

    public Task<bool> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(true);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// Compose providers into a single lookup-by-name factory
var openAiProvider = new OpenAiChatClientProvider();
var factory = new ChatClientFactory(
    providers: new Dictionary<string, IChatClientProvider> { ["openai"] = openAiProvider },
    defaultProvider: openAiProvider);

// Implement IAgentLoopFactory to turn AgentLoopFactoryOptions into an AgentLoop, e.g.:
public class MyAgentLoopFactory(IChatClientFactory chatClients, ContextManager contextManager) : IAgentLoopFactory
{
    public Task<IAgentLoop> CreateAsync(CancellationToken ct = default)
        => CreateAsync(new AgentLoopFactoryOptions(), ct);

    public async Task<IAgentLoop> CreateAsync(AgentLoopFactoryOptions options, CancellationToken ct = default)
    {
        var chatClient = options.Provider is not null
            ? await chatClients.CreateAsync(options.Provider, options.Model, ct)
            : await chatClients.CreateAsync(options.Model, ct);

        return new AgentLoop(chatClient, new AgentOptions { SystemPrompt = options.SystemPrompt }, contextManager: contextManager);
    }
}
```

Register your `IAgentLoopFactory` implementation and `IChatClientFactory`/`IChatClientProvider`s in
DI alongside `AddIronHiveAgent()` — this library has no vendor-neutral default to offer for them.

## Architecture

```
IronHive.Agent/
├── Loop/           # Agent loop (IAgentLoop, AgentLoop, ThinkingAgentLoop)
├── Context/        # Context management (compaction, token counting, goal reminders)
├── Mode/           # Plan/Work/HITL mode system
├── Mcp/            # MCP plugin management and tool discovery
├── Tools/          # Built-in tools (BuiltInTools, TodoTool, SubAgentTool)
├── SubAgent/       # Sub-agent spawning and management
├── Planning/       # Plan-and-execute orchestration (DefaultTaskPlanner, DefaultPlanExecutor, HeuristicPlanEvaluator, PlannerTriggerDetector)
├── Services/       # Cross-cutting services (ICheckpointService for pre-destructive-op snapshots)
├── Permissions/    # Permission evaluation and configuration
├── Tracking/       # Usage tracking and limits
├── Providers/      # Chat client, embedding, rerank provider abstractions
├── Memory/         # Session memory service
├── Webhook/        # Webhook event notifications
├── ErrorRecovery/  # Error categorization and recovery
├── Ironbees/       # Multi-agent orchestration integration
└── Extensions/     # DI registration extensions
```

## Native (In-Process) Tools

`AgentOptions.Tools` accepts a plain `IList<AITool>` — `McpPluginManager` is only one way to
populate it. If your app already references a library directly (no separate process needed), wrap
its methods with `Microsoft.Extensions.AI.AIFunctionFactory.Create(...)` and add them to the same
list; the agent loop, tool retrieval, and schema compression all treat these identically to
MCP-discovered tools since both are just `AITool` instances. This is exactly how `BuiltInTools`
(`ReadFile`, `WriteFile`, `ExecuteCommand`, ...) is implemented — see `Tools/BuiltInTools.cs`.

**Registering a tool here is not enough to make it run.** The agent loop only extracts
`FunctionCallContent` from the model's response — it does not itself invoke a matching tool. The
`IChatClient` you pass to the agent loop's constructor must be wrapped with
`Microsoft.Extensions.AI`'s function-invocation middleware for a registered tool to ever actually
execute:

```csharp
var chatClient = baseChatClient.AsBuilder().UseFunctionInvocation().Build();

public class SandboxTools(ISandboxRunner sandbox)
{
    [Description("Run a command in the sandbox and return its output.")]
    public Task<string> RunCommand(string command) => sandbox.RunAsync(command);
}

var sandboxTools = new SandboxTools(mySandboxRunner);
var agentLoop = new AgentLoop(chatClient, new AgentOptions
{
    Tools = [AIFunctionFactory.Create(sandboxTools.RunCommand)]
});
```

Without `UseFunctionInvocation()`, the tool's schema still reaches the model and the model can
still request a call — but nothing executes it, and the agent loop reports the call as succeeded
regardless.

No MCP transport, child process, or server is required to expose an in-process capability as a
tool — that machinery exists only for tools that genuinely live in a separate process or remote
service.

## MCP Transport Options

`McpPluginManager` supports two transport types via `McpPluginConfig.Transport`:

| Transport | Value | When to use |
|-----------|-------|-------------|
| Stdio | `McpTransportType.Stdio` (default) | Spawns a local process over stdin/stdout |
| HTTP/SSE | `McpTransportType.Http` | Connects to a remote MCP server over HTTP or SSE |

```csharp
// Stdio transport (default) — spawns a local process
await manager.ConnectAsync("filesystem", new McpPluginConfig
{
    Transport = McpTransportType.Stdio,
    Command = "npx",
    Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"]
});

// HTTP/SSE transport — connects to a remote server
await manager.ConnectAsync("my-server", new McpPluginConfig
{
    Transport = McpTransportType.Http,
    Url = "http://localhost:3000/mcp",
    // Optional: custom headers sent with every request (e.g. session/tenant scoping)
    Headers = new Dictionary<string, string> { ["X-Session-Id"] = sessionId }
});
```

In YAML plugin config (`Transport` key accepts `stdio` or `http` case-insensitively):

```yaml
plugins:
  remote-tool:
    transport: http
    url: http://localhost:3000/mcp
    headers:
      X-Session-Id: abc123
```

## MCP Tool-Call Guardrail (opt-in)

`McpPluginManager` accepts an optional `FluxGuard.Remote.MCP.IMCPGuardrail` — when supplied, every
`CallToolAsync` validates the request before dispatch and the result before returning it (server/
tool allowlisting, dangerous-argument detection, indirect-injection and sensitive-data checks on
tool results). Nothing changes if you don't pass one — this is off by default.

```csharp
using FluxGuard.Remote.MCP;

// Or register it via DI: services.AddFluxGuardMcpGuardrail();
var guardrail = new MCPToolValidator();
guardrail.RegisterServer(new MCPServerInfo { Name = "filesystem", IsTrusted = true });

var manager = new McpPluginManager(guardrail: guardrail);
await manager.ConnectAsync("filesystem", config);

// A call to an unregistered server, or one whose result trips the injection/sensitive-data
// checks, comes back as an error result (result.IsError == true) — the underlying tool call is
// never dispatched in the request-block case, and its result is never surfaced in the
// result-block case.
```

A guardrail that itself throws is treated as fail-closed (the call is blocked, not silently
dispatched unguarded) — see `McpPluginManager.CallToolAsync`'s XML doc remarks for the reasoning.

## Available Tools Context

After the agent loop factory filters tools via `IModeToolFilter.FilterTools()`, it should populate
`IAvailableToolsContext` so that tool implementations can generate context-aware error messages.

```csharp
// In your agent loop factory (e.g. FilerAgentLoopFactory.CreateAsync):
var filteredTools = modeToolFilter.FilterTools(allTools, modeManager.CurrentMode);

// Expose filtered tool names to tool implementations via DI
var context = serviceProvider.GetRequiredService<IAvailableToolsContext>();
context.SetAvailableTools(filteredTools.OfType<AIFunction>().Select(t => t.Name));
```

Tool implementations can then inject `IAvailableToolsContext` to produce accurate guidance:

```csharp
public class FileSystemTools(IAvailableToolsContext availableTools)
{
    [AIFunction]
    public string WriteFile(string path, string content)
    {
        if (content.Length == 0)
        {
            var hint = availableTools.IsAvailable("DeleteFile")
                ? "To delete a file, use the DeleteFile tool instead."
                : "To delete a file, use a dedicated delete operation.";
            return $"Error: empty content is not allowed. {hint}";
        }
        // ...
    }
}
```

`IAvailableToolsContext` is registered as a singleton by `AddIronHiveAgent()`. Returns empty list
before `SetAvailableTools` is called (i.e., before the first agent loop is created).

## Tool Retrieval Scoring

`KeywordToolRetriever` (the dependency-free `IToolRetriever`) scores a tool by how much of **the
tool's own** name and description the query covers:

```
score = nameCoverage * 0.75 + descriptionCoverage * 0.25
```

- The score is **independent of query length**. Extra query tokens can only add matches, so a long
  system prompt or a large block of retrieved context no longer pushes every tool below
  `MinRelevanceScore`.
- The name and the description are normalised separately. Sharing one denominator would bury the
  name signal under a long description, penalising a well-documented tool.
- Name tokens may match by substring, but only from **3 characters up** — shorter tokens must match
  exactly, so a stopword such as `to` does not claim a match against `ListDirectory`.

Scores are on a different scale than before this rule; if you hand-tuned `MinRelevanceScore`,
re-check it against the default of `0.3`.

### Reserved scored-slot budget

Both `KeywordToolRetriever` and `EmbeddingToolRetriever` select `AlwaysInclude` pins first, then fill
the remaining budget with the top-scored tools. If a caller grows `AlwaysInclude` at runtime (e.g.
merging enabled-plugin tool names into the pin list on every turn), pins can accumulate past
`MaxTools` — and without a reserved floor, every extra pin silently shrinks the scored tail, down to
zero once pins alone reach `MaxTools`. `ToolRetrievalOptions.MinScoredSlots` guarantees the scored
tail at least this many slots regardless of how many pins already consumed the nominal `MaxTools`
budget:

```
floor        = min(MinScoredSlots, MaxTools)
scoredBudget = max(floor, MaxTools - pinnedCount)
```

- Pins can still exceed `MaxTools`; worst-case total selection size is `pinnedCount + MinScoredSlots`,
  not `pinnedCount` alone.
- The floor is clamped to `MaxTools`, so a caller that has deliberately lowered `MaxTools` (e.g. a
  small-context model capping tool-schema token cost) is respected rather than silently overridden.
- Default: `0` — reproduces the pre-existing behavior exactly (pins can shrink the scored tail to
  zero). Set it explicitly to opt into the reserved floor.

## Permission Defaults

`PermissionConfig.CreateDefault()` (the out-of-the-box default) ships the following rules:

**Read** — Allow `**/*`; Ask on `.env*` files; Deny `**/secrets/**`

**Edit** — Allow `src/**/*` and `tests/**/*`; Ask on `*.json`, `*.yaml`, `*.yml`

**Bash** — Allow `git *`, `dotnet *`, `npm *`, `cargo *`; Deny `rm -rf *`, `sudo *`, `curl * | *sh*`

**McpTools** — Allow tools matching `*_help`, `*_get`, `*_list`

**DefaultAction** — `Ask` for anything unmatched

Override any category in your config:

```csharp
services.Configure<PermissionConfig>(config =>
{
    config.McpTools.Add(new PermissionRule
    {
        Pattern = "my_plugin_*",
        Action = PermissionAction.Allow,
        Priority = 5
    });
});
```

## Requirements

- .NET 10.0+

## Related Projects

- [ironhive](https://github.com/iyulab/ironhive) - LLM abstraction layer
- [ironhive-cli](https://github.com/iyulab/ironhive-cli) - CLI application using this agent engine
- [ironbees](https://github.com/iyulab/ironbees) - Multi-agent management

## License

MIT
