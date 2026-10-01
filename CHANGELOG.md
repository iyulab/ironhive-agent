# Changelog

All notable changes to this project are documented in this file. Versions follow
[Semantic Versioning](https://semver.org/); while the major version is 0, a minor release may contain
breaking changes, and each one is marked **Breaking** with a migration note.

## [0.31.0] - Unreleased

### Added
- **Every agent turn is an OpenTelemetry `invoke_agent` span** (GenAI semantic conventions) on the `IronHive.Agent`
  source (`AgentTelemetry.SourceName`): agent name, model, token usage (incl. cache reads), tool-call count, and the
  exception type with an error status when the turn throws. With `UseOpenTelemetry()` on the chat client, the model
  calls (`chat`) and tool runs (`execute_tool`) of the turn nest under it. No span is created while nothing listens.
- **`AgentOptions.Name`**: the agent's name on its spans.

## [0.30.0] - 2026-10-01

### Added
- **The repeated-error guard now sees a failure a tool reports as its result.** An MCP tool reports failure as an
  `isError: true` result and never throws, so a model repeating the same failing MCP call used to run until the iteration
  limit; it now ends after `MaxRepeatedErrors` identical failures, like a throwing tool. Recognised out of the box: MCP
  `CallToolResult`, the `JsonElement` an MCP client tool returns for it, and `McpToolResult`.
- **`ToolInvocationOptions.FailureOf`** (`Func<object?, string?>`): tells the loop guards how your own tools report
  failure as a value (return the error text, or null). A JSON string result arrives as a `string`.

### Fixed
- **A failing call repeated with the same arguments is no longer refused as "already ran successfully".** The
  repeated-call guard counted result-carried failures as successful runs.

## [0.29.0] - 2026-10-01

### Changed
- **Breaking: a tool call's verdict comes from `IToolCallPolicy`, not `IModeToolFilter`.** `IModeToolFilter.AssessRisk` is
  removed; `IToolCallPolicy.Evaluate(toolName, arguments)` returns the same `RiskAssessment` (`Allow` / `Deny` / `Ask`).
  `ToolCallPolicy` is the default (the permission rules, as before) and `AddIronHiveAgent` /
  `AddIronHiveAgentApprovalGate` register it unless you registered your own. `IModeToolFilter` keeps `FilterTools` and
  `IsToolPermitted`. Migration: an `IModeToolFilter` that overrode `AssessRisk` becomes an `IToolCallPolicy` registered in
  the container.
- **Breaking: `ApprovalGateMiddleware` and `ApprovalGate` take the policy.** Constructors are
  `(IToolCallPolicy policy, IHumanApprovalService? approver = null, ILogger? logger = null, IModeManager? modeManager = null,
  IModeToolFilter? modeToolFilter = null)`. Migration: `new ApprovalGateMiddleware(new ModeToolFilter(config), approver)`
  becomes `new ApprovalGateMiddleware(new ToolCallPolicy(config), approver)`.
- **Planning mode is enforced at the gate.** With a mode manager (with DI, whenever `IModeManager` is registered), a tool
  that is not read-only is denied while the mode is `Planning`, whatever the policy says. Idle and HumanInTheLoop are
  not verdicts, so a host that never fires a mode trigger is unaffected.
- **Read-only tools have one list.** Planning's read-only test (`IPermissionEvaluator.IsReadOnlyTool`) and the default
  `Tools` allow rules read the same built-in list; Planning now also permits the advisor and any `glob*` / `grep*` name.
- **Breaking: `IPermissionEvaluator.EvaluateDelete(filePath)`** (new member) — the `Edit` verdict, asked about when
  `PermissionConfig.AskBeforeDelete` (new, default `true`) is on. Before, every allowed delete was asked about with no way to
  turn it off. A custom evaluator implements the new member (delegating to `EvaluateEdit` keeps the old edit verdict).
- Re-pinned sibling package(s) `IndexThinking` 0.23.0 -> 0.23.1, `IronHive.Abstractions` 0.45.3 -> 0.45.4.
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.21.8 -> 0.21.9, `Ironbees.Core` 0.21.8 -> 0.21.9.

### Added
- **`ApprovalRequest.CallId`**: the model's call id, so an approver on a wire can pair its request with the call's events.

## [0.28.0] - 2026-10-01

### Added
- **`McpPluginManager.WithDeclaredRetrievalHints(McpClientTool)` and `ToolRetrievalHints.ParseValue(object?)` are public**,
  so a host that lists MCP tools through its own clients carries `_meta` retrieval hints with one call.

### Fixed
- **Retrieval aliases match Korean, Japanese and Chinese requests.** An alias word had to be a whole word of the query.
  In Korean, particles and endings are written attached to the word (`전사` appears as `전사해줘`), and Han and Kana
  write words without spaces, so an alias in those scripts never matched a real request. Now:
  - A Hangul alias word of two or more syllables matches a query word it begins.
  - A Han or Kana alias word matches anywhere in a query word.
  - Other scripts keep the whole-word rule.

## [0.27.0] - 2026-09-30

### Added
- **Every tool call can run through one ordered pipeline.** `chatClient.AsBuilder().UseToolInvocationPipeline().Build(serviceProvider)`
  replaces `UseFunctionInvocation()` and sets a single `FunctionInvoker`: a `ToolInvocationPipeline` folded from the
  registered `IToolInvocationMiddleware` steps (around each call, first registered outermost) and
  `IToolResultMiddleware` steps (over each result, before the model reads it). A step short-circuits by returning a
  result without calling `next`, and ends the request with `FunctionInvocationContext.Terminate`. Register steps with
  `AddToolInvocationMiddleware<T>()` / `AddToolResultMiddleware<T>()`; `AddIronHiveAgent` registers the pipeline. Without
  DI, use `UseToolInvocationPipeline(new ToolInvocationPipeline(...))`. Building `UseToolInvocationPipeline()` with services
  that hold no pipeline throws rather than running tools unguarded.
- **The permission gate is a pipeline step you turn on.** `services.AddIronHiveAgentApprovalGate()` (or
  `new ApprovalGateMiddleware(filter, approvalService)`). Without it there is no gate; with it and no
  `IHumanApprovalService`, an `Ask` verdict is still refused.
- **Results a host supplies are guarded like in-process ones.** When the loop's chat client uses
  `UseToolInvocationPipeline()`, `ContinueAsync` / `ContinueStreamingAsync` (both loops) put the results appended in
  the trailing tool messages through the pipeline's result stage before the model reads them. Each result is processed
  once: earlier history and results the loop's own turns produced are not processed again.
- **A registered `IToolResultGuard` guards the container's pipeline.** It runs as a `ToolResultGuardMiddleware` result
  step (usable directly without DI), so the same guard covers a function-invoking client and the Ironbees adapter.
- **Default loop guards in the pipeline.** Registered by `AddIronHiveAgent` (and in `ToolInvocationPipeline.CreateDefault()`),
  tuned by `AgentServicesOptions.ToolInvocation` (`ToolInvocationOptions`):
  - a call whose arguments could not be parsed is not run, and the model reads the parse error
    (`RefuseUnparseableArguments`, on);
  - the same tool with identical arguments is not run again after 3 successful runs in a row (`MaxRepeatedCalls`);
  - the same tool failing with the same error 3 times in a row ends the request with a result instead of another retry
    (`MaxRepeatedErrors`).

  They are on by default because they only act on a loop that is already stuck, and they apply only where the pipeline
  is used: a plain `UseFunctionInvocation()` client behaves as before. Set a count to 0 to turn a guard off.
  `ToolCallRefusalKind` gains `InvalidArguments`, `RepeatedCall` and `RepeatedError`.
- **`DefaultPlanExecutor` takes a `ToolInvocationPipeline`** (optional; its tool calls default to the loop guards).
- **The Ironbees adapter stops a run that a tool call terminated.** When a step sets `Terminate`, the run ends after
  that tool turn. A streamed run ends with `FinishReason` `tool_terminated`
  (`ChatClientFrameworkAdapter.ToolTerminatedFinishReason`). Later calls of the same turn are answered as not run.

### Changed
- **The Ironbees adapter runs every tool call through the pipeline** instead of its own copy of the gate and guard,
  so any registered step applies there too. `AddIronbees` passes the container's pipeline and turns the permission gate
  on for it (`AddIronHiveAgentApprovalGate`), which keeps the adapter gated as before. That gate also applies to other
  clients built from the same container with `UseToolInvocationPipeline()`.
- **Breaking:** `ChatClientFrameworkAdapter`'s constructor takes `toolInvocationPipeline` in place of
  `permissionEvaluator`, `modeToolFilter`, `approvalService` and `toolResultGuard` (`maxToolTurns` moves after it).
  Migration: pass `new ToolInvocationPipeline([new ApprovalGateMiddleware(filter, approvalService)], [new ToolResultGuardMiddleware(guard)])`.
- **The adapter's single-client constructor honours tool calls.** `ChatClientFrameworkAdapter(IChatClient)` left the
  tool-turn limit at 0, so a run given tools through `AgentRunOptions.Tools` stopped before its first model call; it now
  uses the default limit (20).
- **Documentation comments describe behaviour only.** Code comments, build-file comments and test descriptions state
  what the code does and the condition that triggers it; references to internal tracking ids and tools are removed.
- Re-pinned sibling package(s) `FluxGuard.Remote` 0.18.0 -> 0.18.1, `IronHive.Abstractions` 0.45.2 -> 0.45.3.
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.21.7 -> 0.21.8, `Ironbees.Core` 0.21.7 -> 0.21.8, `MemoryIndexer` 0.20.1 -> 0.20.2, `WebFlux` 0.19.2 -> 0.19.3.

### Removed
- **Breaking: `ApprovalGatedFunctionInvoker.Create`** (and the class). Migration: register the gate with
  `services.AddIronHiveAgentApprovalGate()`, or add `new ApprovalGateMiddleware(filter, approvalService)` to a
  `ToolInvocationPipeline`; a former `inner` invoker becomes a later `IToolInvocationMiddleware`.
- **Breaking: `ToolResultGuardedFunctionInvoker.Create`** (and the class). Migration: register the `IToolResultGuard`
  in the container, or add `new ToolResultGuardMiddleware(guard)` to a pipeline's result steps.
- **Breaking: `ToolResultGuardedFunctionInvoker.ApplyAsync`.** Migration: a host's own tool loop calls
  `pipeline.ProcessResultAsync(new ToolResultContext { ... })`, or `new ToolResultGuardMiddleware(guard).OnResultAsync(...)`.

## [0.26.0] - 2026-09-30

### Changed
- Re-pinned sibling package(s) `FluxGuard.Remote` 0.17.1 -> 0.18.0, `IronHive.Abstractions` 0.45.1 -> 0.45.2.
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.21.6 -> 0.21.7, `Ironbees.Core` 0.21.6 -> 0.21.7, `WebFlux` 0.19.1 -> 0.19.2.

### Added
- **A tool can declare retrieval aliases and companion tools.** `tool.WithRetrievalHints(aliases:, companions:)`
  (or the `ToolRetrievalHints.AliasesKey` / `CompanionsKey` entries in `AITool.AdditionalProperties`, as a string
  sequence or one comma-separated string). A query holding an alias scores the tool as if it named it, so "undo
  that" reaches `restore_file_version`. A selected tool brings up to three declared companions along, outside the
  scored budget. `EmbeddingToolRetriever` embeds the aliases with the description.
- **A tool the query names exactly takes the first scored slots**, regardless of score and even when pins have
  used up the budget.
- **`ToolRetrievalResult.Selections`** lists every selected tool with its reason (`Pinned`, `ExactName`, `Alias`,
  `Scored`, `Companion`) and score. It is empty by default for other `IToolRetriever` implementations.

### Fixed

- **The shell tool keeps the end of long output.** Only the first 50,000 characters of each stream were kept, so
  the end of a long build, test run or script (where the error and the summary are) never reached the model. Each
  stream now keeps its first 20,000 and last 30,000 characters with a `[... N characters omitted ...]` marker. A
  command that times out also returns what it printed so far instead of only the timeout message.

## [0.25.6] - 2026-09-30

### Changed
- Re-pinned sibling package(s) `FluxGuard.Remote` 0.17.0 -> 0.17.1 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.25.5] - 2026-09-30

