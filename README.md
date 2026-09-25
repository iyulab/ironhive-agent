# IronHive.Agent

Reusable agent engine for AI-powered CLI tools. Provides the core agent loop, context management, mode system, MCP plugin integration, and built-in tools.

## Features

- **Agent Loop**: Single-threaded master loop with streaming support; `RunAsync`/`RunStreamingAsync` accept an optional per-turn `ChatOptions` override (merged onto the loop's configured defaults) for callers that need to adjust temperature, tools, or reasoning flags on a single turn
- **Context Management**: Auto-compaction (92% threshold), goal reminders, prompt caching
- **Mode System**: Plan/Work/HITL mode transitions with tool filtering
- **MCP Plugins**: Model Context Protocol server connections, hot reload; supports Stdio and HTTP/SSE transports; `IsHealthyAsync` for liveness checks
- **Agent Skills (`SKILL.md`)**: `SkillsLoader.Create(new SkillsConfig { Roots = [userSkillsDir, projectSkillsDir] })` discovers skills per the [Agent Skills specification](https://agentskills.io/specification) and validates them as `skills-ref validate` does (invalid ones are *reported* in `Diagnostics`, never thrown; the first root wins a name collision and the shadowed one is reported). `loader.Contributor` puts every name + description in the system instructions; `loader.LoadTool` (`load_skill`) returns a skill's body — or a file inside its directory, and nothing outside it — on demand. `Enabled`/`Exclude`/`Filter` narrow the set per session; `MaxMetadataCharacters` bounds the section and what does not fit is in `Dropped`/`DroppedCount` (still loadable by name). `SkillDiscovery.Discover(config)` is the same discovery as a plain call, for a UI that lists skills before a session exists. Frontmatter keys the specification does not define reject the skill by default (as `skills-ref validate` does); `UnknownFields = UnknownFieldPolicy.Accept` loads such skills and reports the keys as a warning — skill trees written for one client's extensions (e.g. `argument-hint`) need this. A name in `Enabled` that matches nothing is reported (`EnabledNotFound`), not dropped silently. DI: `services.AddAgentSkills(config)` registers the loader and its contributor; add `LoadTool` to the loop's tools yourself. Text only — `scripts/` are never executed; `allowed-tools` is parsed, not enforced
- **System instruction sections**: add to the system instructions without replacing the prompt — implement `ISystemInstructionContributor` and register it in the container (or `ContextManager.AddInstructionContributor`); needs a `ContextManager` on the loop
- **Built-in Tools**: Read, Write, Shell, Glob, Grep, Todo — `BuiltInTools.GetAll(workingDirectory)`. A host decides where they reach and what runs around a write with `FileToolOptions`: `BuiltInTools.GetAll(workingDirectory, new FileToolOptions { AllowedRoots = ["."], WriteInterceptor = ... })`. The working directory alone is not a boundary — `AllowedRoots` is (default: none, opt-in)
- **Delegation (agent as a tool, `IronHive.Agent.Ironbees` package)**: `DelegationTools.Create(orchestrator, new DelegatedAgent { AgentName = "research", Model = ..., MaxToolTurns = ... })` turns an Ironbees named agent into an `AIFunction` the model calls to hand off a sub-task. Each call is an isolated run of that agent — its `agent.yaml` tools (an agent that lists `tools` gets exactly those), prompt and model, with per-delegation model, reasoning level, output cap and tool-turn limit. `DelegationOptions` bounds nesting depth (across agents), concurrency, and feeds the delegated usage into the parent's `IUsageLimiter`/`IUsageTracker`. A run that stops at its turn limit comes back marked partial. Not registered by `AddIronHiveAgent`: create the tools where the orchestrator is available and add them to the loop's `Tools`. To call a named agent from application code instead, use `IAgentOrchestrator.ProcessStructuredAsync(input, new ProcessOptions { AgentName = ... })`
- **Advisor (consult a stronger model)**: `AdvisorTool.Create(strongerClient, new AdvisorOptions { ModelId = ..., MaxCalls = 5 })` gives the working model a no-argument tool that sends the conversation so far — its requests, tool calls and results — to a stronger model and returns that model's review. The working model decides when to consult (the default description says: before committing to an approach, when stuck, before declaring done), so the strong model is paid for only where judgment matters. Works under `FunctionInvokingChatClient` and inside Ironbees agents (`ChatClientFrameworkAdapter`); elsewhere pass `AdvisorOptions.Conversation`. The advisor is never given tools; `MaxCalls`, `UsageLimiter` and `UsageTracker` bound and account for it. Not registered by `AddIronHiveAgent`: add the tool to the loop's `Tools`
- **Long-term session memory (`IronHive.Agent.Memory` package)**: `new SessionMemoryService(memoryService, userId)` implements `ISessionMemoryService` over a MemoryIndexer `IMemoryService`; `EmbeddingServiceAdapter` (over an `IAgentEmbeddingProvider`) and `TextCompletionServiceAdapter` (over an `IChatClient`) supply the MemoryIndexer services it needs. The interfaces (`ISessionMemoryService`, `IAgentEmbeddingProvider`) live in `IronHive.Agent`, so a host that does not use memory ships no MemoryIndexer.
- **Deep research (`IronHive.DeepResearch` package)**: `AddDeepResearch(...)` registers an iterative research pipeline — query planning, web search, content extraction, sufficiency analysis, then a cited report — over one text-generation service (`ChatClientTextGenerationAdapter` for an `IChatClient`). Results report the run's token usage; the cost is left null because the run does not know which model priced its calls. Tune it through the options callback: `services.AddDeepResearch(chatClient, o => { o.SufficiencyThreshold = 0.7m; o.MinSourcesBeforeReport = 5; o.MaxSearchRetriesPerIteration = 2; })`. `SufficiencyThreshold` is the score at which research stops. `MinSourcesBeforeReport` keeps it iterating while fewer sources were collected and a gap remains. The per-query iteration limit is `ResearchRequest.MaxIterations`, capped by `Depth`
- **Permission System**: Rule-based access control for files, commands, and tools; ships with sensible defaults
- **Planning System**: `DefaultTaskPlanner`, `DefaultPlanExecutor`, `HeuristicPlanEvaluator`, `PlannerTriggerDetector`, `PlanAndExecuteOrchestrator`
- **Checkpoint Service**: `ICheckpointService` abstraction for pre-destructive-operation state snapshots and rollback
- **Usage Tracking**: Token/cost tracking (`IUsageTracker`) and session limits (`IUsageLimiter`, registered by `AddIronHiveAgent` when `UsageLimits` is set). Pass the limiter to `AgentLoop` or `ThinkingAgentLoop` (`usageLimiter:`) and a turn past the limit is refused with `UsageLimitExceededException`
- **Error Recovery**: Categorized error handling with recovery strategies (`IErrorRecoveryService`). Passed to either loop (`errorRecovery:`), a buffered turn that fails transiently is retried once
- **Webhook System**: Event notifications with HMAC signing

## Packages

| Package | Purpose |
|---|---|
| `IronHive.Agent` | Agent loop, context management, modes, approvals, MCP plugins, built-in tools, advisor tool, and the guard/memory seams |
| `IronHive.DeepResearch` | Iterative web research pipeline with cited reports (`AddDeepResearch`) |
| `IronHive.Agent.Ironbees` | Ironbees integration: `ChatClientFrameworkAdapter`, `AddIronbees`, `OrchestratedAgentLoop`, `DelegationTools` (a named agent as a tool), `IronbeesEmbeddingProviderAdapter` |
| `IronHive.Agent.FluxGuard` | FluxGuard-backed tool guards: `FluxGuardMcpToolCallGuard` (`IMcpToolCallGuard`), `McpGuardrailToolResultGuard` (`IToolResultGuard`), `AddIronHiveAgentFluxGuard()` |
| `IronHive.Agent.Memory` | Long-term session memory over [MemoryIndexer](https://github.com/iyulab/memory-indexer): `SessionMemoryService` (`ISessionMemoryService`), `EmbeddingServiceAdapter` and `TextCompletionServiceAdapter` |

## Installation

```bash
dotnet add package IronHive.Agent
dotnet add package IronHive.DeepResearch   # only for deep research
dotnet add package IronHive.Agent.Memory   # only for long-term memory (MemoryIndexer)
dotnet add package IronHive.Agent.FluxGuard   # only for FluxGuard tool-call / tool-result guards
dotnet add package IronHive.Agent.Ironbees    # only for Ironbees agents, AddIronbees and delegation tools
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
├── Tools/          # Built-in tools (BuiltInTools, TodoTool)
├── Delegation/     # The advisor tool (AdvisorTool) and ToolInvocationScope
├── Planning/       # Plan-and-execute orchestration (DefaultTaskPlanner, DefaultPlanExecutor, HeuristicPlanEvaluator, PlannerTriggerDetector)
├── Services/       # Cross-cutting services (ICheckpointService for pre-destructive-op snapshots)
├── Permissions/    # Permission evaluation and configuration
├── Tracking/       # Usage tracking and limits
├── Providers/      # Chat client, embedding, rerank provider abstractions
├── Memory/         # Session memory contracts (ISessionMemoryService, IAgentEmbeddingProvider)
├── Webhook/        # Webhook event notifications
├── ErrorRecovery/  # Error categorization and recovery
└── Extensions/     # DI registration extensions

IronHive.Agent.Ironbees/   # Ironbees integration: ChatClientFrameworkAdapter, AddIronbees, OrchestratedAgentLoop,
                           # DelegationTools, IronbeesEmbeddingProviderAdapter
IronHive.Agent.FluxGuard/  # FluxGuard-backed IMcpToolCallGuard / IToolResultGuard
IronHive.Agent.Memory/     # MemoryIndexer-backed SessionMemoryService and adapters
```

`IronHive.Agent` itself references none of Ironbees, FluxGuard or MemoryIndexer (0.19.0), so a host that uses the
loop, modes, approvals and tools ships none of their natives or SDKs. The seams they plug into — `IToolResultGuard`,
`IMcpToolCallGuard`, `ApprovalGate`, `ToolInvocationScope`, `ISessionMemoryService` — are in the core package, so a
host's own adapter judges tool calls and results by the same rules.

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
still request a call — but nothing executes it, and `ToolCallResult.Success` on the returned call is
`null` (unknown), not `true`. `AgentLoop`/`ThinkingAgentLoop` only know the real outcome when the
`IChatClient`'s function-invocation middleware appended a matching result to the same response: in
that case `Success` reflects whether the invocation actually succeeded, and `Result` carries the
real return value.

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

`McpPluginManager` accepts an optional `IMcpToolCallGuard` — when supplied, every `CallToolAsync` checks the request
before dispatch (`McpToolCallVerdict`: `Allow` or `Block(reason)`) and the result before returning it
(`ToolResultVerdict`: `Allow`, `Replace(sanitized)` or `Withhold(reason)`). Nothing changes if you don't pass one —
this is off by default, and `IronHive.Agent` itself references no guard engine.

The `IronHive.Agent.FluxGuard` package backs it with FluxGuard (server/tool allowlisting, dangerous-argument
detection, indirect-injection and sensitive-data checks on tool results):

```csharp
using FluxGuard.Remote.MCP;
using IronHive.Agent.FluxGuard;

var guardrail = new MCPToolValidator();
guardrail.RegisterServer(new MCPServerInfo { Name = "filesystem", IsTrusted = true });

var manager = new McpPluginManager(guard: new FluxGuardMcpToolCallGuard(guardrail));
await manager.ConnectAsync("filesystem", config);

// A call to an unregistered server, or one whose result trips the injection/sensitive-data
// checks, comes back as an error result (result.IsError == true) — the underlying tool call is
// never dispatched in the request-block case, and its result is never surfaced in the
// result-block case.
```

With DI, register FluxGuard's guardrail and then the guards: `services.AddFluxGuardMcpGuardrail();
services.AddIronHiveAgentFluxGuard();` — the `McpPluginManager` that `AddIronHiveAgent` registers picks up the
`IMcpToolCallGuard`, and the Ironbees adapter the `IToolResultGuard`.

A guard that itself throws is treated as fail-closed (the call is blocked, not silently
dispatched unguarded) — see `McpPluginManager.CallToolAsync`'s XML doc remarks for the reasoning.

## In-Process Tool-Result Guard (opt-in)

In-process tools (a web page's text, a file's contents) return text the model then reads, which is the same prompt-injection surface an MCP result has. `IToolResultGuard` inspects every result before the model does. Each inspection gets a verdict: `Allow`, `Replace(sanitized)`, or `Withhold(reason)`.
- A withheld result becomes a `ToolCallRefusal` (`ToolCallRefusalKind.ResultWithheld`). The model reads «Tool result withheld by guard: …» and the loop reports the call with `Success = false`.
- A guard that throws withholds the result. This is fail-closed, the same rule as the MCP guardrail.

On a function-invoking client, compose it behind the permission gate. The gate decides whether a call runs, and the guard decides what its result becomes:

```csharp
var client = inner.AsBuilder()
    .UseFunctionInvocation(configure: c => c.FunctionInvoker = ApprovalGatedFunctionInvoker.Create(
        modeToolFilter, approvalService,
        inner: ToolResultGuardedFunctionInvoker.Create(guard)))
    .Build();
```

The Ironbees adapter applies the same guard through its `toolResultGuard` constructor argument, and `AddIronbees` resolves `IToolResultGuard` from DI. To reuse the FluxGuard guardrail you already give `McpPluginManager`, wrap it: `new McpGuardrailToolResultGuard(guardrail)` (`IronHive.Agent.FluxGuard` package). In-process tools are then reported to it under the server name `in-process`.

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

**Tools** — every other tool, matched by function name: Allow the read-only built-ins
(`ListDirectory`, `GlobFiles`, `GrepFiles` and their snake_case spellings)

**Paths are judged as the tools will open them.** `Read` and `Edit` patterns match the path
*relative to* `PermissionConfig.WorkingDirectory` (default: the current directory — the file tools'
default too; `PermissionConfigLoader.LoadFromDefaultLocations(dir)` sets it to `dir`) after `.` and
`..` are resolved, so `src/a.cs`, `docs/../src/a.cs` and its absolute path get one answer, and
`src/../../x` is not covered by a rule for `src/**`. A path that resolves **outside** the working
directory is not covered by `Read`/`Edit` at all: `ExternalDirectory` rules decide (patterns match the
absolute path with `/` separators), else `DefaultAction`. `ListDirectory`, `GlobFiles` and `GrepFiles`
answer to the `Read` rules for the directory they are pointed at, as well as to their tool-name rule.
Give the permission layer the same working directory you give the tools.

**DefaultAction** — `Ask` for anything unmatched — including a tool no rule names, so an unknown
tool is asked about rather than run

**ReadOnlyTools** — names (patterns, like `Tools`) of *your* tools that only read. Planning mode offers and
permits them next to the built-in read-only file tools; without a declaration a host tool never runs in Planning.
This is a side-effect class, not a permission: `Tools` still decides `Allow` / `Ask` / `Deny`.

```csharp
services.Configure<PermissionConfig>(config => config.ReadOnlyTools.AddRange(["read_current_tab", "list_saved_*"]));
```

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

## Human Approval Gate

The permission rules decide `Allow` / `Deny` / `Ask`; `IHumanApprovalService` answers the `Ask`.
What connects them to an actual tool call is `ApprovalGatedFunctionInvoker` — an `IAgentLoop` never
invokes tools itself, the `UseFunctionInvocation()` middleware on its chat client does, so the gate is
installed there:

```csharp
var modeToolFilter = new ModeToolFilter(permissionConfig);   // or resolve IModeToolFilter from DI
var chatClient = inner.AsBuilder()
    .UseFunctionInvocation(configure: c =>
        c.FunctionInvoker = ApprovalGatedFunctionInvoker.Create(modeToolFilter, approvalService))
    .Build();

var loop = new AgentLoop(chatClient, new AgentOptions { Tools = tools });
```

For each call the gate runs `IModeToolFilter.AssessRisk` and acts on `RiskAssessment.Verdict`:

| Verdict | What happens |
|---|---|
| `Allow` | the tool runs |
| `Deny` | the tool does not run; the model receives `Permission denied: <reason>` as the result |
| `Ask` | `IHumanApprovalService.RequestApprovalAsync` is called; approval runs the tool (with `ModifiedArguments` applied if the approver edited them), rejection returns `Approval rejected: <reason>` |

A refusal is always a tool *result*, never an exception, so the model can read it and change course.
An `Ask` verdict with **no approval service registered is refused**, not passed through — a gate that
lets "ask" through when nobody can be asked is a silent no-op. Either register an
`IHumanApprovalService` or set the rule (or `DefaultAction`) to `Allow`. Remembering an
`ApprovalResult.AlwaysApprove` answer is the service's job; the gate asks every time.

Pass another invoker as `inner` to compose (for example one that turns marshalling errors into
recovery directives). The Ironbees adapter (`ChatClientFrameworkAdapter`, `IronHive.Agent.Ironbees`) applies the same gate (`ApprovalGate`) on its
own tool loop when it is given a filter or evaluator, so a verdict means the same thing on both paths.

## Post-Turn Seam

The loop knows what a turn actually did — which tools it called, with what arguments, and what came
back — because it assembles exactly that to build the turn's history message. `ITurnObserver` hands
that record over instead of dropping it, so a consumer can check the model's closing narration against
it rather than rebuilding the correlation outside the loop.

```csharp
public sealed class ClaimChecker : ITurnObserver
{
    public ValueTask<string?> OnTurnCompletedAsync(TurnRecord turn, CancellationToken ct)
    {
        var claimedItSent = turn.Content.Contains("message sent", StringComparison.OrdinalIgnoreCase);
        var actuallySent = turn.ToolCalls.Any(c => c.ToolName == "send_message");

        return ValueTask.FromResult<string?>(
            claimedItSent && !actuallySent ? "_(no message was sent this turn)_" : null);
    }
}

var loop = new AgentLoop(chatClient, options, turnObservers: [new ClaimChecker()]);
```

**Observe and append, never edit.** The observer runs once the turn is complete, which on
`RunStreamingAsync` means every text delta has already been yielded and rendered — there is nothing
left to amend. What an observer returns is appended *after* the turn's output, identically on both
entry points, so a check does not silently stop firing when a consumer switches to streaming:

- `RunAsync` — appended to the end of `AgentResponse.Content`, and also exposed on its own as
  `AgentResponse.Addendum` so it can be told apart from the model's own words.
- `RunStreamingAsync` — carried as `AgentResponseChunk.Addendum` on the final chunk (the one that
  also carries `Turn`), never as a `TextDelta`, so a consumer that concatenates text deltas gets the
  model's words only and can render the note separately. (Before 0.11.0 it rode as the final chunk's
  `TextDelta`; a consumer that only summed text deltas saw it folded in.)

An addendum reaches the consumer, **not the conversation**: it is never written into history, because
the model did not say it and feeding a fabricated assistant utterance back into the next turn's
context would be worse than the claim it corrects.

`ThinkingAgentLoop` implements the same seam — it is a peer implementation of `IAgentLoop`, not a
wrapper around `AgentLoop`.

### Reading the record without an observer

`RunStreamingAsync` always ends with one final chunk carrying `AgentResponseChunk.Turn`, whether or
not any observer is registered:

```csharp
await foreach (var chunk in loop.RunStreamingAsync(prompt, ct))
{
    if (chunk.Turn is { } turn)
    {
        logger.LogInformation("turn used {Count} tools", turn.ToolCalls.Count);
    }
}
```

`RunAsync` already returned `AgentResponse.ToolCalls`; the streaming path used to yield tool calls one
delta at a time and nothing consolidated.

### Each tool call's outcome as it finishes

When the chat client invokes tools (`UseFunctionInvocation()`), every call's outcome is also yielded on its own
chunk the moment it arrives — for a step timeline that says "reading the page… done" without waiting for the turn:

```csharp
await foreach (var chunk in loop.RunStreamingAsync(prompt, ct))
{
    if (chunk.ToolCallDelta is { } call) ShowStarted(call.Id, call.NameDelta);
    if (chunk.ToolResult is { } done) ShowFinished(done.CallId, done.Success);
}
```

`ToolResult.CallId` is the `ToolCallDelta.Id` of the same call, and the record is the one the final `Turn` carries.

### `ToolCallResult.Success` is `bool?` on purpose

The loop **extracts** the calls the model requested — it does not invoke them. Unless the
`IChatClient` handed to the loop was built with `UseFunctionInvocation()`, there is no
`FunctionResultContent` to correlate against and the honest answer to "did it succeed" is unknown, so
`Success` is `null` rather than a guess. A call's presence and arguments are always populated; if your
check needs the outcome too, confirm your client wraps function invocation.

## Requirements

- .NET 10.0+

## Related Projects

- [ironhive](https://github.com/iyulab/ironhive) - LLM abstraction layer
- [ironhive-cli](https://github.com/iyulab/ironhive-cli) - CLI application using this agent engine
- [ironbees](https://github.com/iyulab/ironbees) - Multi-agent management

## License

MIT
