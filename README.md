# IronHive.Agent

Reusable agent engine for AI-powered CLI tools. Provides the core agent loop, context management, mode system, MCP plugin integration, and built-in tools.

## Features

- **Agent Loop**: Single-threaded master loop with streaming support
- **Context Management**: Auto-compaction (92% threshold), goal reminders, prompt caching
- **Mode System**: Plan/Work/HITL mode transitions with tool filtering
- **MCP Plugins**: Model Context Protocol server connections, hot reload
- **Built-in Tools**: Read, Write, Shell, Glob, Grep, Todo
- **Sub-Agent System**: Explore/General sub-agent spawning with depth and concurrency limits
- **Permission System**: Rule-based access control for files, commands, and tools
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
using IronHive.Agent.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddIronHiveAgent(options =>
{
    options.ChatClient = new OpenAIChatClient("gpt-4o");
});

var provider = services.BuildServiceProvider();
var agentLoop = provider.GetRequiredService<IAgentLoop>();

await foreach (var chunk in agentLoop.RunStreamingAsync("Hello!"))
{
    Console.Write(chunk.Text);
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
├── Permissions/    # Permission evaluation and configuration
├── Tracking/       # Usage tracking and limits
├── Providers/      # Chat client, embedding, rerank provider abstractions
├── Memory/         # Session memory service
├── Webhook/        # Webhook event notifications
├── ErrorRecovery/  # Error categorization and recovery
├── Ironbees/       # Multi-agent orchestration integration
└── Extensions/     # DI registration extensions
```

## Requirements

- .NET 10.0+

## Related Projects

- [ironhive](https://github.com/iyulab/ironhive) - LLM abstraction layer
- [ironhive-cli](https://github.com/iyulab/ironhive-cli) - CLI application using this agent engine
- [ironbees](https://github.com/iyulab/ironbees) - Multi-agent management

## License

MIT