### Changed
- Re-pinned sibling package(s) `MemoryIndexer` 0.20.0 -> 0.20.1 — re-consumption of already-consumed iyulab packages. No source changes.
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.21.5 -> 0.21.6, `Ironbees.Core` 0.21.5 -> 0.21.6 — re-consumption of already-consumed iyulab packages.
- Re-pinned sibling package(s) `WebFlux` 0.19.0 -> 0.19.1 — re-consumption of already-consumed iyulab packages.

## [0.25.4] - 2026-09-29

### Changed
- Re-pinned sibling package(s) `WebFlux` 0.18.0 -> 0.19.0 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.25.3] - 2026-09-29

### Changed
- Re-pinned sibling package(s) `WebFlux` 0.17.0 -> 0.18.0 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.25.2] - 2026-09-29

### Changed
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.21.4 -> 0.21.5, `Ironbees.Core` 0.21.4 -> 0.21.5, `IronHive.Abstractions` 0.45.0 -> 0.45.1 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.25.1] - 2026-09-29

### Changed
- Re-pinned sibling package(s) `WebFlux` 0.16.0 -> 0.17.0 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.25.0] - 2026-09-29

### Added
- **`ContextManager.FromConfig(tokenCounter, config, summarizer, contributors)`** builds a context manager that applies every
  `CompactionConfig` setting on a token counter you supply. `ForModel(name, config)` and the container registration both use it.
