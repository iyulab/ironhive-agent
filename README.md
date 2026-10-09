# IronHive.Agent

Reusable agent engine for AI-powered CLI tools. Provides the core agent loop, context management, mode system, MCP plugin integration, and built-in tools.

## Features

- **Agent Loop**: Single-threaded master loop with streaming support; `RunAsync`/`RunStreamingAsync` accept an optional per-turn `ChatOptions` override (merged onto the loop's configured defaults) for callers that need to adjust temperature, tools, or reasoning flags on a single turn; a turn can also start from a `ChatMessage` (`RunAsync(message)` / `RunStreamingAsync(message)` — text plus an image, a page, an audio clip as `DataContent`/`UriContent`; a user message with at least one part); `ContinueAsync`/`ContinueStreamingAsync` continue from the current history without a new user message — the second half of a host-executed tool round trip (see [Tools the host runs](#tools-the-host-runs)). Every turn says why it ended — `AgentResponse.StopReason` / `TurnRecord.StopReason` (`Completed` · `OutputLimit` · `ContentFilter` · `ToolTerminated` · `AwaitingHostTools` · `StepLimit`); `AgentOptions.MaxTurnDuration` caps one turn's wall-clock time (past it the turn throws `TimeoutException`); `AgentOptions.Reasoning` sets how much the model reasons on every call (`new ReasoningOptions { Effort = ReasoningEffort.Low }` — the OpenAI-compatible provider sends `reasoning_effort`, a thinking-token budget and the chat-template thinking switches; how strictly a server holds a level is up to the server, and `ReasoningEffort.None` is the one servers enforce; a per-turn `ChatOptions.Reasoning` replaces it; unset sends nothing, so a reasoning model may think for tens of thousands of tokens per call)
- **Context Management**: Auto-compaction (by default token-based: the most recent 40k tokens are kept and compaction runs once at least 20k can be pruned; `UseTokenBasedCompaction = false` switches to a 92% threshold). Older messages are summarized, except user messages and the calls and results of `ProtectedToolOutputs` (default: the built-in file-reading tools); a tool call and its results are always kept or summarized together, goal reminders, prompt caching; observation masking of old tool results — per user turn, and with `CompactionConfig.ObservationMaskingProtectedTokens` by a size budget inside one turn (add `.UseToolRoundContext(contextManager)` after `UseFunctionInvocation()` so every round of a turn is reduced, see [Long single-message tasks](#long-single-message-tasks))
- **Mode System**: Plan/Work/HITL mode transitions with tool filtering
- **Tool invocation pipeline**: every tool call runs through ordered `IToolInvocationMiddleware` steps and every result through `IToolResultMiddleware` steps — on a chat client, in the Ironbees adapter, and for results a host supplies before `ContinueAsync`. Turn it on with `chatClient.AsBuilder().UseToolInvocationPipeline().Build(serviceProvider)` in place of `UseFunctionInvocation()` (`AddIronHiveAgent` registers the pipeline with its default loop guards; `AddIronHiveAgentApprovalGate()` adds the permission gate; `AddToolInvocationMiddleware<T>()` / `AddToolResultMiddleware<T>()` add your own). See [Tool Invocation Pipeline](#tool-invocation-pipeline)
- **MCP Plugins**: Model Context Protocol server connections, hot reload; supports Stdio and HTTP/SSE transports; `IsHealthyAsync` for liveness checks. A tool that returns an image hands the loop text and image content, which the IChatClient bridge carries to providers whose tool results hold images; `CallToolAsync` returns them as `McpToolResult.Images`, and `ToolResultText.Of` is how the loop reads such a result as text
- **Agent Skills (`SKILL.md`)**: `SkillsLoader.Create(new SkillsConfig { Roots = [userSkillsDir, projectSkillsDir] })` discovers skills per the [Agent Skills specification](https://agentskills.io/specification) and validates them as `skills-ref validate` does (invalid ones are *reported* in `Diagnostics`, never thrown; the first root wins a name collision and the shadowed one is reported). `loader.Contributor` puts every name + description in the system instructions; `loader.LoadTool` (`load_skill`) returns a skill's body — or a file inside its directory, and nothing outside it — on demand. `Enabled`/`Exclude`/`Filter` narrow the set per session; `MaxMetadataCharacters` bounds the section and what does not fit is in `Dropped`/`DroppedCount` (still loadable by name). `SkillDiscovery.Discover(config)` is the same discovery as a plain call, for a UI that lists skills before a session exists. Frontmatter keys the specification does not define reject the skill by default (as `skills-ref validate` does); `UnknownFields = UnknownFieldPolicy.Accept` loads such skills and reports the keys as a warning — skill trees written for one client's extensions (e.g. `argument-hint`) need this. A name in `Enabled` that matches nothing is reported (`EnabledNotFound`), not dropped silently. DI: `services.AddAgentSkills(config)` registers the loader and its contributor; add `LoadTool` to the loop's tools yourself. Text only — `scripts/` are never executed; `allowed-tools` is parsed, not enforced
- **System instruction sections**: add to the system instructions without replacing the prompt — implement `ISystemInstructionContributor` and register it in the container (or `ContextManager.AddInstructionContributor`); needs a `ContextManager` on the loop
- **AGENTS.md**: `new AgentsMdInstructions(workingDirectory)` is a contributor that adds the [AGENTS.md](https://agents.md) files from the repository root (the directory with `.git`) down to the working directory, root first so the nearest file comes last and wins; nothing above the repository is read, and an edit applies on the next turn. `AgentsMdOptions.MaxCharacters` (default 32,000) bounds it — the farthest files are left out first. Not registered by `AddIronHiveAgent` (it needs the session's working directory): create one per session and pass it to the loop's `ContextManager`
- **Built-in Tools**: Read, Write, Edit, Delete, Move, ListDirectory, Shell, Glob, Grep, Todo — `BuiltInTools.GetAll(workingDirectory)`. `EditFile(path, oldText, newText, replaceAll)` replaces exact text (exactly one match unless `replaceAll`; the file's line endings and byte-order mark are kept), so a one-line change does not rewrite the whole file. `WriteFile` into an existing file (overwrite or `append`) keeps its encoding and line endings too. A host decides where they reach and what runs around a write with `FileToolOptions`: `BuiltInTools.GetAll(workingDirectory, new FileToolOptions { AllowedRoots = ["."], WriteInterceptor = ... })`. The working directory alone is not a boundary — `AllowedRoots` is (default: none, opt-in). A tool that fails (file not found, `oldText` not unique, a command that times out, …) throws, so the loop's guards count it as a failure and `RepeatedErrorGuardMiddleware` ends a request that keeps failing the same way; the model reads the message (`UseToolInvocationPipeline` sets `IncludeDetailedErrors = true`, and the Ironbees adapter always passes it — turn it off in `UseToolInvocationPipeline(configure: c => c.IncludeDetailedErrors = false)`; a plain `UseFunctionInvocation()` keeps Microsoft.Extensions.AI's generic message). A path outside `AllowedRoots` is a refusal and comes back as text
- **Delegation (agent as a tool, `IronHive.Agent.Ironbees` package)**: `DelegationTools.Create(orchestrator, new DelegatedAgent { AgentName = "research", Model = ..., MaxToolTurns = ... })` turns an Ironbees named agent into an `AIFunction` the model calls to hand off a sub-task. Each call is an isolated run of that agent — its `agent.yaml` tools (an agent that lists `tools` gets exactly those), prompt and model, with per-delegation model, reasoning level, output cap and tool-turn limit. `DelegationOptions` bounds nesting depth (across agents), concurrency, and feeds the delegated usage into the parent's `IUsageLimiter`/`IUsageTracker`. A run that stops at its turn limit comes back marked partial; a run that fails throws, naming the agent and the cause, so the loop reports it to the model like any failing tool and `RepeatedErrorGuardMiddleware` ends the request when the same failure repeats. An `agent.yaml` needs a `model:` section (Ironbees refuses to load one without it); leave `deployment` out to run on the caller's model — a deployment is sent to the server as the model id. Not registered by `AddIronHiveAgent`: create the tools where the orchestrator is available and add them to the loop's `Tools`. To call a named agent from application code instead, use `IAgentOrchestrator.ProcessStructuredAsync(input, new ProcessOptions { AgentName = ... })`
- **Advisor (consult a stronger model)**: `AdvisorTool.Create(strongerClient, new AdvisorOptions { ModelId = ..., MaxCalls = 5 })` gives the working model a no-argument tool that sends the conversation so far — its requests, tool calls and results — to a stronger model and returns that model's review. The working model decides when to consult (the default description says: before committing to an approach, when stuck, before declaring done), so the strong model is paid for only where judgment matters. Works under `FunctionInvokingChatClient` and inside Ironbees agents (`ChatClientFrameworkAdapter`); elsewhere pass `AdvisorOptions.Conversation`. The advisor is never given tools; `MaxCalls`, `UsageLimiter` and `UsageTracker` bound and account for it. Not registered by `AddIronHiveAgent`: add the tool to the loop's `Tools`
- **Long-term session memory (`IronHive.Agent.Memory` package)**: `new SessionMemoryService(memoryService, userId)` implements `ISessionMemoryService` over a MemoryIndexer `IMemoryService`; `EmbeddingServiceAdapter` (over the same `IEmbeddingProvider` the tool retriever uses — memories are embedded on the document side, memory searches through `EmbedQueryAsync`) and `TextCompletionServiceAdapter` (over an `IChatClient`) supply the MemoryIndexer services it needs. `ISessionMemoryService` lives in `IronHive.Agent`, so a host that does not use memory ships no MemoryIndexer.
- **Deep research (`IronHive.DeepResearch` package)**: `AddDeepResearch(...)` registers an iterative research pipeline — query planning, web search, content extraction, sufficiency analysis, then a cited report — over one text-generation service (`ChatClientTextGenerationAdapter` for an `IChatClient`). Results report the run's token usage; the cost is left null because the run does not know which model priced its calls. Tune it through the options callback: `services.AddDeepResearch(chatClient, o => { o.SufficiencyThreshold = 0.7m; o.MinSourcesBeforeReport = 5; o.MaxSearchRetriesPerIteration = 2; })`. `SufficiencyThreshold` is the score at which research stops. `MinSourcesBeforeReport` keeps it iterating while fewer sources were collected and a gap remains. The per-query iteration limit is `ResearchRequest.MaxIterations`, capped by `Depth`. Run it through the registered `IDeepResearcher`: `await sp.GetRequiredService<IDeepResearcher>().ResearchAsync(new ResearchRequest { Query = "..." })` (`ResearchStreamAsync` yields progress as it goes). Tavily web search needs its API key in `DeepResearchOptions.SearchApiKeys["tavily"]` (e.g. `o.SearchApiKeys["tavily"] = tavilyKey` in the options callback)
- **Checkpoint contract**: `ICheckpointService` / `CheckpointInfo` — the shape of a host's pre-destructive-operation snapshot store. There is no built-in implementation and the loop does not call it; a host that wants checkpoints implements and invokes it
- **Permission System**: Rule-based access control for files, commands, and tools; ships with sensible defaults. One `IToolCallPolicy` gives each call its verdict (`ToolCallPolicy` over the rules by default — register your own to replace it); `AddIronHiveAgentApprovalGate()` acts on it, enforces Planning mode, and asks the `IHumanApprovalService` on `Ask`. See [Human Approval Gate](#human-approval-gate)
- **Planning System**: `DefaultTaskPlanner`, `DefaultPlanExecutor`, `HeuristicPlanEvaluator`, `PlannerTriggerDetector`, `PlanAndExecuteOrchestrator`. Not registered by `AddIronHiveAgent` — construct them where an `IChatClient` and the tools are available (or register them yourself)
- **Usage Tracking**: Token/cost tracking (`IUsageTracker`) and session limits (`IUsageLimiter`, registered by `AddIronHiveAgent` when `UsageLimits` is set). Pass the limiter to `AgentLoop` or `ThinkingAgentLoop` (`usageLimiter:`) and a turn past the limit is refused with `UsageLimitExceededException`. The loop checks once per turn; to check every model call — each tool round of a turn too — put `UseUsageLimit()` inside function invocation (`.UseToolInvocationPipeline().UseUsageLimit()`): the loop binds its limiter to it, and the round after the one that reached the limit throws instead of calling the model
- **Tracing (OpenTelemetry, GenAI semantic conventions)**: every turn of `AgentLoop` / `ThinkingAgentLoop` is an `invoke_agent` span on the `AgentTelemetry.SourceName` (`"IronHive.Agent"`) source — `gen_ai.agent.name` (`AgentOptions.Name`), `gen_ai.request.model`, `gen_ai.usage.*`, tool-call count, `error.type` when the turn throws. Add `.UseOpenTelemetry()` to the chat client and its `chat` and `execute_tool` spans nest under the turn. See [Tracing](#tracing)
- **Error Recovery**: Categorized error handling with recovery strategies (`IErrorRecoveryService`). Passed to either loop (`errorRecovery:`), a buffered turn that fails transiently is retried once. A provider refusal is read with `IronProw.Core`'s `IHttpFailureReader` (status and `Retry-After`) and its transient-status boundary, so the loop retries what the IronProw gateway retries and waits out a rate limit as long as the provider asked; register an extra `IHttpFailureReader` for a provider-specific exception. An account that cannot pay (IronHive's `BillingException`, HTTP 402) is `ErrorCategory.Billing` and escalates — it is never waited out as a rate limit. Per-call HTTP headers for a gateway (an attribution tag per run) go in the turn's override `ChatOptions.AdditionalProperties[ChatClientAdapter.RequestHeadersKey]` (`RunAsync(prompt, options)`)
- **Webhook System**: Event notifications with HMAC signing. Turn it on with `services.AddIronHiveAgent(o => o.Webhook = new WebhookConfig { Endpoints = [new WebhookEndpoint { Url = "...", Secret = "..." }] })` (per endpoint: `EventFilter`, `Headers`, `TimeoutSeconds`, `RetryCount`). The built-in sender is the usage limiter, which posts `TokenLimitWarning` / `CostLimitWarning` events — so it also needs `UsageLimits` set; other events are sent through `IWebhookService` by your own code

## Packages

| Package | Purpose |
|---|---|
| `IronHive.Agent` | Agent loop, context management, modes, approvals, MCP plugins, built-in tools, advisor tool, and the guard/memory seams |
| `IronHive.DeepResearch` | Iterative web research pipeline with cited reports (`AddDeepResearch`) |
| `IronHive.Agent.Ironbees` | Ironbees integration: `ChatClientFrameworkAdapter`, `AddIronbees`, `OrchestratedAgentLoop`, `DelegationTools` (a named agent as a tool), `IronbeesEmbeddingProviderAdapter`. `services.AddIronbees(o => ...)` takes an `Action<IronbeesOptions>` and needs a model client: set `IronbeesOptions.ChatClientFactory` (`Func<ModelConfig, IChatClient>`) or register an `IChatClient`. Options: `AgentsDirectory` (default `./agents`), `DefaultAgentName`, `SelectorType`, `EmbeddingProvider`, `HybridKeywordWeight`, `EnableToolExecution`, `WorkingDirectory`, `MaxToolTurns`, `ConversationsDirectory` |
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
using IronHive.Agent.Loop;
using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;
using OpenAI;

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
├── Permissions/    # Permission evaluation and configuration
├── Tracking/       # Usage tracking and limits
├── Providers/      # Chat client, embedding, rerank provider abstractions
├── Memory/         # Session memory contract (ISessionMemoryService)
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

`McpPluginsConfigLoader.LoadFromDefault(baseDirectory)` reads the first of `.ironhive/plugins.yaml`, `.ironhive/plugins.yml`,
`.ironhive/plugins.json` (then `plugins.yaml|yml|json`) under the directory (default: the current directory), or returns an
empty configuration. For hot reload, hand it to `new McpPluginHotReloader(pluginManager, config, watchDirectory)` and call
`InitializeAsync()`: it connects the configured plugins (when `AutoConnect` is on) and, while the file watcher is on
(`enableFileWatcher`, default `true`), re-reads the config when the file changes — connecting added plugins, disconnecting
removed or excluded ones and reconnecting changed ones. `ReloadAsync(newConfig)` applies a configuration on demand.

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
using IronHive.Agent.Mcp;

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
`IMcpToolCallGuard`, and the container's tool invocation pipeline the `IToolResultGuard`.

A guard that itself throws is treated as fail-closed (the call is blocked, not silently
dispatched unguarded) — see `McpPluginManager.CallToolAsync`'s XML doc remarks for the reasoning.

## Tool Invocation Pipeline

An `IAgentLoop` never runs tools itself; the function-invoking client it is given does. `UseToolInvocationPipeline()`
installs that client with one `FunctionInvoker`, a `ToolInvocationPipeline` folded from ordered middleware:

```csharp
services.AddIronHiveAgent();                 // registers the pipeline with its default loop guards
services.AddIronHiveAgentApprovalGate();     // opt-in permission gate (see Human Approval Gate)
services.AddToolInvocationMiddleware<AuditMiddleware>();   // your own steps, in registration order

var chatClient = inner.AsBuilder().UseToolInvocationPipeline().Build(serviceProvider);
var loop = new AgentLoop(chatClient, new AgentOptions { Tools = tools });
```

- **Invocation steps** (`IToolInvocationMiddleware.InvokeAsync(context, next, ct)`) run around each call, the first
  registered outermost. Call `next` to run the call. Return a result without calling it to short-circuit (a
  `ToolCallRefusal` is the model-readable «not run»). Set `context.Terminate` to end the request after this call. Don't
  throw for that: an exception is reported to the model as a failed call.
- **Result steps** (`IToolResultMiddleware.OnResultAsync(context, ct)`) see every result before the model does: right
  after an in-process tool returns (inside every invocation step), and, when a loop continues, each result a host
  supplied for a tool it runs itself (`context.IsHostResult`; see [Tools the host runs](#tools-the-host-runs)). A
  registered `IToolResultGuard` is applied as one of them.
- Without DI, build it yourself: `.UseToolInvocationPipeline(new ToolInvocationPipeline([gate, mine], [resultStep]))`.
  `ToolInvocationPipeline.CreateDefault()` is the default loop guards alone. `UseToolInvocationPipeline()` built without
  services that hold a pipeline throws instead of running tools unguarded.
- The Ironbees adapter (`ChatClientFrameworkAdapter`) runs its own tool loop through the same pipeline
  (`toolInvocationPipeline:`; `AddIronbees` passes the container's). `DefaultPlanExecutor` takes one too; given
  none, it runs `ToolInvocationPipeline.CreateDefault()` — the loop guards only, **no permission gate**. Pass the
  container's pipeline (`serviceProvider.GetRequiredService<ToolInvocationPipeline>()`) to keep the gate.

Default loop guards (`AddIronHiveAgent(o => o.ToolInvocation = new ToolInvocationOptions { ... })`):

| Guard | What the model gets | Option (default) |
|---|---|---|
| `ArgumentParseFailureMiddleware` | a call whose arguments could not be parsed is not run; the model reads the parse error | `RefuseUnparseableArguments` (true) |
| `RepeatedCallGuardMiddleware` | the same tool with identical arguments (or on the same target, for a tool that declares [target arguments](#target-arguments)), after that many successful runs in a row **that returned the same answer** (for a tool without target arguments — an identical status call whose answer moves is progress, not repetition), is not run again; the `MaxRefusedRepeats`-th refusal in a row of that call ends the request (`TurnStopReason.ToolTerminated` on a `RepeatedCall` refusal), so a stuck model does not spend the step budget | `MaxRepeatedCalls` (3; 0 = off) · `MaxRefusedRepeats` (2; 0 = keep refusing) |
| `RepeatedResultGuardMiddleware` | the same tool returning the same result to identical arguments (or the same target) on that many separate visits — read-only and undeclared tools only (see [Read-only tools](#read-only-tools)) (other calls in between) ends the request (`TurnStopReason.ToolTerminated` on a `RepeatedResult` refusal that names the cause) — the shape of a working set larger than the masking budget (`ObservationMaskingProtectedTokens`), re-read in rotation until the step limit or written from memory; consecutive repeats are the call guard's, a changed result does not count | `MaxRepeatedResults` (3; 0 = off) |
| `RepeatedErrorGuardMiddleware` | the same tool failing with the same error that many times in a row ends the request (`Terminate`) with a result, not an exception | `MaxRepeatedErrors` (3; 0 = off) |

A failure is a call that throws **or a result that reports one**: an MCP result with `isError: true` is recognised out of
the box (keyed by its first text content), and `ToolInvocationOptions.FailureOf` (`Func<object?, string?>`, error text or
null) adds your own tools' convention — e.g. `FailureOf = r => r is string s && s.StartsWith("Error") ? s : null`. Both
guards use it: such a result counts toward `MaxRepeatedErrors` and never as a successful run for `MaxRepeatedCalls`.

### Time limit per tool call

`ToolInvocationOptions.MaxInvocationDuration` (null by default — no limit) bounds one tool call. Past it the call's
cancellation token is cancelled and the model reads a `ToolCallRefusal` of kind `TimedOut` naming the tool and the limit
(«'search_files' did not finish within 30 s and was stopped; ask for less at once …»), so it can narrow the request.
A tool that ignores its token is abandoned a few seconds later — it may keep running in the background, but the turn
goes on. Timeouts count as failures for `MaxRepeatedErrors`, so a model that repeats the same slow call is stopped.

```csharp
services.AddIronHiveAgent(o => o.ToolInvocation = new ToolInvocationOptions { MaxInvocationDuration = TimeSpan.FromSeconds(30) });

var build = AIFunctionFactory.Create((string target) => "built", "run_build")
    .WithMaxDuration(TimeSpan.FromMinutes(10));      // its own limit wins over the default
var shell = AIFunctionFactory.Create((string command) => "ok", "shell")
    .WithMaxDuration(Timeout.InfiniteTimeSpan);      // no limit
```

- Only the tool's own run is timed — the pipeline applies the limit innermost, so an approval gate waiting for a person
  does not use it up.
- A pipeline you assemble yourself takes the options as its third argument:
  `new ToolInvocationPipeline(steps, resultSteps, options)`; without it there is no default limit (a tool's own
  `WithMaxDuration` still applies).
- A streamed turn reports the timeout on the call's `ToolResult` chunk (`Success = false`, `RefusalKind = TimedOut`);
  between the call's `ToolCallDelta` and that chunk, a tool is running — not a silent model.
- `ToolInvocationHints.MaxDurationKey` (`"ironhive.invocation.maxduration"`) holds a tool's own limit (a `TimeSpan`, a
  number of seconds, or a string of either). An MCP server's `_meta` is **not** read for it: a server must not lift the
  limit its client set.

### Target arguments

By default two calls are "the same call" when the tool and every argument match. A tool whose arguments split into the
one that names what it acts on (a `path`, a `url`, a record id) and ones that only shape the answer (a question, a
format) can declare the first kind. The repeated-call and repeated-result guards then compare those alone, so a model
that keeps describing one image while it adds or drops a question is stopped like any other repeat:

```csharp
using IronHive.Agent.Invocation;

var describe = AIFunctionFactory.Create(
        (string path, string? question) => "...", "describe_image", "Describe an image, optionally answering a question about it.")
    .WithTargetArguments(["path"]);
```

- The refusal names the target ("already ran 3 times in a row on the same path, whatever the other arguments") and tells
  the model to move on to a different path. The `MaxRefusedRepeats`-th refusal ends the request, as before.
- Every name must be a parameter of the tool. `WithTargetArguments` throws otherwise, since an unknown name would make
  every call one target. It replaces any targets the tool already declares, and the tool is described and invoked
  unchanged. `ToolInvocationHints.GetTargetArguments(tool)` reads them.
- An MCP server declares them in a tool's `_meta` under `ToolInvocationHints.TargetArgumentsKey`
  (`"ironhive.invocation.target"`), as a string array or a comma-separated string. `McpPluginManager` carries them over;
  a name the tool's input schema does not declare is dropped.
- Declare targets only where a new value of every other argument still means "the same thing again". A read with an
  `offset` or a paged search moves on with each call and must not declare its `path`/`query` as the target.

### Read-only tools

`tool.WithReadOnly(false)` says a call can change something (a command, a write); `WithReadOnly(true)` says it only
reads. `RepeatedResultGuardMiddleware` leaves tools declared not read-only alone: re-running a check after an edit and
getting the same failure is the edit-and-check loop, not a re-read. Undeclared tools are guarded as before. An MCP tool
answers with its server's `readOnlyHint` (`ToolInvocationHints.IsReadOnly(tool)` finds it through wrappers too), and the
built-in tools declare it (`ReadFile`, `ListDirectory`, `GlobFiles`, `GrepFiles` read-only; the others not).

## In-Process Tool-Result Guard (opt-in)

In-process tools (a web page's text, a file's contents) return text the model then reads, which is the same prompt-injection surface an MCP result has. `IToolResultGuard` inspects every result before the model does. Each inspection gets a verdict: `Allow`, `Replace(sanitized)`, or `Withhold(reason)`.
- A withheld result becomes a `ToolCallRefusal` (`ToolCallRefusalKind.ResultWithheld`). The model reads «Tool result withheld by guard: …» and the loop reports the call with `Success = false`.
- A guard that throws withholds the result. This is fail-closed, the same rule as the MCP guardrail.

It runs as a result step of the [tool invocation pipeline](#tool-invocation-pipeline). With DI, register the guard
(`services.AddSingleton<IToolResultGuard>(...)`) and the container's pipeline applies it. Without DI, add a
`ToolResultGuardMiddleware`. The gate decides whether a call runs, and the guard decides what its result becomes:

```csharp
var pipeline = new ToolInvocationPipeline(
    [new ApprovalGateMiddleware(policy, approvalService)],
    [new ToolResultGuardMiddleware(guard)]);
var client = inner.AsBuilder().UseToolInvocationPipeline(pipeline).Build();
```

The same guard covers the Ironbees adapter and the results a host supplies before `ContinueAsync`. To reuse the FluxGuard guardrail you already give `McpPluginManager`, wrap it: `new McpGuardrailToolResultGuard(guardrail)` (`IronHive.Agent.FluxGuard` package). In-process tools are then reported to it under the server name `in-process`.

## Available Tools Context

After the agent loop factory filters tools via `IModeToolFilter.FilterTools()`, it should populate
`IAvailableToolsContext` so that tool implementations can generate context-aware error messages.

```csharp
// In your host's own agent loop factory (its IAgentLoopFactory.CreateAsync):
var filteredTools = modeToolFilter.FilterTools(allTools, modeManager.CurrentMode);

// Expose filtered tool names to tool implementations via DI
var context = serviceProvider.GetRequiredService<IAvailableToolsContext>();
context.SetAvailableTools(filteredTools.OfType<AIFunction>().Select(t => t.Name));
```

Tool implementations can then inject `IAvailableToolsContext` to produce accurate guidance:

```csharp
public class FileSystemTools(IAvailableToolsContext availableTools)
{
    [Description("Write content to a file.")]
    public string WriteFile(string path, string content)
    {
        if (content.Length == 0)
        {
            var hint = availableTools.IsAvailable("DeleteFile")
                ? "To delete a file, use the DeleteFile tool instead."
                : "To delete a file, use a dedicated delete operation.";
            return $"Error: empty content is not allowed. {hint}";
        }

        File.WriteAllText(path, content);
        return $"Wrote {content.Length} characters to {path}.";
    }
}

// Register it like any other in-process tool (see Native (In-Process) Tools)
var fileSystemTools = new FileSystemTools(availableToolsContext);
AITool[] tools = [AIFunctionFactory.Create(fileSystemTools.WriteFile)];
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

### Query and document embeddings

`EmbeddingToolRetriever` embeds tool descriptions with `IEmbeddingProvider.EmbedBatchAsync` and the turn it searches
with `IEmbeddingProvider.EmbedQueryAsync`. The query method defaults to `EmbedAsync`, which is right for a
symmetric model. A provider for an asymmetric model (E5, Nomic, a BGE query instruction) implements `EmbedQueryAsync`
with its query convention and applies its document convention in `EmbedAsync`; a provider that wraps another one
(`FallbackEmbeddingProvider`) forwards all three.

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

### Selection order, retrieval hints and the selection trace

Both retrievers select in this order:

1. **Pins** — `AlwaysInclude`, regardless of score.
2. **Exact names** — a tool the query names by its exact name (a whole word, case-insensitive, surrounding
   punctuation and backticks ignored: "use `GrepFiles`.") takes the first scored slots regardless of score or
   `MinRelevanceScore`, and is kept even past the scored budget.
3. **Scored tail** — the top-scored tools above `MinRelevanceScore`, within the budget above.
4. **Companions** — tools a selected tool declares it is used with, added outside the budget.

That order decides **which** tools are selected and is what `ToolRetrievalResult.Selections` reports. The tools are
**sent** ordinal by name (`SelectedTools`), so the same set always serialises identically whatever the scores, the
query, or the order of the catalog. A prefix-cached server (llama-server, vLLM) keeps its prompt cache as long as the
set holds — with a chat template that renders tools before the system text, even a reordering of the same set would
re-read the whole prompt. `McpPluginManager.GetToolsAsync` lists plugins in name order for the same reason.

**Sticky selection** (opt-in, `ToolRetrievalOptions.StickyToolLimit`): the set of tools a conversation already sent
is sent again **unchanged** for as long as it serves the request. Chat templates and prompt caches put the tools first,
so any change to the tool list re-reads the prompt after it — and a hybrid or recurrent model (which can only roll back
to a saved checkpoint) re-reads all of it. The set changes only when the request needs a tool it lacks: a pin, a tool
the request names exactly or by alias, or the request's best-scored tool when it is a confident match — it scores at
least `StickyChangeScore` (default 0.7; a generic follow-up's best match usually sits just above `MinRelevanceScore`,
and changing the set for it would cost a full re-read). Then the request's whole selection joins it,
after the carried tools; when that would exceed the limit, the selection starts over (one cache miss). Lower-ranked
tools a held request would have added are reported in `ToolRetrievalResult.Withheld`, not sent. `AgentLoop` and
`ThinkingAgentLoop` carry the list themselves (`ClearHistory` and `InitializeHistory` forget it); a caller driving a
retriever directly passes the previous result's tool names in `StickyTools`. Carried-only tools appear in
`Selections` as `Carried`. Leave it off (0, the default) when every request should get exactly its own selection.

```csharp
var options = new AgentOptions
{
    Tools = tools,
    ToolRetrievalOptions = new ToolRetrievalOptions { MaxTools = 8, StickyToolLimit = 24 },
};
```

A tool declares **retrieval hints** in `AITool.AdditionalProperties`, so they travel with the tool however it
was created:

```csharp
var restore = AIFunctionFactory.Create(RestoreFileVersion, "restore_file_version", "Restore a file to an earlier saved version.")
    .WithRetrievalHints(
        aliases: ["undo", "revert", "roll back", "put back"], // words people use instead of the name
        companions: ["list_file_versions"]);                   // selected along with it
```

- **Aliases** (`ToolRetrievalHints.AliasesKey`, `"ironhive.retrieval.aliases"`): a query holding an alias scores the
  tool as if it named it (full name coverage). A lexical scorer cannot otherwise get from "undo that" to
  `restore_file_version`. `EmbeddingToolRetriever` embeds the aliases with the description. The query must hold every
  word of a multi-word alias:
  - In most scripts, each word must appear as a whole word, with no substring matching.
  - A Hangul word of two or more syllables matches a query word it begins, since particles and endings attach to the
    word: `전사` matches `전사해줘`.
  - A Han, Hiragana or Katakana word matches anywhere in a query word, since those scripts write words without
    spaces: `文字起こし` matches `この動画を文字起こししてください`.
- **Companions** (`ToolRetrievalHints.CompanionsKey`, `"ironhive.retrieval.companions"`): the first
  `ToolRetrievalHints.MaxCompanionsPerTool` (3) declared names that are available, one level deep.
- A value is a sequence of strings or one comma-separated string (the form a string-only transport such as
  MCP `_meta` carries). `WithRetrievalHints` wraps an `AIFunction` or a declaration-only
  `AIFunctionDeclaration` without changing how it is described or invoked, and merges with hints already there.
- Hints an MCP server declares in a tool's `_meta` reach the retrievers through `McpPluginManager`. A host that lists
  tools with its own MCP clients uses the same bridge: `McpPluginManager.WithDeclaredHints(mcpClientTool)`, which carries
  [target arguments](#target-arguments) too.
  `ToolRetrievalHints.ParseValue(value)` reads a hint value the way the retrievers do.

`ToolRetrievalResult.Selections` records every selected tool with its reason (`Pinned`, `ExactName`, `Alias`,
`Scored`, `Companion`) and score, in selection order. To log what each request was sent, wrap the retriever:

```csharp
sealed class TracingRetriever(IToolRetriever inner, ILogger log) : IToolRetriever
{
    public async Task<ToolRetrievalResult> RetrieveAsync(string query, IList<AITool> tools,
        ToolRetrievalOptions? options = null, CancellationToken ct = default)
    {
        var result = await inner.RetrieveAsync(query, tools, options, ct);
        log.LogDebug("Tools: {Selections}", string.Join(", ", result.Selections.Select(s => $"{s.Name}={s.Reason}")));
        return result;
    }
}
```

## Permission Defaults

`PermissionConfigLoader` reads `.ironhive/permissions.yaml` (or `.yml`, `.json`). With no file, the defaults below apply. A file that exists but is not a permission configuration (malformed, no `permissions` section, a misspelled section or key, an action other than `allow`/`deny`/`ask`) throws `PermissionConfigException` rather than falling back to the defaults, which allow more than a restrictive file would.

`PermissionConfig.CreateDefault()` (the out-of-the-box default) ships the following rules:

**Read** — Allow `**/*`; Ask on `.env*` files; Deny `**/secrets/**`

**Edit** — Allow `src/**/*` and `tests/**/*`; Ask on `*.json`, `*.yaml`, `*.yml`

**Bash** — Allow `git *`, `dotnet *`, `npm *`, `cargo *`; Deny `rm -rf *`, `sudo *`, `curl * | *sh*`

Before any `Bash` rule, a few commands are refused outright and no rule can allow them: `rm -rf /` (or `/*`), a pipe into `sh`/`bash`, `dd` or `>` onto a disk device, `mkfs`, `fdisk`, `format X:`, `chmod 777 /` and the fork bomb. They match the command itself — `rm -rf /tmp/build` or `| sha256sum` are judged by the rules.

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
permits them next to the built-in read-only tools (read, list, glob, grep, the advisor); without a declaration a host
tool never runs in Planning. This is a side-effect class, not a permission: `Tools` still decides `Allow` / `Ask` / `Deny`.

**AskBeforeDelete** — `true` (default): deleting a file the `Edit` rules allow still asks. `false`: the `Edit` rules
decide deletes as they decide writes.

`AddIronHiveAgent()` reads a plain `PermissionConfig` registered in the container (not `IOptions<PermissionConfig>` —
`services.Configure<PermissionConfig>(...)` has no effect); with none registered, `PermissionConfig.CreateDefault()` applies.
To keep the defaults and add to them, register an edited copy:

```csharp
var permissions = PermissionConfig.CreateDefault();
permissions.ReadOnlyTools.AddRange(["read_current_tab", "list_saved_*"]);

// Override any category
permissions.McpTools.Add(new PermissionRule
{
    Pattern = "my_plugin_*",
    Action = PermissionAction.Allow,
    Priority = 5
});

services.AddSingleton(permissions);
```

`services.AddIronHiveAgentPermissions(config => ...)` registers the same way: it starts from
`PermissionConfig.CreateDefault()` and applies your changes on top (before 0.25.0 it started from an empty config and
dropped the default rules).
Its counterpart for compaction is `services.AddIronHiveAgentContext(config => ...)`, which registers the
`CompactionConfig` the container's compaction services read.

## Human Approval Gate

An `IToolCallPolicy` decides `Allow` / `Deny` / `Ask` for each call; `IHumanApprovalService` (the approver) answers
the `Ask`. The default policy, `ToolCallPolicy`, judges calls by the permission rules above; a host with its own rules (a
category table, trust tiers) registers its own `IToolCallPolicy` instead. What connects them to an actual tool call is
`ApprovalGateMiddleware`, a step of the [tool invocation pipeline](#tool-invocation-pipeline). The gate is opt-in: a
pipeline without it has no permission gate. With DI:

```csharp
services.AddIronHiveAgent();
services.AddIronHiveAgentApprovalGate();     // over the container's IToolCallPolicy, IHumanApprovalService and IModeManager

var chatClient = inner.AsBuilder().UseToolInvocationPipeline().Build(serviceProvider);
var loop = new AgentLoop(chatClient, new AgentOptions { Tools = tools });
```

Without DI: `new ToolInvocationPipeline([new ApprovalGateMiddleware(new ToolCallPolicy(permissionConfig), approvalService)])`;
pass `modeManager:` and `modeToolFilter:` too to enforce Planning mode.

**Planning mode is enforced at the gate.** When the gate has an `IModeManager` (with DI: whenever one is registered —
`AddIronHiveAgent` registers it) and the current mode is `Planning`, a tool `IModeToolFilter.IsToolPermitted` does not
permit for Planning (anything but the read-only tools) is denied whatever the policy says. Only Planning: `Idle` and
`HumanInTheLoop` are interaction states, so a host that never fires a mode trigger is not affected.

Then the gate runs `IToolCallPolicy.Evaluate` and acts on `RiskAssessment.Verdict`:

| Verdict | What happens |
|---|---|
| `Allow` | the tool runs |
| `Deny` | the tool does not run; the model receives `Permission denied: <reason>` as the result |
| `Ask` | `IHumanApprovalService.RequestApprovalAsync` is called; approval runs the tool (with `ModifiedArguments` applied if the approver edited them), rejection returns `Approval rejected: <reason>` |

A refusal is always a tool *result*, never an exception, so the model can read it and change course.
An `Ask` verdict with **no approval service registered is refused**, not passed through — a gate that
lets "ask" through when nobody can be asked is a silent no-op. Either register an
`IHumanApprovalService`, set the rule (or `DefaultAction`) to `Allow`, or leave the gate out. Remembering an
`ApprovalResult.AlwaysApprove` answer is the service's job; the gate asks every time. `ApprovalRequest.CallId` carries the
call's id, so an approver on a wire can pair its request with the call's start and end events.

Steps registered after the gate run only for calls it let through. The Ironbees adapter (`ChatClientFrameworkAdapter`,
`IronHive.Agent.Ironbees`) runs its own tool loop through the same pipeline, so a verdict means the same thing on both
paths; `AddIronbees` turns the gate on for the container's pipeline.

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

### Long single-message tasks

One user message followed by many tool rounds — read a document section by section, walk a folder — is one user turn,
so masking by user turns keeps every result of it at full size until the window overflows. Give recent results a **size
budget** instead, and let every model call of the turn pass through the context manager:

```csharp
var contextManager = ContextManager.ForModel("gpt-4o", new CompactionConfig
{
    ObservationMaskingProtectedTokens = 8_000,   // results past the newest 8k tokens become "[Masked: read_section {…} result, …]"
});
var client = chatClient.AsBuilder()
    .UseFunctionInvocation()
    .UseToolRoundContext(contextManager)      // inside function invocation: sees each round
    .Build();
var loop = new AgentLoop(client, options, contextManager: contextManager);
```

Building the pipeline before the manager exists (a `ChatClientFactory` decorator shared by every client it creates)?
Add `.UseToolRoundContext()` with no argument after `UseFunctionInvocation()`; the loop binds its own `ContextManager`
when it is constructed over that client.

The budget is size, not rounds: a round of short results (a write answering "ok") costs little and does not push out the
larger reads before it, and the newest round's results are always sent whole. Pick the budget from the window when you
know it (a quarter of it, say) and a fixed size when you do not. A placeholder names the call — tool and arguments — and
says the content is no longer visible and that the tool can be called again with the same arguments, so a model that needs
it re-reads rather than guesses; a masked result keeps the same text on later rounds. The tool calls stay, so the model
still knows what it read; only results longer than `ObservationMaskingMinResultLength` are replaced, and only in the
request — `History` keeps them in full.
`ToolRoundContextChatClient` also applies tool-result compaction (`EnableToolResultCompaction`) per round. It makes no LLM
call of its own, except to compact after an overflow (below); summarizing compaction otherwise runs once per turn in the loop.

### When the context window is not known

A server that does not report its window (many OpenAI-compatible servers) leaves `MaxContextTokens` unset, and for a model
name the catalog does not know, the counter guesses 8192. Compacting against that guess summarizes far too early on a large
server. With `ToolRoundContextChatClient` in the pipeline (as above), `CompactionConfig.CompactOnOverflow` (default `true`)
waits for the server instead:

- while the window is a guess (`ContextManager.DefersCompactionToOverflow`), no pre-emptive compaction runs;
- a model call that fails with `ContextOverflowException` is compacted once (to `TargetRatio` of the window) and retried once;
  a second overflow propagates, and a streaming call is retried only if nothing was streamed yet;
- the window is learned from the error (`ContextWindow`, or else the size that overflowed as an upper bound), so later
  turns compact pre-emptively against it. `ContextManager.LearnContextWindow(tokens)` sets it directly.

Later tool rounds of the same turn reuse the compacted messages, so a turn compacts once. Set `CompactOnOverflow = false`
to keep compacting against the guess. With a known window nothing changes, except that an overflow that still happens is
caught the same way.

### Tools the host runs

Some tools can only run on the host — reading a browser tab, applying an edit in an IDE, asking the user to confirm.
Declare them without an implementation (`AIFunctionFactory.CreateDeclaration`). With `UseFunctionInvocation()` on the
chat client, the loop stops at such a call: `AgentResponse.ToolCalls` names it and `History` ends with the pending
call. Run it, append the result, and continue:

```csharp
var options = new AgentOptions { Tools = [AIFunctionFactory.CreateDeclaration("read_page", "Reads one open tab's text.", schema)] };
var loop = new AgentLoop(chatClient.AsBuilder().UseFunctionInvocation().Build(), options);

var response = await loop.RunAsync("What is on tab web-1?", ct);
foreach (var call in response.ToolCalls)          // pending host tools
{
    var result = await host.RunAsync(call, ct);
    var history = loop.History.ToList();
    history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId!, result)]));
    loop.InitializeHistory(history);             // or a fresh loop, for a host that keeps the conversation itself
}
response = await loop.ContinueAsync(ct);         // no invented user message
```

`ContinueAsync` refuses a history that does not end with a result for every call of the last assistant message (or
with a user message, for a restored conversation) before calling the model. With `UseToolInvocationPipeline()` in
place of `UseFunctionInvocation()`, the results you appended then go through the pipeline's result stage (a registered
`IToolResultGuard` included) before the model reads them. Each is processed once: only the trailing tool messages are
read, and results the loop's own turns produced are skipped. Declarations keep their own name and
description everywhere the loop reads tools — Planning mode's `ReadOnlyTools`, tool retrieval, schema compression.
`OrchestratedAgentLoop` has no host tools to continue from and throws `NotSupportedException`.

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

### A tool call's arguments while the model writes them

For a call whose real output is one long argument (a whole document or app), set `AgentOptions.StreamToolArguments = true`.
The loop then also yields `ToolCallDelta` chunks with `IsComplete = false` while the model writes: same `Id`,
`NameDelta` on the first, and `ArgumentsDelta` the provider's raw partial JSON. The complete chunk still follows, and that
is what tools run on. It needs a chat client built on IronHive's bridge (`generator.AsChatClient(…)`) and a provider that
streams arguments (Chat Completions, Responses, Anthropic — with IronHive 0.56.0+ Anthropic marks the tools
`eager_input_streaming`, so a long argument streams as written instead of arriving whole at the end). Off by default; a
consumer that counts calls skips `IsComplete = false` chunks.

A thinking model's reasoning streams as `ThinkingDelta` chunks while it thinks (from `AgentLoop` and `ThinkingAgentLoop`
alike), whenever the chat client returns it as `TextReasoningContent` — so a host can show that the model is thinking.
The whole reasoning is also recorded on the turn: `TurnRecord.ThinkingContent` (streaming) and
`AgentResponse.ThinkingContent` (`RunAsync`).

```csharp
await foreach (var chunk in loop.RunStreamingAsync(prompt, ct))
{
    if (chunk.ToolCallDelta is { IsComplete: false } writing) ShowProgress(writing.Id, writing.ArgumentsDelta);
    else if (chunk.ToolCallDelta is { } call) ShowStarted(call.Id, call.NameDelta);
}
```

### `ToolCallResult.Success` is `bool?` on purpose

The loop **extracts** the calls the model requested — it does not invoke them. Unless the
`IChatClient` handed to the loop was built with `UseFunctionInvocation()`, there is no
`FunctionResultContent` to correlate against and the honest answer to "did it succeed" is unknown, so
`Success` is `null` rather than a guess. A call's presence and arguments are always populated; if your
check needs the outcome too, confirm your client wraps function invocation.

A call the tool invocation pipeline refused has `Success = false` and a `RefusalKind` (`Denied`, `ApprovalUnavailable`,
`Rejected`, a loop guard, `InvalidArguments`, `ResultWithheld`); a tool that ran and failed has `RefusalKind = null`.

## Tracing

Each turn is an `invoke_agent` span; the model calls and tool runs inside it become its children when the chat client
carries Microsoft.Extensions.AI's `UseOpenTelemetry()` — below the tool invocation pipeline, so every round is traced:

```csharp
var chatClient = inner.AsBuilder()
    .UseToolInvocationPipeline(ToolInvocationPipeline.CreateDefault())
    .UseOpenTelemetry(sourceName: "MyApp.GenAI")      // chat spans
    .Build();
var loop = new AgentLoop(chatClient, new AgentOptions { Name = "librarian", ModelId = "gpt-4.1" });

// OpenTelemetry SDK: listen to both sources.
// tracing.AddSource(AgentTelemetry.SourceName, "MyApp.GenAI");
```

- `invoke_agent {Name}` carries `gen_ai.operation.name`, `gen_ai.agent.name`, `gen_ai.request.model`,
  `gen_ai.usage.input_tokens` / `output_tokens` / `cache_read.input_tokens`, `ironhive.agent.tool_calls`,
  `ironhive.agent.stop_reason`, and on failure
  `error.type` with an error status. A streamed turn is one span from the first chunk to the last.
- Microsoft.Extensions.AI starts `execute_tool {tool}` on the current activity's source, so it is reported under
  `AgentTelemetry.SourceName` too.
- No span exists while nothing listens, and message content is never recorded.

## Requirements

- .NET 10.0+

## Related Projects

- [ironhive](https://github.com/iyulab/ironhive) - LLM abstraction layer
- [ironhive-host](https://github.com/iyulab/ironhive-host) - Agent host (CLI, server, embedding) using this agent engine
- [ironbees](https://github.com/iyulab/ironbees) - Multi-agent management

## License

MIT
