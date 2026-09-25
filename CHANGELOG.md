# Changelog

All notable changes to this project are documented in this file. Versions follow
[Semantic Versioning](https://semver.org/); while the major version is 0, a minor release may contain
breaking changes, and each one is marked **Breaking** with a migration note.

## [0.19.0] - Unreleased

### Added
- **`ApprovalGate`/`GateDecision`, `ToolInvocationScope` and `ToolResultGuardedFunctionInvoker.ApplyAsync` are public** —
  the rules a tool loop applies (permission verdict before a call, the conversation a tool sees, the result guard after
  it). The Ironbees adapter uses them from its own package, and a host's own loop can judge calls the same way.

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
- **`IronHive.Agent` no longer references `MemoryIndexer.Sdk`**, which nothing in it used. It brought SQLite,
  OpenTelemetry (with an OTLP exporter) and ModelContextProtocol server packages into every host. A host that uses the
  SDK's storage wiring references `MemoryIndexer.Sdk` itself.

## [0.18.0] - Unreleased

### Fixed
- **Prompt-cache reads are priced at the cache-read rate** in the usage tracker, the turn guards' usage limiter and the
  advisor/delegation tools. A provider reports cache reads as part of the input (`UsageDetails.CachedInputTokenCount`),
  and every input token was priced at the full input rate, so a session with cache hits reported and budgeted a cost up
  to ten times too high — since IronHive 0.37.0 counts Anthropic's cache reads in the input, on every provider.
- **`IronHive.Agent` and `IronHive.DeepResearch` declare their license (MIT).** Their packages carried no license
  metadata, so nuget.org showed none and license scanners reported them as unlicensed.

### Added
- `TokenUsage.CachedInputTokens`, `TokenUsage.From(UsageDetails?)` (the one mapping the loops and tools share) and
  `TokenUsage.CostAt(ModelInfo?)`; `SessionUsage.TotalCachedInputTokens`.

## [0.17.1] - 2026-09-24

### Changed
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.19.3 -> 0.19.4, `Ironbees.Core` 0.19.3 -> 0.19.4, `IronHive.Abstractions` 0.36.0 -> 0.37.0 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

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
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.19.2 -> 0.19.3, `Ironbees.Core` 0.19.2 -> 0.19.3, `IronHive.Abstractions` 0.35.0 -> 0.36.0, `MemoryIndexer` 0.19.3 -> 0.19.4, `MemoryIndexer.Sdk` 0.19.3 -> 0.19.4, `WebFlux` 0.15.0 -> 0.16.0 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

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
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.19.1 -> 0.19.2, `Ironbees.Core` 0.19.1 -> 0.19.2, `IronHive.Abstractions` 0.34.0 -> 0.35.0, `MemoryIndexer` 0.19.2 -> 0.19.3, `MemoryIndexer.Sdk` 0.19.2 -> 0.19.3, `TokenMeter` 0.7.7 -> 0.7.8 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

## [0.15.9] - 2026-09-24

### Changed
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.19.0 -> 0.19.1, `Ironbees.Core` 0.19.0 -> 0.19.1, `TokenMeter` 0.7.6 -> 0.7.7, `WebFlux` 0.14.1 -> 0.15.0 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

## [0.15.8] - 2026-09-24

### Changed
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.18.0 -> 0.19.0, `Ironbees.Core` 0.18.0 -> 0.19.0, `IronHive.Abstractions` 0.33.1 -> 0.34.0, `MemoryIndexer` 0.19.0 -> 0.19.2, `MemoryIndexer.Sdk` 0.19.0 -> 0.19.2, `TokenMeter` 0.7.5 -> 0.7.6 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

## [0.15.7] - 2026-09-23

### Changed
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.17.3 -> 0.18.0, `Ironbees.Core` 0.17.3 -> 0.18.0, `MemoryIndexer` 0.18.5 -> 0.19.0, `MemoryIndexer.Sdk` 0.18.5 -> 0.19.0, `WebFlux` 0.14.0 -> 0.14.1 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

## [0.15.6] - 2026-09-23

### Changed
- Re-pinned sibling package(s) `IndexThinking` 0.22.0 -> 0.22.1, `MemoryIndexer` 0.18.4 -> 0.18.5, `MemoryIndexer.Sdk` 0.18.4 -> 0.18.5 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

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
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.17.2 -> 0.17.3, `Ironbees.Core` 0.17.2 -> 0.17.3, `IronHive.Abstractions` 0.33.0 -> 0.33.1 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

## [0.15.3] - 2026-09-23

### Changed
- Re-pinned sibling package(s) `MemoryIndexer` 0.18.3 -> 0.18.4, `MemoryIndexer.Sdk` 0.18.3 -> 0.18.4, `WebFlux` 0.13.0 -> 0.14.0 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

## [0.15.2] - 2026-09-22

### Changed
- Re-pinned sibling package(s) `WebFlux` 0.12.0 -> 0.13.0 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

## [0.15.1] - 2026-09-22

### Changed
- Re-pinned sibling package(s) `MemoryIndexer` 0.18.2 -> 0.18.3, `MemoryIndexer.Sdk` 0.18.2 -> 0.18.3 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

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
- Re-pinned sibling package(s) `WebFlux` 0.11.0 -> 0.12.0 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

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
- Re-pinned sibling package(s) `MemoryIndexer` 0.18.1 -> 0.18.2, `MemoryIndexer.Sdk` 0.18.1 -> 0.18.2, `WebFlux` 0.10.0 -> 0.11.0 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

## [0.14.6] - 2026-09-21

### Fixed
- *(Corrected in 0.14.7 — the first sentence was wrong; see above.)* **The extraction timeout now reaches the crawler.** `WebFluxIntegratedContentExtractor` passed it as `CrawlOptions.Timeout`,
  a second spelling WebFlux never read (removed in WebFlux 0.10.0), so each request ran on the crawler's own 30-second default
  and only the outer cancellation enforced the configured value. It is now passed as `TimeoutMs`.

### Changed
- Re-pinned sibling package(s) `MemoryIndexer` 0.18.0 -> 0.18.1, `MemoryIndexer.Sdk` 0.18.0 -> 0.18.1, `WebFlux` 0.9.0 -> 0.10.0 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

## [0.14.5] - 2026-09-20

### Changed
- `TextCompletionServiceAdapter` no longer maps `TopP`, `PresencePenalty` or `FrequencyPenalty`.
  MemoryIndexer never populated them, so they always arrived null, and they are removed from
  `TextCompletionOptions` in MemoryIndexer 0.18.0. `Temperature`, `MaxTokens` and `StopSequences`
  are unchanged.
- Re-pinned sibling package(s) `FluxGuard.Remote` 0.16.0 -> 0.17.0, `MemoryIndexer` 0.17.16 -> 0.18.0, `MemoryIndexer.Sdk` 0.17.16 -> 0.18.0 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

## [0.14.4] - 2026-09-20

### Changed
- Re-pinned sibling package(s) `WebFlux` 0.8.0 -> 0.9.0 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

## [0.14.3] - 2026-09-20

### Changed
- Re-pinned sibling package(s) `FluxGuard.Remote` 0.15.1 -> 0.16.0 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

## [0.14.2] - 2026-09-20

### Changed
- Re-pinned sibling package(s) `IronHive.Abstractions` 0.32.0 -> 0.33.0, `WebFlux` 0.7.4 -> 0.8.0 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.17.1 -> 0.17.2, `Ironbees.Core` 0.17.1 -> 0.17.2 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

### Fixed
- **DeepResearch with `UseWebFluxPackage = true` extracted a placeholder sentence instead of the
  page.** The WebFlux-backed extractor preferred the crawler registered under the key
  `"Intelligent"`, which in WebFlux up to 0.7.x made no request and returned
  `"Basic Intelligent crawl result for {url}"` as a successful page. The extractor now fetches
  through the HTTP crawler. `UseWebFluxPackage` is off by default, so only consumers that turned it
  on were affected. The "no crawler registered" exception message is now in English.

## [0.14.1] - 2026-09-19

### Changed
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.16.0 -> 0.17.1, `Ironbees.Core` 0.16.0 -> 0.17.1, `IronHive.Abstractions` 0.31.0 -> 0.32.0 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

## [0.14.0] - 2026-09-19

### Changed
- Re-pinned sibling package(s) `Ironbees.Autonomous` 0.15.0 -> 0.16.0, `Ironbees.Core` 0.15.0 -> 0.16.0 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.
- Re-pinned sibling package(s) `IronHive.Abstractions` 0.30.0 -> 0.31.0 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

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
- Re-pinned sibling package(s) `IronHive.Abstractions` 0.29.1 -> 0.29.2 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.
- Re-pinned sibling package(s) `IndexThinking` 0.21.2 -> 0.22.0 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.
- Re-pinned sibling package(s) `IronHive.Abstractions` 0.29.2 -> 0.30.0 — re-consumption of already-consumed iyulab packages via `check-pin-drift.ps1 -Fix`. No source changes.

- Re-pinned `Ironbees.Core` and `Ironbees.Autonomous` 0.14.12 -> 0.15.0.

## [0.12.12] - 2026-09-19

This file starts at 0.12.12. Changes in earlier releases were not recorded here; the commit history is
the record for them.