- **`PermissionConfigLoader.TryLoadFromDefaultLocations(dir, out config)`** loads the project permission file and says whether
  there was one, so a host with its own source of rules can fall back to it instead of to the built-in default.

### Fixed
- **The container's `ContextManager` applies the registered `CompactionConfig` in full.** `AddIronHiveAgent` built it with the bare
  constructor: of the config registered with `AddIronHiveAgentContext` only the trigger and compactor settings applied. Observation
  masking, tool-result compaction, the goal-reminder settings, `CompactOnOverflow` (now on by default, as documented) and
  `TargetRatio` never did, and `MaxContextTokens` did not reach the container's token counter.
- **`AddIronHiveAgentPermissions(configure)` starts from the default rules** (`PermissionConfig.CreateDefault()`). It started from an
  empty config, so configuring one rule silently dropped every default rule. **Behaviour change** for code that relied on the
  empty start: clear the lists in `configure` to get it back.
- **README:** the permissions example (`Configure<PermissionConfig>` had no effect), deep-research entry point and Tavily key,
  `[AIFunction]` (no such attribute), missing `using`s, MCP config file locations and hot reload, webhooks, planning, `AddIronbees`
  options, and the compaction default (token-based, not a 92% threshold) are now accurate.

## [0.24.1] - 2026-09-28

### Changed
- Re-pinned sibling package(s) `IronHive.Abstractions` 0.44.0 -> 0.45.0 — re-consumption of already-consumed iyulab packages.
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.21.3 -> 0.21.4, `Ironbees.Core` 0.21.3 -> 0.21.4 — re-consumption of already-consumed iyulab packages.

### Fixed
- **The packages carry the README**, so their nuget.org page shows it.

## [0.24.0] - 2026-09-28

### Changed
- Re-pinned sibling package(s) `MemoryIndexer` 0.19.17 -> 0.20.0 — re-consumption of already-consumed iyulab packages.

### Fixed
- **The anchored compaction state block stays within `CompactionConfig.MaxAnchorStateChars`.** Anchors are merged across
  compaction rounds, and the block grew for the whole session whatever the setting said. Over the limit, the oldest entries
  are now left out first: completed steps, then errors, modified files and key decisions, with failed approaches last.
  `ConversationAnchors.FormatStateBlock(maxChars)` does the same for a caller.

### Removed
- **Breaking: `CompactionConfig.ToolSchemaCompression`.** Nothing read it. Tool schemas are compressed by
  `AgentOptions.ToolSchemaCompression`, the one the loops apply. Delete the `CompactionConfig` assignment; set the level on
  the loop's options if you have not already.

## [0.23.0] - 2026-09-28

### Changed
- Re-pinned sibling package(s) `IronHive.Abstractions` 0.43.1 -> 0.44.0 — re-consumption of already-consumed iyulab packages.
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.21.2 -> 0.21.3, `Ironbees.Core` 0.21.2 -> 0.21.3 — re-consumption of already-consumed iyulab packages.

### Added
- **Compaction waits for the server when the context window is not known (`CompactionConfig.CompactOnOverflow`, default
  on).** When `MaxContextTokens` is unset and the model is not in the catalog, the counter guesses 8192, and the loop
  summarized the history from about the third turn on a server with a far larger window. With `ToolRoundContextChatClient` in
  the pipeline, pre-emptive compaction is now withheld while the window is a guess. A model call that fails with
  `ContextOverflowException` is compacted once and retried once, and the window is learned from the error for the rest of the
  session (`ContextManager.LearnContextWindow`, `IContextTokenCounter.IsContextWindowEstimated`). Without that client, or with
  a known window, compaction stays pre-emptive as before.

### Fixed
- **`CompactionConfig.TargetRatio` is honoured.** Compaction always reduced the history to 70 % of the window whatever the
  setting said; it now uses the configured ratio (default still 0.70).

## [0.22.0] - 2026-09-28

### Changed
- Re-pinned sibling package(s) `IronHive.Abstractions` 0.43.0 -> 0.43.1 — re-consumption of already-consumed iyulab packages.
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.21.1 -> 0.21.2, `Ironbees.Core` 0.21.1 -> 0.21.2 — re-consumption of already-consumed iyulab packages.

### Added

- **A pipeline built before the loop's `ContextManager` exists can still mask per tool round.** `UseToolRoundContext()`
  (no argument) adds an unbound `ToolRoundContextChatClient`, which passes requests through until it is bound. `AgentLoop`
  and `ThinkingAgentLoop` bind their own manager when they are constructed over a client that contains one (found through
  `GetService`). This is the shape of a `ChatClientFactory` decorator, which is shared by every client the factory creates,
  while the manager is made per loop. `ToolRoundContextChatClient.Bind` / `.ContextManager` are public. Binding a second
  loop's manager to a client already bound to another throws: one pipeline serves one loop's context.
- `CompactionConfig.GoalReminder` (`GoalReminderOptions?`) reaches `ContextManager.ForModel`. Before, turning the reminder
  off meant not using `ForModel`.
- `ContextManager.IsInjected(message)`: whether a message was composed by the manager for one preparation.

### Fixed

- **The goal reminder no longer piles up or points at an old question.** It was appended as a user message on every model
  call and kept (three calls → three reminders), it named the session's *first* question as the "current goal", and tool
  retrieval read it as the user's request (a turn asking for `GrepFiles` retrieved tools for turn 1's question). The
  reminder is now marked as injected and recomposed on each preparation (at most one), it names the latest user request,
  and the loops skip it when they read the request.

## [0.21.0] - 2026-09-28

### Added

- **Observation masking inside one turn: `CompactionConfig.ObservationMaskingProtectedRounds` and
  `ToolRoundContextChatClient` (`.UseToolRoundContext(contextManager)`).** A task that is one user message followed by
  many tool rounds (reading a long document, walking a folder) is a single user turn, so
  `ObservationMaskingProtectedTurns` protected all of its results and every earlier result was re-sent at full size on
  every round until the model's window overflowed. With `ObservationMaskingProtectedRounds = N`, tool results older than
  the last N rounds are masked even inside the protected turn (the calls stay). Those rounds run inside
  `FunctionInvokingChatClient`, out of the loop's reach, so `UseToolRoundContext(contextManager)` placed after
  `UseFunctionInvocation()` applies the context manager's cheap reductions (tool-result compaction, masking — no LLM
  call) to each round's request. The loop's `History` keeps the full results. Default off: behaviour unchanged.
