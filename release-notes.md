:robot: I have created a release *beep* *boop*
---


<details><summary>mobile: 0.3.0</summary>

## [0.3.0](https://github.com/aer-works/baton/compare/mobile-v0.2.0...mobile-v0.3.0) (2026-08-26)


### Features

* **adapters,daemon,ui,mobile:** light up the depth mark from the registered model-purpose mapping ([#1341](https://github.com/aer-works/baton/issues/1341)) ([9b17744](https://github.com/aer-works/baton/commit/9b1774424a0618f84d60cbda25838f35b7dcfdca))
* Conversational permission gate (0022)  engine, adapters, desktop + mobile ([#1087](https://github.com/aer-works/baton/issues/1087)) ([9a9192a](https://github.com/aer-works/baton/commit/9a9192a4c9446ab4c12dea0379b4979d6b729ce6))
* **daemon,ui,mobile:** A message to a dormant room is answered by the product, never dispatched ([#1182](https://github.com/aer-works/baton/issues/1182)) ([7a177c4](https://github.com/aer-works/baton/commit/7a177c4e6e1d71a148a665a99b48949b48ee2ac6))
* **daemon,ui,mobile:** orchestrator is mandatory, visible, and reassignable ([#1317](https://github.com/aer-works/baton/issues/1317)) ([75ed55a](https://github.com/aer-works/baton/commit/75ed55aca1767281208b302b0db3f3dd71b2ce45))
* **daemon,ui:** Switcher orders by last activity, derived from the journal ([#640](https://github.com/aer-works/baton/issues/640)) ([#1009](https://github.com/aer-works/baton/issues/1009)) ([439cb61](https://github.com/aer-works/baton/commit/439cb618c3ef87038faa5c20b3f601305c8b244f))
* **flow,daemon,ui:** Participants  identity, naming, and properties for a room's workers ([#1310](https://github.com/aer-works/baton/issues/1310)) ([7f8306d](https://github.com/aer-works/baton/commit/7f8306d55e51f006170a6baad8924063ee5e3da9))
* **flow,ui,mobile:** Permission answers render as transcript turns on both surfaces (transcript-events phase 1) ([#1145](https://github.com/aer-works/baton/issues/1145)) ([91dc53b](https://github.com/aer-works/baton/commit/91dc53b235a568d01a0995da62f00b0675b7520e))
* **mobile,daemon:** A stopped room on the phone says so, and its answered decisions stay readable ([#1247](https://github.com/aer-works/baton/issues/1247)) ([046d0f9](https://github.com/aer-works/baton/commit/046d0f95ebe6428c054c03e70b3c346dc08b07aa))
* **mobile:** A workflow room opens in the phone's transcript, with its gate answered there ([#1196](https://github.com/aer-works/baton/issues/1196) slice 6a) ([#1228](https://github.com/aer-works/baton/issues/1228)) ([938849f](https://github.com/aer-works/baton/commit/938849f68494c4eaff62965ca256d2f2d1e9f8f4))
* **mobile:** First-run rooms screen gets a "New room" primary action (J8) ([#1043](https://github.com/aer-works/baton/issues/1043)) ([ec1e2b7](https://github.com/aer-works/baton/commit/ec1e2b768813678c24e30228736d061feeb1ad10))
* **mobile:** Make the switcher the phone landing, with tap-to-open rooms (front-door rebuild, slice 1) ([#1046](https://github.com/aer-works/baton/issues/1046)) ([7946bab](https://github.com/aer-works/baton/commit/7946bab12f7cbd9ef3cdb70f7e1d6343974466cf))
* **mobile:** Messages typed mid-turn queue and drain on turn completion ([#1137](https://github.com/aer-works/baton/issues/1137)) ([eb402b4](https://github.com/aer-works/baton/commit/eb402b4e442cce05683927fb657109ebe820c4b4))
* **mobile:** Render markdown & code in the phone chat transcript ([#1080](https://github.com/aer-works/baton/issues/1080)) ([#1081](https://github.com/aer-works/baton/issues/1081)) ([5857514](https://github.com/aer-works/baton/commit/585751498b5fd6dc31858d29f7ade3dc09cb786d))
* **mobile:** Render WaitingOnLock on the phone ([#1302](https://github.com/aer-works/baton/issues/1302)) ([09fbdb4](https://github.com/aer-works/baton/commit/09fbdb4fc23e1db856e340220cc3639571b47c5a))
* **mobile:** Rooms list renders StatusMark per status; OutOfPlan joins the token file ([#1136](https://github.com/aer-works/baton/issues/1136)) ([44c25eb](https://github.com/aer-works/baton/commit/44c25ebc212d0f6cc8e84bbb4e4f9a5a97c99aa8))
* **mobile:** The phone room header names the room and its workers ([#1237](https://github.com/aer-works/baton/issues/1237)) ([4908cfe](https://github.com/aer-works/baton/commit/4908cfe3b4335f03d30d7e9a6dc96df6b6f294d6))
* **mobile:** The phone's nav  Rooms, Needs you, Settings, where Needs you is a filter ([#1235](https://github.com/aer-works/baton/issues/1235)) ([19f23b1](https://github.com/aer-works/baton/commit/19f23b18418d5f27175052c52362c7566982e82b))
* **mobile:** Truthful room states on the switcher  reply vs review, waiting-on-you first (J3, slice 2a) ([#1050](https://github.com/aer-works/baton/issues/1050)) ([c089190](https://github.com/aer-works/baton/commit/c089190c00d07e5805ccedbf86005f213efa3c64))
* **ui-core:** The projection carries when a step paused and when its decision was recorded ([#1198](https://github.com/aer-works/baton/issues/1198)) ([1db28fc](https://github.com/aer-works/baton/commit/1db28fc6ccf3154471e3cb9268b1968f197844f8))
* **ui,adapters,tools:** Equal-weight permission gate with its checker, and agy stdout quota classification ([#1129](https://github.com/aer-works/baton/issues/1129)) ([7179daf](https://github.com/aer-works/baton/commit/7179dafc7d0177896ee0f57dfd49197287c0cb7d))
* **ui,cli:** Running out of plan reads as a state with a reset time on every surface ([#1116](https://github.com/aer-works/baton/issues/1116)) ([#1123](https://github.com/aer-works/baton/issues/1123)) ([a7ded6a](https://github.com/aer-works/baton/commit/a7ded6a69dfb9c5d6119081bcba1a2e44888ef4f))
* **ui,daemon:** addressing  tag chips, a sticky tag, and untagged goes to the orchestrator ([#1329](https://github.com/aer-works/baton/issues/1329)) ([991b8ad](https://github.com/aer-works/baton/commit/991b8ad87e76f1588781d02945f06e9398049bd9))
* **ui,daemon:** Concurrency cap (3 global / 2 per vendor) with WaitingToStart room state ([#1297](https://github.com/aer-works/baton/issues/1297)) ([c64503e](https://github.com/aer-works/baton/commit/c64503e251f89ef1b0afd82f25aeb788bba9c8b4))
* **ui,daemon:** Promote WaitingOnLock into the canonical room state machine ([#1301](https://github.com/aer-works/baton/issues/1301)) ([6616a58](https://github.com/aer-works/baton/commit/6616a589268d7d7a05aa64e481045ac64a1cf3ce))
* **ui,daemon:** room files  the versioned, attributed file model, with the desktop Files list ([#1344](https://github.com/aer-works/baton/issues/1344)) ([da25b46](https://github.com/aer-works/baton/commit/da25b46cc204ccc2243fcdb8e2b1647217eb9187))
* **ui,mobile,adapters,daemon:** depth and effort meters on the workflow-room worker chip ([#1337](https://github.com/aer-works/baton/issues/1337)) ([9c6d0cd](https://github.com/aer-works/baton/commit/9c6d0cdeed09dbb66769b41738a93b884bbd4a0f))
* **ui,mobile,daemon:** Dormancy renders as a transcript turn on both surfaces (transcript-events phase 4) ([#1181](https://github.com/aer-works/baton/issues/1181)) ([256ef2a](https://github.com/aer-works/baton/commit/256ef2a3d86d0818aea7d79a7289d0d525db03f0))
* **ui,mobile:** A failed turn renders as a failure card that offers the fix (transcript-events phase 3) ([#1177](https://github.com/aer-works/baton/issues/1177)) ([bebd86b](https://github.com/aer-works/baton/commit/bebd86b2fbddf3b8f9bba0972c8a20b536f1316b))
* **ui,mobile:** Generate both toolkits' themes from one token file ([#450](https://github.com/aer-works/baton/issues/450)) ([c4666a2](https://github.com/aer-works/baton/commit/c4666a29796bbe0f76d4b7803b09b8835fa4b026))
* **ui,mobile:** Point both apps at the shipped typefaces ([#457](https://github.com/aer-works/baton/issues/457)) ([360cd81](https://github.com/aer-works/baton/commit/360cd8189a1e69f6f4eb278501dc99b3c91c614a))
* **ui,mobile:** Report thinking time after the fact, never as a live counter ([#1295](https://github.com/aer-works/baton/issues/1295)) ([2775a7a](https://github.com/aer-works/baton/commit/2775a7a66d2f18ab41ad65580ea205a9a8345aff))
* **ui,mobile:** Room list sorts by 0018's four attention bands on both surfaces ([#1134](https://github.com/aer-works/baton/issues/1134)) ([4290b24](https://github.com/aer-works/baton/commit/4290b2478d26d6fff25d97e1380b1dc3b2411997))
* **ui,mobile:** Ship Source Sans 3 + JetBrains Mono and give code its own surface ([#454](https://github.com/aer-works/baton/issues/454)) ([f9b742e](https://github.com/aer-works/baton/commit/f9b742eb79ec5be5e30dfa0d277c8a75ada6e26e))
* **ui,mobile:** The open permission gate renders as a transcript turn (transcript-events phase 2) ([#1174](https://github.com/aer-works/baton/issues/1174)) ([01b689e](https://github.com/aer-works/baton/commit/01b689ea230febe19039bfb6efa901e68748147a))
* **ui,mobile:** The phone chip shows the participant's model  [#391](https://github.com/aer-works/baton/issues/391)'s session-room remainder ([#1312](https://github.com/aer-works/baton/issues/1312)) ([20f8c3b](https://github.com/aer-works/baton/commit/20f8c3bd1e624d3f4f6ec3a85aede1cd857fcc8e))
* **ui:** A room's workflow can be switched off, and stays off ([#1196](https://github.com/aer-works/baton/issues/1196) slice 4b) ([#1221](https://github.com/aer-works/baton/issues/1221)) ([5479768](https://github.com/aer-works/baton/commit/5479768412d538ef9c98beadb4683c6daaaa318b))


### Bug Fixes

* **daemon,ui,mobile:** An exhausted interactive turn renders as out-of-plan state, not a failure card (0026 §4) ([#1185](https://github.com/aer-works/baton/issues/1185)) ([7e874c3](https://github.com/aer-works/baton/commit/7e874c30c56a133ed9c760b8c65c7d5ab6d538db))
* **mobile:** A failed workflow room says what broke instead of nothing ([#1252](https://github.com/aer-works/baton/issues/1252)) ([729a37d](https://github.com/aer-works/baton/commit/729a37d0605767d861b3fb81deb406e2a74248c8))
* **mobile:** Rename Forget pairing to honest sign-out with confirmation ([#399](https://github.com/aer-works/baton/issues/399)) ([34f5a58](https://github.com/aer-works/baton/commit/34f5a586e6915ae7279b695fbe9b090c17431b01))
* **mobile:** Starting a non-chat template leaves the phone on the empty state ([#389](https://github.com/aer-works/baton/issues/389)) ([2405bf6](https://github.com/aer-works/baton/commit/2405bf65c862440f37d5ecfee36fed614bbf26eb))
* **mobile:** the phone's paused-step card tells the truth about what it can resolve ([#1335](https://github.com/aer-works/baton/issues/1335)) ([a5bc0ce](https://github.com/aer-works/baton/commit/a5bc0ce32c4f72185f038ce4b20b7a7d4bb062c0))
* Separate concatenated progress events with a mid-dot (desktop + mobile) ([#1291](https://github.com/aer-works/baton/issues/1291)) ([7989812](https://github.com/aer-works/baton/commit/7989812392fb386a2556c38ac69d481a495c28f4))
* **ui,mobile:** Complete the status vocabulary and make fill agree across toolkits ([#463](https://github.com/aer-works/baton/issues/463)) ([6dd8c87](https://github.com/aer-works/baton/commit/6dd8c874e18e3b11211b349905f346a7fd144d3a))
* **ui,mobile:** Draw status marks as shapes instead of codepoints ([#460](https://github.com/aer-works/baton/issues/460)) ([8f74855](https://github.com/aer-works/baton/commit/8f7485588c8cd7cd1bc9c2e3ebbf87002d56ee68))
* **ui,mobile:** One app-level guard per surface, so an unexpected error neither kills the app nor vanishes ([#1190](https://github.com/aer-works/baton/issues/1190)) ([765ac43](https://github.com/aer-works/baton/commit/765ac4362b343d6df38100dc5757a8cd416a706e))
* **ui:** A paused step leads with what it produced, not the instructions it was given ([#1195](https://github.com/aer-works/baton/issues/1195)) ([cce3212](https://github.com/aer-works/baton/commit/cce32124d53c8bbba6aa506bd9f524e64cef454b))
* **ui:** One state machine again  a tenth state for a room whose process died ([#1220](https://github.com/aer-works/baton/issues/1220)) ([2f8463c](https://github.com/aer-works/baton/commit/2f8463c201a971b58cd607a37f6666ff385389cc))


### Code Refactoring

* **adapters,cli,daemon,ui:** rename GeminiWorkerAdapter to AgyWorkerAdapter ([#1032](https://github.com/aer-works/baton/issues/1032)) ([#1034](https://github.com/aer-works/baton/issues/1034)) ([2a44e13](https://github.com/aer-works/baton/commit/2a44e1322c4e5054b5240b3cff3a2d1e7ec887f0))
* **core,daemon,ui:** Rename identifiers and spec terms to the two nouns ([#1038](https://github.com/aer-works/baton/issues/1038)) ([8d097af](https://github.com/aer-works/baton/commit/8d097afc11a21840e1d8420215019f239883a502))
* **ui,mobile,docs:** Rename the product to Baton on every user-facing surface (0045) ([#865](https://github.com/aer-works/baton/issues/865)) ([fc8cae5](https://github.com/aer-works/baton/commit/fc8cae5fd3d0c0e36b51a94942f2ea36b6b27add))


### Documentation

* Record the M25 design, verify it against both vendors, and consolidate the doc tree ([#473](https://github.com/aer-works/baton/issues/473)) ([a4afade](https://github.com/aer-works/baton/commit/a4afadebd7d239f2da6a49d53ea3f5217e978d6a))
* Retire IMPLEMENTATION_PLAN.md into gated homes and audit the doc surface ([#379](https://github.com/aer-works/baton/issues/379)) ([5a0b2ba](https://github.com/aer-works/baton/commit/5a0b2ba04854245beca447ad1b1611b02b96a461))


### Continuous Integration

* **audit:** Lint user-facing strings for engine vocabulary ([#956](https://github.com/aer-works/baton/issues/956)) ([825c6f7](https://github.com/aer-works/baton/commit/825c6f7d7570bd39896a1d5514e6a81f769e5da4))
* Wire the journey gate into CI, and make the token drift check bidirectional ([#490](https://github.com/aer-works/baton/issues/490)) ([67b7b18](https://github.com/aer-works/baton/commit/67b7b18acd458d9849162b107aba1ae59ee719f3))


### Tests

* Add the journey-test harness with its first driveable legs ([#372](https://github.com/aer-works/baton/issues/372)) ([3c30827](https://github.com/aer-works/baton/commit/3c308276ced96654a4326cb948547fc2821f9a35))
* **daemon,mobile:** Golden wire-contract fixtures from the daemon's real serializer ([#955](https://github.com/aer-works/baton/issues/955)) ([f984f81](https://github.com/aer-works/baton/commit/f984f8197d3fb0cf7108671d85e0c0af99985d16))


### Miscellaneous

* **mobile:** Unblock x86_64 emulator testing for Aer.Mobile ([#304](https://github.com/aer-works/baton/issues/304)) ([eb0afc2](https://github.com/aer-works/baton/commit/eb0afc2a120f32af4977451a84a057b2f7d71590))
</details>

<details><summary>flow: 0.20.0</summary>

## [0.20.0](https://github.com/aer-works/baton/compare/flow-v0.19.0...flow-v0.20.0) (2026-08-26)


### Features

* **adapters,daemon:** The orchestrator occupant  role template and the L1 turn contract ([#996](https://github.com/aer-works/baton/issues/996)) ([3421f37](https://github.com/aer-works/baton/commit/3421f37b09403b8b97abca3dc20d71ed809a38eb))
* **adapters,flow:** claude credits_required classifies ExhaustedUntil on both channels - and an unknown instant parks instead of machine-gunning ([#1115](https://github.com/aer-works/baton/issues/1115)) ([#1119](https://github.com/aer-works/baton/issues/1119)) ([0a27361](https://github.com/aer-works/baton/commit/0a27361f5690c855ce4cdbf5d89570de8af3c0a2))
* **adapters,flow:** Pass --settings/--mcp-config, cap subagent depth ([#553](https://github.com/aer-works/baton/issues/553)) ([22870d6](https://github.com/aer-works/baton/commit/22870d6cec26430540edd25bb3e6db4b3ec05008))
* **adapters,flow:** The deterministic command step  declared argv, stdout as the first declared output ([#887](https://github.com/aer-works/baton/issues/887) stage 2 slice 1) ([#963](https://github.com/aer-works/baton/issues/963)) ([33f8335](https://github.com/aer-works/baton/commit/33f83355e143f47ad9d845f45488458409279db3))
* **adapters:** Vendor memory is isolated scratch; room memory is the only durable layer ([#442](https://github.com/aer-works/baton/issues/442)) ([#1021](https://github.com/aer-works/baton/issues/1021)) ([86a3d15](https://github.com/aer-works/baton/commit/86a3d15208244354f89f7af5a759d69169d23ca7))
* **cli:** Every command prints where each produced output landed ([#777](https://github.com/aer-works/baton/issues/777)) ([0e64c30](https://github.com/aer-works/baton/commit/0e64c306450571f9bc9b94dc60d82f87bdf4157b))
* Conversational permission gate (0022)  engine, adapters, desktop + mobile ([#1087](https://github.com/aer-works/baton/issues/1087)) ([9a9192a](https://github.com/aer-works/baton/commit/9a9192a4c9446ab4c12dea0379b4979d6b729ce6))
* **daemon,flow,adapters:** Memory-proposal captures land per-execution with room attribution by construction ([#853](https://github.com/aer-works/baton/issues/853)) ([d4e7ca9](https://github.com/aer-works/baton/commit/d4e7ca96817bcf6aa1a9139af8db808164fd336a))
* **daemon,flow:** Periodic room-retention sweep wiring journal compaction ([#1040](https://github.com/aer-works/baton/issues/1040)) ([559451c](https://github.com/aer-works/baton/commit/559451c70fdc4a8c83bef4c90b2767565dc862c5))
* **daemon,flow:** The resident room turn host  wake-consuming loop, host throttles, and the failure breaker ([#995](https://github.com/aer-works/baton/issues/995)) ([9c1fc0f](https://github.com/aer-works/baton/commit/9c1fc0fe1b8745108a3994b1c2751ba9887ec427))
* **daemon,ui,mobile:** orchestrator is mandatory, visible, and reassignable ([#1317](https://github.com/aer-works/baton/issues/1317)) ([75ed55a](https://github.com/aer-works/baton/commit/75ed55aca1767281208b302b0db3f3dd71b2ce45))
* **daemon:** RoomWakeBridge derives wakes from journals, never stores them ([#799](https://github.com/aer-works/baton/issues/799)) ([#819](https://github.com/aer-works/baton/issues/819)) ([94b6525](https://github.com/aer-works/baton/commit/94b6525e2608f5d251e27195c14490189559db68))
* **flow,adapters:** A reviewer hands back an applyable patch, not prose to retype ([#881](https://github.com/aer-works/baton/issues/881)) ([#1022](https://github.com/aer-works/baton/issues/1022)) ([a23335d](https://github.com/aer-works/baton/commit/a23335d01b66c22ad47ac7648ac7cbcdfd0d583d))
* **flow,adapters:** Gemini quota exhaustion is a state with a reset time, not a failure ([#807](https://github.com/aer-works/baton/issues/807)) ([821bfb3](https://github.com/aer-works/baton/commit/821bfb379b501e667699084483e938bffc8f454a))
* **flow,adapters:** GrantAuditMode  the audited write grant, recorded in the journal ([#901](https://github.com/aer-works/baton/issues/901) PR 1) ([#1011](https://github.com/aer-works/baton/issues/1011)) ([899762a](https://github.com/aer-works/baton/commit/899762adebc8a7297f2de1e66b2ace309166b5e2))
* **flow,cli:** Engine identity recorded, liveness probed at read time -- a silence gets its provenance ([#796](https://github.com/aer-works/baton/issues/796)) ([5d4d914](https://github.com/aer-works/baton/commit/5d4d9147aeef2fec13bf449726e2db687c3617ea))
* **flow,cli:** Engine-provisioned git worktree workspaces (Rung 4, progresses [#669](https://github.com/aer-works/baton/issues/669)) ([#937](https://github.com/aer-works/baton/issues/937)) ([971f390](https://github.com/aer-works/baton/commit/971f39003253931e9f02304ea6e004d18e5bacb2))
* **flow,daemon,ui:** Participants  identity, naming, and properties for a room's workers ([#1310](https://github.com/aer-works/baton/issues/1310)) ([7f8306d](https://github.com/aer-works/baton/commit/7f8306d55e51f006170a6baad8924063ee5e3da9))
* **flow,daemon:** Held-work escalation subjects, and occupant references must resolve ([#1001](https://github.com/aer-works/baton/issues/1001)) ([#1002](https://github.com/aer-works/baton/issues/1002)) ([d318c2b](https://github.com/aer-works/baton/commit/d318c2b982a309ab350ca792ef74cb8ea929ae92))
* **flow,daemon:** Held-work resolve surface applies approved memory proposals ([#859](https://github.com/aer-works/baton/issues/859)) ([de46b9d](https://github.com/aer-works/baton/commit/de46b9d89225e56e89800d0e4a41796ac6ed0190))
* **flow,daemon:** Journal a standing permission's revocation ([#1278](https://github.com/aer-works/baton/issues/1278)) ([1298f00](https://github.com/aer-works/baton/commit/1298f002a5f8a80df11fc605e4d407f7c6a8d10b))
* **flow,ui,mobile:** Permission answers render as transcript turns on both surfaces (transcript-events phase 1) ([#1145](https://github.com/aer-works/baton/issues/1145)) ([91dc53b](https://github.com/aer-works/baton/commit/91dc53b235a568d01a0995da62f00b0675b7520e))
* **flow:** Artifact pruning mechanism for completed runs ([#973](https://github.com/aer-works/baton/issues/973)) ([#1028](https://github.com/aer-works/baton/issues/1028)) ([cfa7f3b](https://github.com/aer-works/baton/commit/cfa7f3b0a7b8dc2d0731f555269ed651d3326418))
* **flow:** Bounded event-log reads via validated seek-to-tail and checkpointed core aggregates ([#978](https://github.com/aer-works/baton/issues/978)) ([0b700fe](https://github.com/aer-works/baton/commit/0b700feb6c11d36792df981d54853ea090627ffe))
* **flow:** Bounded room open via a projection checkpoint ([#903](https://github.com/aer-works/baton/issues/903) scope 1a) ([#961](https://github.com/aer-works/baton/issues/961)) ([89f556f](https://github.com/aer-works/baton/commit/89f556fcc2384d22a267c018fb639b98edcf4756))
* **flow:** Deliver oversize worker prompts out-of-band through prompt.txt ([#748](https://github.com/aer-works/baton/issues/748)) ([#1015](https://github.com/aer-works/baton/issues/1015)) ([a25c21f](https://github.com/aer-works/baton/commit/a25c21f7a06d3a190bead1324fd9f7a6c0733de7))
* **flow:** Engine-level auto-denied-tool signal (FailureClassification.ToolDenied) ([#914](https://github.com/aer-works/baton/issues/914)) ([#1029](https://github.com/aer-works/baton/issues/1029)) ([aa9480d](https://github.com/aer-works/baton/commit/aa9480d583df4e9f765fc91e435d78d94c3ff5e8))
* **flow:** Grant record shapes  delegated authority becomes recordable ([#778](https://github.com/aer-works/baton/issues/778) design §D) ([#964](https://github.com/aer-works/baton/issues/964)) ([dd70a3e](https://github.com/aer-works/baton/commit/dd70a3edba76c999328eeff766fa6edf9d6a761e))
* **flow:** Held work is a room record -- room.jsonl, sole writer, loud reconciliation ([#812](https://github.com/aer-works/baton/issues/812)) ([84ba2a7](https://github.com/aer-works/baton/commit/84ba2a7b03df86f537afaee2520844cb3245805a))
* **flow:** Machine retries get real backoff  StepRetryScheduled, steady default ([#720](https://github.com/aer-works/baton/issues/720)) ([6653cf6](https://github.com/aer-works/baton/commit/6653cf6ce017c39256139f799034b9c14ca51832))
* **flow:** Name the holder on journal read-path sharing violations ([#398](https://github.com/aer-works/baton/issues/398)) ([#1006](https://github.com/aer-works/baton/issues/1006)) ([51736b8](https://github.com/aer-works/baton/commit/51736b890d42032fdd90ded4559cc42e7413a50c))
* **flow:** Operator retry-now reaches a quota-parked step ([#815](https://github.com/aer-works/baton/issues/815)) ([#848](https://github.com/aer-works/baton/issues/848)) ([644d070](https://github.com/aer-works/baton/commit/644d070cfef1d9ea4589ed564bfaad439f43b598))
* **flow:** Post-run grant audit  an audited worker's stray writes fail the run ([#901](https://github.com/aer-works/baton/issues/901)) ([#1013](https://github.com/aer-works/baton/issues/1013)) ([cdd702d](https://github.com/aer-works/baton/commit/cdd702d43c096c8c230e4104576de8697496f013))
* **flow:** Room turn throttles  the caps that make constant vendor spend impossible ([#778](https://github.com/aer-works/baton/issues/778) addendum) ([#966](https://github.com/aer-works/baton/issues/966)) ([6dda481](https://github.com/aer-works/baton/commit/6dda4813a45a8575964a5f2cf9bd463e40882d72))
* **flow:** Split PausePoint into needs-input and ready-for-review kinds ([#435](https://github.com/aer-works/baton/issues/435)) ([82a9d95](https://github.com/aer-works/baton/commit/82a9d955da69be3bed3778d25415f9f2ec0185e7))
* **flow:** Structured review verdict as a schema-checked contract output ([#779](https://github.com/aer-works/baton/issues/779)) ([021755e](https://github.com/aer-works/baton/commit/021755e3466b04b59a346cba933bdd60ad3616eb))
* **flow:** The orchestrator cursor carries content identity, and compaction arrives unwired ([#972](https://github.com/aer-works/baton/issues/972)) ([#1026](https://github.com/aer-works/baton/issues/1026)) ([737addd](https://github.com/aer-works/baton/commit/737addd95479fbfb47bcd0dc60b7200cf56fb446))
* **flow:** The orchestrator turn input and session cursor ([#778](https://github.com/aer-works/baton/issues/778) design §A/§B) ([#965](https://github.com/aer-works/baton/issues/965)) ([66c745c](https://github.com/aer-works/baton/commit/66c745cf7bda9072916e46a1f7bee318f3c34914))
* **flow:** The versioned room memory document and its read surface ([#672](https://github.com/aer-works/baton/issues/672) M26 floor) ([#962](https://github.com/aer-works/baton/issues/962)) ([0dbc284](https://github.com/aer-works/baton/commit/0dbc284ccf776bb9c3eb41a7314217d2013725cb))
* **flow:** WriterUtcTimestamp on the journal envelope; aer status renders per-step times ([#824](https://github.com/aer-works/baton/issues/824)) ([267baf9](https://github.com/aer-works/baton/commit/267baf92f15e094f3efa7a2d90deb964be1b0d3f))
* **mcp:** memory-edit-proposal tool on the AER server, wired for both vendors; decision 0044 ([#834](https://github.com/aer-works/baton/issues/834)) ([5c8c47d](https://github.com/aer-works/baton/commit/5c8c47d22a55e9e7d19c42210dbbd7b53785e6ad))
* **ui-core:** The projection carries when a step paused and when its decision was recorded ([#1198](https://github.com/aer-works/baton/issues/1198)) ([1db28fc](https://github.com/aer-works/baton/commit/1db28fc6ccf3154471e3cb9268b1968f197844f8))
* **ui,daemon:** Concurrency cap (3 global / 2 per vendor) with WaitingToStart room state ([#1297](https://github.com/aer-works/baton/issues/1297)) ([c64503e](https://github.com/aer-works/baton/commit/c64503e251f89ef1b0afd82f25aeb788bba9c8b4))
* **ui,daemon:** Make the concurrency cap adjustable from Settings ([#1304](https://github.com/aer-works/baton/issues/1304)) ([b4bf34b](https://github.com/aer-works/baton/commit/b4bf34bc445a10a11c2594019c06752ca044f81b))
* **ui,mobile,daemon:** Dormancy renders as a transcript turn on both surfaces (transcript-events phase 4) ([#1181](https://github.com/aer-works/baton/issues/1181)) ([256ef2a](https://github.com/aer-works/baton/commit/256ef2a3d86d0818aea7d79a7289d0d525db03f0))
* **ui:** A failure shows what broke, in the room, with the worker that failed there to be asked ([#617](https://github.com/aer-works/baton/issues/617)) ([#982](https://github.com/aer-works/baton/issues/982)) ([15bdc44](https://github.com/aer-works/baton/commit/15bdc44d738222fb4708258d6b2ff5e18f6d9baa))
* **ui:** A room's workflow can be switched off, and stays off ([#1196](https://github.com/aer-works/baton/issues/1196) slice 4b) ([#1221](https://github.com/aer-works/baton/issues/1221)) ([5479768](https://github.com/aer-works/baton/commit/5479768412d538ef9c98beadb4683c6daaaa318b))
* **ui:** One gate is one object seen from three places, and the lock names its holder ([#618](https://github.com/aer-works/baton/issues/618)) ([#988](https://github.com/aer-works/baton/issues/988)) ([3a0f3d7](https://github.com/aer-works/baton/commit/3a0f3d7615e39751b38fe4e077bb8f99217c84f9))


### Bug Fixes

* **adapters,flow:** Make the PreToolUse gate true on every spawn path, and enforced ([#705](https://github.com/aer-works/baton/issues/705)) ([6b4568f](https://github.com/aer-works/baton/commit/6b4568ffaa098ad4ff1f60667e089be6324becba))
* **adapters:** Bound the bindings-register replace by wall-clock, not attempt count ([#1268](https://github.com/aer-works/baton/issues/1268)) ([8a5843c](https://github.com/aer-works/baton/commit/8a5843c20291dc82b0f303af03d3d8bc23b38acb))
* **cli,flow:** Live stream files for captured worker output, tailed by status --follow ([#805](https://github.com/aer-works/baton/issues/805)) ([7a86bfc](https://github.com/aer-works/baton/commit/7a86bfcc92e8bc77d4c70244203e9d733095144f))
* **cli:** decide/cancel/supply provision declared worktrees, and cancel does it lazily ([#1012](https://github.com/aer-works/baton/issues/1012)) ([#1024](https://github.com/aer-works/baton/issues/1024)) ([15362b5](https://github.com/aer-works/baton/commit/15362b5116187317b447293de908fa1ab8198640))
* **cli:** Malformed template and bindings JSON errors name the file and the expected shape ([#738](https://github.com/aer-works/baton/issues/738)) ([b2e2ea1](https://github.com/aer-works/baton/commit/b2e2ea1251b13446a971dac98ff230c591a41025))
* **cli:** resolve a task directory at the CLI boundary, and refuse one that is not ([#681](https://github.com/aer-works/baton/issues/681)) ([7e61c42](https://github.com/aer-works/baton/commit/7e61c42b43ecc41654ff52ff37249656fac99512))
* **cli:** Surface a failing worker's stderr instead of discarding it ([#608](https://github.com/aer-works/baton/issues/608)) ([55e3d74](https://github.com/aer-works/baton/commit/55e3d7442241fe479fcbdb2bb59467151b928938))
* **cli:** Typed refusal when a command hits a journal held by a live engine ([#816](https://github.com/aer-works/baton/issues/816)) ([#821](https://github.com/aer-works/baton/issues/821)) ([319acfc](https://github.com/aer-works/baton/commit/319acfc2b5dc9d68dc148002dde88396eac905a4))
* **daemon,flow:** An exhausted interactive turn settles instead of parking for the vendor's whole reset window ([#1188](https://github.com/aer-works/baton/issues/1188)) ([02c0e63](https://github.com/aer-works/baton/commit/02c0e630c3ec95770d4b1db9fb748ac9a2120236))
* **daemon,flow:** Comma-proof agy id scrape; snapshot readers stop blocking the persist rename ([#837](https://github.com/aer-works/baton/issues/837), [#842](https://github.com/aer-works/baton/issues/842)) ([#844](https://github.com/aer-works/baton/issues/844)) ([c9609ee](https://github.com/aer-works/baton/commit/c9609eeada0e89130662bcd88678bb378ab7d53c))
* **daemon,mcp,adapters:** A pending runtime permission dies with its turn  timeout, turn end, cancel, restart ([#1098](https://github.com/aer-works/baton/issues/1098), [#1100](https://github.com/aer-works/baton/issues/1100), [#1101](https://github.com/aer-works/baton/issues/1101)) ([#1102](https://github.com/aer-works/baton/issues/1102)) ([ecfd7dc](https://github.com/aer-works/baton/commit/ecfd7dc10b0413789ed151d923c97e74f82b16ed))
* **daemon:** Return a meaningful message when opening a locked task ([#415](https://github.com/aer-works/baton/issues/415)) ([f33cf89](https://github.com/aer-works/baton/commit/f33cf89b5edbf954ada40a5a81aeafda721dfbc8))
* **dialogue,flow,cli,daemon,ui,adapters:** Pin every redirected child stream's decode to UTF-8 ([#466](https://github.com/aer-works/baton/issues/466)) ([#1017](https://github.com/aer-works/baton/issues/1017)) ([3489faf](https://github.com/aer-works/baton/commit/3489faf1b349c9d271c4fdf82c653b5fd22eb29d))
* **dispatch:** Enforce a POSIX command-line ceiling up-front ([#612](https://github.com/aer-works/baton/issues/612)) ([#896](https://github.com/aer-works/baton/issues/896)) ([0aa10dc](https://github.com/aer-works/baton/commit/0aa10dc47f9281bf208e2c107ff345d4b2bdcb3c))
* **dispatch:** Make the dispatch front door runnable end-to-end ([#1082](https://github.com/aer-works/baton/issues/1082), [#1083](https://github.com/aer-works/baton/issues/1083), [#1084](https://github.com/aer-works/baton/issues/1084)) ([#1085](https://github.com/aer-works/baton/issues/1085)) ([93c514e](https://github.com/aer-works/baton/commit/93c514e06b00a8f1ede97d906d538433d604dbf1))
* **flow,daemon:** Held-work rendering and wake derivation are shape-aware ([#835](https://github.com/aer-works/baton/issues/835)) ([7725f59](https://github.com/aer-works/baton/commit/7725f591f36d5ff967a0624524f279958ce60310))
* **flow,daemon:** Room-event appends take their own lock  a mid-turn ask can finally journal ([#1109](https://github.com/aer-works/baton/issues/1109)) ([#1111](https://github.com/aer-works/baton/issues/1111)) ([755e445](https://github.com/aer-works/baton/commit/755e44540c4cd44c2e7852dcbe54fd75efea1488))
* **flow,test:** The two standing macos CI failures  /private symlink path equality and the live-sweep lock race ([#1103](https://github.com/aer-works/baton/issues/1103), [#1104](https://github.com/aer-works/baton/issues/1104)) ([#1105](https://github.com/aer-works/baton/issues/1105)) ([6356978](https://github.com/aer-works/baton/commit/6356978af852164c928fd5662a0ed68f6ef62562))
* **flow:** A failed execution records its stderr tail durably ([#784](https://github.com/aer-works/baton/issues/784)) ([01ed2d6](https://github.com/aer-works/baton/commit/01ed2d6053cba989eb4a9a2e7d0acaa185a39981))
* **flow:** A refused spawn records ExecutionFailed -- the whole family, never silence ([#768](https://github.com/aer-works/baton/issues/768)) ([fe170a2](https://github.com/aer-works/baton/commit/fe170a2e47a5c5d1ae26c8f6e5dc8eb640b19e2c))
* **flow:** Atomic snapshot persistence via temp file + rename ([#818](https://github.com/aer-works/baton/issues/818)) ([#822](https://github.com/aer-works/baton/issues/822)) ([ce71fe4](https://github.com/aer-works/baton/commit/ce71fe4e3530e9baf187da3de5bfdd4cf402c3e8))
* **flow:** Bound StdoutLineBuffer with a marked split at a measured ceiling ([#721](https://github.com/aer-works/baton/issues/721)) ([a1e4aaa](https://github.com/aer-works/baton/commit/a1e4aaa1c820fab3794654bd09a51f16ca59172e))
* **flow:** Converter value errors surface their own message, not the malformed-JSON preamble ([#793](https://github.com/aer-works/baton/issues/793)) ([cc8448d](https://github.com/aer-works/baton/commit/cc8448d48b18b19fe125b41a3f48cba250224dda))
* **flow:** Crash-recovery classification reads the recorded contract, with the live one preferred ([#769](https://github.com/aer-works/baton/issues/769)) ([b933e88](https://github.com/aer-works/baton/commit/b933e88223a5e74956b5908698c90cb1e5c9b516))
* **flow:** Decode stdout statefully, so a character split across two chunks survives ([#700](https://github.com/aer-works/baton/issues/700)) ([1e8fbe1](https://github.com/aer-works/baton/commit/1e8fbe11aa85389a18e2170c3dfc66368c48f979))
* **flow:** End a $NAME expansion at an identifier boundary, and accept ${NAME} ([#715](https://github.com/aer-works/baton/issues/715)) ([5981506](https://github.com/aer-works/baton/commit/5981506c546423cbe37a227ab99f2b5f45cca632))
* **flow:** Fail replay loudly on a lost member, and persist enums by name ([#621](https://github.com/aer-works/baton/issues/621)) ([a6d966f](https://github.com/aer-works/baton/commit/a6d966fb660bca8e97738e43f9c2ae36d9ef85eb))
* **flow:** Name the failure AER already detected instead of an unclassified Failed ([#607](https://github.com/aer-works/baton/issues/607)) ([9bbecb6](https://github.com/aer-works/baton/commit/9bbecb6a0da18ca1e4d9b0481caf3a1039d3838f))
* **flow:** Refuse an over-long command line before spawning ([#613](https://github.com/aer-works/baton/issues/613)) ([a5d7630](https://github.com/aer-works/baton/commit/a5d7630786ba56e2fd83b5b032c9ace83318f5b9))
* **flow:** Resolve reparse points before the memory-apply containment check ([#876](https://github.com/aer-works/baton/issues/876)) ([cd4daaa](https://github.com/aer-works/baton/commit/cd4daaaf064997c36456d3285eac0ac8c435cfdb))
* **flow:** Serialize worktree provisioning and make it idempotent ([#1023](https://github.com/aer-works/baton/issues/1023)) ([#1030](https://github.com/aer-works/baton/issues/1030)) ([c6148f4](https://github.com/aer-works/baton/commit/c6148f472493dd0c07fd32d42a679fbf44624e33))
* **flow:** snapshot.json persists enums by name; templates keep the validator's rejection ([#722](https://github.com/aer-works/baton/issues/722)) ([c181d1a](https://github.com/aer-works/baton/commit/c181d1a1acb1ed8d67e879f0d6211bea714d4e8f))
* **flow:** Terminal means nothing further to dispatch -- phantom mid-run Terminal killed ([#811](https://github.com/aer-works/baton/issues/811)) ([de0934f](https://github.com/aer-works/baton/commit/de0934f1b76fe1d7eaf99a43716a8e4c27d89b79))
* **flow:** Wait out a contended room lock on the operator's resolve path ([#879](https://github.com/aer-works/baton/issues/879)) ([ed79270](https://github.com/aer-works/baton/commit/ed792707cd375ec4e7fa47a9c04becb76694522e))
* **flow:** Wall-clock-bounded File.Move at every unretried atomic-move site ([#985](https://github.com/aer-works/baton/issues/985)) ([#1003](https://github.com/aer-works/baton/issues/1003)) ([5fccc8b](https://github.com/aer-works/baton/commit/5fccc8bc88d4b9044fc9a201b3533ca35e509548))
* **test:** Retry cancellation delivery until the watched execution is registered ([#557](https://github.com/aer-works/baton/issues/557)) ([2341b84](https://github.com/aer-works/baton/commit/2341b8482e97f6289cf4b791e3f7af73d952c1da))
* **ui,flow:** stream logs are not room files, file rows carry their summary, and a failure banner keeps its actions reachable ([#1347](https://github.com/aer-works/baton/issues/1347)) ([3747fa3](https://github.com/aer-works/baton/commit/3747fa335f9d0f1865cec2f0e7a77d7525bd26fa))


### Code Refactoring

* **adapters,cli,daemon,ui:** rename GeminiWorkerAdapter to AgyWorkerAdapter ([#1032](https://github.com/aer-works/baton/issues/1032)) ([#1034](https://github.com/aer-works/baton/issues/1034)) ([2a44e13](https://github.com/aer-works/baton/commit/2a44e1322c4e5054b5240b3cff3a2d1e7ec887f0))
* **core,daemon,ui:** Rename identifiers and spec terms to the two nouns ([#1038](https://github.com/aer-works/baton/issues/1038)) ([8d097af](https://github.com/aer-works/baton/commit/8d097afc11a21840e1d8420215019f239883a502))
* **flow,cli,docs:** Adopt "the ledger" as the journal's user-facing noun (0045) ([#854](https://github.com/aer-works/baton/issues/854)) ([5f04eb8](https://github.com/aer-works/baton/commit/5f04eb84ac60d456db1d58cd8f87e10a735ab736))
* **flow:** Rename LaneJournalCitation to an honest HeldWorkCitation shape ([#886](https://github.com/aer-works/baton/issues/886)) ([dd5f7a7](https://github.com/aer-works/baton/commit/dd5f7a721faa276fa5e63d1446cf25d8cb8ef7fd))
</details>

<details><summary>adapters: 0.20.0</summary>

## [0.20.0](https://github.com/aer-works/baton/compare/adapters-v0.19.0...adapters-v0.20.0) (2026-08-26)


### Features

* **adapters,cli:** honour pattern-scoped agy shell grants via a strict hook matcher ([#659](https://github.com/aer-works/baton/issues/659)) ([#1031](https://github.com/aer-works/baton/issues/1031)) ([900b3b9](https://github.com/aer-works/baton/commit/900b3b9ca2b378ee527c6162017663a941615f32))
* **adapters,cli:** Let a withheld write reach the worker's outbox on claude ([#666](https://github.com/aer-works/baton/issues/666)) ([fc884cd](https://github.com/aer-works/baton/commit/fc884cd6dac19f16d803c28246e101e1c9fef493))
* **adapters,cli:** Ship a PreToolUse hook on every spawned claude worker ([#555](https://github.com/aer-works/baton/issues/555)) ([a4ad817](https://github.com/aer-works/baton/commit/a4ad8178e0263502cd873002a66718f5c7313833))
* **adapters,cli:** Ship agy's workspace PreToolUse gate ([#603](https://github.com/aer-works/baton/issues/603)) ([ea2d40c](https://github.com/aer-works/baton/commit/ea2d40ca3e62dc25d47ef1ef9d93ec0324d9c738))
* **adapters,cli:** The dispatch-role catalog becomes an engine export served by aer templates ([#887](https://github.com/aer-works/baton/issues/887) stage 1) ([#960](https://github.com/aer-works/baton/issues/960)) ([1e11847](https://github.com/aer-works/baton/commit/1e118477b2fd1254c3f147087d0f503eb717fe28))
* **adapters,daemon,ui,mobile:** light up the depth mark from the registered model-purpose mapping ([#1341](https://github.com/aer-works/baton/issues/1341)) ([9b17744](https://github.com/aer-works/baton/commit/9b1774424a0618f84d60cbda25838f35b7dcfdca))
* **adapters,daemon:** A standing permission can be taken back ([#1250](https://github.com/aer-works/baton/issues/1250)) ([99c06b2](https://github.com/aer-works/baton/commit/99c06b251de81f5b6ac7a3d4533ae8e0ae312483))
* **adapters,daemon:** The orchestrator occupant  role template and the L1 turn contract ([#996](https://github.com/aer-works/baton/issues/996)) ([3421f37](https://github.com/aer-works/baton/commit/3421f37b09403b8b97abca3dc20d71ed809a38eb))
* **adapters,flow:** claude credits_required classifies ExhaustedUntil on both channels - and an unknown instant parks instead of machine-gunning ([#1115](https://github.com/aer-works/baton/issues/1115)) ([#1119](https://github.com/aer-works/baton/issues/1119)) ([0a27361](https://github.com/aer-works/baton/commit/0a27361f5690c855ce4cdbf5d89570de8af3c0a2))
* **adapters,flow:** Pass --settings/--mcp-config, cap subagent depth ([#553](https://github.com/aer-works/baton/issues/553)) ([22870d6](https://github.com/aer-works/baton/commit/22870d6cec26430540edd25bb3e6db4b3ec05008))
* **adapters,flow:** The deterministic command step  declared argv, stdout as the first declared output ([#887](https://github.com/aer-works/baton/issues/887) stage 2 slice 1) ([#963](https://github.com/aer-works/baton/issues/963)) ([33f8335](https://github.com/aer-works/baton/commit/33f83355e143f47ad9d845f45488458409279db3))
* **adapters:** Compose a workflow template into a runnable DAG (rung-3 slice-2a) ([#916](https://github.com/aer-works/baton/issues/916)) ([204d205](https://github.com/aer-works/baton/commit/204d205997a550885d772253fa591592a0b867c4))
* **adapters:** Declare per-role structured outputs in the worker-role catalog ([#899](https://github.com/aer-works/baton/issues/899)) ([4b9a0f4](https://github.com/aer-works/baton/commit/4b9a0f4db15f71270d80c4656621ddf2a37a546e))
* **adapters:** Engine-native worker-role catalog (rung 1 of [#887](https://github.com/aer-works/baton/issues/887)) ([#889](https://github.com/aer-works/baton/issues/889)) ([811c1c4](https://github.com/aer-works/baton/commit/811c1c444231778d56d8a395a45b38cd8eb5eb62))
* **adapters:** grant agy shell commands when network access is also requested ([#561](https://github.com/aer-works/baton/issues/561)) ([8963f5b](https://github.com/aer-works/baton/commit/8963f5ba63df926dfd3c1f77661323ae4ae83e7d))
* **adapters:** thread an Effort field through WorkerInvocation to both vendors' --effort flags ([#569](https://github.com/aer-works/baton/issues/569)) ([c744ecd](https://github.com/aer-works/baton/commit/c744ecdd2ad2c733c179f3ec034af33cd57e10cd))
* **adapters:** Vendor memory is isolated scratch; room memory is the only durable layer ([#442](https://github.com/aer-works/baton/issues/442)) ([#1021](https://github.com/aer-works/baton/issues/1021)) ([86a3d15](https://github.com/aer-works/baton/commit/86a3d15208244354f89f7af5a759d69169d23ca7))
* **adapters:** WorkflowTemplate as data + WorkflowTemplateCatalog over the role catalog (rung 3 slice 1) ([#907](https://github.com/aer-works/baton/issues/907)) ([d830233](https://github.com/aer-works/baton/commit/d830233093ecd5fcc2d5b59c32a919287c386be0))
* **cli,adapters:** Add aer dispatch &lt;role&gt; over a shared RoleBinding primitive ([#902](https://github.com/aer-works/baton/issues/902)) ([8b565f3](https://github.com/aer-works/baton/commit/8b565f3b205b60a349bee9f21aaa8c6dcb829f1d))
* **cli,adapters:** Run a composed template end-to-end  template-or-role dispatch + capture adapter (rung-3 2b+2c) ([#921](https://github.com/aer-works/baton/issues/921)) ([62772b9](https://github.com/aer-works/baton/commit/62772b94ffc66d750296e55b1b58ce80d5512a65))
* Conversational permission gate (0022)  engine, adapters, desktop + mobile ([#1087](https://github.com/aer-works/baton/issues/1087)) ([9a9192a](https://github.com/aer-works/baton/commit/9a9192a4c9446ab4c12dea0379b4979d6b729ce6))
* **daemon,flow,adapters:** Memory-proposal captures land per-execution with room attribution by construction ([#853](https://github.com/aer-works/baton/issues/853)) ([d4e7ca9](https://github.com/aer-works/baton/commit/d4e7ca96817bcf6aa1a9139af8db808164fd336a))
* **daemon,ui,mobile:** A message to a dormant room is answered by the product, never dispatched ([#1182](https://github.com/aer-works/baton/issues/1182)) ([7a177c4](https://github.com/aer-works/baton/commit/7a177c4e6e1d71a148a665a99b48949b48ee2ac6))
* **daemon:** A room's standing permissions can be read back ([#1255](https://github.com/aer-works/baton/issues/1255)) ([dbb3177](https://github.com/aer-works/baton/commit/dbb317786dd517bcda1d33b07b24dd5573f92546))
* **flow,adapters:** A reviewer hands back an applyable patch, not prose to retype ([#881](https://github.com/aer-works/baton/issues/881)) ([#1022](https://github.com/aer-works/baton/issues/1022)) ([a23335d](https://github.com/aer-works/baton/commit/a23335d01b66c22ad47ac7648ac7cbcdfd0d583d))
* **flow,adapters:** Gemini quota exhaustion is a state with a reset time, not a failure ([#807](https://github.com/aer-works/baton/issues/807)) ([821bfb3](https://github.com/aer-works/baton/commit/821bfb379b501e667699084483e938bffc8f454a))
* **flow,adapters:** GrantAuditMode  the audited write grant, recorded in the journal ([#901](https://github.com/aer-works/baton/issues/901) PR 1) ([#1011](https://github.com/aer-works/baton/issues/1011)) ([899762a](https://github.com/aer-works/baton/commit/899762adebc8a7297f2de1e66b2ace309166b5e2))
* **flow,cli:** Engine-provisioned git worktree workspaces (Rung 4, progresses [#669](https://github.com/aer-works/baton/issues/669)) ([#937](https://github.com/aer-works/baton/issues/937)) ([971f390](https://github.com/aer-works/baton/commit/971f39003253931e9f02304ea6e004d18e5bacb2))
* **flow,daemon,ui:** Participants  identity, naming, and properties for a room's workers ([#1310](https://github.com/aer-works/baton/issues/1310)) ([7f8306d](https://github.com/aer-works/baton/commit/7f8306d55e51f006170a6baad8924063ee5e3da9))
* **flow:** Deliver oversize worker prompts out-of-band through prompt.txt ([#748](https://github.com/aer-works/baton/issues/748)) ([#1015](https://github.com/aer-works/baton/issues/1015)) ([a25c21f](https://github.com/aer-works/baton/commit/a25c21f7a06d3a190bead1324fd9f7a6c0733de7))
* **flow:** Engine-level auto-denied-tool signal (FailureClassification.ToolDenied) ([#914](https://github.com/aer-works/baton/issues/914)) ([#1029](https://github.com/aer-works/baton/issues/1029)) ([aa9480d](https://github.com/aer-works/baton/commit/aa9480d583df4e9f765fc91e435d78d94c3ff5e8))
* **flow:** Post-run grant audit  an audited worker's stray writes fail the run ([#901](https://github.com/aer-works/baton/issues/901)) ([#1013](https://github.com/aer-works/baton/issues/1013)) ([cdd702d](https://github.com/aer-works/baton/commit/cdd702d43c096c8c230e4104576de8697496f013))
* **flow:** Split PausePoint into needs-input and ready-for-review kinds ([#435](https://github.com/aer-works/baton/issues/435)) ([82a9d95](https://github.com/aer-works/baton/commit/82a9d955da69be3bed3778d25415f9f2ec0185e7))
* **mcp:** memory-edit-proposal tool on the AER server, wired for both vendors; decision 0044 ([#834](https://github.com/aer-works/baton/issues/834)) ([5c8c47d](https://github.com/aer-works/baton/commit/5c8c47d22a55e9e7d19c42210dbbd7b53785e6ad))
* **ui,adapters,tools:** Equal-weight permission gate with its checker, and agy stdout quota classification ([#1129](https://github.com/aer-works/baton/issues/1129)) ([7179daf](https://github.com/aer-works/baton/commit/7179dafc7d0177896ee0f57dfd49197287c0cb7d))
* **ui,daemon:** addressing  tag chips, a sticky tag, and untagged goes to the orchestrator ([#1329](https://github.com/aer-works/baton/issues/1329)) ([991b8ad](https://github.com/aer-works/baton/commit/991b8ad87e76f1588781d02945f06e9398049bd9))
* **ui,daemon:** Make the concurrency cap adjustable from Settings ([#1304](https://github.com/aer-works/baton/issues/1304)) ([b4bf34b](https://github.com/aer-works/baton/commit/b4bf34bc445a10a11c2594019c06752ca044f81b))
* **ui,mobile,adapters,daemon:** depth and effort meters on the workflow-room worker chip ([#1337](https://github.com/aer-works/baton/issues/1337)) ([9c6d0cd](https://github.com/aer-works/baton/commit/9c6d0cdeed09dbb66769b41738a93b884bbd4a0f))


### Bug Fixes

* **adapters,cli:** Make the agy PreToolUse gate actually run, and make the checks able to notice when it does not ([#709](https://github.com/aer-works/baton/issues/709)) ([f25e707](https://github.com/aer-works/baton/commit/f25e70749fe3ba55736151f3115ecdc626c3c565))
* **adapters,cli:** Vendor-tag the denied-tools channel so absent and empty differ ([#661](https://github.com/aer-works/baton/issues/661)) ([d9c9762](https://github.com/aer-works/baton/commit/d9c97629c60a59aaa210d52df28cb34ff602111d))
* **adapters,flow:** Make the PreToolUse gate true on every spawn path, and enforced ([#705](https://github.com/aer-works/baton/issues/705)) ([6b4568f](https://github.com/aer-works/baton/commit/6b4568ffaa098ad4ff1f60667e089be6324becba))
* **adapters:** A losing rename is not a losing writer -- re-check content identity on every failed attempt ([#739](https://github.com/aer-works/baton/issues/739)) ([f758312](https://github.com/aer-works/baton/commit/f758312c4767b08fbe01cadc996263a8662114d1))
* **adapters:** Bind the room's directory for agy, which ignores the process cwd ([#492](https://github.com/aer-works/baton/issues/492)) ([898ed73](https://github.com/aer-works/baton/commit/898ed73297f93ff49f70b063b9cabcd0a4514138))
* **adapters:** Bound the bindings-register replace by wall-clock, not attempt count ([#1268](https://github.com/aer-works/baton/issues/1268)) ([8a5843c](https://github.com/aer-works/baton/commit/8a5843c20291dc82b0f303af03d3d8bc23b38acb))
* **adapters:** Derive agy's --print-timeout from AER's own configured timeout ([#610](https://github.com/aer-works/baton/issues/610)) ([97016c6](https://github.com/aer-works/baton/commit/97016c6c9b4310b319d892e2621d12023afc7911))
* **adapters:** Enforce withheld permissions with --disallowedTools ([#380](https://github.com/aer-works/baton/issues/380)) ([145d9c9](https://github.com/aer-works/baton/commit/145d9c97255a551c85f812ae6870d51bfeee94c6))
* **adapters:** Guard the fourth permission category, and say what actually withholds it ([#625](https://github.com/aer-works/baton/issues/625)) ([6199e56](https://github.com/aer-works/baton/commit/6199e56277f3bea607683e82bf4c6fede765d95e))
* **adapters:** Refuse a pattern-scoped shell grant on agy instead of dropping the patterns ([#656](https://github.com/aer-works/baton/issues/656)) ([33121d1](https://github.com/aer-works/baton/commit/33121d1bafebfab3d595921246f00ffb187c4366))
* **adapters:** Refuse a permission grant whose shell defeats a withheld category ([#646](https://github.com/aer-works/baton/issues/646)) ([bf23b27](https://github.com/aer-works/baton/commit/bf23b2728c8bfb9902e9ce3d6d8be5389bd39d0a))
* **adapters:** Remove --bare, which suppresses the gate 0029 requires ([#551](https://github.com/aer-works/baton/issues/551)) ([1ded959](https://github.com/aer-works/baton/commit/1ded959ad762adcc64973154e624fd3ef2049877))
* **adapters:** ReviewRun's review step writes report.md, matching the catalog ([#946](https://github.com/aer-works/baton/issues/946)) ([efec011](https://github.com/aer-works/baton/commit/efec011b2781501adc47da1b25d56149a1fe07f5))
* **adapters:** Stop rewriting a launch config that already holds the content being written ([#683](https://github.com/aer-works/baton/issues/683)) ([28a4a96](https://github.com/aer-works/baton/commit/28a4a96bc726baf6ab0f12174e9d79060773b85b))
* **adapters:** Write a room's bindings register atomically ([#1265](https://github.com/aer-works/baton/issues/1265)) ([fb59244](https://github.com/aer-works/baton/commit/fb59244c4004ad9174aa08e25cb8c94cf3c169b9))
* **cli,adapters:** Bound a granted write to the workspace and the outbox ([#684](https://github.com/aer-works/baton/issues/684)) ([a32d9e2](https://github.com/aer-works/baton/commit/a32d9e2d11fd659cb90b5d9e6765148bdd6ecc67))
* **cli,flow:** Live stream files for captured worker output, tailed by status --follow ([#805](https://github.com/aer-works/baton/issues/805)) ([7a86bfc](https://github.com/aer-works/baton/commit/7a86bfcc92e8bc77d4c70244203e9d733095144f))
* **cli:** decide/cancel/supply provision declared worktrees, and cancel does it lazily ([#1012](https://github.com/aer-works/baton/issues/1012)) ([#1024](https://github.com/aer-works/baton/issues/1024)) ([15362b5](https://github.com/aer-works/baton/commit/15362b5116187317b447293de908fa1ab8198640))
* **cli:** Lazily resolve worker bindings so unrelated unresolvable entries cannot block cancel/supply ([#727](https://github.com/aer-works/baton/issues/727)) ([bf97775](https://github.com/aer-works/baton/commit/bf97775022766b42907fc7c43459255dc2172406))
* **cli:** Malformed template and bindings JSON errors name the file and the expected shape ([#738](https://github.com/aer-works/baton/issues/738)) ([b2e2ea1](https://github.com/aer-works/baton/commit/b2e2ea1251b13446a971dac98ff230c591a41025))
* **daemon,adapters:** Stop a concurrent read failing a session metadata write ([#353](https://github.com/aer-works/baton/issues/353)) ([1cb2265](https://github.com/aer-works/baton/commit/1cb2265f6dc03b9fbb62869f381d362125416514))
* **daemon,flow:** A decision resolves the room's own workers, not whichever room ran last ([#1244](https://github.com/aer-works/baton/issues/1244)) ([fca14f2](https://github.com/aer-works/baton/commit/fca14f2c108da1f22cb5f67869918197818badee))
* **daemon,mcp,adapters:** A pending runtime permission dies with its turn  timeout, turn end, cancel, restart ([#1098](https://github.com/aer-works/baton/issues/1098), [#1100](https://github.com/aer-works/baton/issues/1100), [#1101](https://github.com/aer-works/baton/issues/1101)) ([#1102](https://github.com/aer-works/baton/issues/1102)) ([ecfd7dc](https://github.com/aer-works/baton/commit/ecfd7dc10b0413789ed151d923c97e74f82b16ed))
* **daemon,ui,mobile:** An exhausted interactive turn renders as out-of-plan state, not a failure card (0026 §4) ([#1185](https://github.com/aer-works/baton/issues/1185)) ([7e874c3](https://github.com/aer-works/baton/commit/7e874c30c56a133ed9c760b8c65c7d5ab6d538db))
* **daemon:** An unparseable bindings.json fails legibly, except where the work is already done ([#1276](https://github.com/aer-works/baton/issues/1276)) ([5936d94](https://github.com/aer-works/baton/commit/5936d944a5ed3b899bf0d4bfd513001c5a921eaa))
* **daemon:** Fail closed when an interactive session has no working directory ([#402](https://github.com/aer-works/baton/issues/402)) ([5a02b6f](https://github.com/aer-works/baton/commit/5a02b6f4add443bf143f34a0547f676edb2cfd54))
* **daemon:** Run a directory-less session in its own dir, not the inherited cwd ([#440](https://github.com/aer-works/baton/issues/440)) ([513c6d0](https://github.com/aer-works/baton/commit/513c6d0ccd286f2f40f5e7dffc4a42f8415e7792))
* **dialogue,flow,cli,daemon,ui,adapters:** Pin every redirected child stream's decode to UTF-8 ([#466](https://github.com/aer-works/baton/issues/466)) ([#1017](https://github.com/aer-works/baton/issues/1017)) ([3489faf](https://github.com/aer-works/baton/commit/3489faf1b349c9d271c4fdf82c653b5fd22eb29d))
* **dispatch:** Make the dispatch front door runnable end-to-end ([#1082](https://github.com/aer-works/baton/issues/1082), [#1083](https://github.com/aer-works/baton/issues/1083), [#1084](https://github.com/aer-works/baton/issues/1084)) ([#1085](https://github.com/aer-works/baton/issues/1085)) ([93c514e](https://github.com/aer-works/baton/commit/93c514e06b00a8f1ede97d906d538433d604dbf1))
* **flow:** End a $NAME expansion at an identifier boundary, and accept ${NAME} ([#715](https://github.com/aer-works/baton/issues/715)) ([5981506](https://github.com/aer-works/baton/commit/5981506c546423cbe37a227ab99f2b5f45cca632))
* **flow:** Fail replay loudly on a lost member, and persist enums by name ([#621](https://github.com/aer-works/baton/issues/621)) ([a6d966f](https://github.com/aer-works/baton/commit/a6d966fb660bca8e97738e43f9c2ae36d9ef85eb))
* **flow:** Refuse a contract whose outputs the grant cannot write ([#660](https://github.com/aer-works/baton/issues/660)) ([52f3bbb](https://github.com/aer-works/baton/commit/52f3bbbc53f3f794bf8a3a813010bc2e105ec1b0))
* **flow:** Serialize worktree provisioning and make it idempotent ([#1023](https://github.com/aer-works/baton/issues/1023)) ([#1030](https://github.com/aer-works/baton/issues/1030)) ([c6148f4](https://github.com/aer-works/baton/commit/c6148f472493dd0c07fd32d42a679fbf44624e33))
* **flow:** Stop declaring a chat output AER does not require ([#655](https://github.com/aer-works/baton/issues/655)) ([df781e6](https://github.com/aer-works/baton/commit/df781e6f8cbf8224f3e01db01e811f13533cbda2))
* Sharing-violation losers retry instead of treating a transient race as terminal ([#839](https://github.com/aer-works/baton/issues/839), [#840](https://github.com/aer-works/baton/issues/840)) ([#841](https://github.com/aer-works/baton/issues/841)) ([bf5d081](https://github.com/aer-works/baton/commit/bf5d0814cf37ecb86a311a5019dbe98ab680b41b))
* **ui,adapters:** Refuse to save a permission grant that is inert or that the engine rejects ([#711](https://github.com/aer-works/baton/issues/711)) ([273b7c2](https://github.com/aer-works/baton/commit/273b7c29c932690f3e212eeccd7676014a555ce5))
* **ui,flow:** stream logs are not room files, file rows carry their summary, and a failure banner keeps its actions reachable ([#1347](https://github.com/aer-works/baton/issues/1347)) ([3747fa3](https://github.com/aer-works/baton/commit/3747fa335f9d0f1865cec2f0e7a77d7525bd26fa))


### Code Refactoring

* **adapters,cli,daemon,ui:** rename GeminiWorkerAdapter to AgyWorkerAdapter ([#1032](https://github.com/aer-works/baton/issues/1032)) ([#1034](https://github.com/aer-works/baton/issues/1034)) ([2a44e13](https://github.com/aer-works/baton/commit/2a44e1322c4e5054b5240b3cff3a2d1e7ec887f0))
* **adapters:** Review-run's review step adopts the catalog review role; contracts disclose the inputs the step delivers ([#1148](https://github.com/aer-works/baton/issues/1148)) ([fcb17fb](https://github.com/aer-works/baton/commit/fcb17fb3337988e219d113e4612fa348c4a366f6))
* **core,daemon,ui:** Rename identifiers and spec terms to the two nouns ([#1038](https://github.com/aer-works/baton/issues/1038)) ([8d097af](https://github.com/aer-works/baton/commit/8d097afc11a21840e1d8420215019f239883a502))
* **core,daemon,ui:** Unify sessions and tasks into one storage root, with migration ([#444](https://github.com/aer-works/baton/issues/444)) ([04a11b8](https://github.com/aer-works/baton/commit/04a11b8ce095f37f4ff4bc9eba137461016dbbc0))
* **core:** Introduce AerPaths so the storage root has a single seam ([#362](https://github.com/aer-works/baton/issues/362)) ([4b81e57](https://github.com/aer-works/baton/commit/4b81e573b73a839e9dd713acb662b0d1bfb357e9))
* **daemon,adapters:** every room.json write goes through one guarded read-modify-write ([#1336](https://github.com/aer-works/baton/issues/1336)) ([85b3477](https://github.com/aer-works/baton/commit/85b3477405a08ebd052ddf8964fdc6bc04c0425c))
* **daemon:** Key host session state per session so the daemon can hold more than one ([#449](https://github.com/aer-works/baton/issues/449)) ([bc3bc98](https://github.com/aer-works/baton/commit/bc3bc98455548e4be9c9aa7e0834aba11d1a4e8b))
* **dialogue:** StopSentinel fully retired from config and every authoring surface ([#830](https://github.com/aer-works/baton/issues/830)) ([71e112d](https://github.com/aer-works/baton/commit/71e112d69db051aa494a047b18c6551d08e191d0))
* **ui:** Push back the view-model predicates that decide state rather than project it ([#980](https://github.com/aer-works/baton/issues/980)) ([6612829](https://github.com/aer-works/baton/commit/6612829f17a8cfd481a7455d7534ac64872e2bb8))


### Documentation

* **decisions:** An authority grant is not a standing permission ([#1243](https://github.com/aer-works/baton/issues/1243)) ([85465bb](https://github.com/aer-works/baton/commit/85465bbe0a8b0ea170983202cf3f92d883e34af0))
* Read both vendors' documentation in full, and add the tool that did it ([#528](https://github.com/aer-works/baton/issues/528)) ([fd1aa00](https://github.com/aer-works/baton/commit/fd1aa00b9582652ccad0ab66e4c2e6ae3caa76d9))


### Tests

* **adapters:** Hold IPermissionGrantTranslator to the adapters that read a grant ([#654](https://github.com/aer-works/baton/issues/654)) ([ec5073a](https://github.com/aer-works/baton/commit/ec5073ab7fb16ab4b5e37a99b325806b4c757853))


### Miscellaneous

* **adapters:** Remove the dialogue worker's inert shell wrap ([#771](https://github.com/aer-works/baton/issues/771)) ([b72600f](https://github.com/aer-works/baton/commit/b72600f90a228ea69ef062d7df3131c23f8c0db7))
</details>

<details><summary>dialogue-worker: 0.20.0</summary>

## [0.20.0](https://github.com/aer-works/baton/compare/dialogue-worker-v0.19.0...dialogue-worker-v0.20.0) (2026-08-26)


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
</details>

<details><summary>cli: 0.20.0</summary>

## [0.20.0](https://github.com/aer-works/baton/compare/cli-v0.19.0...cli-v0.20.0) (2026-08-26)


### Features

* **adapters,cli:** honour pattern-scoped agy shell grants via a strict hook matcher ([#659](https://github.com/aer-works/baton/issues/659)) ([#1031](https://github.com/aer-works/baton/issues/1031)) ([900b3b9](https://github.com/aer-works/baton/commit/900b3b9ca2b378ee527c6162017663a941615f32))
* **adapters,cli:** Let a withheld write reach the worker's outbox on claude ([#666](https://github.com/aer-works/baton/issues/666)) ([fc884cd](https://github.com/aer-works/baton/commit/fc884cd6dac19f16d803c28246e101e1c9fef493))
* **adapters,cli:** Ship a PreToolUse hook on every spawned claude worker ([#555](https://github.com/aer-works/baton/issues/555)) ([a4ad817](https://github.com/aer-works/baton/commit/a4ad8178e0263502cd873002a66718f5c7313833))
* **adapters,cli:** Ship agy's workspace PreToolUse gate ([#603](https://github.com/aer-works/baton/issues/603)) ([ea2d40c](https://github.com/aer-works/baton/commit/ea2d40ca3e62dc25d47ef1ef9d93ec0324d9c738))
* **adapters,cli:** The dispatch-role catalog becomes an engine export served by aer templates ([#887](https://github.com/aer-works/baton/issues/887) stage 1) ([#960](https://github.com/aer-works/baton/issues/960)) ([1e11847](https://github.com/aer-works/baton/commit/1e118477b2fd1254c3f147087d0f503eb717fe28))
* **cli,adapters:** Add aer dispatch &lt;role&gt; over a shared RoleBinding primitive ([#902](https://github.com/aer-works/baton/issues/902)) ([8b565f3](https://github.com/aer-works/baton/commit/8b565f3b205b60a349bee9f21aaa8c6dcb829f1d))
* **cli,adapters:** Run a composed template end-to-end  template-or-role dispatch + capture adapter (rung-3 2b+2c) ([#921](https://github.com/aer-works/baton/issues/921)) ([62772b9](https://github.com/aer-works/baton/commit/62772b94ffc66d750296e55b1b58ce80d5512a65))
* **cli:** aer run --echo-worker streams worker stdout live ([#882](https://github.com/aer-works/baton/issues/882)) ([#1010](https://github.com/aer-works/baton/issues/1010)) ([f23cea0](https://github.com/aer-works/baton/commit/f23cea0176ed24ac44dfb1f1fed7c774836133e6))
* **cli:** aer status &lt;task-dir&gt; --follow -- watch a running workflow from the recorded events ([#766](https://github.com/aer-works/baton/issues/766)) ([e9275fd](https://github.com/aer-works/baton/commit/e9275fd28a4b403b551460d302ea35e15d158ced))
* **cli:** aer status renders a parked step's classification and dated local retry time ([#829](https://github.com/aer-works/baton/issues/829)) ([b8514b7](https://github.com/aer-works/baton/commit/b8514b7b039c02990f88fed7390ad7b4a593f23e))
* **cli:** Every command prints where each produced output landed ([#777](https://github.com/aer-works/baton/issues/777)) ([0e64c30](https://github.com/aer-works/baton/commit/0e64c306450571f9bc9b94dc60d82f87bdf4157b))
* Conversational permission gate (0022)  engine, adapters, desktop + mobile ([#1087](https://github.com/aer-works/baton/issues/1087)) ([9a9192a](https://github.com/aer-works/baton/commit/9a9192a4c9446ab4c12dea0379b4979d6b729ce6))
* **flow,cli:** Engine identity recorded, liveness probed at read time -- a silence gets its provenance ([#796](https://github.com/aer-works/baton/issues/796)) ([5d4d914](https://github.com/aer-works/baton/commit/5d4d9147aeef2fec13bf449726e2db687c3617ea))
* **flow,cli:** Engine-provisioned git worktree workspaces (Rung 4, progresses [#669](https://github.com/aer-works/baton/issues/669)) ([#937](https://github.com/aer-works/baton/issues/937)) ([971f390](https://github.com/aer-works/baton/commit/971f39003253931e9f02304ea6e004d18e5bacb2))
* **flow:** Bounded room open via a projection checkpoint ([#903](https://github.com/aer-works/baton/issues/903) scope 1a) ([#961](https://github.com/aer-works/baton/issues/961)) ([89f556f](https://github.com/aer-works/baton/commit/89f556fcc2384d22a267c018fb639b98edcf4756))
* **flow:** Operator retry-now reaches a quota-parked step ([#815](https://github.com/aer-works/baton/issues/815)) ([#848](https://github.com/aer-works/baton/issues/848)) ([644d070](https://github.com/aer-works/baton/commit/644d070cfef1d9ea4589ed564bfaad439f43b598))
* **flow:** Split PausePoint into needs-input and ready-for-review kinds ([#435](https://github.com/aer-works/baton/issues/435)) ([82a9d95](https://github.com/aer-works/baton/commit/82a9d955da69be3bed3778d25415f9f2ec0185e7))
* **flow:** WriterUtcTimestamp on the journal envelope; aer status renders per-step times ([#824](https://github.com/aer-works/baton/issues/824)) ([267baf9](https://github.com/aer-works/baton/commit/267baf92f15e094f3efa7a2d90deb964be1b0d3f))
* **ui,cli:** Running out of plan reads as a state with a reset time on every surface ([#1116](https://github.com/aer-works/baton/issues/1116)) ([#1123](https://github.com/aer-works/baton/issues/1123)) ([a7ded6a](https://github.com/aer-works/baton/commit/a7ded6a69dfb9c5d6119081bcba1a2e44888ef4f))
* **ui:** One gate is one object seen from three places, and the lock names its holder ([#618](https://github.com/aer-works/baton/issues/618)) ([#988](https://github.com/aer-works/baton/issues/988)) ([3a0f3d7](https://github.com/aer-works/baton/commit/3a0f3d7615e39751b38fe4e077bb8f99217c84f9))
* **ui:** Room turn-host surface  throttle values, live usage, and dormancy visibility ([#994](https://github.com/aer-works/baton/issues/994)) ([#1000](https://github.com/aer-works/baton/issues/1000)) ([8260639](https://github.com/aer-works/baton/commit/8260639be29240f7f5b9a5160a5dd5dbfa1e3d7a))


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
* **daemon,flow:** An exhausted interactive turn settles instead of parking for the vendor's whole reset window ([#1188](https://github.com/aer-works/baton/issues/1188)) ([02c0e63](https://github.com/aer-works/baton/commit/02c0e630c3ec95770d4b1db9fb748ac9a2120236))
* **dialogue,flow,cli,daemon,ui,adapters:** Pin every redirected child stream's decode to UTF-8 ([#466](https://github.com/aer-works/baton/issues/466)) ([#1017](https://github.com/aer-works/baton/issues/1017)) ([3489faf](https://github.com/aer-works/baton/commit/3489faf1b349c9d271c4fdf82c653b5fd22eb29d))
* **dispatch:** Make the dispatch front door runnable end-to-end ([#1082](https://github.com/aer-works/baton/issues/1082), [#1083](https://github.com/aer-works/baton/issues/1083), [#1084](https://github.com/aer-works/baton/issues/1084)) ([#1085](https://github.com/aer-works/baton/issues/1085)) ([93c514e](https://github.com/aer-works/baton/commit/93c514e06b00a8f1ede97d906d538433d604dbf1))
* **flow:** Fail replay loudly on a lost member, and persist enums by name ([#621](https://github.com/aer-works/baton/issues/621)) ([a6d966f](https://github.com/aer-works/baton/commit/a6d966fb660bca8e97738e43f9c2ae36d9ef85eb))
* **flow:** Name the failure AER already detected instead of an unclassified Failed ([#607](https://github.com/aer-works/baton/issues/607)) ([9bbecb6](https://github.com/aer-works/baton/commit/9bbecb6a0da18ca1e4d9b0481caf3a1039d3838f))
* **flow:** Wait out a contended room lock on the operator's resolve path ([#879](https://github.com/aer-works/baton/issues/879)) ([ed79270](https://github.com/aer-works/baton/commit/ed792707cd375ec4e7fa47a9c04becb76694522e))
* **ui:** The workflow-path box never carries a bare template id, and the resume template check fires ([#969](https://github.com/aer-works/baton/issues/969)) ([c6ae9c1](https://github.com/aer-works/baton/commit/c6ae9c176fc3410a88d48b65ad2619b86d8e6134))


### Code Refactoring

* **adapters,cli,daemon,ui:** rename GeminiWorkerAdapter to AgyWorkerAdapter ([#1032](https://github.com/aer-works/baton/issues/1032)) ([#1034](https://github.com/aer-works/baton/issues/1034)) ([2a44e13](https://github.com/aer-works/baton/commit/2a44e1322c4e5054b5240b3cff3a2d1e7ec887f0))
* **core,daemon,ui:** Rename identifiers and spec terms to the two nouns ([#1038](https://github.com/aer-works/baton/issues/1038)) ([8d097af](https://github.com/aer-works/baton/commit/8d097afc11a21840e1d8420215019f239883a502))
* **flow,cli,docs:** Adopt "the ledger" as the journal's user-facing noun (0045) ([#854](https://github.com/aer-works/baton/issues/854)) ([5f04eb8](https://github.com/aer-works/baton/commit/5f04eb84ac60d456db1d58cd8f87e10a735ab736))
</details>

<details><summary>ui-core: 0.20.0</summary>

## [0.20.0](https://github.com/aer-works/baton/compare/ui-core-v0.19.0...ui-core-v0.20.0) (2026-08-26)


### Features

* **adapters,daemon,ui,mobile:** light up the depth mark from the registered model-purpose mapping ([#1341](https://github.com/aer-works/baton/issues/1341)) ([9b17744](https://github.com/aer-works/baton/commit/9b1774424a0618f84d60cbda25838f35b7dcfdca))
* **cli:** Every command prints where each produced output landed ([#777](https://github.com/aer-works/baton/issues/777)) ([0e64c30](https://github.com/aer-works/baton/commit/0e64c306450571f9bc9b94dc60d82f87bdf4157b))
* Conversational permission gate (0022)  engine, adapters, desktop + mobile ([#1087](https://github.com/aer-works/baton/issues/1087)) ([9a9192a](https://github.com/aer-works/baton/commit/9a9192a4c9446ab4c12dea0379b4979d6b729ce6))
* **daemon,ui,mobile:** A message to a dormant room is answered by the product, never dispatched ([#1182](https://github.com/aer-works/baton/issues/1182)) ([7a177c4](https://github.com/aer-works/baton/commit/7a177c4e6e1d71a148a665a99b48949b48ee2ac6))
* **daemon,ui,mobile:** orchestrator is mandatory, visible, and reassignable ([#1317](https://github.com/aer-works/baton/issues/1317)) ([75ed55a](https://github.com/aer-works/baton/commit/75ed55aca1767281208b302b0db3f3dd71b2ce45))
* **daemon,ui:** Switcher orders by last activity, derived from the journal ([#640](https://github.com/aer-works/baton/issues/640)) ([#1009](https://github.com/aer-works/baton/issues/1009)) ([439cb61](https://github.com/aer-works/baton/commit/439cb618c3ef87038faa5c20b3f601305c8b244f))
* **daemon:** Add timestamps to the task list contract ([#416](https://github.com/aer-works/baton/issues/416)) ([439c927](https://github.com/aer-works/baton/commit/439c927a31bf338bb2d8b194adc230f0a54c9d94))
* **design,ui,test:** The interaction-state register, and the end of the status fall-through class ([#977](https://github.com/aer-works/baton/issues/977)) ([21738c7](https://github.com/aer-works/baton/commit/21738c7339aa115297114fdf2387fbcb10b80d42))
* **flow,daemon,ui:** Participants  identity, naming, and properties for a room's workers ([#1310](https://github.com/aer-works/baton/issues/1310)) ([7f8306d](https://github.com/aer-works/baton/commit/7f8306d55e51f006170a6baad8924063ee5e3da9))
* **flow,ui,mobile:** Permission answers render as transcript turns on both surfaces (transcript-events phase 1) ([#1145](https://github.com/aer-works/baton/issues/1145)) ([91dc53b](https://github.com/aer-works/baton/commit/91dc53b235a568d01a0995da62f00b0675b7520e))
* **flow:** Bounded room open via a projection checkpoint ([#903](https://github.com/aer-works/baton/issues/903) scope 1a) ([#961](https://github.com/aer-works/baton/issues/961)) ([89f556f](https://github.com/aer-works/baton/commit/89f556fcc2384d22a267c018fb639b98edcf4756))
* **flow:** Engine-level auto-denied-tool signal (FailureClassification.ToolDenied) ([#914](https://github.com/aer-works/baton/issues/914)) ([#1029](https://github.com/aer-works/baton/issues/1029)) ([aa9480d](https://github.com/aer-works/baton/commit/aa9480d583df4e9f765fc91e435d78d94c3ff5e8))
* **flow:** Split PausePoint into needs-input and ready-for-review kinds ([#435](https://github.com/aer-works/baton/issues/435)) ([82a9d95](https://github.com/aer-works/baton/commit/82a9d955da69be3bed3778d25415f9f2ec0185e7))
* **flow:** WriterUtcTimestamp on the journal envelope; aer status renders per-step times ([#824](https://github.com/aer-works/baton/issues/824)) ([267baf9](https://github.com/aer-works/baton/commit/267baf92f15e094f3efa7a2d90deb964be1b0d3f))
* **mcp:** aer yield via AER's first MCP server host; dialogue sentinel retired ([#827](https://github.com/aer-works/baton/issues/827)) ([7ef1de5](https://github.com/aer-works/baton/commit/7ef1de568a3425bdb84870f1018a6eb1250b90f7))
* **mobile:** Make the switcher the phone landing, with tap-to-open rooms (front-door rebuild, slice 1) ([#1046](https://github.com/aer-works/baton/issues/1046)) ([7946bab](https://github.com/aer-works/baton/commit/7946bab12f7cbd9ef3cdb70f7e1d6343974466cf))
* **mobile:** Rooms list renders StatusMark per status; OutOfPlan joins the token file ([#1136](https://github.com/aer-works/baton/issues/1136)) ([44c25eb](https://github.com/aer-works/baton/commit/44c25ebc212d0f6cc8e84bbb4e4f9a5a97c99aa8))
* **mobile:** Truthful room states on the switcher  reply vs review, waiting-on-you first (J3, slice 2a) ([#1050](https://github.com/aer-works/baton/issues/1050)) ([c089190](https://github.com/aer-works/baton/commit/c089190c00d07e5805ccedbf86005f213efa3c64))
* **ui-core:** The projection carries when a step paused and when its decision was recorded ([#1198](https://github.com/aer-works/baton/issues/1198)) ([1db28fc](https://github.com/aer-works/baton/commit/1db28fc6ccf3154471e3cb9268b1968f197844f8))
* **ui-core:** The transcript knows about the room's decisions  live cards and answered rows ([#1203](https://github.com/aer-works/baton/issues/1203)) ([e3f0942](https://github.com/aer-works/baton/commit/e3f09429c46941ab99709db37aa86d66cf8fec4f))
* **ui,cli:** Running out of plan reads as a state with a reset time on every surface ([#1116](https://github.com/aer-works/baton/issues/1116)) ([#1123](https://github.com/aer-works/baton/issues/1123)) ([a7ded6a](https://github.com/aer-works/baton/commit/a7ded6a69dfb9c5d6119081bcba1a2e44888ef4f))
* **ui,daemon:** addressing  tag chips, a sticky tag, and untagged goes to the orchestrator ([#1329](https://github.com/aer-works/baton/issues/1329)) ([991b8ad](https://github.com/aer-works/baton/commit/991b8ad87e76f1588781d02945f06e9398049bd9))
* **ui,daemon:** Concurrency cap (3 global / 2 per vendor) with WaitingToStart room state ([#1297](https://github.com/aer-works/baton/issues/1297)) ([c64503e](https://github.com/aer-works/baton/commit/c64503e251f89ef1b0afd82f25aeb788bba9c8b4))
* **ui,daemon:** Make the concurrency cap adjustable from Settings ([#1304](https://github.com/aer-works/baton/issues/1304)) ([b4bf34b](https://github.com/aer-works/baton/commit/b4bf34bc445a10a11c2594019c06752ca044f81b))
* **ui,daemon:** Promote WaitingOnLock into the canonical room state machine ([#1301](https://github.com/aer-works/baton/issues/1301)) ([6616a58](https://github.com/aer-works/baton/commit/6616a589268d7d7a05aa64e481045ac64a1cf3ce))
* **ui,daemon:** room files  the versioned, attributed file model, with the desktop Files list ([#1344](https://github.com/aer-works/baton/issues/1344)) ([da25b46](https://github.com/aer-works/baton/commit/da25b46cc204ccc2243fcdb8e2b1647217eb9187))
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
* **ui:** Desktop composer never blocks  messages queue and drain on completion ([#1074](https://github.com/aer-works/baton/issues/1074)) ([#1075](https://github.com/aer-works/baton/issues/1075)) ([da61922](https://github.com/aer-works/baton/commit/da619229d8614b480eaaa84473ec9e23c40de1fe))
* **ui:** Desktop switcher rows and ordering to the daily-driver design (M26) ([#1054](https://github.com/aer-works/baton/issues/1054)) ([cb02bfd](https://github.com/aer-works/baton/commit/cb02bfdaaae7ec73472dff7fd75daaa20c147822))
* **ui:** One gate is one object seen from three places, and the lock names its holder ([#618](https://github.com/aer-works/baton/issues/618)) ([#988](https://github.com/aer-works/baton/issues/988)) ([3a0f3d7](https://github.com/aer-works/baton/commit/3a0f3d7615e39751b38fe4e077bb8f99217c84f9))
* **ui:** Replace the six-destination rail with one switcher shell ([#464](https://github.com/aer-works/baton/issues/464)) ([849c3b8](https://github.com/aer-works/baton/commit/849c3b82205753d84b4dd508615760e5d7f246f0))
* **ui:** Room turn-host surface  throttle values, live usage, and dormancy visibility ([#994](https://github.com/aer-works/baton/issues/994)) ([#1000](https://github.com/aer-works/baton/issues/1000)) ([8260639](https://github.com/aer-works/baton/commit/8260639be29240f7f5b9a5160a5dd5dbfa1e3d7a))
* **ui:** Ship desktop surface for a room's standing permissions ([#1277](https://github.com/aer-works/baton/issues/1277)) ([28f4129](https://github.com/aer-works/baton/commit/28f4129515cc70ff8b109ee051c2b7569285fb3d))
* **ui:** The room header is the room's, not the engine's ([#1196](https://github.com/aer-works/baton/issues/1196) slice 4a) ([#1218](https://github.com/aer-works/baton/issues/1218)) ([5b46c59](https://github.com/aer-works/baton/commit/5b46c5987e3e44a93d90fc79829a89aef011c427))
* **ui:** Three icon-only rail + the inbox as a needs-you filter (M26) ([#1071](https://github.com/aer-works/baton/issues/1071), [#1072](https://github.com/aer-works/baton/issues/1072)) ([#1073](https://github.com/aer-works/baton/issues/1073)) ([5140983](https://github.com/aer-works/baton/commit/51409839973b5434d282528affbc2899d117a5f7))
* **ui:** Truthful room states on the desktop switcher  waiting-on-you first, mark on load, drop the mislabel (J3, slice 2b) ([#1052](https://github.com/aer-works/baton/issues/1052)) ([f220c12](https://github.com/aer-works/baton/commit/f220c12d5285f909e8e3e1503e16cb27aa528256))
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
* **ui:** One state machine again  a tenth state for a room whose process died ([#1220](https://github.com/aer-works/baton/issues/1220)) ([2f8463c](https://github.com/aer-works/baton/commit/2f8463c201a971b58cd607a37f6666ff385389cc))
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
* **ui:** One rendering for a room  retire ShellSection.Task and the workflow-file preview ([#1196](https://github.com/aer-works/baton/issues/1196) slice 5) ([#1225](https://github.com/aer-works/baton/issues/1225)) ([0b8e69b](https://github.com/aer-works/baton/commit/0b8e69bc7ce6da8f16db0f998669e73d7626d6d5))
* **ui:** Push back the view-model predicates that decide state rather than project it ([#980](https://github.com/aer-works/baton/issues/980)) ([6612829](https://github.com/aer-works/baton/commit/6612829f17a8cfd481a7455d7534ac64872e2bb8))
* **ui:** Split TaskSession god file into partial-class files ([#427](https://github.com/aer-works/baton/issues/427)) ([c772587](https://github.com/aer-works/baton/commit/c772587a4d46a9344a030e3004ad0db6ff30dc56))


### Documentation

* Record the M25 design, verify it against both vendors, and consolidate the doc tree ([#473](https://github.com/aer-works/baton/issues/473)) ([a4afade](https://github.com/aer-works/baton/commit/a4afadebd7d239f2da6a49d53ea3f5217e978d6a))
* Retire IMPLEMENTATION_PLAN.md into gated homes and audit the doc surface ([#379](https://github.com/aer-works/baton/issues/379)) ([5a0b2ba](https://github.com/aer-works/baton/commit/5a0b2ba04854245beca447ad1b1611b02b96a461))


### Continuous Integration

* **audit:** Lint user-facing strings for engine vocabulary ([#956](https://github.com/aer-works/baton/issues/956)) ([825c6f7](https://github.com/aer-works/baton/commit/825c6f7d7570bd39896a1d5514e6a81f769e5da4))
* Wire the journey gate into CI, and make the token drift check bidirectional ([#490](https://github.com/aer-works/baton/issues/490)) ([67b7b18](https://github.com/aer-works/baton/commit/67b7b18acd458d9849162b107aba1ae59ee719f3))
</details>

<details><summary>ui: 0.20.0</summary>

## [0.20.0](https://github.com/aer-works/baton/compare/ui-v0.19.0...ui-v0.20.0) (2026-08-26)


### Features

* **adapters,daemon,ui,mobile:** light up the depth mark from the registered model-purpose mapping ([#1341](https://github.com/aer-works/baton/issues/1341)) ([9b17744](https://github.com/aer-works/baton/commit/9b1774424a0618f84d60cbda25838f35b7dcfdca))
* **cli:** Every command prints where each produced output landed ([#777](https://github.com/aer-works/baton/issues/777)) ([0e64c30](https://github.com/aer-works/baton/commit/0e64c306450571f9bc9b94dc60d82f87bdf4157b))
* Conversational permission gate (0022)  engine, adapters, desktop + mobile ([#1087](https://github.com/aer-works/baton/issues/1087)) ([9a9192a](https://github.com/aer-works/baton/commit/9a9192a4c9446ab4c12dea0379b4979d6b729ce6))
* **daemon,ui,mobile:** orchestrator is mandatory, visible, and reassignable ([#1317](https://github.com/aer-works/baton/issues/1317)) ([75ed55a](https://github.com/aer-works/baton/commit/75ed55aca1767281208b302b0db3f3dd71b2ce45))
* **design,ui,test:** The interaction-state register, and the end of the status fall-through class ([#977](https://github.com/aer-works/baton/issues/977)) ([21738c7](https://github.com/aer-works/baton/commit/21738c7339aa115297114fdf2387fbcb10b80d42))
* **flow,daemon,ui:** Participants  identity, naming, and properties for a room's workers ([#1310](https://github.com/aer-works/baton/issues/1310)) ([7f8306d](https://github.com/aer-works/baton/commit/7f8306d55e51f006170a6baad8924063ee5e3da9))
* **flow,ui,mobile:** Permission answers render as transcript turns on both surfaces (transcript-events phase 1) ([#1145](https://github.com/aer-works/baton/issues/1145)) ([91dc53b](https://github.com/aer-works/baton/commit/91dc53b235a568d01a0995da62f00b0675b7520e))
* **flow:** Split PausePoint into needs-input and ready-for-review kinds ([#435](https://github.com/aer-works/baton/issues/435)) ([82a9d95](https://github.com/aer-works/baton/commit/82a9d955da69be3bed3778d25415f9f2ec0185e7))
* **mcp:** aer yield via AER's first MCP server host; dialogue sentinel retired ([#827](https://github.com/aer-works/baton/issues/827)) ([7ef1de5](https://github.com/aer-works/baton/commit/7ef1de568a3425bdb84870f1018a6eb1250b90f7))
* **mobile:** Rooms list renders StatusMark per status; OutOfPlan joins the token file ([#1136](https://github.com/aer-works/baton/issues/1136)) ([44c25eb](https://github.com/aer-works/baton/commit/44c25ebc212d0f6cc8e84bbb4e4f9a5a97c99aa8))
* **ui-core:** The projection carries when a step paused and when its decision was recorded ([#1198](https://github.com/aer-works/baton/issues/1198)) ([1db28fc](https://github.com/aer-works/baton/commit/1db28fc6ccf3154471e3cb9268b1968f197844f8))
* **ui-core:** The transcript knows about the room's decisions  live cards and answered rows ([#1203](https://github.com/aer-works/baton/issues/1203)) ([e3f0942](https://github.com/aer-works/baton/commit/e3f09429c46941ab99709db37aa86d66cf8fec4f))
* **ui,adapters,tools:** Equal-weight permission gate with its checker, and agy stdout quota classification ([#1129](https://github.com/aer-works/baton/issues/1129)) ([7179daf](https://github.com/aer-works/baton/commit/7179dafc7d0177896ee0f57dfd49197287c0cb7d))
* **ui,cli:** Running out of plan reads as a state with a reset time on every surface ([#1116](https://github.com/aer-works/baton/issues/1116)) ([#1123](https://github.com/aer-works/baton/issues/1123)) ([a7ded6a](https://github.com/aer-works/baton/commit/a7ded6a69dfb9c5d6119081bcba1a2e44888ef4f))
* **ui,daemon:** addressing  tag chips, a sticky tag, and untagged goes to the orchestrator ([#1329](https://github.com/aer-works/baton/issues/1329)) ([991b8ad](https://github.com/aer-works/baton/commit/991b8ad87e76f1588781d02945f06e9398049bd9))
* **ui,daemon:** Concurrency cap (3 global / 2 per vendor) with WaitingToStart room state ([#1297](https://github.com/aer-works/baton/issues/1297)) ([c64503e](https://github.com/aer-works/baton/commit/c64503e251f89ef1b0afd82f25aeb788bba9c8b4))
* **ui,daemon:** Make the concurrency cap adjustable from Settings ([#1304](https://github.com/aer-works/baton/issues/1304)) ([b4bf34b](https://github.com/aer-works/baton/commit/b4bf34bc445a10a11c2594019c06752ca044f81b))
* **ui,daemon:** Promote WaitingOnLock into the canonical room state machine ([#1301](https://github.com/aer-works/baton/issues/1301)) ([6616a58](https://github.com/aer-works/baton/commit/6616a589268d7d7a05aa64e481045ac64a1cf3ce))
* **ui,daemon:** room files  the versioned, attributed file model, with the desktop Files list ([#1344](https://github.com/aer-works/baton/issues/1344)) ([da25b46](https://github.com/aer-works/baton/commit/da25b46cc204ccc2243fcdb8e2b1647217eb9187))
* **ui,mobile,adapters,daemon:** depth and effort meters on the workflow-room worker chip ([#1337](https://github.com/aer-works/baton/issues/1337)) ([9c6d0cd](https://github.com/aer-works/baton/commit/9c6d0cdeed09dbb66769b41738a93b884bbd4a0f))
* **ui,mobile,daemon:** Dormancy renders as a transcript turn on both surfaces (transcript-events phase 4) ([#1181](https://github.com/aer-works/baton/issues/1181)) ([256ef2a](https://github.com/aer-works/baton/commit/256ef2a3d86d0818aea7d79a7289d0d525db03f0))
* **ui,mobile:** A failed turn renders as a failure card that offers the fix (transcript-events phase 3) ([#1177](https://github.com/aer-works/baton/issues/1177)) ([bebd86b](https://github.com/aer-works/baton/commit/bebd86b2fbddf3b8f9bba0972c8a20b536f1316b))
* **ui,mobile:** Generate both toolkits' themes from one token file ([#450](https://github.com/aer-works/baton/issues/450)) ([c4666a2](https://github.com/aer-works/baton/commit/c4666a29796bbe0f76d4b7803b09b8835fa4b026))
* **ui,mobile:** Point both apps at the shipped typefaces ([#457](https://github.com/aer-works/baton/issues/457)) ([360cd81](https://github.com/aer-works/baton/commit/360cd8189a1e69f6f4eb278501dc99b3c91c614a))
* **ui,mobile:** Report thinking time after the fact, never as a live counter ([#1295](https://github.com/aer-works/baton/issues/1295)) ([2775a7a](https://github.com/aer-works/baton/commit/2775a7a66d2f18ab41ad65580ea205a9a8345aff))
* **ui,mobile:** Ship Source Sans 3 + JetBrains Mono and give code its own surface ([#454](https://github.com/aer-works/baton/issues/454)) ([f9b742e](https://github.com/aer-works/baton/commit/f9b742eb79ec5be5e30dfa0d277c8a75ada6e26e))
* **ui,mobile:** The open permission gate renders as a transcript turn (transcript-events phase 2) ([#1174](https://github.com/aer-works/baton/issues/1174)) ([01b689e](https://github.com/aer-works/baton/commit/01b689ea230febe19039bfb6efa901e68748147a))
* **ui:** A failure shows what broke, in the room, with the worker that failed there to be asked ([#617](https://github.com/aer-works/baton/issues/617)) ([#982](https://github.com/aer-works/baton/issues/982)) ([15bdc44](https://github.com/aer-works/baton/commit/15bdc44d738222fb4708258d6b2ff5e18f6d9baa))
* **ui:** A room's workflow can be switched off, and stays off ([#1196](https://github.com/aer-works/baton/issues/1196) slice 4b) ([#1221](https://github.com/aer-works/baton/issues/1221)) ([5479768](https://github.com/aer-works/baton/commit/5479768412d538ef9c98beadb4683c6daaaa318b))
* **ui:** A workflow room opens in the transcript, with its shape beside it ([#1210](https://github.com/aer-works/baton/issues/1210)) ([6e1dd53](https://github.com/aer-works/baton/commit/6e1dd53859f52106c383101800e9df349f2eaf75))
* **ui:** Add a Settings surface (Workers, Your phone, Appearance), folding Remote in ([#1068](https://github.com/aer-works/baton/issues/1068)) ([997fe11](https://github.com/aer-works/baton/commit/997fe115a24a67b1aaa716398cf6cca275b885cc))
* **ui:** Adopt the Quiet palette (blue-&gt;teal) and differentiate transcript turns ([#1064](https://github.com/aer-works/baton/issues/1064), [#1065](https://github.com/aer-works/baton/issues/1065)) ([80ad1bc](https://github.com/aer-works/baton/commit/80ad1bc220d6ed655d3537b4cdd6e933984b26ba))
* **ui:** Daily-driver chat header is the room name + worker chip (M26) ([#1058](https://github.com/aer-works/baton/issues/1058)) ([3c8bb9a](https://github.com/aer-works/baton/commit/3c8bb9a380a90dfe552190b697ac3c6f3c0b219d))
* **ui:** Daily-driver composer  "Reply&", Enter sends / Shift+Enter newline (M26) ([#1060](https://github.com/aer-works/baton/issues/1060)) ([ca223a4](https://github.com/aer-works/baton/commit/ca223a4ba3ef2d9426810509e6e77547317aac12))
* **ui:** Desktop composer never blocks  messages queue and drain on completion ([#1074](https://github.com/aer-works/baton/issues/1074)) ([#1075](https://github.com/aer-works/baton/issues/1075)) ([da61922](https://github.com/aer-works/baton/commit/da619229d8614b480eaaa84473ec9e23c40de1fe))
* **ui:** Desktop lands in your work, not the Home dashboard (rooms-as-root, M26) ([#1056](https://github.com/aer-works/baton/issues/1056)) ([16dcc21](https://github.com/aer-works/baton/commit/16dcc21f7e8e38826790776e04e263d3d485ff92))
* **ui:** Desktop switcher rows and ordering to the daily-driver design (M26) ([#1054](https://github.com/aer-works/baton/issues/1054)) ([cb02bfd](https://github.com/aer-works/baton/commit/cb02bfdaaae7ec73472dff7fd75daaa20c147822))
* **ui:** One gate is one object seen from three places, and the lock names its holder ([#618](https://github.com/aer-works/baton/issues/618)) ([#988](https://github.com/aer-works/baton/issues/988)) ([3a0f3d7](https://github.com/aer-works/baton/commit/3a0f3d7615e39751b38fe4e077bb8f99217c84f9))
* **ui:** Render markdown & code in the desktop chat transcript ([#1076](https://github.com/aer-works/baton/issues/1076)) ([#1079](https://github.com/aer-works/baton/issues/1079)) ([363f691](https://github.com/aer-works/baton/commit/363f6919aaa4a8a07bdbed40ec63bf4626993750))
* **ui:** Replace the six-destination rail with one switcher shell ([#464](https://github.com/aer-works/baton/issues/464)) ([849c3b8](https://github.com/aer-works/baton/commit/849c3b82205753d84b4dd508615760e5d7f246f0))
* **ui:** Room turn-host surface  throttle values, live usage, and dormancy visibility ([#994](https://github.com/aer-works/baton/issues/994)) ([#1000](https://github.com/aer-works/baton/issues/1000)) ([8260639](https://github.com/aer-works/baton/commit/8260639be29240f7f5b9a5160a5dd5dbfa1e3d7a))
* **ui:** Ship desktop surface for a room's standing permissions ([#1277](https://github.com/aer-works/baton/issues/1277)) ([28f4129](https://github.com/aer-works/baton/commit/28f4129515cc70ff8b109ee051c2b7569285fb3d))
* **ui:** Switcher "+ New" and remove Home duplicate template button (M26) ([#1063](https://github.com/aer-works/baton/issues/1063)) ([bccfc5b](https://github.com/aer-works/baton/commit/bccfc5bf01916c509a7accf28099c2ca2aa5bfb1))
* **ui:** The room header is the room's, not the engine's ([#1196](https://github.com/aer-works/baton/issues/1196) slice 4a) ([#1218](https://github.com/aer-works/baton/issues/1218)) ([5b46c59](https://github.com/aer-works/baton/commit/5b46c5987e3e44a93d90fc79829a89aef011c427))
* **ui:** Three icon-only rail + the inbox as a needs-you filter (M26) ([#1071](https://github.com/aer-works/baton/issues/1071), [#1072](https://github.com/aer-works/baton/issues/1072)) ([#1073](https://github.com/aer-works/baton/issues/1073)) ([5140983](https://github.com/aer-works/baton/commit/51409839973b5434d282528affbc2899d117a5f7))
* **ui:** Truthful room states on the desktop switcher  waiting-on-you first, mark on load, drop the mislabel (J3, slice 2b) ([#1052](https://github.com/aer-works/baton/issues/1052)) ([f220c12](https://github.com/aer-works/baton/commit/f220c12d5285f909e8e3e1503e16cb27aa528256))


### Bug Fixes

* **cli:** Refuse a resume whose named workflow is a different template ([#652](https://github.com/aer-works/baton/issues/652)) ([bbfd524](https://github.com/aer-works/baton/commit/bbfd524cc752c9b55d11287521c442f7b398ac38))
* **daemon,ui,mobile:** An exhausted interactive turn renders as out-of-plan state, not a failure card (0026 §4) ([#1185](https://github.com/aer-works/baton/issues/1185)) ([7e874c3](https://github.com/aer-works/baton/commit/7e874c30c56a133ed9c760b8c65c7d5ab6d538db))
* **flow:** Name the failure AER already detected instead of an unclassified Failed ([#607](https://github.com/aer-works/baton/issues/607)) ([9bbecb6](https://github.com/aer-works/baton/commit/9bbecb6a0da18ca1e4d9b0481caf3a1039d3838f))
* **ui,adapters:** Refuse to save a permission grant that is inert or that the engine rejects ([#711](https://github.com/aer-works/baton/issues/711)) ([273b7c2](https://github.com/aer-works/baton/commit/273b7c29c932690f3e212eeccd7676014a555ce5))
* **ui,flow:** stream logs are not room files, file rows carry their summary, and a failure banner keeps its actions reachable ([#1347](https://github.com/aer-works/baton/issues/1347)) ([3747fa3](https://github.com/aer-works/baton/commit/3747fa335f9d0f1865cec2f0e7a77d7525bd26fa))
* **ui,mobile:** Complete the status vocabulary and make fill agree across toolkits ([#463](https://github.com/aer-works/baton/issues/463)) ([6dd8c87](https://github.com/aer-works/baton/commit/6dd8c874e18e3b11211b349905f346a7fd144d3a))
* **ui,mobile:** Draw status marks as shapes instead of codepoints ([#460](https://github.com/aer-works/baton/issues/460)) ([8f74855](https://github.com/aer-works/baton/commit/8f7485588c8cd7cd1bc9c2e3ebbf87002d56ee68))
* **ui,mobile:** One app-level guard per surface, so an unexpected error neither kills the app nor vanishes ([#1190](https://github.com/aer-works/baton/issues/1190)) ([765ac43](https://github.com/aer-works/baton/commit/765ac4362b343d6df38100dc5757a8cd416a706e))
* **ui:** A paused step leads with what it produced, not the instructions it was given ([#1195](https://github.com/aer-works/baton/issues/1195)) ([cce3212](https://github.com/aer-works/baton/commit/cce32124d53c8bbba6aa506bd9f524e64cef454b))
* **ui:** Composite status marks, the eye's pupil, and desktop mark reachability ([#1294](https://github.com/aer-works/baton/issues/1294)) ([a6dda6e](https://github.com/aer-works/baton/commit/a6dda6e83ccd8b2111834ae84547c5edc93839fa))
* **ui:** Fable's ruling on [#1279](https://github.com/aer-works/baton/issues/1279)'s Tab-reachability fork  display-only button, Enter is the handoff ([#1284](https://github.com/aer-works/baton/issues/1284)) ([97fb0f4](https://github.com/aer-works/baton/commit/97fb0f4846b343cc6a6ff6a7415b8419aa395001))
* **ui:** Legacy Status.* brushes are generated from the token register ([#1141](https://github.com/aer-works/baton/issues/1141)) ([e6ffe02](https://github.com/aer-works/baton/commit/e6ffe02451d4dc044800e05bb02ce12874cdfc36))
* **ui:** MarkdownRenderer takes its code face from AerFonts.Mono ([#1130](https://github.com/aer-works/baton/issues/1130)) ([37e009c](https://github.com/aer-works/baton/commit/37e009ce12084a7f9b8371301ddc4e5f961444b3))
* **ui:** One state machine again  a tenth state for a room whose process died ([#1220](https://github.com/aer-works/baton/issues/1220)) ([2f8463c](https://github.com/aer-works/baton/commit/2f8463c201a971b58cd607a37f6666ff385389cc))
* **ui:** Refuse to save worker bindings over a live room's register ([#1273](https://github.com/aer-works/baton/issues/1273)) ([8fe1475](https://github.com/aer-works/baton/commit/8fe1475a90224a0cdaab600256e5061bf8aabfc6))
* **ui:** Remote advertises the unreachable address and hides the not-encrypted warning ([#392](https://github.com/aer-works/baton/issues/392)) ([41ec69f](https://github.com/aer-works/baton/commit/41ec69f945a874210f51e5277529d18470070822))
* **ui:** Stop the new-chat adapter combo rendering blank after adapter refresh ([#990](https://github.com/aer-works/baton/issues/990)) ([c2bf1fd](https://github.com/aer-works/baton/commit/c2bf1fdb8cef63019dd4960cffa81fa08ff3048c))
* **ui:** The new-chat vendor picker shows Claude/Gemini, not raw adapter contract keys ([#1285](https://github.com/aer-works/baton/issues/1285)) ([a70e6a0](https://github.com/aer-works/baton/commit/a70e6a0ff38b33c135f995ac9095210126006c27))
* **ui:** The newest artifact-preview request wins, and its test no longer bets on timing ([#873](https://github.com/aer-works/baton/issues/873)) ([e05d94d](https://github.com/aer-works/baton/commit/e05d94d1a845c6ff55487bc717e952cf158bc6b9))
* **ui:** The queue holds and a typed send joins it during an open permission gate - explicit, not an accident of IsSending ([#1170](https://github.com/aer-works/baton/issues/1170)) ([582aaef](https://github.com/aer-works/baton/commit/582aaef8771ef8b1b1f9a8bd1f623ec1138016a2))
* **ui:** The room drill-in's Conversation tab no longer shows a stale prior step's rendered exchange ([#1289](https://github.com/aer-works/baton/issues/1289)) ([ca0a3f1](https://github.com/aer-works/baton/commit/ca0a3f1fc12c8dbc2d09ae3bd2ee586ef84eb472))
* **ui:** The room header spans the transcript and the shape panel, so Stop cannot be clipped away ([#1248](https://github.com/aer-works/baton/issues/1248)) ([a63a902](https://github.com/aer-works/baton/commit/a63a90262eaf8518537fdf9cb83ab7507cd56eb1))
* **ui:** The switcher can receive keyboard focus, so arrow-key traversal is finally reachable ([#1280](https://github.com/aer-works/baton/issues/1280)) ([f8e1a94](https://github.com/aer-works/baton/commit/f8e1a944a9d1609e41b40b1892044cd6ce6ebaf4))
* **ui:** The version-skew check compares versions that can actually be equal ([#1263](https://github.com/aer-works/baton/issues/1263)) ([7182e73](https://github.com/aer-works/baton/commit/7182e7375e2b180f5fffcceecc02345e7c3f1f92))
* **ui:** The workflow-path box never carries a bare template id, and the resume template check fires ([#969](https://github.com/aer-works/baton/issues/969)) ([c6ae9c1](https://github.com/aer-works/baton/commit/c6ae9c176fc3410a88d48b65ad2619b86d8e6134))


### Code Refactoring

* **adapters,cli,daemon,ui:** rename GeminiWorkerAdapter to AgyWorkerAdapter ([#1032](https://github.com/aer-works/baton/issues/1032)) ([#1034](https://github.com/aer-works/baton/issues/1034)) ([2a44e13](https://github.com/aer-works/baton/commit/2a44e1322c4e5054b5240b3cff3a2d1e7ec887f0))
* **core,daemon,ui:** Rename identifiers and spec terms to the two nouns ([#1038](https://github.com/aer-works/baton/issues/1038)) ([8d097af](https://github.com/aer-works/baton/commit/8d097afc11a21840e1d8420215019f239883a502))
* **core,daemon,ui:** Unify sessions and tasks into one storage root, with migration ([#444](https://github.com/aer-works/baton/issues/444)) ([04a11b8](https://github.com/aer-works/baton/commit/04a11b8ce095f37f4ff4bc9eba137461016dbbc0))
* **core:** Introduce AerPaths so the storage root has a single seam ([#362](https://github.com/aer-works/baton/issues/362)) ([4b81e57](https://github.com/aer-works/baton/commit/4b81e573b73a839e9dd713acb662b0d1bfb357e9))
* **dialogue:** StopSentinel fully retired from config and every authoring surface ([#830](https://github.com/aer-works/baton/issues/830)) ([71e112d](https://github.com/aer-works/baton/commit/71e112d69db051aa494a047b18c6551d08e191d0))
* **ui,mobile,docs:** Rename the product to Baton on every user-facing surface (0045) ([#865](https://github.com/aer-works/baton/issues/865)) ([fc8cae5](https://github.com/aer-works/baton/commit/fc8cae5fd3d0c0e36b51a94942f2ea36b6b27add))
* **ui:** One rendering for a room  retire ShellSection.Task and the workflow-file preview ([#1196](https://github.com/aer-works/baton/issues/1196) slice 5) ([#1225](https://github.com/aer-works/baton/issues/1225)) ([0b8e69b](https://github.com/aer-works/baton/commit/0b8e69bc7ce6da8f16db0f998669e73d7626d6d5))
* **ui:** Push back the view-model predicates that decide state rather than project it ([#980](https://github.com/aer-works/baton/issues/980)) ([6612829](https://github.com/aer-works/baton/commit/6612829f17a8cfd481a7455d7534ac64872e2bb8))
* **ui:** Status brushes speak the register's vocabulary; supersede edge and OutOfPlan stop borrowing ([#1143](https://github.com/aer-works/baton/issues/1143)) ([8fabab2](https://github.com/aer-works/baton/commit/8fabab2c00a69573b690290f304d09e13ebb51ea))


### Documentation

* Record the M25 design, verify it against both vendors, and consolidate the doc tree ([#473](https://github.com/aer-works/baton/issues/473)) ([a4afade](https://github.com/aer-works/baton/commit/a4afadebd7d239f2da6a49d53ea3f5217e978d6a))


### Continuous Integration

* **audit:** Lint user-facing strings for engine vocabulary ([#956](https://github.com/aer-works/baton/issues/956)) ([825c6f7](https://github.com/aer-works/baton/commit/825c6f7d7570bd39896a1d5514e6a81f769e5da4))
* Wire the journey gate into CI, and make the token drift check bidirectional ([#490](https://github.com/aer-works/baton/issues/490)) ([67b7b18](https://github.com/aer-works/baton/commit/67b7b18acd458d9849162b107aba1ae59ee719f3))


### Tests

* Add the journey-test harness with its first driveable legs ([#372](https://github.com/aer-works/baton/issues/372)) ([3c30827](https://github.com/aer-works/baton/commit/3c308276ced96654a4326cb948547fc2821f9a35))


### Miscellaneous

* **ui:** Remove a triplicated comment block in MainWindow.axaml.cs ([#1288](https://github.com/aer-works/baton/issues/1288)) ([136d649](https://github.com/aer-works/baton/commit/136d6493fbdaa9738e91ed98b6a7141513176a0e))
</details>

<details><summary>daemon: 0.20.0</summary>

## [0.20.0](https://github.com/aer-works/baton/compare/daemon-v0.19.0...daemon-v0.20.0) (2026-08-26)


### Features

* **adapters,daemon,ui,mobile:** light up the depth mark from the registered model-purpose mapping ([#1341](https://github.com/aer-works/baton/issues/1341)) ([9b17744](https://github.com/aer-works/baton/commit/9b1774424a0618f84d60cbda25838f35b7dcfdca))
* **adapters,daemon:** A standing permission can be taken back ([#1250](https://github.com/aer-works/baton/issues/1250)) ([99c06b2](https://github.com/aer-works/baton/commit/99c06b251de81f5b6ac7a3d4533ae8e0ae312483))
* **adapters,daemon:** The orchestrator occupant  role template and the L1 turn contract ([#996](https://github.com/aer-works/baton/issues/996)) ([3421f37](https://github.com/aer-works/baton/commit/3421f37b09403b8b97abca3dc20d71ed809a38eb))
* **cli:** Every command prints where each produced output landed ([#777](https://github.com/aer-works/baton/issues/777)) ([0e64c30](https://github.com/aer-works/baton/commit/0e64c306450571f9bc9b94dc60d82f87bdf4157b))
* Conversational permission gate (0022)  engine, adapters, desktop + mobile ([#1087](https://github.com/aer-works/baton/issues/1087)) ([9a9192a](https://github.com/aer-works/baton/commit/9a9192a4c9446ab4c12dea0379b4979d6b729ce6))
* **daemon,flow,adapters:** Memory-proposal captures land per-execution with room attribution by construction ([#853](https://github.com/aer-works/baton/issues/853)) ([d4e7ca9](https://github.com/aer-works/baton/commit/d4e7ca96817bcf6aa1a9139af8db808164fd336a))
* **daemon,flow:** Periodic room-retention sweep wiring journal compaction ([#1040](https://github.com/aer-works/baton/issues/1040)) ([559451c](https://github.com/aer-works/baton/commit/559451c70fdc4a8c83bef4c90b2767565dc862c5))
* **daemon,flow:** The resident room turn host  wake-consuming loop, host throttles, and the failure breaker ([#995](https://github.com/aer-works/baton/issues/995)) ([9c1fc0f](https://github.com/aer-works/baton/commit/9c1fc0fe1b8745108a3994b1c2751ba9887ec427))
* **daemon,ui,mobile:** A message to a dormant room is answered by the product, never dispatched ([#1182](https://github.com/aer-works/baton/issues/1182)) ([7a177c4](https://github.com/aer-works/baton/commit/7a177c4e6e1d71a148a665a99b48949b48ee2ac6))
* **daemon,ui,mobile:** orchestrator is mandatory, visible, and reassignable ([#1317](https://github.com/aer-works/baton/issues/1317)) ([75ed55a](https://github.com/aer-works/baton/commit/75ed55aca1767281208b302b0db3f3dd71b2ce45))
* **daemon,ui:** Switcher orders by last activity, derived from the journal ([#640](https://github.com/aer-works/baton/issues/640)) ([#1009](https://github.com/aer-works/baton/issues/1009)) ([439cb61](https://github.com/aer-works/baton/commit/439cb618c3ef87038faa5c20b3f601305c8b244f))
* **daemon:** A room's standing permissions can be read back ([#1255](https://github.com/aer-works/baton/issues/1255)) ([dbb3177](https://github.com/aer-works/baton/commit/dbb317786dd517bcda1d33b07b24dd5573f92546))
* **daemon:** Add timestamps to the task list contract ([#416](https://github.com/aer-works/baton/issues/416)) ([439c927](https://github.com/aer-works/baton/commit/439c927a31bf338bb2d8b194adc230f0a54c9d94))
* **daemon:** Per-directory dispatch lock on the task endpoints; dispatch failures durably recorded ([#831](https://github.com/aer-works/baton/issues/831)) ([7c0d5c2](https://github.com/aer-works/baton/commit/7c0d5c22db7fe79370a00f5a3ef8e02ca567d864))
* **daemon:** Prune terminal runs' artifacts via the retention sweep, with a grace window ([#1041](https://github.com/aer-works/baton/issues/1041)) ([3633782](https://github.com/aer-works/baton/commit/363378292445f0d91413261b7b52bcc1262707b9))
* **daemon:** RoomWakeBridge derives wakes from journals, never stores them ([#799](https://github.com/aer-works/baton/issues/799)) ([#819](https://github.com/aer-works/baton/issues/819)) ([94b6525](https://github.com/aer-works/baton/commit/94b6525e2608f5d251e27195c14490189559db68))
* **flow,daemon,ui:** Participants  identity, naming, and properties for a room's workers ([#1310](https://github.com/aer-works/baton/issues/1310)) ([7f8306d](https://github.com/aer-works/baton/commit/7f8306d55e51f006170a6baad8924063ee5e3da9))
* **flow,daemon:** Held-work escalation subjects, and occupant references must resolve ([#1001](https://github.com/aer-works/baton/issues/1001)) ([#1002](https://github.com/aer-works/baton/issues/1002)) ([d318c2b](https://github.com/aer-works/baton/commit/d318c2b982a309ab350ca792ef74cb8ea929ae92))
* **flow,daemon:** Held-work resolve surface applies approved memory proposals ([#859](https://github.com/aer-works/baton/issues/859)) ([de46b9d](https://github.com/aer-works/baton/commit/de46b9d89225e56e89800d0e4a41796ac6ed0190))
* **flow,daemon:** Journal a standing permission's revocation ([#1278](https://github.com/aer-works/baton/issues/1278)) ([1298f00](https://github.com/aer-works/baton/commit/1298f002a5f8a80df11fc605e4d407f7c6a8d10b))
* **mobile,daemon:** A stopped room on the phone says so, and its answered decisions stay readable ([#1247](https://github.com/aer-works/baton/issues/1247)) ([046d0f9](https://github.com/aer-works/baton/commit/046d0f95ebe6428c054c03e70b3c346dc08b07aa))
* **ui,daemon:** addressing  tag chips, a sticky tag, and untagged goes to the orchestrator ([#1329](https://github.com/aer-works/baton/issues/1329)) ([991b8ad](https://github.com/aer-works/baton/commit/991b8ad87e76f1588781d02945f06e9398049bd9))
* **ui,daemon:** Concurrency cap (3 global / 2 per vendor) with WaitingToStart room state ([#1297](https://github.com/aer-works/baton/issues/1297)) ([c64503e](https://github.com/aer-works/baton/commit/c64503e251f89ef1b0afd82f25aeb788bba9c8b4))
* **ui,daemon:** Make the concurrency cap adjustable from Settings ([#1304](https://github.com/aer-works/baton/issues/1304)) ([b4bf34b](https://github.com/aer-works/baton/commit/b4bf34bc445a10a11c2594019c06752ca044f81b))
* **ui,mobile,adapters,daemon:** depth and effort meters on the workflow-room worker chip ([#1337](https://github.com/aer-works/baton/issues/1337)) ([9c6d0cd](https://github.com/aer-works/baton/commit/9c6d0cdeed09dbb66769b41738a93b884bbd4a0f))
* **ui,mobile,daemon:** Dormancy renders as a transcript turn on both surfaces (transcript-events phase 4) ([#1181](https://github.com/aer-works/baton/issues/1181)) ([256ef2a](https://github.com/aer-works/baton/commit/256ef2a3d86d0818aea7d79a7289d0d525db03f0))
* **ui:** A room's workflow can be switched off, and stays off ([#1196](https://github.com/aer-works/baton/issues/1196) slice 4b) ([#1221](https://github.com/aer-works/baton/issues/1221)) ([5479768](https://github.com/aer-works/baton/commit/5479768412d538ef9c98beadb4683c6daaaa318b))
* **ui:** Room turn-host surface  throttle values, live usage, and dormancy visibility ([#994](https://github.com/aer-works/baton/issues/994)) ([#1000](https://github.com/aer-works/baton/issues/1000)) ([8260639](https://github.com/aer-works/baton/commit/8260639be29240f7f5b9a5160a5dd5dbfa1e3d7a))


### Bug Fixes

* **adapters:** Remove --bare, which suppresses the gate 0029 requires ([#551](https://github.com/aer-works/baton/issues/551)) ([1ded959](https://github.com/aer-works/baton/commit/1ded959ad762adcc64973154e624fd3ef2049877))
* **adapters:** Write a room's bindings register atomically ([#1265](https://github.com/aer-works/baton/issues/1265)) ([fb59244](https://github.com/aer-works/baton/commit/fb59244c4004ad9174aa08e25cb8c94cf3c169b9))
* **daemon,adapters:** Stop a concurrent read failing a session metadata write ([#353](https://github.com/aer-works/baton/issues/353)) ([1cb2265](https://github.com/aer-works/baton/commit/1cb2265f6dc03b9fbb62869f381d362125416514))
* **daemon,flow:** A decision resolves the room's own workers, not whichever room ran last ([#1244](https://github.com/aer-works/baton/issues/1244)) ([fca14f2](https://github.com/aer-works/baton/commit/fca14f2c108da1f22cb5f67869918197818badee))
* **daemon,flow:** An exhausted interactive turn settles instead of parking for the vendor's whole reset window ([#1188](https://github.com/aer-works/baton/issues/1188)) ([02c0e63](https://github.com/aer-works/baton/commit/02c0e630c3ec95770d4b1db9fb748ac9a2120236))
* **daemon,flow:** Comma-proof agy id scrape; snapshot readers stop blocking the persist rename ([#837](https://github.com/aer-works/baton/issues/837), [#842](https://github.com/aer-works/baton/issues/842)) ([#844](https://github.com/aer-works/baton/issues/844)) ([c9609ee](https://github.com/aer-works/baton/commit/c9609eeada0e89130662bcd88678bb378ab7d53c))
* **daemon,mcp,adapters:** A pending runtime permission dies with its turn  timeout, turn end, cancel, restart ([#1098](https://github.com/aer-works/baton/issues/1098), [#1100](https://github.com/aer-works/baton/issues/1100), [#1101](https://github.com/aer-works/baton/issues/1101)) ([#1102](https://github.com/aer-works/baton/issues/1102)) ([ecfd7dc](https://github.com/aer-works/baton/commit/ecfd7dc10b0413789ed151d923c97e74f82b16ed))
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
* **flow,daemon:** Room-event appends take their own lock  a mid-turn ask can finally journal ([#1109](https://github.com/aer-works/baton/issues/1109)) ([#1111](https://github.com/aer-works/baton/issues/1111)) ([755e445](https://github.com/aer-works/baton/commit/755e44540c4cd44c2e7852dcbe54fd75efea1488))
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
</details>

---
This PR was generated with [Release Please](https://github.com/googleapis/release-please). See [documentation](https://github.com/googleapis/release-please#release-please).