# Changelog

## [0.20.0](https://github.com/aer-works/baton/compare/daemon-v0.19.0...daemon-v0.20.0) (2026-08-17)


### Features

* **adapters,daemon,ui,mobile:** light up the depth mark from the registered model-purpose mapping ([#1341](https://github.com/aer-works/baton/issues/1341)) ([9b17744](https://github.com/aer-works/baton/commit/9b1774424a0618f84d60cbda25838f35b7dcfdca))
* **adapters,daemon:** A standing permission can be taken back ([#1250](https://github.com/aer-works/baton/issues/1250)) ([99c06b2](https://github.com/aer-works/baton/commit/99c06b251de81f5b6ac7a3d4533ae8e0ae312483))
* **adapters,daemon:** The orchestrator occupant — role template and the L1 turn contract ([#996](https://github.com/aer-works/baton/issues/996)) ([3421f37](https://github.com/aer-works/baton/commit/3421f37b09403b8b97abca3dc20d71ed809a38eb))
* **cli:** Every command prints where each produced output landed ([#777](https://github.com/aer-works/baton/issues/777)) ([0e64c30](https://github.com/aer-works/baton/commit/0e64c306450571f9bc9b94dc60d82f87bdf4157b))
* Conversational permission gate (0022) — engine, adapters, desktop + mobile ([#1087](https://github.com/aer-works/baton/issues/1087)) ([9a9192a](https://github.com/aer-works/baton/commit/9a9192a4c9446ab4c12dea0379b4979d6b729ce6))
* **daemon,flow,adapters:** Memory-proposal captures land per-execution with room attribution by construction ([#853](https://github.com/aer-works/baton/issues/853)) ([d4e7ca9](https://github.com/aer-works/baton/commit/d4e7ca96817bcf6aa1a9139af8db808164fd336a))
* **daemon,flow:** Periodic room-retention sweep wiring journal compaction ([#1040](https://github.com/aer-works/baton/issues/1040)) ([559451c](https://github.com/aer-works/baton/commit/559451c70fdc4a8c83bef4c90b2767565dc862c5))
* **daemon,flow:** The resident room turn host — wake-consuming loop, host throttles, and the failure breaker ([#995](https://github.com/aer-works/baton/issues/995)) ([9c1fc0f](https://github.com/aer-works/baton/commit/9c1fc0fe1b8745108a3994b1c2751ba9887ec427))
* **daemon,ui,mobile:** A message to a dormant room is answered by the product, never dispatched ([#1182](https://github.com/aer-works/baton/issues/1182)) ([7a177c4](https://github.com/aer-works/baton/commit/7a177c4e6e1d71a148a665a99b48949b48ee2ac6))
* **daemon,ui,mobile:** orchestrator is mandatory, visible, and reassignable ([#1317](https://github.com/aer-works/baton/issues/1317)) ([75ed55a](https://github.com/aer-works/baton/commit/75ed55aca1767281208b302b0db3f3dd71b2ce45))
* **daemon,ui:** Switcher orders by last activity, derived from the journal ([#640](https://github.com/aer-works/baton/issues/640)) ([#1009](https://github.com/aer-works/baton/issues/1009)) ([439cb61](https://github.com/aer-works/baton/commit/439cb618c3ef87038faa5c20b3f601305c8b244f))
* **daemon:** A room's standing permissions can be read back ([#1255](https://github.com/aer-works/baton/issues/1255)) ([dbb3177](https://github.com/aer-works/baton/commit/dbb317786dd517bcda1d33b07b24dd5573f92546))
* **daemon:** Add timestamps to the task list contract ([#416](https://github.com/aer-works/baton/issues/416)) ([439c927](https://github.com/aer-works/baton/commit/439c927a31bf338bb2d8b194adc230f0a54c9d94))
* **daemon:** Per-directory dispatch lock on the task endpoints; dispatch failures durably recorded ([#831](https://github.com/aer-works/baton/issues/831)) ([7c0d5c2](https://github.com/aer-works/baton/commit/7c0d5c22db7fe79370a00f5a3ef8e02ca567d864))
* **daemon:** Prune terminal runs' artifacts via the retention sweep, with a grace window ([#1041](https://github.com/aer-works/baton/issues/1041)) ([3633782](https://github.com/aer-works/baton/commit/363378292445f0d91413261b7b52bcc1262707b9))
* **daemon:** RoomWakeBridge derives wakes from journals, never stores them ([#799](https://github.com/aer-works/baton/issues/799)) ([#819](https://github.com/aer-works/baton/issues/819)) ([94b6525](https://github.com/aer-works/baton/commit/94b6525e2608f5d251e27195c14490189559db68))
* **flow,daemon,ui:** Participants — identity, naming, and properties for a room's workers ([#1310](https://github.com/aer-works/baton/issues/1310)) ([7f8306d](https://github.com/aer-works/baton/commit/7f8306d55e51f006170a6baad8924063ee5e3da9))
* **flow,daemon:** Held-work escalation subjects, and occupant references must resolve ([#1001](https://github.com/aer-works/baton/issues/1001)) ([#1002](https://github.com/aer-works/baton/issues/1002)) ([d318c2b](https://github.com/aer-works/baton/commit/d318c2b982a309ab350ca792ef74cb8ea929ae92))
* **flow,daemon:** Held-work resolve surface applies approved memory proposals ([#859](https://github.com/aer-works/baton/issues/859)) ([de46b9d](https://github.com/aer-works/baton/commit/de46b9d89225e56e89800d0e4a41796ac6ed0190))
* **flow,daemon:** Journal a standing permission's revocation ([#1278](https://github.com/aer-works/baton/issues/1278)) ([1298f00](https://github.com/aer-works/baton/commit/1298f002a5f8a80df11fc605e4d407f7c6a8d10b))
* **mobile,daemon:** A stopped room on the phone says so, and its answered decisions stay readable ([#1247](https://github.com/aer-works/baton/issues/1247)) ([046d0f9](https://github.com/aer-works/baton/commit/046d0f95ebe6428c054c03e70b3c346dc08b07aa))
* **ui,daemon:** addressing — tag chips, a sticky tag, and untagged goes to the orchestrator ([#1329](https://github.com/aer-works/baton/issues/1329)) ([991b8ad](https://github.com/aer-works/baton/commit/991b8ad87e76f1588781d02945f06e9398049bd9))
* **ui,daemon:** Concurrency cap (3 global / 2 per vendor) with WaitingToStart room state ([#1297](https://github.com/aer-works/baton/issues/1297)) ([c64503e](https://github.com/aer-works/baton/commit/c64503e251f89ef1b0afd82f25aeb788bba9c8b4))
* **ui,daemon:** Make the concurrency cap adjustable from Settings ([#1304](https://github.com/aer-works/baton/issues/1304)) ([b4bf34b](https://github.com/aer-works/baton/commit/b4bf34bc445a10a11c2594019c06752ca044f81b))
* **ui,mobile,adapters,daemon:** depth and effort meters on the workflow-room worker chip ([#1337](https://github.com/aer-works/baton/issues/1337)) ([9c6d0cd](https://github.com/aer-works/baton/commit/9c6d0cdeed09dbb66769b41738a93b884bbd4a0f))
* **ui,mobile,daemon:** Dormancy renders as a transcript turn on both surfaces (transcript-events phase 4) ([#1181](https://github.com/aer-works/baton/issues/1181)) ([256ef2a](https://github.com/aer-works/baton/commit/256ef2a3d86d0818aea7d79a7289d0d525db03f0))
* **ui:** A room's workflow can be switched off, and stays off ([#1196](https://github.com/aer-works/baton/issues/1196) slice 4b) ([#1221](https://github.com/aer-works/baton/issues/1221)) ([5479768](https://github.com/aer-works/baton/commit/5479768412d538ef9c98beadb4683c6daaaa318b))
* **ui:** Room turn-host surface — throttle values, live usage, and dormancy visibility ([#994](https://github.com/aer-works/baton/issues/994)) ([#1000](https://github.com/aer-works/baton/issues/1000)) ([8260639](https://github.com/aer-works/baton/commit/8260639be29240f7f5b9a5160a5dd5dbfa1e3d7a))


### Bug Fixes

* **adapters:** Remove --bare, which suppresses the gate 0029 requires ([#551](https://github.com/aer-works/baton/issues/551)) ([1ded959](https://github.com/aer-works/baton/commit/1ded959ad762adcc64973154e624fd3ef2049877))
* **adapters:** Write a room's bindings register atomically ([#1265](https://github.com/aer-works/baton/issues/1265)) ([fb59244](https://github.com/aer-works/baton/commit/fb59244c4004ad9174aa08e25cb8c94cf3c169b9))
* **daemon,adapters:** Stop a concurrent read failing a session metadata write ([#353](https://github.com/aer-works/baton/issues/353)) ([1cb2265](https://github.com/aer-works/baton/commit/1cb2265f6dc03b9fbb62869f381d362125416514))
* **daemon,flow:** A decision resolves the room's own workers, not whichever room ran last ([#1244](https://github.com/aer-works/baton/issues/1244)) ([fca14f2](https://github.com/aer-works/baton/commit/fca14f2c108da1f22cb5f67869918197818badee))
* **daemon,flow:** An exhausted interactive turn settles instead of parking for the vendor's whole reset window ([#1188](https://github.com/aer-works/baton/issues/1188)) ([02c0e63](https://github.com/aer-works/baton/commit/02c0e630c3ec95770d4b1db9fb748ac9a2120236))
* **daemon,flow:** Comma-proof agy id scrape; snapshot readers stop blocking the persist rename ([#837](https://github.com/aer-works/baton/issues/837), [#842](https://github.com/aer-works/baton/issues/842)) ([#844](https://github.com/aer-works/baton/issues/844)) ([c9609ee](https://github.com/aer-works/baton/commit/c9609eeada0e89130662bcd88678bb378ab7d53c))
* **daemon,mcp,adapters:** A pending runtime permission dies with its turn — timeout, turn end, cancel, restart ([#1098](https://github.com/aer-works/baton/issues/1098), [#1100](https://github.com/aer-works/baton/issues/1100), [#1101](https://github.com/aer-works/baton/issues/1101)) ([#1102](https://github.com/aer-works/baton/issues/1102)) ([ecfd7dc](https://github.com/aer-works/baton/commit/ecfd7dc10b0413789ed151d923c97e74f82b16ed))
* **daemon,ui,mobile:** An exhausted interactive turn renders as out-of-plan state, not a failure card (0026 §4) ([#1185](https://github.com/aer-works/baton/issues/1185)) ([7e874c3](https://github.com/aer-works/baton/commit/7e874c30c56a133ed9c760b8c65c7d5ab6d538db))
* **daemon,ui:** broadcast desktop-started runs to connected WS clients ([#401](https://github.com/aer-works/baton/issues/401)) ([ef9f0c5](https://github.com/aer-works/baton/commit/ef9f0c5b49b19910f1a768f96d9e204073977389))
* **daemon:** A decision the daemon cannot carry out is refused, not accepted and lost ([#1231](https://github.com/aer-works/baton/issues/1231)) ([a789033](https://github.com/aer-works/baton/commit/a789033c62c5fbda6edb510c1fc5422e6eba6533))
* **daemon:** An unparseable bindings.json fails legibly, except where the work is already done ([#1276](https://github.com/aer-works/baton/issues/1276)) ([5936d94](https://github.com/aer-works/baton/commit/5936d944a5ed3b899bf0d4bfd513001c5a921eaa))
* **daemon:** Fail closed when an interactive session has no working directory ([#402](https://github.com/aer-works/baton/issues/402)) ([5a02b6f](https://github.com/aer-works/baton/commit/5a02b6f4add443bf143f34a0547f676edb2cfd54))
* **daemon:** Guard session re-materialization against live flow state ([#394](https://github.com/aer-works/baton/issues/394)) ([951f69a](https://github.com/aer-works/baton/commit/951f69a90e8d587271d8f437f53425e0e40f1fe0))
* **daemon:** Keep --remote port stable so a restart doesn't strand paired phones ([#400](https://github.com/aer-works/baton/issues/400)) ([25c8631](https://github.com/aer-works/baton/commit/25c8631fbfdad0c8712c3734472b6c7acf3c3f71))
* **daemon:** Key session continuity to turn success, not to a file write ([#541](https://github.com/aer-works/baton/issues/541)) ([80a0e98](https://github.com/aer-works/baton/commit/80a0e98369e63757459ab8d407e482ca21423899))
* **daemon:** Mark a directory-less agy chat established via its log id ([#556](https://github.com/aer-works/baton/issues/556)) ([0f18b79](https://github.com/aer-works/baton/commit/0f18b795d76b94d73204db1e2b6864e67c9837ab))
* **daemon:** One unreadable room no longer 500s the lookup of a healthy session ([#1239](https://github.com/aer-works/baton/issues/1239)) ([5e3644b](https://github.com/aer-works/baton/commit/5e3644b0740e3c107cfaf6a0b2f31a30113073f0))
* **daemon:** Recover a chat turn's answer from the vendor's structured result ([#539](https://github.com/aer-works/baton/issues/539)) ([b3cfe1c](https://github.com/aer-works/baton/commit/b3cfe1cfc9f001c70c0b9dce77d3010d7b6f7eb2))
* **daemon:** Return a meaningful message when opening a locked task ([#415](https://github.com/aer-works/baton/issues/415)) ([f33cf89](https://github.com/aer-works/baton/commit/f33cf89b5edbf954ada40a5a81aeafda721dfbc8))
* **daemon:** Run a directory-less session in its own dir, not the inherited cwd ([#440](https://github.com/aer-works/baton/issues/440)) ([513c6d0](https://github.com/aer-works/baton/commit/513c6d0ccd286f2f40f5e7dffc4a42f8415e7792))
* **daemon:** Serialize a room's first bind so two decides cannot both take it ([#1269](https://github.com/aer-works/baton/issues/1269)) ([9524a00](https://github.com/aer-works/baton/commit/9524a00f0aefd118716dcab77b4b3f92dbc2139f))
* **daemon:** Serialize per-session turns so re-materialization can't race a live turn ([#441](https://github.com/aer-works/baton/issues/441)) ([318c85d](https://github.com/aer-works/baton/commit/318c85df01dbf409e4601ce90e600a2e697fe5ef))
* **dialogue,flow,cli,daemon,ui,adapters:** Pin every redirected child stream's decode to UTF-8 ([#466](https://github.com/aer-works/baton/issues/466)) ([#1017](https://github.com/aer-works/baton/issues/1017)) ([3489faf](https://github.com/aer-works/baton/commit/3489faf1b349c9d271c4fdf82c653b5fd22eb29d))
* **flow,daemon:** Held-work rendering and wake derivation are shape-aware ([#835](https://github.com/aer-works/baton/issues/835)) ([7725f59](https://github.com/aer-works/baton/commit/7725f591f36d5ff967a0624524f279958ce60310))
* **flow,daemon:** Room-event appends take their own lock — a mid-turn ask can finally journal ([#1109](https://github.com/aer-works/baton/issues/1109)) ([#1111](https://github.com/aer-works/baton/issues/1111)) ([755e445](https://github.com/aer-works/baton/commit/755e44540c4cd44c2e7852dcbe54fd75efea1488))
* **flow:** Stop declaring a chat output AER does not require ([#655](https://github.com/aer-works/baton/issues/655)) ([df781e6](https://github.com/aer-works/baton/commit/df781e6f8cbf8224f3e01db01e811f13533cbda2))
* **flow:** Wait out a contended room lock on the operator's resolve path ([#879](https://github.com/aer-works/baton/issues/879)) ([ed79270](https://github.com/aer-works/baton/commit/ed792707cd375ec4e7fa47a9c04becb76694522e))
* **ui,adapters:** Refuse to save a permission grant that is inert or that the engine rejects ([#711](https://github.com/aer-works/baton/issues/711)) ([273b7c2](https://github.com/aer-works/baton/commit/273b7c29c932690f3e212eeccd7676014a555ce5))
* **ui,daemon:** A live permission gate ranks as Needs you everywhere - and a re-raised orphan ask finally expires ([#1112](https://github.com/aer-works/baton/issues/1112), [#1113](https://github.com/aer-works/baton/issues/1113)) ([#1114](https://github.com/aer-works/baton/issues/1114)) ([1b5b225](https://github.com/aer-works/baton/commit/1b5b225a3159a72456f3601d7726f05f17d3ae11))
* **ui:** The desktop sends its bindings file on a decide, so 0056 can heal a room ([#1261](https://github.com/aer-works/baton/issues/1261)) ([e26cf47](https://github.com/aer-works/baton/commit/e26cf47293b6ae7752207f99a4133777ac2dca99))


### Code Refactoring

* **adapters,cli,daemon,ui:** rename GeminiWorkerAdapter to AgyWorkerAdapter ([#1032](https://github.com/aer-works/baton/issues/1032)) ([#1034](https://github.com/aer-works/baton/issues/1034)) ([2a44e13](https://github.com/aer-works/baton/commit/2a44e1322c4e5054b5240b3cff3a2d1e7ec887f0))
* **core,daemon,ui:** Rename identifiers and spec terms to the two nouns ([#1038](https://github.com/aer-works/baton/issues/1038)) ([8d097af](https://github.com/aer-works/baton/commit/8d097afc11a21840e1d8420215019f239883a502))
* **core,daemon,ui:** Unify sessions and tasks into one storage root, with migration ([#444](https://github.com/aer-works/baton/issues/444)) ([04a11b8](https://github.com/aer-works/baton/commit/04a11b8ce095f37f4ff4bc9eba137461016dbbc0))
* **core:** Introduce AerPaths so the storage root has a single seam ([#362](https://github.com/aer-works/baton/issues/362)) ([4b81e57](https://github.com/aer-works/baton/commit/4b81e573b73a839e9dd713acb662b0d1bfb357e9))
* **daemon,adapters:** every room.json write goes through one guarded read-modify-write ([#1336](https://github.com/aer-works/baton/issues/1336)) ([85b3477](https://github.com/aer-works/baton/commit/85b3477405a08ebd052ddf8964fdc6bc04c0425c))
* **daemon:** Extract the broadcast subsystem into DaemonBroadcast ([#433](https://github.com/aer-works/baton/issues/433)) ([03245fa](https://github.com/aer-works/baton/commit/03245facf740ce439d27bcbac18034fc65a41a19))
* **daemon:** Key host session state per session so the daemon can hold more than one ([#449](https://github.com/aer-works/baton/issues/449)) ([bc3bc98](https://github.com/aer-works/baton/commit/bc3bc98455548e4be9c9aa7e0834aba11d1a4e8b))


### Documentation

* **decisions:** An authority grant is not a standing permission ([#1243](https://github.com/aer-works/baton/issues/1243)) ([85465bb](https://github.com/aer-works/baton/commit/85465bbe0a8b0ea170983202cf3f92d883e34af0))
* Retire IMPLEMENTATION_PLAN.md into gated homes and audit the doc surface ([#379](https://github.com/aer-works/baton/issues/379)) ([5a0b2ba](https://github.com/aer-works/baton/commit/5a0b2ba04854245beca447ad1b1611b02b96a461))


### Tests

* **daemon,mobile:** Golden wire-contract fixtures from the daemon's real serializer ([#955](https://github.com/aer-works/baton/issues/955)) ([f984f81](https://github.com/aer-works/baton/commit/f984f8197d3fb0cf7108671d85e0c0af99985d16))
* **daemon:** The restart seam re-presents gates in what clients read - and reconcile now pushes its heals ([#1172](https://github.com/aer-works/baton/issues/1172)) ([455442e](https://github.com/aer-works/baton/commit/455442ef08903ad5fc2e8e2853804dd00e8eb22d))

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