- `ContextManager.ReduceToolResults(history)`: those reductions as one call (also used by `PrepareHistoryAsync`).

## [0.20.0] - 2026-09-28

### Changed
- Re-pinned sibling package(s) `IronHive.Abstractions` 0.42.0 -> 0.43.0 — re-consumption of already-consumed iyulab packages.
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.21.0 -> 0.21.1, `Ironbees.Core` 0.21.0 -> 0.21.1 — re-consumption of already-consumed iyulab packages.

### Added

- **`IAgentLoop.ContinueAsync` / `ContinueStreamingAsync`: continue a conversation after the host has run a tool.**
  A tool declared without an implementation (`AIFunctionFactory.CreateDeclaration`) stops the turn with its call
  pending; the host runs it, appends a `Tool` message with the `FunctionResultContent`, and continues from that history
  without inventing a user message. Both have an overload with per-turn `ChatOptions` overrides (same merge as
  `RunAsync`). A history that does not end with a result for every pending call (or with a user message) is refused
  before the model is called, naming the calls still missing. Implemented by `AgentLoop` and `ThinkingAgentLoop`;
  `OrchestratedAgentLoop` throws `NotSupportedException` (the orchestrator has no host tools to continue from).
  **Breaking for your own `IAgentLoop` implementations:** add the four members.

### Fixed

- **Declaration-only tools (the host runs them) are known by their own name and description throughout the loop.**
  Planning mode matched a tool against `PermissionConfig.ReadOnlyTools` by name, but a tool created with
  `AIFunctionFactory.CreateDeclaration` was known by its CLR type name, so a host tool declared read-only was refused in
  Planning. Keyword and embedding tool retrieval scored such a tool without its name or description, and tool schema
  compression left it uncompressed. All of these now read `AITool.Name` / `Description`, and compression returns a
  compressed declaration (still nothing to invoke).

## [0.19.13] - 2026-09-28

### Changed
- Re-pinned sibling package(s) `MemoryIndexer` 0.19.16 -> 0.19.17 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.19.12] - 2026-09-28

### Changed
- Re-pinned sibling package(s) `MemoryIndexer` 0.19.15 -> 0.19.16 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.19.11] - 2026-09-27

### Changed
- Re-pinned sibling package(s) `MemoryIndexer` 0.19.14 -> 0.19.15 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.19.10] - 2026-09-27

### Changed
- Re-pinned sibling package(s) `MemoryIndexer` 0.19.13 -> 0.19.14 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.19.9] - 2026-09-27

### Changed
- Re-pinned sibling package(s) `MemoryIndexer` 0.19.12 -> 0.19.13 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.19.8] - 2026-09-27

### Changed
- Re-pinned sibling package(s) `MemoryIndexer` 0.19.11 -> 0.19.12 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.19.7] - 2026-09-27

### Changed
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.20.4 -> 0.21.0, `Ironbees.Core` 0.20.4 -> 0.21.0, `IronHive.Abstractions` 0.41.0 -> 0.42.0 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.19.6] - 2026-09-27

### Changed
- Re-pinned sibling package(s) `MemoryIndexer` 0.19.10 -> 0.19.11 — re-consumption of already-consumed iyulab packages.

### Fixed
- **`McpPluginHotReloader` can be disposed synchronously.** It implemented only `IAsyncDisposable`, so a host that
  disposed it with `using` or registered it in a synchronously disposed container scope got an exception instead of
  the watcher stopping and the plugins disconnecting. It now implements `IDisposable` too, blocking on `DisposeAsync`.

## [0.19.5] - 2026-09-26

### Changed
- Re-pinned sibling package(s) `MemoryIndexer` 0.19.9 -> 0.19.10 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.19.4] - 2026-09-26

### Changed
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.20.3 -> 0.20.4, `Ironbees.Core` 0.20.3 -> 0.20.4, `IronHive.Abstractions` 0.40.0 -> 0.41.0, `MemoryIndexer` 0.19.8 -> 0.19.9 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.19.3] - 2026-09-26

### Changed
- Re-pinned sibling package(s) `MemoryIndexer` 0.19.7 -> 0.19.8 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.19.2] - 2026-09-26

### Changed
- Re-pinned sibling package(s) `IndexThinking` 0.22.1 -> 0.23.0, `Ironbees.Autonomous` 0.20.2 -> 0.20.3, `Ironbees.Core` 0.20.2 -> 0.20.3, `IronHive.Abstractions` 0.39.0 -> 0.40.0, `MemoryIndexer` 0.19.6 -> 0.19.7 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.19.1] - 2026-09-26

### Changed
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.20.1 -> 0.20.2, `Ironbees.Core` 0.20.1 -> 0.20.2, `IronHive.Abstractions` 0.38.0 -> 0.39.0, `MemoryIndexer` 0.19.5 -> 0.19.6 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.19.0] - 2026-09-25

0.18.0 was never published; its changes ship in 0.19.0.

### Added
- `TokenUsage.CachedInputTokens`, `TokenUsage.From(UsageDetails?)` (the one mapping the loops and tools share) and
  `TokenUsage.CostAt(ModelInfo?)`; `SessionUsage.TotalCachedInputTokens`.
- **`ApprovalGate`/`GateDecision`, `ToolInvocationScope` and `ToolResultGuardedFunctionInvoker.ApplyAsync` are public** —
  the rules a tool loop applies (permission verdict before a call, the conversation a tool sees, the result guard after
  it). The Ironbees adapter uses them from its own package, and a host's own loop can judge calls the same way.

### Fixed
- **Prompt-cache reads are priced at the cache-read rate** in the usage tracker, the turn guards' usage limiter and the
  advisor/delegation tools. A provider reports cache reads as part of the input (`UsageDetails.CachedInputTokenCount`),
  and every input token was priced at the full input rate, so a session with cache hits reported and budgeted a cost up
  to ten times too high — since IronHive 0.37.0 counts Anthropic's cache reads in the input, on every provider.
- **`IronHive.Agent` and `IronHive.DeepResearch` declare their license (MIT).** Their packages carried no license
  metadata, so nuget.org showed none and license scanners reported them as unlicensed.

### Changed
- **Breaking: long-term memory moved to a new package, `IronHive.Agent.Memory`.** `IronHive.Agent` no longer references
  MemoryIndexer, so a host that does not use memory no longer ships it. `SessionMemoryService`, `EmbeddingServiceAdapter`
  and `TextCompletionServiceAdapter` are now in `IronHive.Agent.Memory` (same namespace, `IronHive.Agent.Memory`); the
  interfaces `ISessionMemoryService` and `IAgentEmbeddingProvider` stay in `IronHive.Agent`. Migration: add
  `IronHive.Agent.Memory` if you construct any of the three classes.
