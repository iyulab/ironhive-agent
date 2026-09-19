# Changelog

All notable changes to this project are documented in this file. Versions follow
[Semantic Versioning](https://semver.org/); while the major version is 0, a minor release may contain
breaking changes, and each one is marked **Breaking** with a migration note.

## [0.14.0] - 2026-09-19

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
