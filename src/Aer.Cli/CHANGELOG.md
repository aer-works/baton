# Changelog

## [0.20.0](https://github.com/aer-works/baton/compare/cli-v0.19.0...cli-v0.20.0) (2026-08-08)


### Features

* **adapters,cli:** honour pattern-scoped agy shell grants via a strict hook matcher ([#659](https://github.com/aer-works/baton/issues/659)) ([#1031](https://github.com/aer-works/baton/issues/1031)) ([900b3b9](https://github.com/aer-works/baton/commit/900b3b9ca2b378ee527c6162017663a941615f32))
* **adapters,cli:** Let a withheld write reach the worker's outbox on claude ([#666](https://github.com/aer-works/baton/issues/666)) ([fc884cd](https://github.com/aer-works/baton/commit/fc884cd6dac19f16d803c28246e101e1c9fef493))
* **adapters,cli:** Ship a PreToolUse hook on every spawned claude worker ([#555](https://github.com/aer-works/baton/issues/555)) ([a4ad817](https://github.com/aer-works/baton/commit/a4ad8178e0263502cd873002a66718f5c7313833))
* **adapters,cli:** Ship agy's workspace PreToolUse gate ([#603](https://github.com/aer-works/baton/issues/603)) ([ea2d40c](https://github.com/aer-works/baton/commit/ea2d40ca3e62dc25d47ef1ef9d93ec0324d9c738))
* **adapters,cli:** The dispatch-role catalog becomes an engine export served by aer templates ([#887](https://github.com/aer-works/baton/issues/887) stage 1) ([#960](https://github.com/aer-works/baton/issues/960)) ([1e11847](https://github.com/aer-works/baton/commit/1e118477b2fd1254c3f147087d0f503eb717fe28))
* **cli,adapters:** Add aer dispatch &lt;role&gt; over a shared RoleBinding primitive ([#902](https://github.com/aer-works/baton/issues/902)) ([8b565f3](https://github.com/aer-works/baton/commit/8b565f3b205b60a349bee9f21aaa8c6dcb829f1d))
* **cli,adapters:** Run a composed template end-to-end — template-or-role dispatch + capture adapter (rung-3 2b+2c) ([#921](https://github.com/aer-works/baton/issues/921)) ([62772b9](https://github.com/aer-works/baton/commit/62772b94ffc66d750296e55b1b58ce80d5512a65))
* **cli:** aer run --echo-worker streams worker stdout live ([#882](https://github.com/aer-works/baton/issues/882)) ([#1010](https://github.com/aer-works/baton/issues/1010)) ([f23cea0](https://github.com/aer-works/baton/commit/f23cea0176ed24ac44dfb1f1fed7c774836133e6))
* **cli:** aer status &lt;task-dir&gt; --follow -- watch a running workflow from the recorded events ([#766](https://github.com/aer-works/baton/issues/766)) ([e9275fd](https://github.com/aer-works/baton/commit/e9275fd28a4b403b551460d302ea35e15d158ced))
* **cli:** aer status renders a parked step's classification and dated local retry time ([#829](https://github.com/aer-works/baton/issues/829)) ([b8514b7](https://github.com/aer-works/baton/commit/b8514b7b039c02990f88fed7390ad7b4a593f23e))
* **cli:** Every command prints where each produced output landed ([#777](https://github.com/aer-works/baton/issues/777)) ([0e64c30](https://github.com/aer-works/baton/commit/0e64c306450571f9bc9b94dc60d82f87bdf4157b))
* **flow,cli:** Engine identity recorded, liveness probed at read time -- a silence gets its provenance ([#796](https://github.com/aer-works/baton/issues/796)) ([5d4d914](https://github.com/aer-works/baton/commit/5d4d9147aeef2fec13bf449726e2db687c3617ea))
* **flow,cli:** Engine-provisioned git worktree workspaces (Rung 4, progresses [#669](https://github.com/aer-works/baton/issues/669)) ([#937](https://github.com/aer-works/baton/issues/937)) ([971f390](https://github.com/aer-works/baton/commit/971f39003253931e9f02304ea6e004d18e5bacb2))
* **flow:** Bounded room open via a projection checkpoint ([#903](https://github.com/aer-works/baton/issues/903) scope 1a) ([#961](https://github.com/aer-works/baton/issues/961)) ([89f556f](https://github.com/aer-works/baton/commit/89f556fcc2384d22a267c018fb639b98edcf4756))
* **flow:** Operator retry-now reaches a quota-parked step ([#815](https://github.com/aer-works/baton/issues/815)) ([#848](https://github.com/aer-works/baton/issues/848)) ([644d070](https://github.com/aer-works/baton/commit/644d070cfef1d9ea4589ed564bfaad439f43b598))
* **flow:** Split PausePoint into needs-input and ready-for-review kinds ([#435](https://github.com/aer-works/baton/issues/435)) ([82a9d95](https://github.com/aer-works/baton/commit/82a9d955da69be3bed3778d25415f9f2ec0185e7))
* **flow:** WriterUtcTimestamp on the journal envelope; aer status renders per-step times ([#824](https://github.com/aer-works/baton/issues/824)) ([267baf9](https://github.com/aer-works/baton/commit/267baf92f15e094f3efa7a2d90deb964be1b0d3f))
* **ui:** One gate is one object seen from three places, and the lock names its holder ([#618](https://github.com/aer-works/baton/issues/618)) ([#988](https://github.com/aer-works/baton/issues/988)) ([3a0f3d7](https://github.com/aer-works/baton/commit/3a0f3d7615e39751b38fe4e077bb8f99217c84f9))
* **ui:** Room turn-host surface — throttle values, live usage, and dormancy visibility ([#994](https://github.com/aer-works/baton/issues/994)) ([#1000](https://github.com/aer-works/baton/issues/1000)) ([8260639](https://github.com/aer-works/baton/commit/8260639be29240f7f5b9a5160a5dd5dbfa1e3d7a))


### Bug Fixes

* **adapters,cli:** Make the agy PreToolUse gate actually run, and make the checks able to notice when it does not ([#709](https://github.com/aer-works/baton/issues/709)) ([f25e707](https://github.com/aer-works/baton/commit/f25e70749fe3ba55736151f3115ecdc626c3c565))
* **adapters,cli:** Vendor-tag the denied-tools channel so absent and empty differ ([#661](https://github.com/aer-works/baton/issues/661)) ([d9c9762](https://github.com/aer-works/baton/commit/d9c97629c60a59aaa210d52df28cb34ff602111d))
* **cli,adapters:** Bound a granted write to the workspace and the outbox ([#684](https://github.com/aer-works/baton/issues/684)) ([a32d9e2](https://github.com/aer-works/baton/commit/a32d9e2d11fd659cb90b5d9e6765148bdd6ecc67))
* **cli,flow:** Live stream files for captured worker output, tailed by status --follow ([#805](https://github.com/aer-works/baton/issues/805)) ([7a86bfc](https://github.com/aer-works/baton/commit/7a86bfcc92e8bc77d4c70244203e9d733095144f))
* **cli:** decide/cancel/supply provision declared worktrees, and cancel does it lazily ([#1012](https://github.com/aer-works/baton/issues/1012)) ([#1024](https://github.com/aer-works/baton/issues/1024)) ([15362b5](https://github.com/aer-works/baton/commit/15362b5116187317b447293de908fa1ab8198640))
* **cli:** Lazily resolve worker bindings so unrelated unresolvable entries cannot block cancel/supply ([#727](https://github.com/aer-works/baton/issues/727)) ([bf97775](https://github.com/aer-works/baton/commit/bf97775022766b42907fc7c43459255dc2172406))
* **cli:** Refuse a resume whose named workflow is a different template ([#652](https://github.com/aer-works/baton/issues/652)) ([bbfd524](https://github.com/aer-works/baton/commit/bbfd524cc752c9b55d11287521c442f7b398ac38))
* **cli:** resolve a task directory at the CLI boundary, and refuse one that is not ([#681](https://github.com/aer-works/baton/issues/681)) ([7e61c42](https://github.com/aer-works/baton/commit/7e61c42b43ecc41654ff52ff37249656fac99512))
* **cli:** Typed refusal when a command hits a journal held by a live engine ([#816](https://github.com/aer-works/baton/issues/816)) ([#821](https://github.com/aer-works/baton/issues/821)) ([319acfc](https://github.com/aer-works/baton/commit/319acfc2b5dc9d68dc148002dde88396eac905a4))
* **dialogue,flow,cli,daemon,ui,adapters:** Pin every redirected child stream's decode to UTF-8 ([#466](https://github.com/aer-works/baton/issues/466)) ([#1017](https://github.com/aer-works/baton/issues/1017)) ([3489faf](https://github.com/aer-works/baton/commit/3489faf1b349c9d271c4fdf82c653b5fd22eb29d))
* **flow:** Fail replay loudly on a lost member, and persist enums by name ([#621](https://github.com/aer-works/baton/issues/621)) ([a6d966f](https://github.com/aer-works/baton/commit/a6d966fb660bca8e97738e43f9c2ae36d9ef85eb))
* **flow:** Name the failure AER already detected instead of an unclassified Failed ([#607](https://github.com/aer-works/baton/issues/607)) ([9bbecb6](https://github.com/aer-works/baton/commit/9bbecb6a0da18ca1e4d9b0481caf3a1039d3838f))
* **flow:** Wait out a contended room lock on the operator's resolve path ([#879](https://github.com/aer-works/baton/issues/879)) ([ed79270](https://github.com/aer-works/baton/commit/ed792707cd375ec4e7fa47a9c04becb76694522e))
* **ui:** The workflow-path box never carries a bare template id, and the resume template check fires ([#969](https://github.com/aer-works/baton/issues/969)) ([c6ae9c1](https://github.com/aer-works/baton/commit/c6ae9c176fc3410a88d48b65ad2619b86d8e6134))


### Code Refactoring

* **adapters,cli,daemon,ui:** rename GeminiWorkerAdapter to AgyWorkerAdapter ([#1032](https://github.com/aer-works/baton/issues/1032)) ([#1034](https://github.com/aer-works/baton/issues/1034)) ([2a44e13](https://github.com/aer-works/baton/commit/2a44e1322c4e5054b5240b3cff3a2d1e7ec887f0))
* **core,daemon,ui:** Rename identifiers and spec terms to the two nouns ([#1038](https://github.com/aer-works/baton/issues/1038)) ([8d097af](https://github.com/aer-works/baton/commit/8d097afc11a21840e1d8420215019f239883a502))
* **flow,cli,docs:** Adopt "the ledger" as the journal's user-facing noun (0045) ([#854](https://github.com/aer-works/baton/issues/854)) ([5f04eb8](https://github.com/aer-works/baton/commit/5f04eb84ac60d456db1d58cd8f87e10a735ab736))

## [0.19.0](https://github.com/aer-works/aer-flow/compare/cli-v0.18.0...cli-v0.19.0) (2026-07-22)


### Features

* **daemon,adapters,ui:** Milestone 24 — Interactive Sessions & Unified Task Creation ([#276](https://github.com/aer-works/aer-flow/issues/276)) ([f7ab4fa](https://github.com/aer-works/aer-flow/commit/f7ab4fad253e730735631d158d92f97b2fc22d03))

## [0.18.0](https://github.com/aer-works/aer-flow/compare/cli-v0.17.0...cli-v0.18.0) (2026-07-21)


### Features

* **flow,adapters:** M23 Phase 3 — Project-Directory-Bound Tasks & Portable Bindings ([#275](https://github.com/aer-works/aer-flow/issues/275)) ([2743172](https://github.com/aer-works/aer-flow/commit/274317233a1f7c419f746c1868bec80b19944e8c))

## [0.17.0](https://github.com/aer-works/aer-flow/compare/cli-v0.16.0...cli-v0.17.0) (2026-07-20)


### Miscellaneous

* **cli:** Synchronize core versions

## [0.16.0](https://github.com/aer-works/aer-flow/compare/cli-v0.15.0...cli-v0.16.0) (2026-07-19)


### Miscellaneous

* **cli:** Synchronize core versions

## [0.15.0](https://github.com/aer-works/aer-flow/compare/cli-v0.14.0...cli-v0.15.0) (2026-07-18)


### Features

* **cli:** M11 Phase 3 — aer run pump (the CLI driver) ([#93](https://github.com/aer-works/aer-flow/issues/93)) ([d4648a1](https://github.com/aer-works/aer-flow/commit/d4648a1cee9e369e2a34d2d6442d4ed5eb7d2631))
* **cli:** M12 Phase 2 — aer cancel + Ctrl+C host-stop wiring ([#103](https://github.com/aer-works/aer-flow/issues/103)) ([08b8d7a](https://github.com/aer-works/aer-flow/commit/08b8d7abd9da77c404ded2a5ce0c764407afca05))
* **cli:** M12 Phase 3 — aer decide + supplementary artifact recording ([#105](https://github.com/aer-works/aer-flow/issues/105)) ([e63b823](https://github.com/aer-works/aer-flow/commit/e63b8235db1ad9f05aed058a052a9ee7a6855d44))
* **cli:** M13 Phase 1 — Pack aer as a dotnet tool (single-platform) ([#114](https://github.com/aer-works/aer-flow/issues/114)) ([e6be44f](https://github.com/aer-works/aer-flow/commit/e6be44f99e542dbcba29f6327ea35c94ad95eaee))
* **cli:** M13 Phase 2 — Version wiring (release-please → package Version) ([#115](https://github.com/aer-works/aer-flow/issues/115)) ([c970aa5](https://github.com/aer-works/aer-flow/commit/c970aa5dbed9ffa0df1a89aa9b67f126f2d5338a))
* **core:** Initialize .NET solution and placeholder projects for aer-flow ([541258d](https://github.com/aer-works/aer-flow/commit/541258d75a43ad50fd25a63b8151d7e64c5d512c))
* **ui:** M15 Phase 1 — Mutation seam + start/resume a workflow ([#145](https://github.com/aer-works/aer-flow/issues/145)) ([8b9c12e](https://github.com/aer-works/aer-flow/commit/8b9c12e686e1a8676df75fbfb7d4ab40ec062e33))
* **ui:** M15 Phase 4 — Cancel: targeted live-execution cancel + host stop ([#148](https://github.com/aer-works/aer-flow/issues/148)) ([f1a2361](https://github.com/aer-works/aer-flow/commit/f1a2361b9ed887f17dbdd941f61034cb6bf63203))


### Continuous Integration

* **cli:** M13 Phase 3 — Multi-RID native-lib bundling (Windows/Linux/macOS) ([#116](https://github.com/aer-works/aer-flow/issues/116)) ([7180dfd](https://github.com/aer-works/aer-flow/commit/7180dfdc7d36c4b14d1b3edbed11a1c9916a6db0))


### Tests

* **flow:** M8 Phase 4 — Fan-out/fan-in + retry end-to-end integration tests ([#56](https://github.com/aer-works/aer-flow/issues/56)) ([15d5adb](https://github.com/aer-works/aer-flow/commit/15d5adb77ff0c508fee275746009cc9f0d1de9de))


### Miscellaneous

* **release:** Split release-please into linked-versions groups (core, desktop) ([#225](https://github.com/aer-works/aer-flow/issues/225)) ([86da732](https://github.com/aer-works/aer-flow/commit/86da732bc469a1a86ba412b1863fb0506aa3e40b))
* **setup:** initialize aer-flow repository ([801f348](https://github.com/aer-works/aer-flow/commit/801f348f5e2d1a21bbd25cd421cfd91c15b22c4d))

## Changelog