- **Breaking: the MCP tool-call guard is an IronHive.Agent seam; FluxGuard moved to `IronHive.Agent.FluxGuard`.**
  `McpPluginManager` takes an `IMcpToolCallGuard` (`guard:`) instead of FluxGuard's `IMCPGuardrail` (`guardrail:`),
  so `IronHive.Agent` no longer references FluxGuard.Remote, whose ONNX Runtime natives every host shipped. Request
  and result checks, the fail-closed policy and the error texts are unchanged. Migration:
  `new McpPluginManager(guard: new FluxGuardMcpToolCallGuard(guardrail))`, or with DI
  `services.AddIronHiveAgentFluxGuard()` after registering the guardrail (a registered `IMCPGuardrail` is no longer
  injected by itself). `McpGuardrailToolResultGuard` moved to the same package (namespace `IronHive.Agent.FluxGuard`).
- **Breaking: the Ironbees integration moved to `IronHive.Agent.Ironbees`.** `IronHive.Agent` no longer references
  Ironbees.Core, whose ONNX Runtime natives and Azure.AI.ContentSafety every host shipped. Moved (same namespaces,
  `IronHive.Agent.Ironbees` and `IronHive.Agent.Delegation`): `ChatClientFrameworkAdapter`, `ChatClientLLMAdapter`,
  `AddIronbees`/`IronbeesOptions`, `OrchestratedAgentLoop`, `DelegationTools`, `DelegatedAgent`, `DelegationOptions`.
  Migration: add `IronHive.Agent.Ironbees` if you use any of them.
- **Breaking: `OpenAICompatibleEmbeddingProvider` no longer implements Ironbees' `IEmbeddingProvider`** (its
  `GenerateEmbeddingAsync`/`GenerateEmbeddingsAsync` are gone; `EmbedAsync`/`EmbedBatchAsync` and `ModelName` remain).
  To give it to Ironbees, wrap it: `new IronbeesEmbeddingProviderAdapter(provider, modelName)`.
- **Breaking: a permission file that cannot be read throws `PermissionConfigException` instead of becoming the
  defaults.** A malformed file, one without a `permissions` section, a misspelled section or key (`raed:`) or an
  unknown action (`dney`) used to yield `PermissionConfig.CreateDefault()` (or, for YAML, silently drop the misspelled
  section's rules, or read the action as `ask`) — the defaults allow more than a restrictive file would, so a typo
  widened what the agent may do. YAML is now read with YamlDotNet (the same keys); an unsupported file extension passed
  to `Load` throws `ArgumentException`. A missing file still yields the defaults.
- **`IronHive.Agent` no longer references `MemoryIndexer.Sdk`**, which nothing in it used. It brought SQLite,
  OpenTelemetry (with an OTLP exporter) and ModelContextProtocol server packages into every host. A host that uses the
  SDK's storage wiring references `MemoryIndexer.Sdk` itself.
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.19.4 -> 0.20.0, `Ironbees.Core` 0.19.4 -> 0.20.0, `IronHive.Abstractions` 0.37.0 -> 0.38.0 — re-consumption of already-consumed iyulab packages. No source changes.
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.20.0 -> 0.20.1, `Ironbees.Core` 0.20.0 -> 0.20.1, `MemoryIndexer` 0.19.4 -> 0.19.5 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.17.1] - 2026-09-24

### Changed
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.19.3 -> 0.19.4, `Ironbees.Core` 0.19.3 -> 0.19.4, `IronHive.Abstractions` 0.36.0 -> 0.37.0 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.17.0] - 2026-09-24

### Added
- **`IToolResultGuard` — in-process tool results can be inspected before the model reads them.** Before, only MCP
  results went through a guard (`IMCPGuardrail`), and in-process tools reached the model unguarded.
  - `ToolResultGuardedFunctionInvoker.Create(guard)` installs the guard on a function-invoking client and composes
    behind `ApprovalGatedFunctionInvoker` through its `inner` argument.
  - The Ironbees adapter takes a `toolResultGuard` constructor argument; `AddIronbees` resolves it from DI.
  - A verdict allows the result, replaces it with a sanitized text, or withholds it. A withheld result is a
    `ToolCallRefusal` with the new `ToolCallRefusalKind.ResultWithheld`, so the call reports `Success = false`.
  - A guard that throws withholds the result (fail-closed, as on the MCP path).
  - `McpGuardrailToolResultGuard` reuses a FluxGuard `IMCPGuardrail` for in-process results.

## [0.16.1] - 2026-09-24

### Changed
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.19.2 -> 0.19.3, `Ironbees.Core` 0.19.2 -> 0.19.3, `IronHive.Abstractions` 0.35.0 -> 0.36.0, `MemoryIndexer` 0.19.3 -> 0.19.4, `MemoryIndexer.Sdk` 0.19.3 -> 0.19.4, `WebFlux` 0.15.0 -> 0.16.0 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.16.0] - 2026-09-24

### Added
- **`AgentResponseChunk.ToolResult` — each tool call's outcome the moment it arrives.** `RunStreamingAsync` (and
  `ThinkingAgentLoop`'s) used to report a call's start (`ToolCallDelta`) and then nothing about it until the final
  `Turn`. It now yields a chunk carrying the `ToolCallResult` (`CallId` = the earlier `ToolCallDelta.Id`, name,
  arguments, result text, `Success`) as soon as the function-invocation middleware returns it — built by the same rule
  as the turn record's entry, so the two agree. Consumers that ignore the member are unaffected.
- **Host-declared read-only tools: `PermissionConfig.ReadOnlyTools`.** Name patterns (matched like `Tools`) of the
  host's own tools that only read; Planning mode offers and permits them next to the built-in read-only file tools.
  Before, a host tool could never run in Planning. It declares a side-effect class only — whether a call is allowed,
  asked about or denied stays with `Tools`. Readable from a permission file too (`readOnlyTools` in JSON,
  `read_only_tools:` list in YAML).

### Changed
- **Breaking** — `IPermissionEvaluator` gains `bool IsReadOnlyTool(string toolName)`. A custom evaluator adds it
  (return `false` to keep the old Planning behaviour).

## [0.15.10] - 2026-09-24

### Changed
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.19.1 -> 0.19.2, `Ironbees.Core` 0.19.1 -> 0.19.2, `IronHive.Abstractions` 0.34.0 -> 0.35.0, `MemoryIndexer` 0.19.2 -> 0.19.3, `MemoryIndexer.Sdk` 0.19.2 -> 0.19.3, `TokenMeter` 0.7.7 -> 0.7.8 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.15.9] - 2026-09-24

