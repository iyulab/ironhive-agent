# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test Commands

```bash
# Build entire solution
dotnet build IronHive.Agent.slnx

# Run all tests
dotnet test IronHive.Agent.slnx

# Run specific test project
dotnet test tests/IronHive.Agent.Tests
dotnet test tests/IronHive.DeepResearch.Tests

# Run a single test class
dotnet test tests/IronHive.Agent.Tests --filter "FullyQualifiedName~AgentLoopTests"

# Run a single test method
dotnet test tests/IronHive.Agent.Tests --filter "FullyQualifiedName~AgentLoopTests.RunAsync_ReturnsResponse"

# Build Release (excludes PDB)
dotnet build IronHive.Agent.slnx -c Release
```

## Architecture

IronHive.Agent is a reusable agent engine for AI-powered CLI tools, extracted from [ironhive-cli](https://github.com/iyulab/ironhive-cli). It provides the core agent loop, context management, mode system, MCP plugin integration, and built-in tools.

### Solution Layout

```
src/
  IronHive.Agent/          # Core agent engine library
  IronHive.DeepResearch/   # Autonomous multi-phase research agent
tests/
  IronHive.Agent.Tests/    # NSubstitute + xUnit
  IronHive.DeepResearch.Tests/  # Moq + FluentAssertions + xUnit
submodules/
  ironhive/                # LLM abstraction layer (IronHive.Abstractions, IronHive.Core)
  ironbees/                # Multi-agent orchestration (Ironbees.Core, Ironbees.Autonomous)
  TokenMeter/              # Token counting & cost calculation (project reference)
  ToolCallParser/          # Multi-provider tool call parsing (project reference)
local-tests/               # LSDD E2E simulation (Console App, 54 scenarios across 9 cycles)
```

### Core Abstraction: Agent Loop

`IAgentLoop` is the central interface. It implements a single-threaded master loop:
1. User provides prompt → 2. History prepared (compaction, goal reminders) → 3. LLM generates response → 4. Tool calls executed → 5. Results added to history → 6. Loop continues until complete.

Two implementations: `AgentLoop` (standard) and `ThinkingAgentLoop` (extended thinking for o1/o3 models).

### Mode System (Plan/Work/HITL)

`IModeManager` manages state transitions: `Idle → Planning → Working → Complete`, with `HumanInTheLoop` for risky operations. `IModeToolFilter` restricts available tools per mode (e.g., no writes in Planning mode).

### Context Management

`ContextManager` orchestrates auto-compaction at 92% of context window, protecting recent tokens (default 8192). Injects goal reminders every N turns to keep the agent focused. Supports prompt caching with ephemeral cache breakpoints.

### MCP Plugin System

`IMcpPluginManager` manages Model Context Protocol server connections via Stdio or HTTP transport. Supports hot reload (`McpPluginHotReloader`), tool discovery, and YAML config loading.

### Sub-Agent System

`ISubAgentService` spawns child agents with depth/concurrency limits, delegating to `IAgentOrchestrator` (ironbees) for execution:
- **Explore**: Read-only tasks (file search, code exploration)
- **General**: Complex multi-step tasks with full tool access

Agents are defined declaratively via YAML (`agents/explore/agent.yaml`, `agents/general/agent.yaml`). Tool filtering uses `AgentConfig.Capabilities` in `ChatClientFrameworkAdapter`.

### Permission System

`IPermissionEvaluator` enforces rule-based access control with glob patterns for files and regex for commands. Rules evaluate to Allow/Deny/Ask per category (Read, Edit, Bash, ExternalDirectory, McpTools).

### DeepResearch Pipeline

8-phase autonomous research: Query Planning → Search Coordination → Content Extraction → Content Enrichment → Analysis → Sufficiency Evaluation → Report Generation → Orchestration. Supports streaming, interactive (HITL), and resumable sessions.

**Autonomous orchestration**: `AutonomousResearchRunner` wires `ResearchTaskExecutor` + `ResearchOracleVerifier` into ironbees `AutonomousOrchestrator` for oracle-driven iterative research with automatic sufficiency checking.

### ChatClientFrameworkAdapter

`ChatClientFrameworkAdapter` bridges ironbees orchestration with M.E.AI's `IChatClient`. Handles tool execution loop, permission evaluation, capabilities-based tool filtering, and dynamic tool injection via `Func<IList<AITool>>` (supports MCP hot reload).

### DI Registration

```csharp
services.AddIronHiveAgent(options => { ... });        // Core agent services
services.AddDeepResearch(chatClient, options => { ... }); // DeepResearch + Autonomous services
```

## Build System

- **Framework**: .NET 10.0, C# latest, nullable enabled
- **Central Package Management (CPM)**: All package versions in `Directory.Packages.props`
- **TreatWarningsAsErrors**: `true` — all warnings break the build
- **EnforceCodeStyleInBuild**: `true` — style violations break the build
- **Version**: `0.1.0` (set in `Directory.Build.props`)

## Code Conventions

- **File-scoped namespaces** (enforced as warning)
- **Private fields**: `_camelCase` with underscore prefix (enforced)
- **Constants / static readonly**: `PascalCase` (enforced)
- **Braces**: Always required (enforced as warning)
- **var**: Preferred everywhere (suggestion)
- **Submodules**: Exempt from naming rules (own conventions)
- **Test mocking**: NSubstitute for Agent tests, Moq for DeepResearch tests

## Key Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.Extensions.AI` | Core AI abstractions (IChatClient, AITool, ChatMessage) |
| `ModelContextProtocol` | MCP server integration |
| `IronHive.Abstractions/Core` | LLM abstraction layer |
| `Ironbees.Core` | Multi-agent orchestration (agent selection, stickiness, YAML loading) |
| `Ironbees.Autonomous` | Autonomous goal-based orchestration (oracle loops, HITL, checkpointing) |
| `IndexThinking` | Token management, reasoning extraction |
| `MemoryIndexer` | Conversation memory, session management |
| `WebFlux` | Content extraction (DeepResearch) |
| `DiffPlex` | Diff generation |
| `TokenMeter` (submodule) | Token counting, multi-provider pricing |
| `ToolCallParser` (submodule) | Multi-provider tool call parsing (20+ providers) |

## Related Projects

- [ironhive](https://github.com/iyulab/ironhive) — LLM abstraction layer (`IronHive.Abstractions`, `IronHive.Core`)
- [ironhive-cli](https://github.com/iyulab/ironhive-cli) — CLI application consuming this agent engine
- [ironbees](https://github.com/iyulab/ironbees) — Multi-agent management/orchestration
