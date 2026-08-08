# Changelog

## [0.20.0](https://github.com/aer-works/baton/compare/ui-v0.19.0...ui-v0.20.0) (2026-08-08)


### Features

* **cli:** Every command prints where each produced output landed ([#777](https://github.com/aer-works/baton/issues/777)) ([0e64c30](https://github.com/aer-works/baton/commit/0e64c306450571f9bc9b94dc60d82f87bdf4157b))
* **design,ui,test:** The interaction-state register, and the end of the status fall-through class ([#977](https://github.com/aer-works/baton/issues/977)) ([21738c7](https://github.com/aer-works/baton/commit/21738c7339aa115297114fdf2387fbcb10b80d42))
* **flow:** Split PausePoint into needs-input and ready-for-review kinds ([#435](https://github.com/aer-works/baton/issues/435)) ([82a9d95](https://github.com/aer-works/baton/commit/82a9d955da69be3bed3778d25415f9f2ec0185e7))
* **mcp:** aer yield via AER's first MCP server host; dialogue sentinel retired ([#827](https://github.com/aer-works/baton/issues/827)) ([7ef1de5](https://github.com/aer-works/baton/commit/7ef1de568a3425bdb84870f1018a6eb1250b90f7))
* **ui,mobile:** Generate both toolkits' themes from one token file ([#450](https://github.com/aer-works/baton/issues/450)) ([c4666a2](https://github.com/aer-works/baton/commit/c4666a29796bbe0f76d4b7803b09b8835fa4b026))
* **ui,mobile:** Point both apps at the shipped typefaces ([#457](https://github.com/aer-works/baton/issues/457)) ([360cd81](https://github.com/aer-works/baton/commit/360cd8189a1e69f6f4eb278501dc99b3c91c614a))
* **ui,mobile:** Ship Source Sans 3 + JetBrains Mono and give code its own surface ([#454](https://github.com/aer-works/baton/issues/454)) ([f9b742e](https://github.com/aer-works/baton/commit/f9b742eb79ec5be5e30dfa0d277c8a75ada6e26e))
* **ui:** A failure shows what broke, in the room, with the worker that failed there to be asked ([#617](https://github.com/aer-works/baton/issues/617)) ([#982](https://github.com/aer-works/baton/issues/982)) ([15bdc44](https://github.com/aer-works/baton/commit/15bdc44d738222fb4708258d6b2ff5e18f6d9baa))
* **ui:** Add a Settings surface (Workers, Your phone, Appearance), folding Remote in ([#1068](https://github.com/aer-works/baton/issues/1068)) ([997fe11](https://github.com/aer-works/baton/commit/997fe115a24a67b1aaa716398cf6cca275b885cc))
* **ui:** Adopt the Quiet palette (blue-&gt;teal) and differentiate transcript turns ([#1064](https://github.com/aer-works/baton/issues/1064), [#1065](https://github.com/aer-works/baton/issues/1065)) ([80ad1bc](https://github.com/aer-works/baton/commit/80ad1bc220d6ed655d3537b4cdd6e933984b26ba))
* **ui:** Daily-driver chat header is the room name + worker chip (M26) ([#1058](https://github.com/aer-works/baton/issues/1058)) ([3c8bb9a](https://github.com/aer-works/baton/commit/3c8bb9a380a90dfe552190b697ac3c6f3c0b219d))
* **ui:** Daily-driver composer — "Reply…", Enter sends / Shift+Enter newline (M26) ([#1060](https://github.com/aer-works/baton/issues/1060)) ([ca223a4](https://github.com/aer-works/baton/commit/ca223a4ba3ef2d9426810509e6e77547317aac12))
* **ui:** Desktop lands in your work, not the Home dashboard (rooms-as-root, M26) ([#1056](https://github.com/aer-works/baton/issues/1056)) ([16dcc21](https://github.com/aer-works/baton/commit/16dcc21f7e8e38826790776e04e263d3d485ff92))
* **ui:** Desktop switcher rows and ordering to the daily-driver design (M26) ([#1054](https://github.com/aer-works/baton/issues/1054)) ([cb02bfd](https://github.com/aer-works/baton/commit/cb02bfdaaae7ec73472dff7fd75daaa20c147822))
* **ui:** One gate is one object seen from three places, and the lock names its holder ([#618](https://github.com/aer-works/baton/issues/618)) ([#988](https://github.com/aer-works/baton/issues/988)) ([3a0f3d7](https://github.com/aer-works/baton/commit/3a0f3d7615e39751b38fe4e077bb8f99217c84f9))
* **ui:** Replace the six-destination rail with one switcher shell ([#464](https://github.com/aer-works/baton/issues/464)) ([849c3b8](https://github.com/aer-works/baton/commit/849c3b82205753d84b4dd508615760e5d7f246f0))
* **ui:** Room turn-host surface — throttle values, live usage, and dormancy visibility ([#994](https://github.com/aer-works/baton/issues/994)) ([#1000](https://github.com/aer-works/baton/issues/1000)) ([8260639](https://github.com/aer-works/baton/commit/8260639be29240f7f5b9a5160a5dd5dbfa1e3d7a))
* **ui:** Switcher "+ New" and remove Home duplicate template button (M26) ([#1063](https://github.com/aer-works/baton/issues/1063)) ([bccfc5b](https://github.com/aer-works/baton/commit/bccfc5bf01916c509a7accf28099c2ca2aa5bfb1))
* **ui:** Three icon-only rail + the inbox as a needs-you filter (M26) ([#1071](https://github.com/aer-works/baton/issues/1071), [#1072](https://github.com/aer-works/baton/issues/1072)) ([#1073](https://github.com/aer-works/baton/issues/1073)) ([5140983](https://github.com/aer-works/baton/commit/51409839973b5434d282528affbc2899d117a5f7))
* **ui:** Truthful room states on the desktop switcher — waiting-on-you first, mark on load, drop the mislabel (J3, slice 2b) ([#1052](https://github.com/aer-works/baton/issues/1052)) ([f220c12](https://github.com/aer-works/baton/commit/f220c12d5285f909e8e3e1503e16cb27aa528256))


### Bug Fixes

* **cli:** Refuse a resume whose named workflow is a different template ([#652](https://github.com/aer-works/baton/issues/652)) ([bbfd524](https://github.com/aer-works/baton/commit/bbfd524cc752c9b55d11287521c442f7b398ac38))
* **flow:** Name the failure AER already detected instead of an unclassified Failed ([#607](https://github.com/aer-works/baton/issues/607)) ([9bbecb6](https://github.com/aer-works/baton/commit/9bbecb6a0da18ca1e4d9b0481caf3a1039d3838f))
* **ui,adapters:** Refuse to save a permission grant that is inert or that the engine rejects ([#711](https://github.com/aer-works/baton/issues/711)) ([273b7c2](https://github.com/aer-works/baton/commit/273b7c29c932690f3e212eeccd7676014a555ce5))
* **ui,mobile:** Complete the status vocabulary and make fill agree across toolkits ([#463](https://github.com/aer-works/baton/issues/463)) ([6dd8c87](https://github.com/aer-works/baton/commit/6dd8c874e18e3b11211b349905f346a7fd144d3a))
* **ui,mobile:** Draw status marks as shapes instead of codepoints ([#460](https://github.com/aer-works/baton/issues/460)) ([8f74855](https://github.com/aer-works/baton/commit/8f7485588c8cd7cd1bc9c2e3ebbf87002d56ee68))
* **ui:** Remote advertises the unreachable address and hides the not-encrypted warning ([#392](https://github.com/aer-works/baton/issues/392)) ([41ec69f](https://github.com/aer-works/baton/commit/41ec69f945a874210f51e5277529d18470070822))
* **ui:** Stop the new-chat adapter combo rendering blank after adapter refresh ([#990](https://github.com/aer-works/baton/issues/990)) ([c2bf1fd](https://github.com/aer-works/baton/commit/c2bf1fdb8cef63019dd4960cffa81fa08ff3048c))
* **ui:** The newest artifact-preview request wins, and its test no longer bets on timing ([#873](https://github.com/aer-works/baton/issues/873)) ([e05d94d](https://github.com/aer-works/baton/commit/e05d94d1a845c6ff55487bc717e952cf158bc6b9))
* **ui:** The workflow-path box never carries a bare template id, and the resume template check fires ([#969](https://github.com/aer-works/baton/issues/969)) ([c6ae9c1](https://github.com/aer-works/baton/commit/c6ae9c176fc3410a88d48b65ad2619b86d8e6134))


### Code Refactoring

* **adapters,cli,daemon,ui:** rename GeminiWorkerAdapter to AgyWorkerAdapter ([#1032](https://github.com/aer-works/baton/issues/1032)) ([#1034](https://github.com/aer-works/baton/issues/1034)) ([2a44e13](https://github.com/aer-works/baton/commit/2a44e1322c4e5054b5240b3cff3a2d1e7ec887f0))
* **core,daemon,ui:** Rename identifiers and spec terms to the two nouns ([#1038](https://github.com/aer-works/baton/issues/1038)) ([8d097af](https://github.com/aer-works/baton/commit/8d097afc11a21840e1d8420215019f239883a502))
* **core,daemon,ui:** Unify sessions and tasks into one storage root, with migration ([#444](https://github.com/aer-works/baton/issues/444)) ([04a11b8](https://github.com/aer-works/baton/commit/04a11b8ce095f37f4ff4bc9eba137461016dbbc0))
* **core:** Introduce AerPaths so the storage root has a single seam ([#362](https://github.com/aer-works/baton/issues/362)) ([4b81e57](https://github.com/aer-works/baton/commit/4b81e573b73a839e9dd713acb662b0d1bfb357e9))
* **dialogue:** StopSentinel fully retired from config and every authoring surface ([#830](https://github.com/aer-works/baton/issues/830)) ([71e112d](https://github.com/aer-works/baton/commit/71e112d69db051aa494a047b18c6551d08e191d0))
* **ui,mobile,docs:** Rename the product to Baton on every user-facing surface (0045) ([#865](https://github.com/aer-works/baton/issues/865)) ([fc8cae5](https://github.com/aer-works/baton/commit/fc8cae5fd3d0c0e36b51a94942f2ea36b6b27add))
* **ui:** Push back the view-model predicates that decide state rather than project it ([#980](https://github.com/aer-works/baton/issues/980)) ([6612829](https://github.com/aer-works/baton/commit/6612829f17a8cfd481a7455d7534ac64872e2bb8))


### Documentation

* Record the M25 design, verify it against both vendors, and consolidate the doc tree ([#473](https://github.com/aer-works/baton/issues/473)) ([a4afade](https://github.com/aer-works/baton/commit/a4afadebd7d239f2da6a49d53ea3f5217e978d6a))


### Continuous Integration

* **audit:** Lint user-facing strings for engine vocabulary ([#956](https://github.com/aer-works/baton/issues/956)) ([825c6f7](https://github.com/aer-works/baton/commit/825c6f7d7570bd39896a1d5514e6a81f769e5da4))
* Wire the journey gate into CI, and make the token drift check bidirectional ([#490](https://github.com/aer-works/baton/issues/490)) ([67b7b18](https://github.com/aer-works/baton/commit/67b7b18acd458d9849162b107aba1ae59ee719f3))


### Tests

* Add the journey-test harness with its first driveable legs ([#372](https://github.com/aer-works/baton/issues/372)) ([3c30827](https://github.com/aer-works/baton/commit/3c308276ced96654a4326cb948547fc2821f9a35))

## [0.19.0](https://github.com/aer-works/aer-flow/compare/ui-v0.18.0...ui-v0.19.0) (2026-07-22)


### Features

* **daemon,adapters,ui:** Milestone 24 — Interactive Sessions & Unified Task Creation ([#276](https://github.com/aer-works/aer-flow/issues/276)) ([f7ab4fa](https://github.com/aer-works/aer-flow/commit/f7ab4fad253e730735631d158d92f97b2fc22d03))
* **flow,adapters,ui:** Durably capture and surface the resolved prompt for ordinary workflow steps ([#297](https://github.com/aer-works/aer-flow/issues/297)) ([b91b3a1](https://github.com/aer-works/aer-flow/commit/b91b3a1242893df4a20cfdc3cc69044c2eea53e8))
* **ui,mobile:** Add a direct "start new chat" entry point ([#301](https://github.com/aer-works/aer-flow/issues/301)) ([03c2c62](https://github.com/aer-works/aer-flow/commit/03c2c62b8178878664083f015877a1d68637d593))
* **ui,mobile:** Bulk select for archive/delete in the Tasks view ([#294](https://github.com/aer-works/aer-flow/issues/294)) ([263bc4d](https://github.com/aer-works/aer-flow/commit/263bc4d3917a2a1a50e8b1571c8b642c54c6faba))


### Bug Fixes

* **daemon,ui,mobile:** Wire command picker to real actions and show active mode ([#298](https://github.com/aer-works/aer-flow/issues/298)) ([3ed5d2d](https://github.com/aer-works/aer-flow/commit/3ed5d2d0b4841bb84c5d43c6e7b58bc3ef2398d8))

## [0.18.0](https://github.com/aer-works/aer-flow/compare/ui-v0.17.0...ui-v0.18.0) (2026-07-21)


### Features

* **dialogue:** M23 Phase 1 — generalize the dialogue worker to N-party ([#273](https://github.com/aer-works/aer-flow/issues/273)) ([0a44f58](https://github.com/aer-works/aer-flow/commit/0a44f58062f9eda622452852e0e1ed29217b75b1))
* **flow,adapters:** M23 Phase 3 — Project-Directory-Bound Tasks & Portable Bindings ([#275](https://github.com/aer-works/aer-flow/issues/275)) ([2743172](https://github.com/aer-works/aer-flow/commit/274317233a1f7c419f746c1868bec80b19944e8c))

## [0.17.0](https://github.com/aer-works/aer-flow/compare/ui-v0.16.0...ui-v0.17.0) (2026-07-20)


### Features

* **templates:** implement built-in workflow template library ([#250](https://github.com/aer-works/aer-flow/issues/250)) ([#251](https://github.com/aer-works/aer-flow/issues/251)) ([2ca7490](https://github.com/aer-works/aer-flow/commit/2ca74902f829e24a6fe412030db373f78e473f17))

## [0.16.0](https://github.com/aer-works/aer-flow/compare/ui-v0.15.0...ui-v0.16.0) (2026-07-19)


### Features

* **adapters:** Add structured PermissionGrant model for worker bindings ([#230](https://github.com/aer-works/aer-flow/issues/230)) ([b958e8d](https://github.com/aer-works/aer-flow/commit/b958e8d0a1126a5f9520ab9dcb70526ac0ec87bc))
* **brand:** Refresh the AER mark and share it across desktop/mobile ([#239](https://github.com/aer-works/aer-flow/issues/239)) ([11a56e7](https://github.com/aer-works/aer-flow/commit/11a56e7822bda554ab43f5786f5705031a651720))
* **daemon,ui,sidecar:** M21 Phase 5+6 — Zero-Config Tailscale Embedding + Close M20's Deferred Hardening ([#244](https://github.com/aer-works/aer-flow/issues/244)) ([90fb9f9](https://github.com/aer-works/aer-flow/commit/90fb9f9d01145befa3255b62823f8adb2bfc13dd))
* **ui,mobile:** M21 Phase 3 — Desktop Pairing UX ([#235](https://github.com/aer-works/aer-flow/issues/235)) ([946743c](https://github.com/aer-works/aer-flow/commit/946743cd88176e05075f8c9264885e9ca830b57f))

## [0.15.0](https://github.com/aer-works/aer-flow/compare/ui-v0.14.0...ui-v0.15.0) (2026-07-18)


### Features

* **adapters:** M17 Phase 4 — Dispatch integration: the third adapter ([#174](https://github.com/aer-works/aer-flow/issues/174)) ([0b11c9a](https://github.com/aer-works/aer-flow/commit/0b11c9a1970fd6fb0ebd4bb6ce4f48489bd14cdb))
* Milestone 20 - Daemonization, Security, and Remote Control ([#223](https://github.com/aer-works/aer-flow/issues/223)) ([5a5b604](https://github.com/aer-works/aer-flow/commit/5a5b604d41e717ba643f18093c407b581b3666bd))
* **ui:** M14 Phase 1 — Stack decision + walking skeleton ([#127](https://github.com/aer-works/aer-flow/issues/127)) ([5974df4](https://github.com/aer-works/aer-flow/commit/5974df4e68fde19a0c8029d8aec7771380be9e8f))
* **ui:** M14 Phase 2 — Task & execution projection + change observation ([#128](https://github.com/aer-works/aer-flow/issues/128)) ([d242321](https://github.com/aer-works/aer-flow/commit/d242321320e291f9e96eba56918765c22f5c2934))
* **ui:** M14 Phase 3 — DAG view (snapshot topology + status overlay) ([#134](https://github.com/aer-works/aer-flow/issues/134)) ([7604196](https://github.com/aer-works/aer-flow/commit/7604196be9d6be98818c11de144a2d1bd5304431))
* **ui:** M14 Phase 4 — Artifact lineage + snapshot-vs-template diff ([#135](https://github.com/aer-works/aer-flow/issues/135)) ([6b87cc4](https://github.com/aer-works/aer-flow/commit/6b87cc41984b985a09095c263f8e8e282f65794e))
* **ui:** M15 Phase 1 — Mutation seam + start/resume a workflow ([#145](https://github.com/aer-works/aer-flow/issues/145)) ([8b9c12e](https://github.com/aer-works/aer-flow/commit/8b9c12e686e1a8676df75fbfb7d4ab40ec062e33))
* **ui:** M15 Phase 2 — Resolve decisions: Approve / Reject ([#146](https://github.com/aer-works/aer-flow/issues/146)) ([f7109c7](https://github.com/aer-works/aer-flow/commit/f7109c7ff744d0622003a71925a3663df84762d0))
* **ui:** M15 Phase 3 — Artifact-carrying decisions: Retry-with-revision + Send-back ([#147](https://github.com/aer-works/aer-flow/issues/147)) ([5ee0c0a](https://github.com/aer-works/aer-flow/commit/5ee0c0a1c7e5f2f0dacce1d805a0427b950746d3))
* **ui:** M15 Phase 4 — Cancel: targeted live-execution cancel + host stop ([#148](https://github.com/aer-works/aer-flow/issues/148)) ([f1a2361](https://github.com/aer-works/aer-flow/commit/f1a2361b9ed887f17dbdd941f61034cb6bf63203))
* **ui:** M16 Phase 1 — Template write seam + create/save walking skeleton ([#158](https://github.com/aer-works/aer-flow/issues/158)) ([e925a57](https://github.com/aer-works/aer-flow/commit/e925a57c727cfb4d6c6c2e61d24dba46af09c653))
* **ui:** M16 Phase 2 — Step & graph editing with live structural validation ([#160](https://github.com/aer-works/aer-flow/issues/160)) ([dc98f69](https://github.com/aer-works/aer-flow/commit/dc98f69b514781d4b9026f0c6b84e9fe6557f872))
* **ui:** M16 Phase 3 — PausePoint + SupersedeTargets editing ([#162](https://github.com/aer-works/aer-flow/issues/162)) ([46f66cd](https://github.com/aer-works/aer-flow/commit/46f66cd4628de3d024d9b344d797d4737bcd4369))
* **ui:** M16 Phase 4 — Worker-binding configuration editing ([#161](https://github.com/aer-works/aer-flow/issues/161)) ([b5acbb5](https://github.com/aer-works/aer-flow/commit/b5acbb583fdc658d5c1d873dc3225677c730a821))
* **ui:** M18 Phase 1 — Transcript read seam ([#182](https://github.com/aer-works/aer-flow/issues/182)) ([4aef5d2](https://github.com/aer-works/aer-flow/commit/4aef5d2d354a50c7a531f7e6d2afe896cbdee37c))
* **ui:** M18 Phase 2 — The conversation view ([#183](https://github.com/aer-works/aer-flow/issues/183)) ([1e57b8d](https://github.com/aer-works/aer-flow/commit/1e57b8d1062710a8791e3821b5f65f07649a8e78))
* **ui:** M19 Phase 2 - Navigation shell, Aer.Ui.Core seam, MVVM re-home, and the decision inbox ([#195](https://github.com/aer-works/aer-flow/issues/195)) ([2f25168](https://github.com/aer-works/aer-flow/commit/2f25168d843c609d986a0a73679e93087577c831))
* **ui:** M19 Phase 3 - Task view, human-first ([#196](https://github.com/aer-works/aer-flow/issues/196)) ([2743338](https://github.com/aer-works/aer-flow/commit/2743338e07313087221d6c091dceea3fe9592d0f))
* **ui:** M19 Phase 4 - Guided authoring, no hand-edited config files ([#197](https://github.com/aer-works/aer-flow/issues/197)) ([a3ef3b8](https://github.com/aer-works/aer-flow/commit/a3ef3b86c282194478e2247967a720356a93e86c))
* **ui:** M19 Phase 5 - Visual design pass ([#198](https://github.com/aer-works/aer-flow/issues/198)) ([8b500f5](https://github.com/aer-works/aer-flow/commit/8b500f56d6e0eb2e7d5175f2d58bfe8d675a1c9a))


### Bug Fixes

* **ui:** DAG node colors resolve the window's actual theme variant ([#205](https://github.com/aer-works/aer-flow/issues/205)) ([5dee3e1](https://github.com/aer-works/aer-flow/commit/5dee3e152ef64805c3fbfca631777f7e8d259413))
* **ui:** Polish title bar, navigation rail transitions, and step preview cache ([#221](https://github.com/aer-works/aer-flow/issues/221)) ([fc0ae3c](https://github.com/aer-works/aer-flow/commit/fc0ae3c17a66542a3a18dbf11073e11c2990bfc7))


### Tests

* **flow:** M8 Phase 4 — Fan-out/fan-in + retry end-to-end integration tests ([#56](https://github.com/aer-works/aer-flow/issues/56)) ([15d5adb](https://github.com/aer-works/aer-flow/commit/15d5adb77ff0c508fee275746009cc9f0d1de9de))


### Miscellaneous

* **release:** Split release-please into linked-versions groups (core, desktop) ([#225](https://github.com/aer-works/aer-flow/issues/225)) ([86da732](https://github.com/aer-works/aer-flow/commit/86da732bc469a1a86ba412b1863fb0506aa3e40b))
* **release:** Version aer-ui as its own release-please package ([#129](https://github.com/aer-works/aer-flow/issues/129)) ([2cb929c](https://github.com/aer-works/aer-flow/commit/2cb929ccebe030439ac1f4d6557dab217d34d30a))

## Changelog