### Changed
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.19.0 -> 0.19.1, `Ironbees.Core` 0.19.0 -> 0.19.1, `TokenMeter` 0.7.6 -> 0.7.7, `WebFlux` 0.14.1 -> 0.15.0 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.15.8] - 2026-09-24

### Changed
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.18.0 -> 0.19.0, `Ironbees.Core` 0.18.0 -> 0.19.0, `IronHive.Abstractions` 0.33.1 -> 0.34.0, `MemoryIndexer` 0.19.0 -> 0.19.2, `MemoryIndexer.Sdk` 0.19.0 -> 0.19.2, `TokenMeter` 0.7.5 -> 0.7.6 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.15.7] - 2026-09-23

### Changed
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.17.3 -> 0.18.0, `Ironbees.Core` 0.17.3 -> 0.18.0, `MemoryIndexer` 0.18.5 -> 0.19.0, `MemoryIndexer.Sdk` 0.18.5 -> 0.19.0, `WebFlux` 0.14.0 -> 0.14.1 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.15.6] - 2026-09-23

### Changed
- Re-pinned sibling package(s) `IndexThinking` 0.22.0 -> 0.22.1, `MemoryIndexer` 0.18.4 -> 0.18.5, `MemoryIndexer.Sdk` 0.18.4 -> 0.18.5 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.15.5] - 2026-09-23

### Fixed
- **`FallbackChatClientProvider.GetAvailableModelsAsync` lists the models of the providers it holds.** It fell through to
  `IChatClientProvider`'s default and returned an empty list. It now returns the models of every available provider, in
  chain order, each entry naming its `Provider`.
- **`ChatClientLLMAdapter` (the Ironbees adapter over `IChatClientFactory`) carries usage, reasoning and the finish reason
  on the structured surface.** `RunStructuredAsync` and `StreamStructuredAsync` fell through to Ironbees' interface
  defaults: the result's `Usage` was always null although the chat response reported it, and the stream forwarded text
  only. `RunStructuredAsync` now returns `Usage`; `StreamStructuredAsync` emits `ThinkingChunk` for streamed reasoning,
  `UsageChunk`, and the finish reason on the closing `CompletionChunk`. Per-invoke `AgentRunOptions` are refused as before.

## [0.15.4] - 2026-09-23

### Changed
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.17.2 -> 0.17.3, `Ironbees.Core` 0.17.2 -> 0.17.3, `IronHive.Abstractions` 0.33.0 -> 0.33.1 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.15.3] - 2026-09-23

### Changed
- Re-pinned sibling package(s) `MemoryIndexer` 0.18.3 -> 0.18.4, `MemoryIndexer.Sdk` 0.18.3 -> 0.18.4, `WebFlux` 0.13.0 -> 0.14.0 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.15.2] - 2026-09-22

### Changed
- Re-pinned sibling package(s) `WebFlux` 0.12.0 -> 0.13.0 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.15.1] - 2026-09-22

### Changed
- Re-pinned sibling package(s) `MemoryIndexer` 0.18.2 -> 0.18.3, `MemoryIndexer.Sdk` 0.18.2 -> 0.18.3 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.15.0] - 2026-09-21

### Added
- **Agent Skills loader (`IronHive.Agent.Skills`).** A consumer that wants `SKILL.md` bundles no longer
  builds discovery, prompt injection and the load tool itself. `SkillDiscovery.Discover(SkillsConfig)`
  finds skills under the configured roots and validates the frontmatter as the specification's
  reference validator does — name pattern/length and directory match (NFKC), description 1–1024,
  compatibility ≤ 500, `metadata` string→string, no unknown keys — reporting what it rejects in
  `Diagnostics` instead of throwing. `SkillsLoader` turns the result into progressive disclosure that is
  enforceable: `Contributor` (an `ISystemInstructionContributor`, so it survives compaction and does not
  depend on the host's prompt) lists name + description; `LoadTool` (`load_skill`) returns the body, or a
  file inside the skill's directory, and refuses any path that resolves outside it. `SkillsConfig`:
  `Roots` (first root wins a name collision; the shadowed skill is reported), `Enabled` (also the
  order and the drop order), `Exclude`, `Filter`, `MaxMetadataCharacters` (default 12 000; what does
  not fit is dropped from the tail into `Dropped`, and stays loadable by name). `AddAgentSkills(config)`
  registers the loader and its contributor. Text only; `allowed-tools` is parsed and exposed, not enforced.
  `SkillsConfig.UnknownFields` decides what an undefined frontmatter key does — `Reject` (default, the
  validator's behaviour) or `Accept` with a warning diagnostic; a real skill tree measured here had 7 of 22
  skills carrying client-specific keys (`argument-hint`, `when_to_use`, …), which the strict default keeps out.
- **`ISystemInstructionContributor` — add a section to the system instructions without replacing the
  system prompt.** Until now the only way to tell the agent something more was `AgentOptions.SystemPrompt`,
  a whole-prompt replacement: adding one paragraph meant copying the host's default prompt, and the
  copy went stale whenever the host changed it. Register a contributor (in the container, or with
  `ContextManager.AddInstructionContributor`) and `ContextManager` adds its text as a system message
  after the system prompt on every prepared turn — contributors in order, then the scratchpad. Sections
  are recomputed each turn and are not stored in the conversation, so they are always current and
  survive compaction. With no contributor the prepared history is what it was. An agent loop that runs
  without a `ContextManager` does not apply contributors.
- `ContextFitWarning.Sections` — the system prompt and each contributed section with its token cost,
  largest first, so an over-budget warning says which section caused it. `ValidateContextFit` now
  judges the sections that will be sent, not only the stored system messages.
- **`FileToolOptions.AllowedRoots` — a boundary for the built-in file tools.** The working directory
  was never one: it is where relative paths start, and absolute paths or `..` left it freely. A host can
  now declare the directories `ReadFile`, `WriteFile`, `ListDirectory`, `GlobFiles` and `GrepFiles` may
  touch; a path that resolves outside all of them is refused with a message that tells the model it is
  a policy boundary rather than returning content, and glob / grep results found through a climbing
  pattern (`../**`) are held to it too. The check is made where the path is resolved, on the path the
  tool will actually open. **Default: empty — no boundary, behaviour unchanged.** Not covered, and said
  so in the XML docs: symbolic links and junctions are not followed, and `ExecuteCommand` is a shell
  that no path check confines.
- **`IFileWriteInterceptor` — a hook around the built-in `WriteFile` tool.** Set
  `FileToolOptions.WriteInterceptor` (`new ToolProvider(workingDirectory, options)` or
  `BuiltInTools.GetAll(workingDirectory, options)`) and it runs around every write: it receives the absolute path the tool resolved and a delegate that
  performs the write, may decline to call it, and may return text appended to the tool's success
  message. This is how a host attaches snapshots, auditing or a policy check to file writes without
  keeping its own copy of the file tools. Without an interceptor nothing changes.

### Fixed
- **Path rules judge the path the tool will open, not the text the model typed.** The permission
  layer matched the unresolved argument: with `Read: src/** -> Allow` and a default of `Deny`,
  `src/../../outside.txt` was **allowed** and the tool then opened a file outside the working
  directory; `public/../secrets/key.pem` slipped past a `secrets/**` deny; an absolute path to a file
  inside the working directory matched no relative rule. Paths are now resolved the way the file
  tools resolve them (against the new `PermissionConfig.WorkingDirectory`, default: the current
  directory) and matched as working-directory-relative paths, so every spelling of one file gets one
  answer.
- **`ExternalDirectory` rules are consulted.** Nothing called them. A path that resolves outside the
  working directory is now judged by them — and *not* by `Read`/`Edit`, whose catch-all `**/*` used
  to cover it — falling back to `DefaultAction`. **Behaviour change with the default configuration:**
  a read that climbs out of the working directory is asked about instead of allowed.
  `PermissionResult.OutsideWorkingDirectory` says when this happened.
- **`ListDirectory`, `GlobFiles` and `GrepFiles` answer to the `Read` rules** for the directory they
  search, in addition to their tool-name rule. A directory closed to `ReadFile` could be read through
  `GrepFiles`.
- **The scratchpad block no longer piles up.** The agent loops store the prepared history and prepare it
  again on the next turn, so every turn added another copy of the scratchpad's system message. Blocks
  that `ContextManager` composes are now marked, removed and recomputed on each preparation.

### Changed
- `BuiltInTools.GetAll` returns a mutable list, so a host can append its own tools to it.

## [0.14.8] - 2026-09-22

### Changed
- Re-pinned sibling package(s) `WebFlux` 0.11.0 -> 0.12.0 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.14.7] - 2026-09-21

### Fixed
- **Correction to the 0.14.6 notes below.** They said the extraction timeout "now reaches the
  crawler". It did not: WebFlux 0.10.0's HTTP crawlers read neither timeout option, so through
  0.14.6 the configured value was enforced only by the extractor's own outer cancellation, exactly
  as before. WebFlux 0.11.0 (re-pinned here) is the release that honours `CrawlOptions.TimeoutMs`
  on every HTTP request. What a caller of `WebFluxIntegratedContentExtractor` observes is unchanged
  either way — a slow page fails after about `ContentExtractionOptions.Timeout` — because the
  outer cancellation is still in place with the same value.

### Changed
- Re-pinned sibling package(s) `MemoryIndexer` 0.18.1 -> 0.18.2, `MemoryIndexer.Sdk` 0.18.1 -> 0.18.2, `WebFlux` 0.10.0 -> 0.11.0 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.14.6] - 2026-09-21

