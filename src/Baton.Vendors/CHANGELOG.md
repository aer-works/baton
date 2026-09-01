# Changelog

## [0.27.0](https://github.com/philipreese/baton/compare/vendors-v0.26.1...vendors-v0.27.0) (2026-09-01)


### Miscellaneous

* **vendors:** Synchronize core versions

## [0.26.1](https://github.com/philipreese/baton/compare/vendors-v0.26.0...vendors-v0.26.1) (2026-09-01)


### Bug Fixes

* **vendors:** Instruct agy to run gates in the foreground, not poll manage_task ([#1625](https://github.com/philipreese/baton/issues/1625)) ([b9f0ec7](https://github.com/philipreese/baton/commit/b9f0ec7e9f979b0d432c1c68be3c74d5f10d24eb))

## [0.26.0](https://github.com/philipreese/baton/compare/vendors-v0.25.0...vendors-v0.26.0) (2026-09-01)


### Features

* **cli:** Dispatch accepts a --label so rooms are legible in every fleet view ([#1527](https://github.com/philipreese/baton/issues/1527)) ([ab695d7](https://github.com/philipreese/baton/commit/ab695d777bb22524cf9ae89481ea6b52ed5c1da8))
* **cli:** Dispatch ergonomics — spec/grant lint, --attach context files, --list-capabilities ([#1573](https://github.com/philipreese/baton/issues/1573)) ([9d6bc21](https://github.com/philipreese/baton/commit/9d6bc21e433e04bae836c12c9c93138db4af9015))
* **dispatch:** Surface the worker's skill roster at dispatch, and fix skill discovery on both adapters ([#1566](https://github.com/philipreese/baton/issues/1566)) ([b8c27dc](https://github.com/philipreese/baton/commit/b8c27dcdbaec1afc368e74ef5f71c0ba8beb4378))
* **flow:** Extend WorkerUsage with cache-read, cache-creation and thinking tokens ([#1587](https://github.com/philipreese/baton/issues/1587)) ([275c806](https://github.com/philipreese/baton/commit/275c806a48a00e3ae55a33a9aafeb7bf4b3616d1))
* **vendors:** Stream claude dispatches so a running lane's stdout log fills incrementally ([#1559](https://github.com/philipreese/baton/issues/1559)) ([b28f5b3](https://github.com/philipreese/baton/commit/b28f5b310cf7bcac9cdf0db4c470b9896ef97395))


### Bug Fixes

* **cli:** Render status/result envelopes, never swallow unknown ones ([#1585](https://github.com/philipreese/baton/issues/1585)) ([4b90189](https://github.com/philipreese/baton/commit/4b901895d09af4bdcb859bdd47dc3ad501ad2c1c))
* **dispatch:** Materialize a missing declared output from the worker's result envelope, loudly ([#1606](https://github.com/philipreese/baton/issues/1606)) ([17b31fe](https://github.com/philipreese/baton/commit/17b31fed653ee6197e55ef85d8c8af00769b653b))
* **flow:** Freeze vendor attribution — record Adapter/Model on ExecutionRequest ([#1579](https://github.com/philipreese/baton/issues/1579)) ([cea2d16](https://github.com/philipreese/baton/commit/cea2d163d5e2aab1713622abebd9c1f9606f8e01))
* **hooks:** Wire the shell-pattern hook channel as a second enforcement layer for scoped grants ([#1506](https://github.com/philipreese/baton/issues/1506)) ([f1f34fa](https://github.com/philipreese/baton/commit/f1f34faa61e60295c5bc3eb97296853b0509b463))
* **vendors:** One usage parser per vendor - adapters delegate to the standard parsers ([#1612](https://github.com/philipreese/baton/issues/1612)) ([bc8b5b1](https://github.com/philipreese/baton/commit/bc8b5b1a1b5bc15d6b64fb8609394bf13cc0bae7))


### Code Refactoring

* **vendors:** Freeze baton env config in an ambient snapshot instead of per-access re-reads ([#1526](https://github.com/philipreese/baton/issues/1526)) ([9a513aa](https://github.com/philipreese/baton/commit/9a513aa64a5a81951d72ad11b4a0c141f45ebc2a))

## [0.25.0](https://github.com/philipreese/baton/compare/vendors-v0.24.0...vendors-v0.25.0) (2026-08-31)


### Bug Fixes

* **ci:** Publish the verify-pack claude stub as a real exe the direct spawn can resolve ([#1473](https://github.com/philipreese/baton/issues/1473)) ([fcb8af8](https://github.com/philipreese/baton/commit/fcb8af8f702f123d44f7d438823a3a8192dc6aad))


### Miscellaneous

* **reset:** Baton everywhere -- the mechanical 1:1 token rename ([#1467](https://github.com/philipreese/baton/issues/1467)) ([fc08bac](https://github.com/philipreese/baton/commit/fc08bacc46968a0c94b82538c7a37ef74b142f1d))
* **reset:** Consolidate to one binary, five projects -- baton mcp and baton daemon verbs ([#1471](https://github.com/philipreese/baton/issues/1471)) ([1e7f297](https://github.com/philipreese/baton/commit/1e7f2971a7031329c58c8b81c8d8ac400c78a542))
* **reset:** Port native/core to C# and delete the Rust crate ([#1479](https://github.com/philipreese/baton/issues/1479)) ([444bcfe](https://github.com/philipreese/baton/commit/444bcfef5b7e77cb0fe7f8defc8e89ecadf69cde))

## [0.21.0](https://github.com/aer-works/baton/compare/adapters-v0.20.0...adapters-v0.21.0) (2026-08-27)


### Features

* **dispatch:** Agent-first dispatch -- worktree auto-provisioning, output override, dispatch-time facts ([#1380](https://github.com/aer-works/baton/issues/1380)) ([bd0c2a5](https://github.com/aer-works/baton/commit/bd0c2a54d0c7905a75520248b1db10d6c1d72d26))
* **dispatch:** Print the resolved grant profile; least-privilege review verified ([#1385](https://github.com/aer-works/baton/issues/1385)) ([9e88a6a](https://github.com/aer-works/baton/commit/9e88a6a9408a95ed47e7b02d0d01468856adf45e))
* **flow:** First-class resume verb -- continue a worker session with a message ([#1388](https://github.com/aer-works/baton/issues/1388)) ([1453d04](https://github.com/aer-works/baton/commit/1453d042cf83b951b0b4ea053ac73458fd694297))
* **store:** Per-execution usage accounting -- wall-clock always, tokens where the vendor reports them ([#1389](https://github.com/aer-works/baton/issues/1389)) ([15b6519](https://github.com/aer-works/baton/commit/15b6519d55acd404ce938defd55f5f0e23a92d37))


### Miscellaneous

* **cli:** Add TryInvocation lines to CLI and validation errors ([#1382](https://github.com/aer-works/baton/issues/1382)) ([859dadc](https://github.com/aer-works/baton/commit/859dadc0b07fc7f6929a8dbef6eeff549bbf581e))

## [0.20.0](https://github.com/aer-works/baton/compare/adapters-v0.19.0...adapters-v0.20.0) (2026-08-26)


### Features

* **adapters,cli:** honour pattern-scoped agy shell grants via a strict hook matcher ([#659](https://github.com/aer-works/baton/issues/659)) ([#1031](https://github.com/aer-works/baton/issues/1031)) ([900b3b9](https://github.com/aer-works/baton/commit/900b3b9ca2b378ee527c6162017663a941615f32))
* **adapters,cli:** Let a withheld write reach the worker's outbox on claude ([#666](https://github.com/aer-works/baton/issues/666)) ([fc884cd](https://github.com/aer-works/baton/commit/fc884cd6dac19f16d803c28246e101e1c9fef493))
* **adapters,cli:** Ship a PreToolUse hook on every spawned claude worker ([#555](https://github.com/aer-works/baton/issues/555)) ([a4ad817](https://github.com/aer-works/baton/commit/a4ad8178e0263502cd873002a66718f5c7313833))
* **adapters,cli:** Ship agy's workspace PreToolUse gate ([#603](https://github.com/aer-works/baton/issues/603)) ([ea2d40c](https://github.com/aer-works/baton/commit/ea2d40ca3e62dc25d47ef1ef9d93ec0324d9c738))
* **adapters,cli:** The dispatch-role catalog becomes an engine export served by aer templates ([#887](https://github.com/aer-works/baton/issues/887) stage 1) ([#960](https://github.com/aer-works/baton/issues/960)) ([1e11847](https://github.com/aer-works/baton/commit/1e118477b2fd1254c3f147087d0f503eb717fe28))
* **adapters,daemon,ui,mobile:** light up the depth mark from the registered model-purpose mapping ([#1341](https://github.com/aer-works/baton/issues/1341)) ([9b17744](https://github.com/aer-works/baton/commit/9b1774424a0618f84d60cbda25838f35b7dcfdca))
* **adapters,daemon:** A standing permission can be taken back ([#1250](https://github.com/aer-works/baton/issues/1250)) ([99c06b2](https://github.com/aer-works/baton/commit/99c06b251de81f5b6ac7a3d4533ae8e0ae312483))
* **adapters,daemon:** The orchestrator occupant — role template and the L1 turn contract ([#996](https://github.com/aer-works/baton/issues/996)) ([3421f37](https://github.com/aer-works/baton/commit/3421f37b09403b8b97abca3dc20d71ed809a38eb))
* **adapters,flow:** claude credits_required classifies ExhaustedUntil on both channels - and an unknown instant parks instead of machine-gunning ([#1115](https://github.com/aer-works/baton/issues/1115)) ([#1119](https://github.com/aer-works/baton/issues/1119)) ([0a27361](https://github.com/aer-works/baton/commit/0a27361f5690c855ce4cdbf5d89570de8af3c0a2))
* **adapters,flow:** Pass --settings/--mcp-config, cap subagent depth ([#553](https://github.com/aer-works/baton/issues/553)) ([22870d6](https://github.com/aer-works/baton/commit/22870d6cec26430540edd25bb3e6db4b3ec05008))
* **adapters,flow:** The deterministic command step — declared argv, stdout as the first declared output ([#887](https://github.com/aer-works/baton/issues/887) stage 2 slice 1) ([#963](https://github.com/aer-works/baton/issues/963)) ([33f8335](https://github.com/aer-works/baton/commit/33f83355e143f47ad9d845f45488458409279db3))
* **adapters:** Compose a workflow template into a runnable DAG (rung-3 slice-2a) ([#916](https://github.com/aer-works/baton/issues/916)) ([204d205](https://github.com/aer-works/baton/commit/204d205997a550885d772253fa591592a0b867c4))
* **adapters:** Declare per-role structured outputs in the worker-role catalog ([#899](https://github.com/aer-works/baton/issues/899)) ([4b9a0f4](https://github.com/aer-works/baton/commit/4b9a0f4db15f71270d80c4656621ddf2a37a546e))
* **adapters:** Engine-native worker-role catalog (rung 1 of [#887](https://github.com/aer-works/baton/issues/887)) ([#889](https://github.com/aer-works/baton/issues/889)) ([811c1c4](https://github.com/aer-works/baton/commit/811c1c444231778d56d8a395a45b38cd8eb5eb62))
* **adapters:** grant agy shell commands when network access is also requested ([#561](https://github.com/aer-works/baton/issues/561)) ([8963f5b](https://github.com/aer-works/baton/commit/8963f5ba63df926dfd3c1f77661323ae4ae83e7d))
* **adapters:** thread an Effort field through WorkerInvocation to both vendors' --effort flags ([#569](https://github.com/aer-works/baton/issues/569)) ([c744ecd](https://github.com/aer-works/baton/commit/c744ecdd2ad2c733c179f3ec034af33cd57e10cd))
* **adapters:** Vendor memory is isolated scratch; room memory is the only durable layer ([#442](https://github.com/aer-works/baton/issues/442)) ([#1021](https://github.com/aer-works/baton/issues/1021)) ([86a3d15](https://github.com/aer-works/baton/commit/86a3d15208244354f89f7af5a759d69169d23ca7))
* **adapters:** WorkflowTemplate as data + WorkflowTemplateCatalog over the role catalog (rung 3 slice 1) ([#907](https://github.com/aer-works/baton/issues/907)) ([d830233](https://github.com/aer-works/baton/commit/d830233093ecd5fcc2d5b59c32a919287c386be0))
* **cli,adapters:** Add aer dispatch &lt;role&gt; over a shared RoleBinding primitive ([#902](https://github.com/aer-works/baton/issues/902)) ([8b565f3](https://github.com/aer-works/baton/commit/8b565f3b205b60a349bee9f21aaa8c6dcb829f1d))
* **cli,adapters:** Run a composed template end-to-end — template-or-role dispatch + capture adapter (rung-3 2b+2c) ([#921](https://github.com/aer-works/baton/issues/921)) ([62772b9](https://github.com/aer-works/baton/commit/62772b94ffc66d750296e55b1b58ce80d5512a65))
* Conversational permission gate (0022) — engine, adapters, desktop + mobile ([#1087](https://github.com/aer-works/baton/issues/1087)) ([9a9192a](https://github.com/aer-works/baton/commit/9a9192a4c9446ab4c12dea0379b4979d6b729ce6))
* **daemon,flow,adapters:** Memory-proposal captures land per-execution with room attribution by construction ([#853](https://github.com/aer-works/baton/issues/853)) ([d4e7ca9](https://github.com/aer-works/baton/commit/d4e7ca96817bcf6aa1a9139af8db808164fd336a))
* **daemon,ui,mobile:** A message to a dormant room is answered by the product, never dispatched ([#1182](https://github.com/aer-works/baton/issues/1182)) ([7a177c4](https://github.com/aer-works/baton/commit/7a177c4e6e1d71a148a665a99b48949b48ee2ac6))
* **daemon:** A room's standing permissions can be read back ([#1255](https://github.com/aer-works/baton/issues/1255)) ([dbb3177](https://github.com/aer-works/baton/commit/dbb317786dd517bcda1d33b07b24dd5573f92546))
* **flow,adapters:** A reviewer hands back an applyable patch, not prose to retype ([#881](https://github.com/aer-works/baton/issues/881)) ([#1022](https://github.com/aer-works/baton/issues/1022)) ([a23335d](https://github.com/aer-works/baton/commit/a23335d01b66c22ad47ac7648ac7cbcdfd0d583d))
* **flow,adapters:** Gemini quota exhaustion is a state with a reset time, not a failure ([#807](https://github.com/aer-works/baton/issues/807)) ([821bfb3](https://github.com/aer-works/baton/commit/821bfb379b501e667699084483e938bffc8f454a))
* **flow,adapters:** GrantAuditMode — the audited write grant, recorded in the journal ([#901](https://github.com/aer-works/baton/issues/901) PR 1) ([#1011](https://github.com/aer-works/baton/issues/1011)) ([899762a](https://github.com/aer-works/baton/commit/899762adebc8a7297f2de1e66b2ace309166b5e2))
* **flow,cli:** Engine-provisioned git worktree workspaces (Rung 4, progresses [#669](https://github.com/aer-works/baton/issues/669)) ([#937](https://github.com/aer-works/baton/issues/937)) ([971f390](https://github.com/aer-works/baton/commit/971f39003253931e9f02304ea6e004d18e5bacb2))
* **flow,daemon,ui:** Participants — identity, naming, and properties for a room's workers ([#1310](https://github.com/aer-works/baton/issues/1310)) ([7f8306d](https://github.com/aer-works/baton/commit/7f8306d55e51f006170a6baad8924063ee5e3da9))
* **flow:** Deliver oversize worker prompts out-of-band through prompt.txt ([#748](https://github.com/aer-works/baton/issues/748)) ([#1015](https://github.com/aer-works/baton/issues/1015)) ([a25c21f](https://github.com/aer-works/baton/commit/a25c21f7a06d3a190bead1324fd9f7a6c0733de7))
* **flow:** Engine-level auto-denied-tool signal (FailureClassification.ToolDenied) ([#914](https://github.com/aer-works/baton/issues/914)) ([#1029](https://github.com/aer-works/baton/issues/1029)) ([aa9480d](https://github.com/aer-works/baton/commit/aa9480d583df4e9f765fc91e435d78d94c3ff5e8))
* **flow:** Post-run grant audit — an audited worker's stray writes fail the run ([#901](https://github.com/aer-works/baton/issues/901)) ([#1013](https://github.com/aer-works/baton/issues/1013)) ([cdd702d](https://github.com/aer-works/baton/commit/cdd702d43c096c8c230e4104576de8697496f013))
* **flow:** Split PausePoint into needs-input and ready-for-review kinds ([#435](https://github.com/aer-works/baton/issues/435)) ([82a9d95](https://github.com/aer-works/baton/commit/82a9d955da69be3bed3778d25415f9f2ec0185e7))
* **mcp:** memory-edit-proposal tool on the AER server, wired for both vendors; decision 0044 ([#834](https://github.com/aer-works/baton/issues/834)) ([5c8c47d](https://github.com/aer-works/baton/commit/5c8c47d22a55e9e7d19c42210dbbd7b53785e6ad))
* **ui,adapters,tools:** Equal-weight permission gate with its checker, and agy stdout quota classification ([#1129](https://github.com/aer-works/baton/issues/1129)) ([7179daf](https://github.com/aer-works/baton/commit/7179dafc7d0177896ee0f57dfd49197287c0cb7d))
* **ui,daemon:** addressing — tag chips, a sticky tag, and untagged goes to the orchestrator ([#1329](https://github.com/aer-works/baton/issues/1329)) ([991b8ad](https://github.com/aer-works/baton/commit/991b8ad87e76f1588781d02945f06e9398049bd9))
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
* **daemon,mcp,adapters:** A pending runtime permission dies with its turn — timeout, turn end, cancel, restart ([#1098](https://github.com/aer-works/baton/issues/1098), [#1100](https://github.com/aer-works/baton/issues/1100), [#1101](https://github.com/aer-works/baton/issues/1101)) ([#1102](https://github.com/aer-works/baton/issues/1102)) ([ecfd7dc](https://github.com/aer-works/baton/commit/ecfd7dc10b0413789ed151d923c97e74f82b16ed))
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

## [0.19.0](https://github.com/aer-works/aer-flow/compare/adapters-v0.18.0...adapters-v0.19.0) (2026-07-22)


### Features

* **daemon,adapters,ui:** Milestone 24 — Interactive Sessions & Unified Task Creation ([#276](https://github.com/aer-works/aer-flow/issues/276)) ([f7ab4fa](https://github.com/aer-works/aer-flow/commit/f7ab4fad253e730735631d158d92f97b2fc22d03))
* **flow,adapters,ui:** Durably capture and surface the resolved prompt for ordinary workflow steps ([#297](https://github.com/aer-works/aer-flow/issues/297)) ([b91b3a1](https://github.com/aer-works/aer-flow/commit/b91b3a1242893df4a20cfdc3cc69044c2eea53e8))


### Bug Fixes

* **adapters,daemon:** Give chat continuation a legal Supersede target ([#291](https://github.com/aer-works/aer-flow/issues/291)) ([fb13594](https://github.com/aer-works/aer-flow/commit/fb13594513233dcd0813f504d06b6ae8ce0f474f))
* **adapters:** Grant Claude Code access to the artifacts root via --add-dir ([#299](https://github.com/aer-works/aer-flow/issues/299)) ([79c0f68](https://github.com/aer-works/aer-flow/commit/79c0f68d2495a44264514c64421500a627cb9d67))

## [0.18.0](https://github.com/aer-works/aer-flow/compare/adapters-v0.17.0...adapters-v0.18.0) (2026-07-21)


### Features

* **flow,adapters:** M23 Phase 3 — Project-Directory-Bound Tasks & Portable Bindings ([#275](https://github.com/aer-works/aer-flow/issues/275)) ([2743172](https://github.com/aer-works/aer-flow/commit/274317233a1f7c419f746c1868bec80b19944e8c))

## [0.17.0](https://github.com/aer-works/aer-flow/compare/adapters-v0.16.0...adapters-v0.17.0) (2026-07-20)


### Features

* **templates:** implement built-in workflow template library ([#250](https://github.com/aer-works/aer-flow/issues/250)) ([#251](https://github.com/aer-works/aer-flow/issues/251)) ([2ca7490](https://github.com/aer-works/aer-flow/commit/2ca74902f829e24a6fe412030db373f78e473f17))

## [0.16.0](https://github.com/aer-works/aer-flow/compare/adapters-v0.15.0...adapters-v0.16.0) (2026-07-19)


### Features

* **adapters:** Add structured PermissionGrant model for worker bindings ([#230](https://github.com/aer-works/aer-flow/issues/230)) ([b958e8d](https://github.com/aer-works/aer-flow/commit/b958e8d0a1126a5f9520ab9dcb70526ac0ec87bc))

## [0.15.0](https://github.com/aer-works/aer-flow/compare/adapters-v0.14.0...adapters-v0.15.0) (2026-07-18)


### Features

* **adapters:** M11 Phase 1 — Canonical worker-invocation protocol + Aer.Adapters seam ([#91](https://github.com/aer-works/aer-flow/issues/91)) ([4388492](https://github.com/aer-works/aer-flow/commit/43884920d76955ff75b7d4940b2f3531f3e91315))
* **adapters:** M11 Phase 2 — Claude worker adapter (headless claude CLI) ([#92](https://github.com/aer-works/aer-flow/issues/92)) ([b7395bd](https://github.com/aer-works/aer-flow/commit/b7395bd1c33a6527217bfde32ba7e73c40f64771))
* **adapters:** M12 Phase 1 — Gemini worker adapter (headless agy CLI) ([#102](https://github.com/aer-works/aer-flow/issues/102)) ([4b944f5](https://github.com/aer-works/aer-flow/commit/4b944f5bc0ab6296ad4d408b5279d61068a80be4))
* **adapters:** M17 Phase 4 — Dispatch integration: the third adapter ([#174](https://github.com/aer-works/aer-flow/issues/174)) ([0b11c9a](https://github.com/aer-works/aer-flow/commit/0b11c9a1970fd6fb0ebd4bb6ce4f48489bd14cdb))
* **cli:** M11 Phase 3 — aer run pump (the CLI driver) ([#93](https://github.com/aer-works/aer-flow/issues/93)) ([d4648a1](https://github.com/aer-works/aer-flow/commit/d4648a1cee9e369e2a34d2d6442d4ed5eb7d2631))
* **cli:** M12 Phase 4 — Live mixed-vendor paused run (gated end-to-end) ([#106](https://github.com/aer-works/aer-flow/issues/106)) ([371049b](https://github.com/aer-works/aer-flow/commit/371049b08630fe078628693eecf1ed87732349bb))
* **core:** Initialize .NET solution and placeholder projects for aer-flow ([541258d](https://github.com/aer-works/aer-flow/commit/541258d75a43ad50fd25a63b8151d7e64c5d512c))
* Milestone 20 - Daemonization, Security, and Remote Control ([#223](https://github.com/aer-works/aer-flow/issues/223)) ([5a5b604](https://github.com/aer-works/aer-flow/commit/5a5b604d41e717ba643f18093c407b581b3666bd))
* **ui:** M16 Phase 4 — Worker-binding configuration editing ([#161](https://github.com/aer-works/aer-flow/issues/161)) ([b5acbb5](https://github.com/aer-works/aer-flow/commit/b5acbb583fdc658d5c1d873dc3225677c730a821))
* **ui:** M19 Phase 4 - Guided authoring, no hand-edited config files ([#197](https://github.com/aer-works/aer-flow/issues/197)) ([a3ef3b8](https://github.com/aer-works/aer-flow/commit/a3ef3b86c282194478e2247967a720356a93e86c))


### Tests

* **flow:** M8 Phase 4 — Fan-out/fan-in + retry end-to-end integration tests ([#56](https://github.com/aer-works/aer-flow/issues/56)) ([15d5adb](https://github.com/aer-works/aer-flow/commit/15d5adb77ff0c508fee275746009cc9f0d1de9de))


### Miscellaneous

* **release:** Split release-please into linked-versions groups (core, desktop) ([#225](https://github.com/aer-works/aer-flow/issues/225)) ([86da732](https://github.com/aer-works/aer-flow/commit/86da732bc469a1a86ba412b1863fb0506aa3e40b))
* **setup:** initialize aer-flow repository ([801f348](https://github.com/aer-works/aer-flow/commit/801f348f5e2d1a21bbd25cd421cfd91c15b22c4d))

## Changelog
