# Changelog

## [0.21.0](https://github.com/aer-works/baton/compare/ui-core-v0.20.0...ui-core-v0.21.0) (2026-08-27)


### Features

* **flow:** Machine completion contract -- --wait, exit codes, status --json, terminal state for pre-ledger failures ([#1374](https://github.com/aer-works/baton/issues/1374)) ([a6ca232](https://github.com/aer-works/baton/commit/a6ca2322b155aae37a46a26865e7ea7b1cf6ee0b))

## [0.20.0](https://github.com/aer-works/baton/compare/ui-core-v0.19.0...ui-core-v0.20.0) (2026-08-26)


### Features

* **adapters,daemon,ui,mobile:** light up the depth mark from the registered model-purpose mapping ([#1341](https://github.com/aer-works/baton/issues/1341)) ([9b17744](https://github.com/aer-works/baton/commit/9b1774424a0618f84d60cbda25838f35b7dcfdca))
* **cli:** Every command prints where each produced output landed ([#777](https://github.com/aer-works/baton/issues/777)) ([0e64c30](https://github.com/aer-works/baton/commit/0e64c306450571f9bc9b94dc60d82f87bdf4157b))
* Conversational permission gate (0022) — engine, adapters, desktop + mobile ([#1087](https://github.com/aer-works/baton/issues/1087)) ([9a9192a](https://github.com/aer-works/baton/commit/9a9192a4c9446ab4c12dea0379b4979d6b729ce6))
* **daemon,ui,mobile:** A message to a dormant room is answered by the product, never dispatched ([#1182](https://github.com/aer-works/baton/issues/1182)) ([7a177c4](https://github.com/aer-works/baton/commit/7a177c4e6e1d71a148a665a99b48949b48ee2ac6))
* **daemon,ui,mobile:** orchestrator is mandatory, visible, and reassignable ([#1317](https://github.com/aer-works/baton/issues/1317)) ([75ed55a](https://github.com/aer-works/baton/commit/75ed55aca1767281208b302b0db3f3dd71b2ce45))
* **daemon,ui:** Switcher orders by last activity, derived from the journal ([#640](https://github.com/aer-works/baton/issues/640)) ([#1009](https://github.com/aer-works/baton/issues/1009)) ([439cb61](https://github.com/aer-works/baton/commit/439cb618c3ef87038faa5c20b3f601305c8b244f))
* **daemon:** Add timestamps to the task list contract ([#416](https://github.com/aer-works/baton/issues/416)) ([439c927](https://github.com/aer-works/baton/commit/439c927a31bf338bb2d8b194adc230f0a54c9d94))
* **design,ui,test:** The interaction-state register, and the end of the status fall-through class ([#977](https://github.com/aer-works/baton/issues/977)) ([21738c7](https://github.com/aer-works/baton/commit/21738c7339aa115297114fdf2387fbcb10b80d42))
* **flow,daemon,ui:** Participants — identity, naming, and properties for a room's workers ([#1310](https://github.com/aer-works/baton/issues/1310)) ([7f8306d](https://github.com/aer-works/baton/commit/7f8306d55e51f006170a6baad8924063ee5e3da9))
* **flow,ui,mobile:** Permission answers render as transcript turns on both surfaces (transcript-events phase 1) ([#1145](https://github.com/aer-works/baton/issues/1145)) ([91dc53b](https://github.com/aer-works/baton/commit/91dc53b235a568d01a0995da62f00b0675b7520e))
* **flow:** Bounded room open via a projection checkpoint ([#903](https://github.com/aer-works/baton/issues/903) scope 1a) ([#961](https://github.com/aer-works/baton/issues/961)) ([89f556f](https://github.com/aer-works/baton/commit/89f556fcc2384d22a267c018fb639b98edcf4756))
* **flow:** Engine-level auto-denied-tool signal (FailureClassification.ToolDenied) ([#914](https://github.com/aer-works/baton/issues/914)) ([#1029](https://github.com/aer-works/baton/issues/1029)) ([aa9480d](https://github.com/aer-works/baton/commit/aa9480d583df4e9f765fc91e435d78d94c3ff5e8))
* **flow:** Split PausePoint into needs-input and ready-for-review kinds ([#435](https://github.com/aer-works/baton/issues/435)) ([82a9d95](https://github.com/aer-works/baton/commit/82a9d955da69be3bed3778d25415f9f2ec0185e7))
* **flow:** WriterUtcTimestamp on the journal envelope; aer status renders per-step times ([#824](https://github.com/aer-works/baton/issues/824)) ([267baf9](https://github.com/aer-works/baton/commit/267baf92f15e094f3efa7a2d90deb964be1b0d3f))
* **mcp:** aer yield via AER's first MCP server host; dialogue sentinel retired ([#827](https://github.com/aer-works/baton/issues/827)) ([7ef1de5](https://github.com/aer-works/baton/commit/7ef1de568a3425bdb84870f1018a6eb1250b90f7))
* **mobile:** Make the switcher the phone landing, with tap-to-open rooms (front-door rebuild, slice 1) ([#1046](https://github.com/aer-works/baton/issues/1046)) ([7946bab](https://github.com/aer-works/baton/commit/7946bab12f7cbd9ef3cdb70f7e1d6343974466cf))
* **mobile:** Rooms list renders StatusMark per status; OutOfPlan joins the token file ([#1136](https://github.com/aer-works/baton/issues/1136)) ([44c25eb](https://github.com/aer-works/baton/commit/44c25ebc212d0f6cc8e84bbb4e4f9a5a97c99aa8))
* **mobile:** Truthful room states on the switcher — reply vs review, waiting-on-you first (J3, slice 2a) ([#1050](https://github.com/aer-works/baton/issues/1050)) ([c089190](https://github.com/aer-works/baton/commit/c089190c00d07e5805ccedbf86005f213efa3c64))
* **ui-core:** The projection carries when a step paused and when its decision was recorded ([#1198](https://github.com/aer-works/baton/issues/1198)) ([1db28fc](https://github.com/aer-works/baton/commit/1db28fc6ccf3154471e3cb9268b1968f197844f8))
* **ui-core:** The transcript knows about the room's decisions — live cards and answered rows ([#1203](https://github.com/aer-works/baton/issues/1203)) ([e3f0942](https://github.com/aer-works/baton/commit/e3f09429c46941ab99709db37aa86d66cf8fec4f))
* **ui,cli:** Running out of plan reads as a state with a reset time on every surface ([#1116](https://github.com/aer-works/baton/issues/1116)) ([#1123](https://github.com/aer-works/baton/issues/1123)) ([a7ded6a](https://github.com/aer-works/baton/commit/a7ded6a69dfb9c5d6119081bcba1a2e44888ef4f))
* **ui,daemon:** addressing — tag chips, a sticky tag, and untagged goes to the orchestrator ([#1329](https://github.com/aer-works/baton/issues/1329)) ([991b8ad](https://github.com/aer-works/baton/commit/991b8ad87e76f1588781d02945f06e9398049bd9))
* **ui,daemon:** Concurrency cap (3 global / 2 per vendor) with WaitingToStart room state ([#1297](https://github.com/aer-works/baton/issues/1297)) ([c64503e](https://github.com/aer-works/baton/commit/c64503e251f89ef1b0afd82f25aeb788bba9c8b4))
* **ui,daemon:** Make the concurrency cap adjustable from Settings ([#1304](https://github.com/aer-works/baton/issues/1304)) ([b4bf34b](https://github.com/aer-works/baton/commit/b4bf34bc445a10a11c2594019c06752ca044f81b))
* **ui,daemon:** Promote WaitingOnLock into the canonical room state machine ([#1301](https://github.com/aer-works/baton/issues/1301)) ([6616a58](https://github.com/aer-works/baton/commit/6616a589268d7d7a05aa64e481045ac64a1cf3ce))
* **ui,daemon:** room files — the versioned, attributed file model, with the desktop Files list ([#1344](https://github.com/aer-works/baton/issues/1344)) ([da25b46](https://github.com/aer-works/baton/commit/da25b46cc204ccc2243fcdb8e2b1647217eb9187))
* **ui,mobile,adapters,daemon:** depth and effort meters on the workflow-room worker chip ([#1337](https://github.com/aer-works/baton/issues/1337)) ([9c6d0cd](https://github.com/aer-works/baton/commit/9c6d0cdeed09dbb66769b41738a93b884bbd4a0f))
* **ui,mobile,daemon:** Dormancy renders as a transcript turn on both surfaces (transcript-events phase 4) ([#1181](https://github.com/aer-works/baton/issues/1181)) ([256ef2a](https://github.com/aer-works/baton/commit/256ef2a3d86d0818aea7d79a7289d0d525db03f0))
* **ui,mobile:** A failed turn renders as a failure card that offers the fix (transcript-events phase 3) ([#1177](https://github.com/aer-works/baton/issues/1177)) ([bebd86b](https://github.com/aer-works/baton/commit/bebd86b2fbddf3b8f9bba0972c8a20b536f1316b))
* **ui,mobile:** Report thinking time after the fact, never as a live counter ([#1295](https://github.com/aer-works/baton/issues/1295)) ([2775a7a](https://github.com/aer-works/baton/commit/2775a7a66d2f18ab41ad65580ea205a9a8345aff))
* **ui,mobile:** Room list sorts by 0018's four attention bands on both surfaces ([#1134](https://github.com/aer-works/baton/issues/1134)) ([4290b24](https://github.com/aer-works/baton/commit/4290b2478d26d6fff25d97e1380b1dc3b2411997))
* **ui:** A failure shows what broke, in the room, with the worker that failed there to be asked ([#617](https://github.com/aer-works/baton/issues/617)) ([#982](https://github.com/aer-works/baton/issues/982)) ([15bdc44](https://github.com/aer-works/baton/commit/15bdc44d738222fb4708258d6b2ff5e18f6d9baa))
* **ui:** A room's workflow can be switched off, and stays off ([#1196](https://github.com/aer-works/baton/issues/1196) slice 4b) ([#1221](https://github.com/aer-works/baton/issues/1221)) ([5479768](https://github.com/aer-works/baton/commit/5479768412d538ef9c98beadb4683c6daaaa318b))
* **ui:** A workflow room opens in the transcript, with its shape beside it ([#1210](https://github.com/aer-works/baton/issues/1210)) ([6e1dd53](https://github.com/aer-works/baton/commit/6e1dd53859f52106c383101800e9df349f2eaf75))
* **ui:** Add a Settings surface (Workers, Your phone, Appearance), folding Remote in ([#1068](https://github.com/aer-works/baton/issues/1068)) ([997fe11](https://github.com/aer-works/baton/commit/997fe115a24a67b1aaa716398cf6cca275b885cc))
* **ui:** Daily-driver chat header is the room name + worker chip (M26) ([#1058](https://github.com/aer-works/baton/issues/1058)) ([3c8bb9a](https://github.com/aer-works/baton/commit/3c8bb9a380a90dfe552190b697ac3c6f3c0b219d))
* **ui:** Desktop composer never blocks — messages queue and drain on completion ([#1074](https://github.com/aer-works/baton/issues/1074)) ([#1075](https://github.com/aer-works/baton/issues/1075)) ([da61922](https://github.com/aer-works/baton/commit/da619229d8614b480eaaa84473ec9e23c40de1fe))
* **ui:** Desktop switcher rows and ordering to the daily-driver design (M26) ([#1054](https://github.com/aer-works/baton/issues/1054)) ([cb02bfd](https://github.com/aer-works/baton/commit/cb02bfdaaae7ec73472dff7fd75daaa20c147822))
* **ui:** One gate is one object seen from three places, and the lock names its holder ([#618](https://github.com/aer-works/baton/issues/618)) ([#988](https://github.com/aer-works/baton/issues/988)) ([3a0f3d7](https://github.com/aer-works/baton/commit/3a0f3d7615e39751b38fe4e077bb8f99217c84f9))
* **ui:** Replace the six-destination rail with one switcher shell ([#464](https://github.com/aer-works/baton/issues/464)) ([849c3b8](https://github.com/aer-works/baton/commit/849c3b82205753d84b4dd508615760e5d7f246f0))
* **ui:** Room turn-host surface — throttle values, live usage, and dormancy visibility ([#994](https://github.com/aer-works/baton/issues/994)) ([#1000](https://github.com/aer-works/baton/issues/1000)) ([8260639](https://github.com/aer-works/baton/commit/8260639be29240f7f5b9a5160a5dd5dbfa1e3d7a))
* **ui:** Ship desktop surface for a room's standing permissions ([#1277](https://github.com/aer-works/baton/issues/1277)) ([28f4129](https://github.com/aer-works/baton/commit/28f4129515cc70ff8b109ee051c2b7569285fb3d))
* **ui:** The room header is the room's, not the engine's ([#1196](https://github.com/aer-works/baton/issues/1196) slice 4a) ([#1218](https://github.com/aer-works/baton/issues/1218)) ([5b46c59](https://github.com/aer-works/baton/commit/5b46c5987e3e44a93d90fc79829a89aef011c427))
* **ui:** Three icon-only rail + the inbox as a needs-you filter (M26) ([#1071](https://github.com/aer-works/baton/issues/1071), [#1072](https://github.com/aer-works/baton/issues/1072)) ([#1073](https://github.com/aer-works/baton/issues/1073)) ([5140983](https://github.com/aer-works/baton/commit/51409839973b5434d282528affbc2899d117a5f7))
* **ui:** Truthful room states on the desktop switcher — waiting-on-you first, mark on load, drop the mislabel (J3, slice 2b) ([#1052](https://github.com/aer-works/baton/issues/1052)) ([f220c12](https://github.com/aer-works/baton/commit/f220c12d5285f909e8e3e1503e16cb27aa528256))
* **workers:** FinalOutputMode -- a dialogue's declared output can carry the full transcript ([#765](https://github.com/aer-works/baton/issues/765)) ([3a80b67](https://github.com/aer-works/baton/commit/3a80b67fa6b828430025eb7034105e01b2a0fb38))


### Bug Fixes

* **daemon,flow:** A decision resolves the room's own workers, not whichever room ran last ([#1244](https://github.com/aer-works/baton/issues/1244)) ([fca14f2](https://github.com/aer-works/baton/commit/fca14f2c108da1f22cb5f67869918197818badee))
* **daemon,flow:** An exhausted interactive turn settles instead of parking for the vendor's whole reset window ([#1188](https://github.com/aer-works/baton/issues/1188)) ([02c0e63](https://github.com/aer-works/baton/commit/02c0e630c3ec95770d4b1db9fb748ac9a2120236))
* **daemon,ui,mobile:** An exhausted interactive turn renders as out-of-plan state, not a failure card (0026 §4) ([#1185](https://github.com/aer-works/baton/issues/1185)) ([7e874c3](https://github.com/aer-works/baton/commit/7e874c30c56a133ed9c760b8c65c7d5ab6d538db))
* **daemon,ui:** broadcast desktop-started runs to connected WS clients ([#401](https://github.com/aer-works/baton/issues/401)) ([ef9f0c5](https://github.com/aer-works/baton/commit/ef9f0c5b49b19910f1a768f96d9e204073977389))
* **dialogue,flow,cli,daemon,ui,adapters:** Pin every redirected child stream's decode to UTF-8 ([#466](https://github.com/aer-works/baton/issues/466)) ([#1017](https://github.com/aer-works/baton/issues/1017)) ([3489faf](https://github.com/aer-works/baton/commit/3489faf1b349c9d271c4fdf82c653b5fd22eb29d))
* **flow:** Name the failure AER already detected instead of an unclassified Failed ([#607](https://github.com/aer-works/baton/issues/607)) ([9bbecb6](https://github.com/aer-works/baton/commit/9bbecb6a0da18ca1e4d9b0481caf3a1039d3838f))
* Separate concatenated progress events with a mid-dot (desktop + mobile) ([#1291](https://github.com/aer-works/baton/issues/1291)) ([7989812](https://github.com/aer-works/baton/commit/7989812392fb386a2556c38ac69d481a495c28f4))
* **ui,adapters:** Refuse to save a permission grant that is inert or that the engine rejects ([#711](https://github.com/aer-works/baton/issues/711)) ([273b7c2](https://github.com/aer-works/baton/commit/273b7c29c932690f3e212eeccd7676014a555ce5))
* **ui,daemon:** A live permission gate ranks as Needs you everywhere - and a re-raised orphan ask finally expires ([#1112](https://github.com/aer-works/baton/issues/1112), [#1113](https://github.com/aer-works/baton/issues/1113)) ([#1114](https://github.com/aer-works/baton/issues/1114)) ([1b5b225](https://github.com/aer-works/baton/commit/1b5b225a3159a72456f3601d7726f05f17d3ae11))
* **ui,flow:** stream logs are not room files, file rows carry their summary, and a failure banner keeps its actions reachable ([#1347](https://github.com/aer-works/baton/issues/1347)) ([3747fa3](https://github.com/aer-works/baton/commit/3747fa335f9d0f1865cec2f0e7a77d7525bd26fa))
* **ui,mobile:** Complete the status vocabulary and make fill agree across toolkits ([#463](https://github.com/aer-works/baton/issues/463)) ([6dd8c87](https://github.com/aer-works/baton/commit/6dd8c874e18e3b11211b349905f346a7fd144d3a))
* **ui,mobile:** Draw status marks as shapes instead of codepoints ([#460](https://github.com/aer-works/baton/issues/460)) ([8f74855](https://github.com/aer-works/baton/commit/8f7485588c8cd7cd1bc9c2e3ebbf87002d56ee68))
* **ui,mobile:** One app-level guard per surface, so an unexpected error neither kills the app nor vanishes ([#1190](https://github.com/aer-works/baton/issues/1190)) ([765ac43](https://github.com/aer-works/baton/commit/765ac4362b343d6df38100dc5757a8cd416a706e))
* **ui:** A paused step leads with what it produced, not the instructions it was given ([#1195](https://github.com/aer-works/baton/issues/1195)) ([cce3212](https://github.com/aer-works/baton/commit/cce32124d53c8bbba6aa506bd9f524e64cef454b))
* **ui:** One state machine again — a tenth state for a room whose process died ([#1220](https://github.com/aer-works/baton/issues/1220)) ([2f8463c](https://github.com/aer-works/baton/commit/2f8463c201a971b58cd607a37f6666ff385389cc))
* **ui:** Reconcile PausedSteps/RunningExecutions by key instead of rebuilding every tick ([#1292](https://github.com/aer-works/baton/issues/1292)) ([ed2c3bc](https://github.com/aer-works/baton/commit/ed2c3bce33d69749d82031a535cb1a08e7833044))
* **ui:** Refuse to save worker bindings over a live room's register ([#1273](https://github.com/aer-works/baton/issues/1273)) ([8fe1475](https://github.com/aer-works/baton/commit/8fe1475a90224a0cdaab600256e5061bf8aabfc6))
* **ui:** Remote advertises the unreachable address and hides the not-encrypted warning ([#392](https://github.com/aer-works/baton/issues/392)) ([41ec69f](https://github.com/aer-works/baton/commit/41ec69f945a874210f51e5277529d18470070822))
* **ui:** The desktop sends its bindings file on a decide, so 0056 can heal a room ([#1261](https://github.com/aer-works/baton/issues/1261)) ([e26cf47](https://github.com/aer-works/baton/commit/e26cf47293b6ae7752207f99a4133777ac2dca99))
* **ui:** The queue holds and a typed send joins it during an open permission gate - explicit, not an accident of IsSending ([#1170](https://github.com/aer-works/baton/issues/1170)) ([582aaef](https://github.com/aer-works/baton/commit/582aaef8771ef8b1b1f9a8bd1f623ec1138016a2))
* **ui:** The version-skew check compares versions that can actually be equal ([#1263](https://github.com/aer-works/baton/issues/1263)) ([7182e73](https://github.com/aer-works/baton/commit/7182e7375e2b180f5fffcceecc02345e7c3f1f92))
* **ui:** Validate refuses the grant shapes the adapter refuses at bind ([#968](https://github.com/aer-works/baton/issues/968)) ([488f695](https://github.com/aer-works/baton/commit/488f695155903f269fe52a731727bfaa0012b1f6))


### Code Refactoring

* **adapters,cli,daemon,ui:** rename GeminiWorkerAdapter to AgyWorkerAdapter ([#1032](https://github.com/aer-works/baton/issues/1032)) ([#1034](https://github.com/aer-works/baton/issues/1034)) ([2a44e13](https://github.com/aer-works/baton/commit/2a44e1322c4e5054b5240b3cff3a2d1e7ec887f0))
* **core,daemon,ui:** Rename identifiers and spec terms to the two nouns ([#1038](https://github.com/aer-works/baton/issues/1038)) ([8d097af](https://github.com/aer-works/baton/commit/8d097afc11a21840e1d8420215019f239883a502))
* **core:** Introduce AerPaths so the storage root has a single seam ([#362](https://github.com/aer-works/baton/issues/362)) ([4b81e57](https://github.com/aer-works/baton/commit/4b81e573b73a839e9dd713acb662b0d1bfb357e9))
* **daemon:** Key host session state per session so the daemon can hold more than one ([#449](https://github.com/aer-works/baton/issues/449)) ([bc3bc98](https://github.com/aer-works/baton/commit/bc3bc98455548e4be9c9aa7e0834aba11d1a4e8b))
* **dialogue:** StopSentinel fully retired from config and every authoring surface ([#830](https://github.com/aer-works/baton/issues/830)) ([71e112d](https://github.com/aer-works/baton/commit/71e112d69db051aa494a047b18c6551d08e191d0))
* **ui,mobile,docs:** Rename the product to Baton on every user-facing surface (0045) ([#865](https://github.com/aer-works/baton/issues/865)) ([fc8cae5](https://github.com/aer-works/baton/commit/fc8cae5fd3d0c0e36b51a94942f2ea36b6b27add))
* **ui:** One rendering for a room — retire ShellSection.Task and the workflow-file preview ([#1196](https://github.com/aer-works/baton/issues/1196) slice 5) ([#1225](https://github.com/aer-works/baton/issues/1225)) ([0b8e69b](https://github.com/aer-works/baton/commit/0b8e69bc7ce6da8f16db0f998669e73d7626d6d5))
* **ui:** Push back the view-model predicates that decide state rather than project it ([#980](https://github.com/aer-works/baton/issues/980)) ([6612829](https://github.com/aer-works/baton/commit/6612829f17a8cfd481a7455d7534ac64872e2bb8))
* **ui:** Split TaskSession god file into partial-class files ([#427](https://github.com/aer-works/baton/issues/427)) ([c772587](https://github.com/aer-works/baton/commit/c772587a4d46a9344a030e3004ad0db6ff30dc56))


### Documentation

* Record the M25 design, verify it against both vendors, and consolidate the doc tree ([#473](https://github.com/aer-works/baton/issues/473)) ([a4afade](https://github.com/aer-works/baton/commit/a4afadebd7d239f2da6a49d53ea3f5217e978d6a))
* Retire IMPLEMENTATION_PLAN.md into gated homes and audit the doc surface ([#379](https://github.com/aer-works/baton/issues/379)) ([5a0b2ba](https://github.com/aer-works/baton/commit/5a0b2ba04854245beca447ad1b1611b02b96a461))


### Continuous Integration

* **audit:** Lint user-facing strings for engine vocabulary ([#956](https://github.com/aer-works/baton/issues/956)) ([825c6f7](https://github.com/aer-works/baton/commit/825c6f7d7570bd39896a1d5514e6a81f769e5da4))
* Wire the journey gate into CI, and make the token drift check bidirectional ([#490](https://github.com/aer-works/baton/issues/490)) ([67b7b18](https://github.com/aer-works/baton/commit/67b7b18acd458d9849162b107aba1ae59ee719f3))

## [0.19.0](https://github.com/aer-works/aer-flow/compare/ui-core-v0.18.0...ui-core-v0.19.0) (2026-07-22)


### Features

* **daemon,adapters,ui:** Milestone 24 — Interactive Sessions & Unified Task Creation ([#276](https://github.com/aer-works/aer-flow/issues/276)) ([f7ab4fa](https://github.com/aer-works/aer-flow/commit/f7ab4fad253e730735631d158d92f97b2fc22d03))
* **flow,adapters,ui:** Durably capture and surface the resolved prompt for ordinary workflow steps ([#297](https://github.com/aer-works/aer-flow/issues/297)) ([b91b3a1](https://github.com/aer-works/aer-flow/commit/b91b3a1242893df4a20cfdc3cc69044c2eea53e8))
* **ui,mobile:** Add a direct "start new chat" entry point ([#301](https://github.com/aer-works/aer-flow/issues/301)) ([03c2c62](https://github.com/aer-works/aer-flow/commit/03c2c62b8178878664083f015877a1d68637d593))
* **ui,mobile:** Bulk select for archive/delete in the Tasks view ([#294](https://github.com/aer-works/aer-flow/issues/294)) ([263bc4d](https://github.com/aer-works/aer-flow/commit/263bc4d3917a2a1a50e8b1571c8b642c54c6faba))


### Bug Fixes

* **daemon,ui,mobile:** Wire command picker to real actions and show active mode ([#298](https://github.com/aer-works/aer-flow/issues/298)) ([3ed5d2d](https://github.com/aer-works/aer-flow/commit/3ed5d2d0b4841bb84c5d43c6e7b58bc3ef2398d8))

## [0.18.0](https://github.com/aer-works/aer-flow/compare/ui-core-v0.17.0...ui-core-v0.18.0) (2026-07-21)


### Features

* **dialogue:** M23 Phase 1 — generalize the dialogue worker to N-party ([#273](https://github.com/aer-works/aer-flow/issues/273)) ([0a44f58](https://github.com/aer-works/aer-flow/commit/0a44f58062f9eda622452852e0e1ed29217b75b1))
* **flow,adapters:** M23 Phase 3 — Project-Directory-Bound Tasks & Portable Bindings ([#275](https://github.com/aer-works/aer-flow/issues/275)) ([2743172](https://github.com/aer-works/aer-flow/commit/274317233a1f7c419f746c1868bec80b19944e8c))

## [0.17.0](https://github.com/aer-works/aer-flow/compare/ui-core-v0.16.0...ui-core-v0.17.0) (2026-07-20)


### Features

* **templates:** implement built-in workflow template library ([#250](https://github.com/aer-works/aer-flow/issues/250)) ([#251](https://github.com/aer-works/aer-flow/issues/251)) ([2ca7490](https://github.com/aer-works/aer-flow/commit/2ca74902f829e24a6fe412030db373f78e473f17))

## [0.16.0](https://github.com/aer-works/aer-flow/compare/ui-core-v0.15.0...ui-core-v0.16.0) (2026-07-19)


### Features

* **adapters:** Add structured PermissionGrant model for worker bindings ([#230](https://github.com/aer-works/aer-flow/issues/230)) ([b958e8d](https://github.com/aer-works/aer-flow/commit/b958e8d0a1126a5f9520ab9dcb70526ac0ec87bc))
* **daemon,ui,sidecar:** M21 Phase 5+6 — Zero-Config Tailscale Embedding + Close M20's Deferred Hardening ([#244](https://github.com/aer-works/aer-flow/issues/244)) ([90fb9f9](https://github.com/aer-works/aer-flow/commit/90fb9f9d01145befa3255b62823f8adb2bfc13dd))
* **ui,mobile:** M21 Phase 3 — Desktop Pairing UX ([#235](https://github.com/aer-works/aer-flow/issues/235)) ([946743c](https://github.com/aer-works/aer-flow/commit/946743cd88176e05075f8c9264885e9ca830b57f))

## [0.15.0](https://github.com/aer-works/aer-flow/compare/ui-core-v0.14.0...ui-core-v0.15.0) (2026-07-18)


### Features

* Milestone 20 - Daemonization, Security, and Remote Control ([#223](https://github.com/aer-works/aer-flow/issues/223)) ([5a5b604](https://github.com/aer-works/aer-flow/commit/5a5b604d41e717ba643f18093c407b581b3666bd))
* **ui:** M19 Phase 2 - Navigation shell, Aer.Ui.Core seam, MVVM re-home, and the decision inbox ([#195](https://github.com/aer-works/aer-flow/issues/195)) ([2f25168](https://github.com/aer-works/aer-flow/commit/2f25168d843c609d986a0a73679e93087577c831))
* **ui:** M19 Phase 3 - Task view, human-first ([#196](https://github.com/aer-works/aer-flow/issues/196)) ([2743338](https://github.com/aer-works/aer-flow/commit/2743338e07313087221d6c091dceea3fe9592d0f))
* **ui:** M19 Phase 4 - Guided authoring, no hand-edited config files ([#197](https://github.com/aer-works/aer-flow/issues/197)) ([a3ef3b8](https://github.com/aer-works/aer-flow/commit/a3ef3b86c282194478e2247967a720356a93e86c))
* **ui:** M19 Phase 5 - Visual design pass ([#198](https://github.com/aer-works/aer-flow/issues/198)) ([8b500f5](https://github.com/aer-works/aer-flow/commit/8b500f56d6e0eb2e7d5175f2d58bfe8d675a1c9a))


### Bug Fixes

* **ui:** Polish title bar, navigation rail transitions, and step preview cache ([#221](https://github.com/aer-works/aer-flow/issues/221)) ([fc0ae3c](https://github.com/aer-works/aer-flow/commit/fc0ae3c17a66542a3a18dbf11073e11c2990bfc7))


### Tests

* **flow:** M8 Phase 4 — Fan-out/fan-in + retry end-to-end integration tests ([#56](https://github.com/aer-works/aer-flow/issues/56)) ([15d5adb](https://github.com/aer-works/aer-flow/commit/15d5adb77ff0c508fee275746009cc9f0d1de9de))


### Miscellaneous

* **release:** Split release-please into linked-versions groups (core, desktop) ([#225](https://github.com/aer-works/aer-flow/issues/225)) ([86da732](https://github.com/aer-works/aer-flow/commit/86da732bc469a1a86ba412b1863fb0506aa3e40b))

## Changelog