### Fixed
- *(Corrected in 0.14.7 — the first sentence was wrong; see above.)* **The extraction timeout now reaches the crawler.** `WebFluxIntegratedContentExtractor` passed it as `CrawlOptions.Timeout`,
  a second spelling WebFlux never read (removed in WebFlux 0.10.0), so each request ran on the crawler's own 30-second default
  and only the outer cancellation enforced the configured value. It is now passed as `TimeoutMs`.

### Changed
- Re-pinned sibling package(s) `MemoryIndexer` 0.18.0 -> 0.18.1, `MemoryIndexer.Sdk` 0.18.0 -> 0.18.1, `WebFlux` 0.9.0 -> 0.10.0 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.14.5] - 2026-09-20

### Changed
- `TextCompletionServiceAdapter` no longer maps `TopP`, `PresencePenalty` or `FrequencyPenalty`.
  MemoryIndexer never populated them, so they always arrived null, and they are removed from
  `TextCompletionOptions` in MemoryIndexer 0.18.0. `Temperature`, `MaxTokens` and `StopSequences`
  are unchanged.
- Re-pinned sibling package(s) `FluxGuard.Remote` 0.16.0 -> 0.17.0, `MemoryIndexer` 0.17.16 -> 0.18.0, `MemoryIndexer.Sdk` 0.17.16 -> 0.18.0 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.14.4] - 2026-09-20

### Changed
- Re-pinned sibling package(s) `WebFlux` 0.8.0 -> 0.9.0 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.14.3] - 2026-09-20

### Changed
- Re-pinned sibling package(s) `FluxGuard.Remote` 0.15.1 -> 0.16.0 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.14.2] - 2026-09-20

### Changed
- Re-pinned sibling package(s) `IronHive.Abstractions` 0.32.0 -> 0.33.0, `WebFlux` 0.7.4 -> 0.8.0 — re-consumption of already-consumed iyulab packages. No source changes.
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.17.1 -> 0.17.2, `Ironbees.Core` 0.17.1 -> 0.17.2 — re-consumption of already-consumed iyulab packages. No source changes.

### Fixed
- **DeepResearch with `UseWebFluxPackage = true` extracted a placeholder sentence instead of the
  page.** The WebFlux-backed extractor preferred the crawler registered under the key
  `"Intelligent"`, which in WebFlux up to 0.7.x made no request and returned
  `"Basic Intelligent crawl result for {url}"` as a successful page. The extractor now fetches
  through the HTTP crawler. `UseWebFluxPackage` is off by default, so only consumers that turned it
  on were affected. The "no crawler registered" exception message is now in English.

## [0.14.1] - 2026-09-19

### Changed
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.16.0 -> 0.17.1, `Ironbees.Core` 0.16.0 -> 0.17.1, `IronHive.Abstractions` 0.31.0 -> 0.32.0 — re-consumption of already-consumed iyulab packages. No source changes.

## [0.14.0] - 2026-09-19

### Changed
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.15.0 -> 0.16.0, `Ironbees.Core` 0.15.0 -> 0.16.0 — re-consumption of already-consumed iyulab packages. No source changes.
- Re-pinned sibling package(s) `IronHive.Abstractions` 0.30.0 -> 0.31.0 — re-consumption of already-consumed iyulab packages. No source changes.

### Fixed

- **DeepResearch judges sufficiency against `DeepResearchOptions.SufficiencyThreshold`.** The setting reached a field
  nothing read, and the judgement compared against a hardcoded 0.8, so research stopped at 0.8 whatever the host set.
  `SufficiencyScore` now carries the `Threshold` it was judged against.
