# Changelog

## [0.3.0](https://github.com/aer-works/baton/compare/mobile-v0.2.0...mobile-v0.3.0) (2026-08-26)


### Features

* **adapters,daemon,ui,mobile:** light up the depth mark from the registered model-purpose mapping ([#1341](https://github.com/aer-works/baton/issues/1341)) ([9b17744](https://github.com/aer-works/baton/commit/9b1774424a0618f84d60cbda25838f35b7dcfdca))
* Conversational permission gate (0022) — engine, adapters, desktop + mobile ([#1087](https://github.com/aer-works/baton/issues/1087)) ([9a9192a](https://github.com/aer-works/baton/commit/9a9192a4c9446ab4c12dea0379b4979d6b729ce6))
* **daemon,ui,mobile:** A message to a dormant room is answered by the product, never dispatched ([#1182](https://github.com/aer-works/baton/issues/1182)) ([7a177c4](https://github.com/aer-works/baton/commit/7a177c4e6e1d71a148a665a99b48949b48ee2ac6))
* **daemon,ui,mobile:** orchestrator is mandatory, visible, and reassignable ([#1317](https://github.com/aer-works/baton/issues/1317)) ([75ed55a](https://github.com/aer-works/baton/commit/75ed55aca1767281208b302b0db3f3dd71b2ce45))
* **daemon,ui:** Switcher orders by last activity, derived from the journal ([#640](https://github.com/aer-works/baton/issues/640)) ([#1009](https://github.com/aer-works/baton/issues/1009)) ([439cb61](https://github.com/aer-works/baton/commit/439cb618c3ef87038faa5c20b3f601305c8b244f))
* **flow,daemon,ui:** Participants — identity, naming, and properties for a room's workers ([#1310](https://github.com/aer-works/baton/issues/1310)) ([7f8306d](https://github.com/aer-works/baton/commit/7f8306d55e51f006170a6baad8924063ee5e3da9))
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
* **mobile:** The phone's nav — Rooms, Needs you, Settings, where Needs you is a filter ([#1235](https://github.com/aer-works/baton/issues/1235)) ([19f23b1](https://github.com/aer-works/baton/commit/19f23b18418d5f27175052c52362c7566982e82b))
* **mobile:** Truthful room states on the switcher — reply vs review, waiting-on-you first (J3, slice 2a) ([#1050](https://github.com/aer-works/baton/issues/1050)) ([c089190](https://github.com/aer-works/baton/commit/c089190c00d07e5805ccedbf86005f213efa3c64))
* **ui-core:** The projection carries when a step paused and when its decision was recorded ([#1198](https://github.com/aer-works/baton/issues/1198)) ([1db28fc](https://github.com/aer-works/baton/commit/1db28fc6ccf3154471e3cb9268b1968f197844f8))
* **ui,adapters,tools:** Equal-weight permission gate with its checker, and agy stdout quota classification ([#1129](https://github.com/aer-works/baton/issues/1129)) ([7179daf](https://github.com/aer-works/baton/commit/7179dafc7d0177896ee0f57dfd49197287c0cb7d))
* **ui,cli:** Running out of plan reads as a state with a reset time on every surface ([#1116](https://github.com/aer-works/baton/issues/1116)) ([#1123](https://github.com/aer-works/baton/issues/1123)) ([a7ded6a](https://github.com/aer-works/baton/commit/a7ded6a69dfb9c5d6119081bcba1a2e44888ef4f))
* **ui,daemon:** addressing — tag chips, a sticky tag, and untagged goes to the orchestrator ([#1329](https://github.com/aer-works/baton/issues/1329)) ([991b8ad](https://github.com/aer-works/baton/commit/991b8ad87e76f1588781d02945f06e9398049bd9))
* **ui,daemon:** Concurrency cap (3 global / 2 per vendor) with WaitingToStart room state ([#1297](https://github.com/aer-works/baton/issues/1297)) ([c64503e](https://github.com/aer-works/baton/commit/c64503e251f89ef1b0afd82f25aeb788bba9c8b4))
* **ui,daemon:** Promote WaitingOnLock into the canonical room state machine ([#1301](https://github.com/aer-works/baton/issues/1301)) ([6616a58](https://github.com/aer-works/baton/commit/6616a589268d7d7a05aa64e481045ac64a1cf3ce))
* **ui,daemon:** room files — the versioned, attributed file model, with the desktop Files list ([#1344](https://github.com/aer-works/baton/issues/1344)) ([da25b46](https://github.com/aer-works/baton/commit/da25b46cc204ccc2243fcdb8e2b1647217eb9187))
* **ui,mobile,adapters,daemon:** depth and effort meters on the workflow-room worker chip ([#1337](https://github.com/aer-works/baton/issues/1337)) ([9c6d0cd](https://github.com/aer-works/baton/commit/9c6d0cdeed09dbb66769b41738a93b884bbd4a0f))
* **ui,mobile,daemon:** Dormancy renders as a transcript turn on both surfaces (transcript-events phase 4) ([#1181](https://github.com/aer-works/baton/issues/1181)) ([256ef2a](https://github.com/aer-works/baton/commit/256ef2a3d86d0818aea7d79a7289d0d525db03f0))
* **ui,mobile:** A failed turn renders as a failure card that offers the fix (transcript-events phase 3) ([#1177](https://github.com/aer-works/baton/issues/1177)) ([bebd86b](https://github.com/aer-works/baton/commit/bebd86b2fbddf3b8f9bba0972c8a20b536f1316b))
* **ui,mobile:** Generate both toolkits' themes from one token file ([#450](https://github.com/aer-works/baton/issues/450)) ([c4666a2](https://github.com/aer-works/baton/commit/c4666a29796bbe0f76d4b7803b09b8835fa4b026))
* **ui,mobile:** Point both apps at the shipped typefaces ([#457](https://github.com/aer-works/baton/issues/457)) ([360cd81](https://github.com/aer-works/baton/commit/360cd8189a1e69f6f4eb278501dc99b3c91c614a))
* **ui,mobile:** Report thinking time after the fact, never as a live counter ([#1295](https://github.com/aer-works/baton/issues/1295)) ([2775a7a](https://github.com/aer-works/baton/commit/2775a7a66d2f18ab41ad65580ea205a9a8345aff))
* **ui,mobile:** Room list sorts by 0018's four attention bands on both surfaces ([#1134](https://github.com/aer-works/baton/issues/1134)) ([4290b24](https://github.com/aer-works/baton/commit/4290b2478d26d6fff25d97e1380b1dc3b2411997))
* **ui,mobile:** Ship Source Sans 3 + JetBrains Mono and give code its own surface ([#454](https://github.com/aer-works/baton/issues/454)) ([f9b742e](https://github.com/aer-works/baton/commit/f9b742eb79ec5be5e30dfa0d277c8a75ada6e26e))
* **ui,mobile:** The open permission gate renders as a transcript turn (transcript-events phase 2) ([#1174](https://github.com/aer-works/baton/issues/1174)) ([01b689e](https://github.com/aer-works/baton/commit/01b689ea230febe19039bfb6efa901e68748147a))
* **ui,mobile:** The phone chip shows the participant's model — [#391](https://github.com/aer-works/baton/issues/391)'s session-room remainder ([#1312](https://github.com/aer-works/baton/issues/1312)) ([20f8c3b](https://github.com/aer-works/baton/commit/20f8c3bd1e624d3f4f6ec3a85aede1cd857fcc8e))
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
* **ui:** One state machine again — a tenth state for a room whose process died ([#1220](https://github.com/aer-works/baton/issues/1220)) ([2f8463c](https://github.com/aer-works/baton/commit/2f8463c201a971b58cd607a37f6666ff385389cc))


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

## [0.2.0](https://github.com/aer-works/aer-flow/compare/mobile-v0.1.0...mobile-v0.2.0) (2026-07-22)


### Features

* **daemon,adapters,ui:** Milestone 24 — Interactive Sessions & Unified Task Creation ([#276](https://github.com/aer-works/aer-flow/issues/276)) ([f7ab4fa](https://github.com/aer-works/aer-flow/commit/f7ab4fad253e730735631d158d92f97b2fc22d03))
* **ui,mobile:** Add a direct "start new chat" entry point ([#301](https://github.com/aer-works/aer-flow/issues/301)) ([03c2c62](https://github.com/aer-works/aer-flow/commit/03c2c62b8178878664083f015877a1d68637d593))
* **ui,mobile:** Bulk select for archive/delete in the Tasks view ([#294](https://github.com/aer-works/aer-flow/issues/294)) ([263bc4d](https://github.com/aer-works/aer-flow/commit/263bc4d3917a2a1a50e8b1571c8b642c54c6faba))


### Bug Fixes

* **daemon,ui,mobile:** Wire command picker to real actions and show active mode ([#298](https://github.com/aer-works/aer-flow/issues/298)) ([3ed5d2d](https://github.com/aer-works/aer-flow/commit/3ed5d2d0b4841bb84c5d43c6e7b58bc3ef2398d8))
* **mobile:** Re-ensure tsnet is up before every dial and on app resume ([#293](https://github.com/aer-works/aer-flow/issues/293)) ([8b08e3d](https://github.com/aer-works/aer-flow/commit/8b08e3dc4d1d5ce9785113c8e9d9ce265312296f))


### Miscellaneous

* **mobile:** rebase Aer.Mobile version onto the 0.x line ([#281](https://github.com/aer-works/aer-flow/issues/281)) ([b08b56e](https://github.com/aer-works/aer-flow/commit/b08b56ee4af99b01c781bd273acedff7a0c4658c))

## [0.1.0](https://github.com/aer-works/aer-flow/compare/mobile-v0.0.1...mobile-v0.1.0) (2026-07-20)


### Features

* **templates:** implement built-in workflow template library ([#250](https://github.com/aer-works/aer-flow/issues/250)) ([#251](https://github.com/aer-works/aer-flow/issues/251)) ([2ca7490](https://github.com/aer-works/aer-flow/commit/2ca74902f829e24a6fe412030db373f78e473f17))

## 0.0.1 (2026-07-19)


### Features

* **brand:** Refresh the AER mark and share it across desktop/mobile ([#239](https://github.com/aer-works/aer-flow/issues/239)) ([11a56e7](https://github.com/aer-works/aer-flow/commit/11a56e7822bda554ab43f5786f5705031a651720))
* **mobile:** Aer.Mobile Flutter client for remote decision-inbox control ([#233](https://github.com/aer-works/aer-flow/issues/233)) ([42d6a7e](https://github.com/aer-works/aer-flow/commit/42d6a7e0caedd4d1fc95d7d2564798c0b5057e9e))
* **ui,mobile:** M21 Phase 3 — Desktop Pairing UX ([#235](https://github.com/aer-works/aer-flow/issues/235)) ([946743c](https://github.com/aer-works/aer-flow/commit/946743cd88176e05075f8c9264885e9ca830b57f))


### Tests

* **flow:** M8 Phase 4 — Fan-out/fan-in + retry end-to-end integration tests ([#56](https://github.com/aer-works/aer-flow/issues/56)) ([15d5adb](https://github.com/aer-works/aer-flow/commit/15d5adb77ff0c508fee275746009cc9f0d1de9de))
