# IronHive.Agent

Reusable agent engine for AI-powered CLI tools. Provides the core agent loop, context management, mode system, MCP plugin integration, and built-in tools.

## Features

- **Agent Loop**: Single-threaded master loop with streaming support
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
    Url = "http://localhost:3000/mcp"
});
```

In YAML plugin config (`Transport` key accepts `stdio` or `http` case-insensitively):

```yaml
plugins:
  remote-tool:
    transport: http
    url: http://localhost:3000/mcp
```

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