- **`DeepResearchOptions.MinSourcesBeforeReport` takes effect.** While fewer sources have been collected than it asks for,
  research keeps iterating as long as the analysis still has a gap to search, even when the score is already
  sufficient. With no gap left there is nothing more to search, so research stops.
- **Streaming research (`ExecuteStreamAsync`) retries a search that found nothing and records failed queries**, the same
  as `ExecuteAsync`. Before this it searched once and dropped the failures.

### Removed

- **Breaking: options that nothing read.** `DeepResearchOptions.CheckpointBasePath` (there is no persistent checkpoint store) and
  `SessionExpiration` (there is no session store) are gone. `DefaultMaxIterations` and `DefaultMaxSourcesPerIteration` duplicated
  `ResearchRequest.MaxIterations` / `MaxSourcesPerIteration`, which are what run. `AnalysisOptions.EnableFindingVerification` had no
  verification step behind it. `ContentEnrichmentOptions.ContinueOnError` and `SearchExecutionOptions.ContinueOnError` described
  what always happens: a failed source or query is recorded and the rest continue. `ResearchRequest.OutputFormat` /
  `ReportGenerationOptions.OutputFormat` and the `OutputFormat` enum are gone too: reports are always Markdown, and no renderer
  existed for Html, Pdf or Json. Migration: delete the assignments. None of them changed behaviour.

## [0.13.0] - 2026-09-19

### Fixed

- **DeepResearch results report the tokens the run used.** `ResearchMetadata.TokenUsage` came from a field nothing
  wrote, so every result said 0 tokens. The built-in text-generation adapters now record each call into the run.
  `ResearchMetadata.EstimatedCost` is now `decimal?` and null — the run cannot price calls whose model it does not
  know, and a 0 read as «free».
- **`ThinkingAgentLoop` now enforces a configured usage limit and retries a transient failure once**, the
  same way `AgentLoop` always did. It had no way to receive either, so a session limit was silently ignored
  on the thinking path. The constructor takes two new optional parameters, `errorRecovery` and
  `usageLimiter`; both loops now share one implementation of these per-turn safeguards.
- **A streamed turn reports the usage of every model call it made, not just the last.** Under function invocation a
  turn makes several model calls, each reporting its usage; both loops kept only the last one, so a streamed turn
  with a tool call under-reported (and under-counted against a usage limit). The usage is now summed, as
  `ToChatResponse` does.
- **`ChatClientFrameworkAdapter` honours an agent's `tools` list.** An Ironbees agent that named its tools got the
  whole tool pool on this adapter (only the older `capabilities` filter was applied). It now gets exactly the named
  tools, and a name the pool does not provide fails the run with that name instead of leaving the tool silently out.

### Added

- **`ChatClientFrameworkAdapter` implements the structured run and stream.** Per-request `MaxTokens`, `MaxToolTurns`,
  `ThinkingEffort` (sent as `ChatOptions.Reasoning`) and `Tools` are applied; `Suggestions` is refused. Before, every
  per-request option threw `NotSupportedException` on this adapter. Results report summed usage, the number of model
  round-trips, and whether the run stopped at its tool-turn limit (`AgentRunResult.TurnLimitReached`; the stream ends
  with an unsuccessful `CompletionChunk` whose finish reason is `tool_turn_limit`).

- **Delegation: an Ironbees named agent as a tool the model can call** (`IronHive.Agent.Delegation`).
  `DelegationTools.Create(orchestrator, DelegatedAgent)` returns an `AIFunction` that runs the named agent on the
  sub-task it is given, with a per-delegation model, reasoning level, output cap and tool-turn limit. `DelegationOptions`
  bounds nesting depth (followed across agents), concurrency across a set of tools, and counts the delegated usage —
  priced on the delegated model — against the parent's usage limit. A refused or failed delegation, and a run cut off
  at its turn limit, come back as results the calling model can read.
- **Advisor: a tool that consults a stronger model** (`AdvisorTool.Create(advisorClient, AdvisorOptions)`). It takes
  no arguments; calling it sends the conversation so far, rendered as a transcript (tool results cut, reasoning left
  out), to the advisor model — with no tools — and returns its review. The conversation comes from
  `FunctionInvokingChatClient.CurrentContext`, from the Ironbees adapter's own tool loop, or from
  `AdvisorOptions.Conversation`. `MaxCalls`, `UsageLimiter` and `UsageTracker` bound and account for consultations.
  The default permission rules allow a tool named `advisor` (read-only); delegation tools are not allowed by default,
  since what they can do depends on the delegated agent's tools.

### Removed

- **Breaking: `ChatClientOptions` and `HistoryCompactorOptions.TargetCompressionRatio`.** The record was referenced by
  nothing; the compactor never read the ratio. An options roster test (`Iyu.Conventions.Testing`) now fails when a public
  option nothing reads is added.
- **Breaking: DeepResearch options nothing read** — `DeepResearchOptions.UseSmallModelForAnalysis`,
  `AnalysisModelId`, `SynthesisModelId`, `DefaultMaxBudget` and `ResearchRequest.MaxBudget`. Every research step ran
  on the one registered text-generation service whatever these said, and no budget was ever checked. Migration: delete
  the assignments; choose the model where you register the text-generation service.
- **Breaking: `ISubAgentService`, `SubAgentService`, `SubAgentTool`, `SubAgentType`, `SubAgentConfig`,
  `SubAgentContext`, `SubAgentResult` and `BuiltInTools.GetAll(workingDirectory, ISubAgentService)`.** The service
  set limits it never passed to the run, reported zero turns, could not be cancelled, and offered only two fixed
  profiles. Migration: define `explore`/`general` (or any) agents in Ironbees with the `tools` they may use, and
  expose them with `DelegationTools.Create`; code that called `ExploreAsync`/`GeneralAsync` directly calls
  `IAgentOrchestrator.ProcessStructuredAsync(task, new ProcessOptions { AgentName = "explore" })`.

### Changed
- Re-pinned sibling package(s) `IronHive.Abstractions` 0.29.1 -> 0.29.2 — re-consumption of already-consumed iyulab packages. No source changes.
- Re-pinned sibling package(s) `IndexThinking` 0.21.2 -> 0.22.0 — re-consumption of already-consumed iyulab packages. No source changes.
- Re-pinned sibling package(s) `IronHive.Abstractions` 0.29.2 -> 0.30.0 — re-consumption of already-consumed iyulab packages. No source changes.

- Re-pinned `Ironbees.Core` and `Ironbees.Autonomous` 0.14.12 -> 0.15.0.

## [0.12.12] - 2026-09-19

This file starts at 0.12.12. Changes in earlier releases were not recorded here; the commit history is
the record for them.
