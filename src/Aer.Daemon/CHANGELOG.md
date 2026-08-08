# Changelog

## [0.20.0](https://github.com/aer-works/baton/compare/daemon-v0.19.0...daemon-v0.20.0) (2026-08-08)


### Features

* **adapters,daemon:** The orchestrator occupant — role template and the L1 turn contract ([#996](https://github.com/aer-works/baton/issues/996)) ([3421f37](https://github.com/aer-works/baton/commit/3421f37b09403b8b97abca3dc20d71ed809a38eb))
* **cli:** Every command prints where each produced output landed ([#777](https://github.com/aer-works/baton/issues/777)) ([0e64c30](https://github.com/aer-works/baton/commit/0e64c306450571f9bc9b94dc60d82f87bdf4157b))
* **daemon,flow,adapters:** Memory-proposal captures land per-execution with room attribution by construction ([#853](https://github.com/aer-works/baton/issues/853)) ([d4e7ca9](https://github.com/aer-works/baton/commit/d4e7ca96817bcf6aa1a9139af8db808164fd336a))
* **daemon,flow:** Periodic room-retention sweep wiring journal compaction ([#1040](https://github.com/aer-works/baton/issues/1040)) ([559451c](https://github.com/aer-works/baton/commit/559451c70fdc4a8c83bef4c90b2767565dc862c5))
* **daemon,flow:** The resident room turn host — wake-consuming loop, host throttles, and the failure breaker ([#995](https://github.com/aer-works/baton/issues/995)) ([9c1fc0f](https://github.com/aer-works/baton/commit/9c1fc0fe1b8745108a3994b1c2751ba9887ec427))
* **daemon,ui:** Switcher orders by last activity, derived from the journal ([#640](https://github.com/aer-works/baton/issues/640)) ([#1009](https://github.com/aer-works/baton/issues/1009)) ([439cb61](https://github.com/aer-works/baton/commit/439cb618c3ef87038faa5c20b3f601305c8b244f))
* **daemon:** Add timestamps to the task list contract ([#416](https://github.com/aer-works/baton/issues/416)) ([439c927](https://github.com/aer-works/baton/commit/439c927a31bf338bb2d8b194adc230f0a54c9d94))
* **daemon:** Per-directory dispatch lock on the task endpoints; dispatch failures durably recorded ([#831](https://github.com/aer-works/baton/issues/831)) ([7c0d5c2](https://github.com/aer-works/baton/commit/7c0d5c22db7fe79370a00f5a3ef8e02ca567d864))
* **daemon:** Prune terminal runs' artifacts via the retention sweep, with a grace window ([#1041](https://github.com/aer-works/baton/issues/1041)) ([3633782](https://github.com/aer-works/baton/commit/363378292445f0d91413261b7b52bcc1262707b9))
* **daemon:** RoomWakeBridge derives wakes from journals, never stores them ([#799](https://github.com/aer-works/baton/issues/799)) ([#819](https://github.com/aer-works/baton/issues/819)) ([94b6525](https://github.com/aer-works/baton/commit/94b6525e2608f5d251e27195c14490189559db68))
* **flow,daemon:** Held-work escalation subjects, and occupant references must resolve ([#1001](https://github.com/aer-works/baton/issues/1001)) ([#1002](https://github.com/aer-works/baton/issues/1002)) ([d318c2b](https://github.com/aer-works/baton/commit/d318c2b982a309ab350ca792ef74cb8ea929ae92))
* **flow,daemon:** Held-work resolve surface applies approved memory proposals ([#859](https://github.com/aer-works/baton/issues/859)) ([de46b9d](https://github.com/aer-works/baton/commit/de46b9d89225e56e89800d0e4a41796ac6ed0190))
* **ui:** Room turn-host surface — throttle values, live usage, and dormancy visibility ([#994](https://github.com/aer-works/baton/issues/994)) ([#1000](https://github.com/aer-works/baton/issues/1000)) ([8260639](https://github.com/aer-works/baton/commit/8260639be29240f7f5b9a5160a5dd5dbfa1e3d7a))


### Bug Fixes

* **adapters:** Remove --bare, which suppresses the gate 0029 requires ([#551](https://github.com/aer-works/baton/issues/551)) ([1ded959](https://github.com/aer-works/baton/commit/1ded959ad762adcc64973154e624fd3ef2049877))
* **daemon,adapters:** Stop a concurrent read failing a session metadata write ([#353](https://github.com/aer-works/baton/issues/353)) ([1cb2265](https://github.com/aer-works/baton/commit/1cb2265f6dc03b9fbb62869f381d362125416514))
* **daemon,flow:** Comma-proof agy id scrape; snapshot readers stop blocking the persist rename ([#837](https://github.com/aer-works/baton/issues/837), [#842](https://github.com/aer-works/baton/issues/842)) ([#844](https://github.com/aer-works/baton/issues/844)) ([c9609ee](https://github.com/aer-works/baton/commit/c9609eeada0e89130662bcd88678bb378ab7d53c))
* **daemon,ui:** broadcast desktop-started runs to connected WS clients ([#401](https://github.com/aer-works/baton/issues/401)) ([ef9f0c5](https://github.com/aer-works/baton/commit/ef9f0c5b49b19910f1a768f96d9e204073977389))
* **daemon:** Fail closed when an interactive session has no working directory ([#402](https://github.com/aer-works/baton/issues/402)) ([5a02b6f](https://github.com/aer-works/baton/commit/5a02b6f4add443bf143f34a0547f676edb2cfd54))
* **daemon:** Guard session re-materialization against live flow state ([#394](https://github.com/aer-works/baton/issues/394)) ([951f69a](https://github.com/aer-works/baton/commit/951f69a90e8d587271d8f437f53425e0e40f1fe0))
* **daemon:** Keep --remote port stable so a restart doesn't strand paired phones ([#400](https://github.com/aer-works/baton/issues/400)) ([25c8631](https://github.com/aer-works/baton/commit/25c8631fbfdad0c8712c3734472b6c7acf3c3f71))
* **daemon:** Key session continuity to turn success, not to a file write ([#541](https://github.com/aer-works/baton/issues/541)) ([80a0e98](https://github.com/aer-works/baton/commit/80a0e98369e63757459ab8d407e482ca21423899))
* **daemon:** Mark a directory-less agy chat established via its log id ([#556](https://github.com/aer-works/baton/issues/556)) ([0f18b79](https://github.com/aer-works/baton/commit/0f18b795d76b94d73204db1e2b6864e67c9837ab))
* **daemon:** Recover a chat turn's answer from the vendor's structured result ([#539](https://github.com/aer-works/baton/issues/539)) ([b3cfe1c](https://github.com/aer-works/baton/commit/b3cfe1cfc9f001c70c0b9dce77d3010d7b6f7eb2))
* **daemon:** Return a meaningful message when opening a locked task ([#415](https://github.com/aer-works/baton/issues/415)) ([f33cf89](https://github.com/aer-works/baton/commit/f33cf89b5edbf954ada40a5a81aeafda721dfbc8))
* **daemon:** Run a directory-less session in its own dir, not the inherited cwd ([#440](https://github.com/aer-works/baton/issues/440)) ([513c6d0](https://github.com/aer-works/baton/commit/513c6d0ccd286f2f40f5e7dffc4a42f8415e7792))
* **daemon:** Serialize per-session turns so re-materialization can't race a live turn ([#441](https://github.com/aer-works/baton/issues/441)) ([318c85d](https://github.com/aer-works/baton/commit/318c85df01dbf409e4601ce90e600a2e697fe5ef))
* **dialogue,flow,cli,daemon,ui,adapters:** Pin every redirected child stream's decode to UTF-8 ([#466](https://github.com/aer-works/baton/issues/466)) ([#1017](https://github.com/aer-works/baton/issues/1017)) ([3489faf](https://github.com/aer-works/baton/commit/3489faf1b349c9d271c4fdf82c653b5fd22eb29d))
* **flow,daemon:** Held-work rendering and wake derivation are shape-aware ([#835](https://github.com/aer-works/baton/issues/835)) ([7725f59](https://github.com/aer-works/baton/commit/7725f591f36d5ff967a0624524f279958ce60310))
* **flow:** Stop declaring a chat output AER does not require ([#655](https://github.com/aer-works/baton/issues/655)) ([df781e6](https://github.com/aer-works/baton/commit/df781e6f8cbf8224f3e01db01e811f13533cbda2))
* **flow:** Wait out a contended room lock on the operator's resolve path ([#879](https://github.com/aer-works/baton/issues/879)) ([ed79270](https://github.com/aer-works/baton/commit/ed792707cd375ec4e7fa47a9c04becb76694522e))
* **ui,adapters:** Refuse to save a permission grant that is inert or that the engine rejects ([#711](https://github.com/aer-works/baton/issues/711)) ([273b7c2](https://github.com/aer-works/baton/commit/273b7c29c932690f3e212eeccd7676014a555ce5))


### Code Refactoring

* **adapters,cli,daemon,ui:** rename GeminiWorkerAdapter to AgyWorkerAdapter ([#1032](https://github.com/aer-works/baton/issues/1032)) ([#1034](https://github.com/aer-works/baton/issues/1034)) ([2a44e13](https://github.com/aer-works/baton/commit/2a44e1322c4e5054b5240b3cff3a2d1e7ec887f0))
* **core,daemon,ui:** Rename identifiers and spec terms to the two nouns ([#1038](https://github.com/aer-works/baton/issues/1038)) ([8d097af](https://github.com/aer-works/baton/commit/8d097afc11a21840e1d8420215019f239883a502))
* **core,daemon,ui:** Unify sessions and tasks into one storage root, with migration ([#444](https://github.com/aer-works/baton/issues/444)) ([04a11b8](https://github.com/aer-works/baton/commit/04a11b8ce095f37f4ff4bc9eba137461016dbbc0))
* **core:** Introduce AerPaths so the storage root has a single seam ([#362](https://github.com/aer-works/baton/issues/362)) ([4b81e57](https://github.com/aer-works/baton/commit/4b81e573b73a839e9dd713acb662b0d1bfb357e9))
* **daemon:** Extract the broadcast subsystem into DaemonBroadcast ([#433](https://github.com/aer-works/baton/issues/433)) ([03245fa](https://github.com/aer-works/baton/commit/03245facf740ce439d27bcbac18034fc65a41a19))
* **daemon:** Key host session state per session so the daemon can hold more than one ([#449](https://github.com/aer-works/baton/issues/449)) ([bc3bc98](https://github.com/aer-works/baton/commit/bc3bc98455548e4be9c9aa7e0834aba11d1a4e8b))


### Documentation

* Retire IMPLEMENTATION_PLAN.md into gated homes and audit the doc surface ([#379](https://github.com/aer-works/baton/issues/379)) ([5a0b2ba](https://github.com/aer-works/baton/commit/5a0b2ba04854245beca447ad1b1611b02b96a461))


### Tests

* **daemon,mobile:** Golden wire-contract fixtures from the daemon's real serializer ([#955](https://github.com/aer-works/baton/issues/955)) ([f984f81](https://github.com/aer-works/baton/commit/f984f8197d3fb0cf7108671d85e0c0af99985d16))

## [0.19.0](https://github.com/aer-works/aer-flow/compare/daemon-v0.18.0...daemon-v0.19.0) (2026-07-22)


### Features

* **daemon,adapters,ui:** Milestone 24 — Interactive Sessions & Unified Task Creation ([#276](https://github.com/aer-works/aer-flow/issues/276)) ([f7ab4fa](https://github.com/aer-works/aer-flow/commit/f7ab4fad253e730735631d158d92f97b2fc22d03))


### Bug Fixes

* **adapters,daemon:** Give chat continuation a legal Supersede target ([#291](https://github.com/aer-works/aer-flow/issues/291)) ([fb13594](https://github.com/aer-works/aer-flow/commit/fb13594513233dcd0813f504d06b6ae8ce0f474f))
* **daemon,ui,mobile:** Wire command picker to real actions and show active mode ([#298](https://github.com/aer-works/aer-flow/issues/298)) ([3ed5d2d](https://github.com/aer-works/aer-flow/commit/3ed5d2d0b4841bb84c5d43c6e7b58bc3ef2398d8))


### Tests

* **daemon,ui:** Use OS-assigned dynamic ports for test-fixture daemon instances ([#302](https://github.com/aer-works/aer-flow/issues/302)) ([f41bf43](https://github.com/aer-works/aer-flow/commit/f41bf43dd0f367de40832646d3c2070f68cfc99f))

## [0.18.0](https://github.com/aer-works/aer-flow/compare/daemon-v0.17.0...daemon-v0.18.0) (2026-07-21)


### Miscellaneous

* **daemon:** Synchronize desktop versions

## [0.17.0](https://github.com/aer-works/aer-flow/compare/daemon-v0.16.0...daemon-v0.17.0) (2026-07-20)


### Features

* **templates:** implement built-in workflow template library ([#250](https://github.com/aer-works/aer-flow/issues/250)) ([#251](https://github.com/aer-works/aer-flow/issues/251)) ([2ca7490](https://github.com/aer-works/aer-flow/commit/2ca74902f829e24a6fe412030db373f78e473f17))

## [0.16.0](https://github.com/aer-works/aer-flow/compare/daemon-v0.15.0...daemon-v0.16.0) (2026-07-19)


### Features

* **daemon,ui,sidecar:** M21 Phase 5+6 — Zero-Config Tailscale Embedding + Close M20's Deferred Hardening ([#244](https://github.com/aer-works/aer-flow/issues/244)) ([90fb9f9](https://github.com/aer-works/aer-flow/commit/90fb9f9d01145befa3255b62823f8adb2bfc13dd))
* **mobile:** Aer.Mobile Flutter client for remote decision-inbox control ([#233](https://github.com/aer-works/aer-flow/issues/233)) ([42d6a7e](https://github.com/aer-works/aer-flow/commit/42d6a7e0caedd4d1fc95d7d2564798c0b5057e9e))
* **ui,mobile:** M21 Phase 3 — Desktop Pairing UX ([#235](https://github.com/aer-works/aer-flow/issues/235)) ([946743c](https://github.com/aer-works/aer-flow/commit/946743cd88176e05075f8c9264885e9ca830b57f))

## [0.15.0](https://github.com/aer-works/aer-flow/compare/daemon-v0.14.0...daemon-v0.15.0) (2026-07-18)


### Features

* Milestone 20 - Daemonization, Security, and Remote Control ([#223](https://github.com/aer-works/aer-flow/issues/223)) ([5a5b604](https://github.com/aer-works/aer-flow/commit/5a5b604d41e717ba643f18093c407b581b3666bd))


### Tests

* **flow:** M8 Phase 4 — Fan-out/fan-in + retry end-to-end integration tests ([#56](https://github.com/aer-works/aer-flow/issues/56)) ([15d5adb](https://github.com/aer-works/aer-flow/commit/15d5adb77ff0c508fee275746009cc9f0d1de9de))


### Miscellaneous

* **release:** Split release-please into linked-versions groups (core, desktop) ([#225](https://github.com/aer-works/aer-flow/issues/225)) ([86da732](https://github.com/aer-works/aer-flow/commit/86da732bc469a1a86ba412b1863fb0506aa3e40b))

## Changelog
