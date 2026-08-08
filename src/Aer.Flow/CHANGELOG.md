# Changelog

## [0.20.0](https://github.com/aer-works/baton/compare/flow-v0.19.0...flow-v0.20.0) (2026-08-08)


### Features

* **adapters,daemon:** The orchestrator occupant — role template and the L1 turn contract ([#996](https://github.com/aer-works/baton/issues/996)) ([3421f37](https://github.com/aer-works/baton/commit/3421f37b09403b8b97abca3dc20d71ed809a38eb))
* **adapters,flow:** Pass --settings/--mcp-config, cap subagent depth ([#553](https://github.com/aer-works/baton/issues/553)) ([22870d6](https://github.com/aer-works/baton/commit/22870d6cec26430540edd25bb3e6db4b3ec05008))
* **adapters,flow:** The deterministic command step — declared argv, stdout as the first declared output ([#887](https://github.com/aer-works/baton/issues/887) stage 2 slice 1) ([#963](https://github.com/aer-works/baton/issues/963)) ([33f8335](https://github.com/aer-works/baton/commit/33f83355e143f47ad9d845f45488458409279db3))
* **adapters:** Vendor memory is isolated scratch; room memory is the only durable layer ([#442](https://github.com/aer-works/baton/issues/442)) ([#1021](https://github.com/aer-works/baton/issues/1021)) ([86a3d15](https://github.com/aer-works/baton/commit/86a3d15208244354f89f7af5a759d69169d23ca7))
* **cli:** Every command prints where each produced output landed ([#777](https://github.com/aer-works/baton/issues/777)) ([0e64c30](https://github.com/aer-works/baton/commit/0e64c306450571f9bc9b94dc60d82f87bdf4157b))
* **daemon,flow,adapters:** Memory-proposal captures land per-execution with room attribution by construction ([#853](https://github.com/aer-works/baton/issues/853)) ([d4e7ca9](https://github.com/aer-works/baton/commit/d4e7ca96817bcf6aa1a9139af8db808164fd336a))
* **daemon,flow:** Periodic room-retention sweep wiring journal compaction ([#1040](https://github.com/aer-works/baton/issues/1040)) ([559451c](https://github.com/aer-works/baton/commit/559451c70fdc4a8c83bef4c90b2767565dc862c5))
* **daemon,flow:** The resident room turn host — wake-consuming loop, host throttles, and the failure breaker ([#995](https://github.com/aer-works/baton/issues/995)) ([9c1fc0f](https://github.com/aer-works/baton/commit/9c1fc0fe1b8745108a3994b1c2751ba9887ec427))
* **daemon:** RoomWakeBridge derives wakes from journals, never stores them ([#799](https://github.com/aer-works/baton/issues/799)) ([#819](https://github.com/aer-works/baton/issues/819)) ([94b6525](https://github.com/aer-works/baton/commit/94b6525e2608f5d251e27195c14490189559db68))
* **flow,adapters:** A reviewer hands back an applyable patch, not prose to retype ([#881](https://github.com/aer-works/baton/issues/881)) ([#1022](https://github.com/aer-works/baton/issues/1022)) ([a23335d](https://github.com/aer-works/baton/commit/a23335d01b66c22ad47ac7648ac7cbcdfd0d583d))
* **flow,adapters:** Gemini quota exhaustion is a state with a reset time, not a failure ([#807](https://github.com/aer-works/baton/issues/807)) ([821bfb3](https://github.com/aer-works/baton/commit/821bfb379b501e667699084483e938bffc8f454a))
* **flow,adapters:** GrantAuditMode — the audited write grant, recorded in the journal ([#901](https://github.com/aer-works/baton/issues/901) PR 1) ([#1011](https://github.com/aer-works/baton/issues/1011)) ([899762a](https://github.com/aer-works/baton/commit/899762adebc8a7297f2de1e66b2ace309166b5e2))
* **flow,cli:** Engine identity recorded, liveness probed at read time -- a silence gets its provenance ([#796](https://github.com/aer-works/baton/issues/796)) ([5d4d914](https://github.com/aer-works/baton/commit/5d4d9147aeef2fec13bf449726e2db687c3617ea))
* **flow,cli:** Engine-provisioned git worktree workspaces (Rung 4, progresses [#669](https://github.com/aer-works/baton/issues/669)) ([#937](https://github.com/aer-works/baton/issues/937)) ([971f390](https://github.com/aer-works/baton/commit/971f39003253931e9f02304ea6e004d18e5bacb2))
* **flow,daemon:** Held-work escalation subjects, and occupant references must resolve ([#1001](https://github.com/aer-works/baton/issues/1001)) ([#1002](https://github.com/aer-works/baton/issues/1002)) ([d318c2b](https://github.com/aer-works/baton/commit/d318c2b982a309ab350ca792ef74cb8ea929ae92))
* **flow,daemon:** Held-work resolve surface applies approved memory proposals ([#859](https://github.com/aer-works/baton/issues/859)) ([de46b9d](https://github.com/aer-works/baton/commit/de46b9d89225e56e89800d0e4a41796ac6ed0190))
* **flow:** Artifact pruning mechanism for completed runs ([#973](https://github.com/aer-works/baton/issues/973)) ([#1028](https://github.com/aer-works/baton/issues/1028)) ([cfa7f3b](https://github.com/aer-works/baton/commit/cfa7f3b0a7b8dc2d0731f555269ed651d3326418))
* **flow:** Bounded event-log reads via validated seek-to-tail and checkpointed core aggregates ([#978](https://github.com/aer-works/baton/issues/978)) ([0b700fe](https://github.com/aer-works/baton/commit/0b700feb6c11d36792df981d54853ea090627ffe))
* **flow:** Bounded room open via a projection checkpoint ([#903](https://github.com/aer-works/baton/issues/903) scope 1a) ([#961](https://github.com/aer-works/baton/issues/961)) ([89f556f](https://github.com/aer-works/baton/commit/89f556fcc2384d22a267c018fb639b98edcf4756))
* **flow:** Deliver oversize worker prompts out-of-band through prompt.txt ([#748](https://github.com/aer-works/baton/issues/748)) ([#1015](https://github.com/aer-works/baton/issues/1015)) ([a25c21f](https://github.com/aer-works/baton/commit/a25c21f7a06d3a190bead1324fd9f7a6c0733de7))
* **flow:** Engine-level auto-denied-tool signal (FailureClassification.ToolDenied) ([#914](https://github.com/aer-works/baton/issues/914)) ([#1029](https://github.com/aer-works/baton/issues/1029)) ([aa9480d](https://github.com/aer-works/baton/commit/aa9480d583df4e9f765fc91e435d78d94c3ff5e8))
* **flow:** Grant record shapes — delegated authority becomes recordable ([#778](https://github.com/aer-works/baton/issues/778) design §D) ([#964](https://github.com/aer-works/baton/issues/964)) ([dd70a3e](https://github.com/aer-works/baton/commit/dd70a3edba76c999328eeff766fa6edf9d6a761e))
* **flow:** Held work is a room record -- room.jsonl, sole writer, loud reconciliation ([#812](https://github.com/aer-works/baton/issues/812)) ([84ba2a7](https://github.com/aer-works/baton/commit/84ba2a7b03df86f537afaee2520844cb3245805a))
* **flow:** Machine retries get real backoff — StepRetryScheduled, steady default ([#720](https://github.com/aer-works/baton/issues/720)) ([6653cf6](https://github.com/aer-works/baton/commit/6653cf6ce017c39256139f799034b9c14ca51832))
* **flow:** Name the holder on journal read-path sharing violations ([#398](https://github.com/aer-works/baton/issues/398)) ([#1006](https://github.com/aer-works/baton/issues/1006)) ([51736b8](https://github.com/aer-works/baton/commit/51736b890d42032fdd90ded4559cc42e7413a50c))
* **flow:** Operator retry-now reaches a quota-parked step ([#815](https://github.com/aer-works/baton/issues/815)) ([#848](https://github.com/aer-works/baton/issues/848)) ([644d070](https://github.com/aer-works/baton/commit/644d070cfef1d9ea4589ed564bfaad439f43b598))
* **flow:** Post-run grant audit — an audited worker's stray writes fail the run ([#901](https://github.com/aer-works/baton/issues/901)) ([#1013](https://github.com/aer-works/baton/issues/1013)) ([cdd702d](https://github.com/aer-works/baton/commit/cdd702d43c096c8c230e4104576de8697496f013))
* **flow:** Room turn throttles — the caps that make constant vendor spend impossible ([#778](https://github.com/aer-works/baton/issues/778) addendum) ([#966](https://github.com/aer-works/baton/issues/966)) ([6dda481](https://github.com/aer-works/baton/commit/6dda4813a45a8575964a5f2cf9bd463e40882d72))
* **flow:** Split PausePoint into needs-input and ready-for-review kinds ([#435](https://github.com/aer-works/baton/issues/435)) ([82a9d95](https://github.com/aer-works/baton/commit/82a9d955da69be3bed3778d25415f9f2ec0185e7))
* **flow:** Structured review verdict as a schema-checked contract output ([#779](https://github.com/aer-works/baton/issues/779)) ([021755e](https://github.com/aer-works/baton/commit/021755e3466b04b59a346cba933bdd60ad3616eb))
* **flow:** The orchestrator cursor carries content identity, and compaction arrives unwired ([#972](https://github.com/aer-works/baton/issues/972)) ([#1026](https://github.com/aer-works/baton/issues/1026)) ([737addd](https://github.com/aer-works/baton/commit/737addd95479fbfb47bcd0dc60b7200cf56fb446))
* **flow:** The orchestrator turn input and session cursor ([#778](https://github.com/aer-works/baton/issues/778) design §A/§B) ([#965](https://github.com/aer-works/baton/issues/965)) ([66c745c](https://github.com/aer-works/baton/commit/66c745cf7bda9072916e46a1f7bee318f3c34914))
* **flow:** The versioned room memory document and its read surface ([#672](https://github.com/aer-works/baton/issues/672) M26 floor) ([#962](https://github.com/aer-works/baton/issues/962)) ([0dbc284](https://github.com/aer-works/baton/commit/0dbc284ccf776bb9c3eb41a7314217d2013725cb))
* **flow:** WriterUtcTimestamp on the journal envelope; aer status renders per-step times ([#824](https://github.com/aer-works/baton/issues/824)) ([267baf9](https://github.com/aer-works/baton/commit/267baf92f15e094f3efa7a2d90deb964be1b0d3f))
* **mcp:** memory-edit-proposal tool on the AER server, wired for both vendors; decision 0044 ([#834](https://github.com/aer-works/baton/issues/834)) ([5c8c47d](https://github.com/aer-works/baton/commit/5c8c47d22a55e9e7d19c42210dbbd7b53785e6ad))
* **ui:** A failure shows what broke, in the room, with the worker that failed there to be asked ([#617](https://github.com/aer-works/baton/issues/617)) ([#982](https://github.com/aer-works/baton/issues/982)) ([15bdc44](https://github.com/aer-works/baton/commit/15bdc44d738222fb4708258d6b2ff5e18f6d9baa))
* **ui:** One gate is one object seen from three places, and the lock names its holder ([#618](https://github.com/aer-works/baton/issues/618)) ([#988](https://github.com/aer-works/baton/issues/988)) ([3a0f3d7](https://github.com/aer-works/baton/commit/3a0f3d7615e39751b38fe4e077bb8f99217c84f9))


### Bug Fixes

* **adapters,flow:** Make the PreToolUse gate true on every spawn path, and enforced ([#705](https://github.com/aer-works/baton/issues/705)) ([6b4568f](https://github.com/aer-works/baton/commit/6b4568ffaa098ad4ff1f60667e089be6324becba))
* **cli,flow:** Live stream files for captured worker output, tailed by status --follow ([#805](https://github.com/aer-works/baton/issues/805)) ([7a86bfc](https://github.com/aer-works/baton/commit/7a86bfcc92e8bc77d4c70244203e9d733095144f))
* **cli:** decide/cancel/supply provision declared worktrees, and cancel does it lazily ([#1012](https://github.com/aer-works/baton/issues/1012)) ([#1024](https://github.com/aer-works/baton/issues/1024)) ([15362b5](https://github.com/aer-works/baton/commit/15362b5116187317b447293de908fa1ab8198640))
* **cli:** Malformed template and bindings JSON errors name the file and the expected shape ([#738](https://github.com/aer-works/baton/issues/738)) ([b2e2ea1](https://github.com/aer-works/baton/commit/b2e2ea1251b13446a971dac98ff230c591a41025))
* **cli:** resolve a task directory at the CLI boundary, and refuse one that is not ([#681](https://github.com/aer-works/baton/issues/681)) ([7e61c42](https://github.com/aer-works/baton/commit/7e61c42b43ecc41654ff52ff37249656fac99512))
* **cli:** Surface a failing worker's stderr instead of discarding it ([#608](https://github.com/aer-works/baton/issues/608)) ([55e3d74](https://github.com/aer-works/baton/commit/55e3d7442241fe479fcbdb2bb59467151b928938))
* **cli:** Typed refusal when a command hits a journal held by a live engine ([#816](https://github.com/aer-works/baton/issues/816)) ([#821](https://github.com/aer-works/baton/issues/821)) ([319acfc](https://github.com/aer-works/baton/commit/319acfc2b5dc9d68dc148002dde88396eac905a4))
* **daemon,flow:** Comma-proof agy id scrape; snapshot readers stop blocking the persist rename ([#837](https://github.com/aer-works/baton/issues/837), [#842](https://github.com/aer-works/baton/issues/842)) ([#844](https://github.com/aer-works/baton/issues/844)) ([c9609ee](https://github.com/aer-works/baton/commit/c9609eeada0e89130662bcd88678bb378ab7d53c))
* **daemon:** Return a meaningful message when opening a locked task ([#415](https://github.com/aer-works/baton/issues/415)) ([f33cf89](https://github.com/aer-works/baton/commit/f33cf89b5edbf954ada40a5a81aeafda721dfbc8))
* **dialogue,flow,cli,daemon,ui,adapters:** Pin every redirected child stream's decode to UTF-8 ([#466](https://github.com/aer-works/baton/issues/466)) ([#1017](https://github.com/aer-works/baton/issues/1017)) ([3489faf](https://github.com/aer-works/baton/commit/3489faf1b349c9d271c4fdf82c653b5fd22eb29d))
* **dispatch:** Enforce a POSIX command-line ceiling up-front ([#612](https://github.com/aer-works/baton/issues/612)) ([#896](https://github.com/aer-works/baton/issues/896)) ([0aa10dc](https://github.com/aer-works/baton/commit/0aa10dc47f9281bf208e2c107ff345d4b2bdcb3c))
* **flow,daemon:** Held-work rendering and wake derivation are shape-aware ([#835](https://github.com/aer-works/baton/issues/835)) ([7725f59](https://github.com/aer-works/baton/commit/7725f591f36d5ff967a0624524f279958ce60310))
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


### Code Refactoring

* **adapters,cli,daemon,ui:** rename GeminiWorkerAdapter to AgyWorkerAdapter ([#1032](https://github.com/aer-works/baton/issues/1032)) ([#1034](https://github.com/aer-works/baton/issues/1034)) ([2a44e13](https://github.com/aer-works/baton/commit/2a44e1322c4e5054b5240b3cff3a2d1e7ec887f0))
* **core,daemon,ui:** Rename identifiers and spec terms to the two nouns ([#1038](https://github.com/aer-works/baton/issues/1038)) ([8d097af](https://github.com/aer-works/baton/commit/8d097afc11a21840e1d8420215019f239883a502))
* **flow,cli,docs:** Adopt "the ledger" as the journal's user-facing noun (0045) ([#854](https://github.com/aer-works/baton/issues/854)) ([5f04eb8](https://github.com/aer-works/baton/commit/5f04eb84ac60d456db1d58cd8f87e10a735ab736))
* **flow:** Rename LaneJournalCitation to an honest HeldWorkCitation shape ([#886](https://github.com/aer-works/baton/issues/886)) ([dd5f7a7](https://github.com/aer-works/baton/commit/dd5f7a721faa276fa5e63d1446cf25d8cb8ef7fd))

## [0.19.0](https://github.com/aer-works/aer-flow/compare/flow-v0.18.0...flow-v0.19.0) (2026-07-22)


### Features

* **daemon,adapters,ui:** Milestone 24 — Interactive Sessions & Unified Task Creation ([#276](https://github.com/aer-works/aer-flow/issues/276)) ([f7ab4fa](https://github.com/aer-works/aer-flow/commit/f7ab4fad253e730735631d158d92f97b2fc22d03))
* **flow,adapters,ui:** Durably capture and surface the resolved prompt for ordinary workflow steps ([#297](https://github.com/aer-works/aer-flow/issues/297)) ([b91b3a1](https://github.com/aer-works/aer-flow/commit/b91b3a1242893df4a20cfdc3cc69044c2eea53e8))

## [0.18.0](https://github.com/aer-works/aer-flow/compare/flow-v0.17.0...flow-v0.18.0) (2026-07-21)


### Features

* **flow,adapters:** M23 Phase 3 — Project-Directory-Bound Tasks & Portable Bindings ([#275](https://github.com/aer-works/aer-flow/issues/275)) ([2743172](https://github.com/aer-works/aer-flow/commit/274317233a1f7c419f746c1868bec80b19944e8c))
* **flow:** M23 Phase 2 — Supersede-Chain Hardening ([#274](https://github.com/aer-works/aer-flow/issues/274)) ([d054b5c](https://github.com/aer-works/aer-flow/commit/d054b5ca1b1f833ae05a8980b29f60cf238ab206))

## [0.17.0](https://github.com/aer-works/aer-flow/compare/flow-v0.16.0...flow-v0.17.0) (2026-07-20)


### Miscellaneous

* **flow:** Synchronize core versions

## [0.16.0](https://github.com/aer-works/aer-flow/compare/flow-v0.15.0...flow-v0.16.0) (2026-07-19)


### Miscellaneous

* **flow:** Synchronize core versions

## [0.15.0](https://github.com/aer-works/aer-flow/compare/flow-v0.14.0...flow-v0.15.0) (2026-07-18)


### Features

* **adapters:** M12 Phase 1 — Gemini worker adapter (headless agy CLI) ([#102](https://github.com/aer-works/aer-flow/issues/102)) ([4b944f5](https://github.com/aer-works/aer-flow/commit/4b944f5bc0ab6296ad4d408b5279d61068a80be4))
* **cli:** M11 Phase 3 — aer run pump (the CLI driver) ([#93](https://github.com/aer-works/aer-flow/issues/93)) ([d4648a1](https://github.com/aer-works/aer-flow/commit/d4648a1cee9e369e2a34d2d6442d4ed5eb7d2631))
* **cli:** M12 Phase 2 — aer cancel + Ctrl+C host-stop wiring ([#103](https://github.com/aer-works/aer-flow/issues/103)) ([08b8d7a](https://github.com/aer-works/aer-flow/commit/08b8d7abd9da77c404ded2a5ce0c764407afca05))
* **core:** Initialize .NET solution and placeholder projects for aer-flow ([541258d](https://github.com/aer-works/aer-flow/commit/541258d75a43ad50fd25a63b8151d7e64c5d512c))
* **flow:** Add the Dependency Resolver for step readiness ([#38](https://github.com/aer-works/aer-flow/issues/38)) ([c460641](https://github.com/aer-works/aer-flow/commit/c4606410d54b3988c9139e2d327e31578948bc1f))
* **flow:** Add the Log Manager for crash-safe flow.jsonl appends ([#35](https://github.com/aer-works/aer-flow/issues/35)) ([8d8a728](https://github.com/aer-works/aer-flow/commit/8d8a7281653b87825a06fa4600b7edc146f22ddb))
* **flow:** Add the State Projector for FlowState reconstruction ([#37](https://github.com/aer-works/aer-flow/issues/37)) ([6601423](https://github.com/aer-works/aer-flow/commit/66014230b0aec75181105dfd4ecbdee6a4341b7a))
* **flow:** Add the Template Parser and Snapshot Binder ([#36](https://github.com/aer-works/aer-flow/issues/36)) ([75090a1](https://github.com/aer-works/aer-flow/commit/75090a1925023150fd5c80f8a776610f8094b30f))
* **flow:** Define the Phase 1 domain model ([#34](https://github.com/aer-works/aer-flow/issues/34)) ([61db539](https://github.com/aer-works/aer-flow/commit/61db539efef509d46167d08d0365b09989d70b46))
* **flow:** M10 Phase 1 — Cancellation mutation surface: record, validate, non-process targets ([#75](https://github.com/aer-works/aer-flow/issues/75)) ([df31f67](https://github.com/aer-works/aer-flow/commit/df31f671fa8a1a9259fdc42d5738a6f3f5dac5c0))
* **flow:** M10 Phase 2 — Live cancellation delivery: in-flight Core executions ([#76](https://github.com/aer-works/aer-flow/issues/76)) ([196410e](https://github.com/aer-works/aer-flow/commit/196410ece90a02c0bdb82ea12520dfa26985cf61))
* **flow:** M10 Phase 3 — Crash-recovery reconciliation: reading back the Core log ([#80](https://github.com/aer-works/aer-flow/issues/80)) ([8332030](https://github.com/aer-works/aer-flow/commit/833203098219d7a6c8f8f2ae65d04cbb6e2cec05))
* **flow:** M10 Phase 4 — Cancellation + crash-recovery end-to-end integration tests ([#82](https://github.com/aer-works/aer-flow/issues/82)) ([37afd3d](https://github.com/aer-works/aer-flow/commit/37afd3de76e0c51c21f05d4d883b67884d19a0d8))
* **flow:** M7 Phase 6 — Artifact Manager + Core Dispatcher ([#41](https://github.com/aer-works/aer-flow/issues/41)) ([1a633ce](https://github.com/aer-works/aer-flow/commit/1a633ce4f0794f52730122c2d6f573958ad4ba1d))
* **flow:** M7 Phase 7 — Outcome Classifier + Contract Validator + Mutation Interface ([#43](https://github.com/aer-works/aer-flow/issues/43)) ([97b90a7](https://github.com/aer-works/aer-flow/commit/97b90a79cfebea010a73e6c3eb8efaf37de20350))
* **flow:** M7 Phase 8 — Concurrency Guard + end-to-end integration test ([#44](https://github.com/aer-works/aer-flow/issues/44)) ([eea819f](https://github.com/aer-works/aer-flow/commit/eea819f3a27724ee6af182215ea0aa8ccae950b8))
* **flow:** M8 Phase 1 — Attempt-history projection ([#51](https://github.com/aer-works/aer-flow/issues/51)) ([5579774](https://github.com/aer-works/aer-flow/commit/55797741cc8a48490bf8f4ae7b29fef1ea8fb452))
* **flow:** M8 Phase 2 — Retry Engine + retry-aware readiness ([#53](https://github.com/aer-works/aer-flow/issues/53)) ([a45ad6d](https://github.com/aer-works/aer-flow/commit/a45ad6d745ca029e20f4d50368ca4ba568f8d375))
* **flow:** M8 Phase 3 — Reactive concurrent dispatch ([#54](https://github.com/aer-works/aer-flow/issues/54)) ([afccaf5](https://github.com/aer-works/aer-flow/commit/afccaf5510b085e188023b7037ed809abe3d5806))
* **flow:** M9 Phase 1 — Pause Engine ([#64](https://github.com/aer-works/aer-flow/issues/64)) ([6aa4ff0](https://github.com/aer-works/aer-flow/commit/6aa4ff0330db7a6f727fc565b5fb512a14212220))
* **flow:** M9 Phase 2 — External Decision Handler: record, validate, Resume/Reject ([#65](https://github.com/aer-works/aer-flow/issues/65)) ([a8e580f](https://github.com/aer-works/aer-flow/commit/a8e580f6a30ca0c5ce09a1d8097a75fa618a0704))
* **flow:** M9 Phase 3 — RetryWithRevision + Supersede + the invalidation cascade ([#66](https://github.com/aer-works/aer-flow/issues/66)) ([3569690](https://github.com/aer-works/aer-flow/commit/35696900a6732bb5033680d495edba6f877f02b7))
* **flow:** M9 Phase 4 — Human worker support (non-process executions) ([#67](https://github.com/aer-works/aer-flow/issues/67)) ([e24f273](https://github.com/aer-works/aer-flow/commit/e24f2738cef966d21e8895e82a29761b7c64dd6e))
* Milestone 20 - Daemonization, Security, and Remote Control ([#223](https://github.com/aer-works/aer-flow/issues/223)) ([5a5b604](https://github.com/aer-works/aer-flow/commit/5a5b604d41e717ba643f18093c407b581b3666bd))
* **ui:** M16 Phase 1 — Template write seam + create/save walking skeleton ([#158](https://github.com/aer-works/aer-flow/issues/158)) ([e925a57](https://github.com/aer-works/aer-flow/commit/e925a57c727cfb4d6c6c2e61d24dba46af09c653))
* **ui:** M16 Phase 3 — PausePoint + SupersedeTargets editing ([#162](https://github.com/aer-works/aer-flow/issues/162)) ([46f66cd](https://github.com/aer-works/aer-flow/commit/46f66cd4628de3d024d9b344d797d4737bcd4369))


### Bug Fixes

* **flow:** Order in-flight registry capture before the round's log read ([#83](https://github.com/aer-works/aer-flow/issues/83)) ([55dfbd9](https://github.com/aer-works/aer-flow/commit/55dfbd99ceb605ded552b6a5117ca140bb296ae5))


### Tests

* **flow:** M8 Phase 4 — Fan-out/fan-in + retry end-to-end integration tests ([#56](https://github.com/aer-works/aer-flow/issues/56)) ([15d5adb](https://github.com/aer-works/aer-flow/commit/15d5adb77ff0c508fee275746009cc9f0d1de9de))


### Miscellaneous

* **release:** Split release-please into linked-versions groups (core, desktop) ([#225](https://github.com/aer-works/aer-flow/issues/225)) ([86da732](https://github.com/aer-works/aer-flow/commit/86da732bc469a1a86ba412b1863fb0506aa3e40b))
* **setup:** initialize aer-flow repository ([801f348](https://github.com/aer-works/aer-flow/commit/801f348f5e2d1a21bbd25cd421cfd91c15b22c4d))

## Changelog
