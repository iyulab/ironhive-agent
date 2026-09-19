# Changelog

All notable changes to this project are documented in this file. Versions follow
[Semantic Versioning](https://semver.org/); while the major version is 0, a minor release may contain
breaking changes, and each one is marked **Breaking** with a migration note.

## [0.13.0] - 2026-09-19

### Fixed

- **`ThinkingAgentLoop` now enforces a configured usage limit and retries a transient failure once**, the
  same way `AgentLoop` always did. It had no way to receive either, so a session limit was silently ignored
  on the thinking path. The constructor takes two new optional parameters, `errorRecovery` and
  `usageLimiter`; both loops now share one implementation of these per-turn safeguards.
- **`ChatClientFrameworkAdapter` honours an agent's `tools` list.** An Ironbees agent that named its tools got the
  whole tool pool on this adapter (only the older `capabilities` filter was applied). It now gets exactly the named
  tools, and a name the pool does not provide fails the run with that name instead of leaving the tool silently out.

### Added

- **`ChatClientFrameworkAdapter` implements the structured run and stream.** Per-request `MaxTokens`, `MaxToolTurns`,
  `ThinkingEffort` (sent as `ChatOptions.Reasoning`) and `Tools` are applied; `Suggestions` is refused. Before, every
  per-request option threw `NotSupportedException` on this adapter. Results report summed usage, the number of model
  round-trips, and whether the run stopped at its tool-turn limit (`AgentRunResult.TurnLimitReached`; the stream ends
  with an unsuccessful `CompletionChunk` whose finish reason is `tool_turn_limit`).

### Changed

- Re-pinned `Ironbees.Core` and `Ironbees.Autonomous` 0.14.12 -> 0.15.0.

## [0.12.12] - 2026-09-19

This file starts at 0.12.12. Changes in earlier releases were not recorded here; the commit history is
the record for them.
