# Changelog

## [0.20.0](https://github.com/aer-works/baton/compare/dialogue-worker-v0.19.0...dialogue-worker-v0.20.0) (2026-08-08)


### Features

* **dialogue:** Turns resume the vendor's own session; {PROMPT_FILE} retired ([#838](https://github.com/aer-works/baton/issues/838)) ([25efee5](https://github.com/aer-works/baton/commit/25efee5024d7fdad9bd2b139c78a4c4c24d5478b))
* **mcp:** aer yield via AER's first MCP server host; dialogue sentinel retired ([#827](https://github.com/aer-works/baton/issues/827)) ([7ef1de5](https://github.com/aer-works/baton/commit/7ef1de568a3425bdb84870f1018a6eb1250b90f7))
* **workers:** FinalOutputMode -- a dialogue's declared output can carry the full transcript ([#765](https://github.com/aer-works/baton/issues/765)) ([3a80b67](https://github.com/aer-works/baton/commit/3a80b67fa6b828430025eb7034105e01b2a0fb38))


### Bug Fixes

* **adapters,flow:** Make the PreToolUse gate true on every spawn path, and enforced ([#705](https://github.com/aer-works/baton/issues/705)) ([6b4568f](https://github.com/aer-works/baton/commit/6b4568ffaa098ad4ff1f60667e089be6324becba))
* **dialogue,flow,cli,daemon,ui,adapters:** Pin every redirected child stream's decode to UTF-8 ([#466](https://github.com/aer-works/baton/issues/466)) ([#1017](https://github.com/aer-works/baton/issues/1017)) ([3489faf](https://github.com/aer-works/baton/commit/3489faf1b349c9d271c4fdf82c653b5fd22eb29d))
* **dialogue,tools:** Dialogue preset shapes are single-sourced; the mirror is gone ([#836](https://github.com/aer-works/baton/issues/836)) ([#849](https://github.com/aer-works/baton/issues/849)) ([ca9c0aa](https://github.com/aer-works/baton/commit/ca9c0aacc54698dab9c31590d3432ee8fe073494))
* **dialogue:** pass long turn prompts by file path, not argv ([#580](https://github.com/aer-works/baton/issues/580)) ([351ac47](https://github.com/aer-works/baton/commit/351ac471c926dc6d0a0780c5d3ecd79636e339ff))
* **workers:** An invalid FinalOutputMode value is reported as an invalid value, not malformed JSON ([#781](https://github.com/aer-works/baton/issues/781)) ([9b3a81c](https://github.com/aer-works/baton/commit/9b3a81c9241a3bf0d6fabb418861c5d3efd32677))
* **workers:** Dialogue turns run under a configurable per-turn timeout with honest failure reporting ([#728](https://github.com/aer-works/baton/issues/728)) ([bc77580](https://github.com/aer-works/baton/commit/bc77580ab37d5f09f7f0d6a850744ab539292f50))


### Code Refactoring

* **adapters,cli,daemon,ui:** rename GeminiWorkerAdapter to AgyWorkerAdapter ([#1032](https://github.com/aer-works/baton/issues/1032)) ([#1034](https://github.com/aer-works/baton/issues/1034)) ([2a44e13](https://github.com/aer-works/baton/commit/2a44e1322c4e5054b5240b3cff3a2d1e7ec887f0))
* **dialogue:** StopSentinel fully retired from config and every authoring surface ([#830](https://github.com/aer-works/baton/issues/830)) ([71e112d](https://github.com/aer-works/baton/commit/71e112d69db051aa494a047b18c6551d08e191d0))

## [0.19.0](https://github.com/aer-works/aer-flow/compare/dialogue-worker-v0.18.0...dialogue-worker-v0.19.0) (2026-07-22)


### Miscellaneous

* **dialogue-worker:** Synchronize core versions

## [0.18.0](https://github.com/aer-works/aer-flow/compare/dialogue-worker-v0.17.0...dialogue-worker-v0.18.0) (2026-07-21)


### Features

* **dialogue:** M23 Phase 1 — generalize the dialogue worker to N-party ([#273](https://github.com/aer-works/aer-flow/issues/273)) ([0a44f58](https://github.com/aer-works/aer-flow/commit/0a44f58062f9eda622452852e0e1ed29217b75b1))

## [0.17.0](https://github.com/aer-works/aer-flow/compare/dialogue-worker-v0.16.0...dialogue-worker-v0.17.0) (2026-07-20)


### Miscellaneous

* **dialogue-worker:** Synchronize core versions

## [0.16.0](https://github.com/aer-works/aer-flow/compare/dialogue-worker-v0.15.0...dialogue-worker-v0.16.0) (2026-07-19)


### Miscellaneous

* **dialogue-worker:** Synchronize core versions

## [0.15.0](https://github.com/aer-works/aer-flow/compare/dialogue-worker-v0.14.0...dialogue-worker-v0.15.0) (2026-07-18)


### Features

* **ui:** M19 Phase 4 - Guided authoring, no hand-edited config files ([#197](https://github.com/aer-works/aer-flow/issues/197)) ([a3ef3b8](https://github.com/aer-works/aer-flow/commit/a3ef3b86c282194478e2247967a720356a93e86c))
* **workers:** M17 Phase 2 — Transcript contract + dialogue worker skeleton ([#172](https://github.com/aer-works/aer-flow/issues/172)) ([d4e0f95](https://github.com/aer-works/aer-flow/commit/d4e0f952ddc14a351ec50f29d65847d88f38b70b))
* **workers:** M17 Phase 3 — Turn loop, termination, and failure semantics ([#173](https://github.com/aer-works/aer-flow/issues/173)) ([b974809](https://github.com/aer-works/aer-flow/commit/b9748097ef9e0c0e55a95d9f470494c8e83aee8d))


### Tests

* **flow:** M8 Phase 4 — Fan-out/fan-in + retry end-to-end integration tests ([#56](https://github.com/aer-works/aer-flow/issues/56)) ([15d5adb](https://github.com/aer-works/aer-flow/commit/15d5adb77ff0c508fee275746009cc9f0d1de9de))


### Miscellaneous

* **release:** Split release-please into linked-versions groups (core, desktop) ([#225](https://github.com/aer-works/aer-flow/issues/225)) ([86da732](https://github.com/aer-works/aer-flow/commit/86da732bc469a1a86ba412b1863fb0506aa3e40b))

## Changelog
