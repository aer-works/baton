# Changelog

## [0.30.0](https://github.com/philipreese/baton/compare/cli-v0.29.0...cli-v0.30.0) (2026-09-04)


### Features

* **cli:** Add baton watch, a one-shot terminal notification for a room ([#1766](https://github.com/philipreese/baton/issues/1766)) ([97fff37](https://github.com/philipreese/baton/commit/97fff371078d1b7a9bb1a3de47b2a3367de612b5))
* **daemon:** Port the pusher's stdout-tail renderer into the projection writer ([#1786](https://github.com/philipreese/baton/issues/1786)) ([7a98fab](https://github.com/philipreese/baton/commit/7a98fabc8cff297a22a8d5f5261d063b6699a32f))
* **daemon:** Write the fleet projection file every 30 s ([#1772](https://github.com/philipreese/baton/issues/1772)) ([dcf338b](https://github.com/philipreese/baton/commit/dcf338bab7c8ecba2f976617cd0d7627de6f1a97))
* **flow:** A real terminal-timestamp source for workflow runs ([#1775](https://github.com/philipreese/baton/issues/1775)) ([ec58249](https://github.com/philipreese/baton/commit/ec582494c47f2b91488a3e0b1743570e80b3a02f))
* **flow:** Add the project-keyed permission-ceiling store and the baton trust verb ([#1787](https://github.com/philipreese/baton/issues/1787)) ([11bb629](https://github.com/philipreese/baton/commit/11bb62958da6626f12b084275b42bcd63a0f17a4))
* **flow:** Artifacts are files -- versioned, attributed, never silently overwritten ([#1791](https://github.com/philipreese/baton/issues/1791)) ([9414ef1](https://github.com/philipreese/baton/commit/9414ef13f9bb158df17bc7049dc394ef246166ce))
* **flow:** Delivery state (branch, PR, CI, merged) recorded as room facts ([#1790](https://github.com/philipreese/baton/issues/1790)) ([faf46e1](https://github.com/philipreese/baton/commit/faf46e168e0b2936fac42f3deb00f0537782265a))
* **flow:** Fleet-level burn ledger — append per-execution usage to quota-ledger.jsonl at settle ([#1781](https://github.com/philipreese/baton/issues/1781)) ([a775237](https://github.com/philipreese/baton/commit/a7752374d81951da5db95168c83405a03411863c))
* **flow:** Journal a content-free progress heartbeat and cancellation delivery/rejection events ([#1780](https://github.com/philipreese/baton/issues/1780)) ([d26390c](https://github.com/philipreese/baton/commit/d26390ca735b3423daa92cff9f702108e494a55b))


### Bug Fixes

* **cli:** Print billed/cache/thinking tokens in the status usage roll-up ([#1755](https://github.com/philipreese/baton/issues/1755)) ([9d1a490](https://github.com/philipreese/baton/commit/9d1a4903d09aa59c2b208b76ab2602ffddcc1816))
* **daemon:** Retry FleetProjectionWriter.WriteAtomic past a concurrent reader ([#1789](https://github.com/philipreese/baton/issues/1789)) ([565e307](https://github.com/philipreese/baton/commit/565e307018ceda7b142ae37e541d07150baeb2c0))
* **daemon:** Scope singleton mutex per-home; kill orphaned daemon on task restart ([#1777](https://github.com/philipreese/baton/issues/1777)) ([0b28a18](https://github.com/philipreese/baton/commit/0b28a181fe35085e60e9c460cb20eea5f60c5fd7))
* **dispatch:** Deny label, merge, and API writes to implement and janitor without fatal metacharacters on unscoped grants ([#1748](https://github.com/philipreese/baton/issues/1748)) ([47febb7](https://github.com/philipreese/baton/commit/47febb7e15c01e6ecee4974e32eeb9c6fa0b4a1b))


### Miscellaneous

* **dispatch:** Retire the Python dispatcher in favour of baton dispatch ([#1765](https://github.com/philipreese/baton/issues/1765)) ([1a2e292](https://github.com/philipreese/baton/commit/1a2e29222835ed5ec90e00585f55df3ff51647d7))

## [0.29.0](https://github.com/philipreese/baton/compare/cli-v0.28.0...cli-v0.29.0) (2026-09-03)


### Features

* **cli:** Add --spec-text and --spec - as inline alternatives to a spec file ([#1734](https://github.com/philipreese/baton/issues/1734)) ([aa6fc1b](https://github.com/philipreese/baton/commit/aa6fc1b13193ea49cef2ff1f024f930f46ab0a33))
* **cli:** Add baton deliver and a standing conductor room so orchestrator deliverables reach the glass inbox ([#1672](https://github.com/philipreese/baton/issues/1672)) ([e50de0d](https://github.com/philipreese/baton/commit/e50de0db1851d5ce8a14f1ad43280e44f79058c6))
* **cli:** Add baton room delete and baton rooms prune ([#1667](https://github.com/philipreese/baton/issues/1667)) ([b0e0702](https://github.com/philipreese/baton/commit/b0e0702755c5d5a17f6ba556bc853b856fbcd3e5))
* **cli:** Widen cancel's room-level target to include quota-parked lanes ([#1641](https://github.com/philipreese/baton/issues/1641)) ([c4c2a82](https://github.com/philipreese/baton/commit/c4c2a8279db008c14d7ee56adc348d577ed783d7))
* **dispatch:** Settle captured-response lanes Indeterminate and add the baton resolve verb ([#1644](https://github.com/philipreese/baton/issues/1644)) ([ec8b5eb](https://github.com/philipreese/baton/commit/ec8b5eb6a8840eac550a4515488c97662018cb2e))
* **engine:** Run the verify step and enforce per-execution token budgets ([#1654](https://github.com/philipreese/baton/issues/1654)) ([3e1e42f](https://github.com/philipreese/baton/commit/3e1e42f608669c5cd1132fd615a1b308a1af1e6a))
* **glass:** Surface the redispatch lineage chain that room.json already records ([#1717](https://github.com/philipreese/baton/issues/1717)) ([f8de18c](https://github.com/philipreese/baton/commit/f8de18cb575b67368a141e8e2e9697d2175edde5))
* **infra:** Install baton side by side per commit so a refresh needs no drain ([#1670](https://github.com/philipreese/baton/issues/1670)) ([cedac75](https://github.com/philipreese/baton/commit/cedac759f63a2ac097abbeb4e1d11b70a2d3f978))
* **tools:** Add one-command tool refresh with drain, reinstall, verify and drift warning ([#1653](https://github.com/philipreese/baton/issues/1653)) ([f7c4cca](https://github.com/philipreese/baton/commit/f7c4ccadaf14d72f5421bbf2a733e19bbba27c1c))


### Bug Fixes

* **cli:** Never sweep a live cancel request written during run startup ([#1665](https://github.com/philipreese/baton/issues/1665)) ([a01a892](https://github.com/philipreese/baton/commit/a01a892a1cfb3705369e5db2d2bb79a728f9c9af))
* **cli:** Render worker stream-json as prose in room_detail and status --follow ([#1719](https://github.com/philipreese/baton/issues/1719)) ([7e77d89](https://github.com/philipreese/baton/commit/7e77d897ff9079d173ded4677b6ada1821307dff))
* **cli:** Resume status --follow from the initial tail's offsets instead of reprinting it ([#1737](https://github.com/philipreese/baton/issues/1737)) ([9f54c6f](https://github.com/philipreese/baton/commit/9f54c6f879e452120906e605a3682ae7bcef48c2))
* **cli:** Run the spec/grant linter and accept --attach on redispatch --spec ([#1704](https://github.com/philipreese/baton/issues/1704)) ([a4656c0](https://github.com/philipreese/baton/commit/a4656c002778cc80e1e65e3db05bd66efad1a526))
* **cli:** Write the conductor manifest without a BOM and make the pusher tolerate one ([#1674](https://github.com/philipreese/baton/issues/1674)) ([1f34030](https://github.com/philipreese/baton/commit/1f3403063692f7be019c5e190ab49d93494d7936))
* **dispatch:** Add a sliding-window billed-rate arrest trigger, and ship it unset because no value separates ([#1707](https://github.com/philipreese/baton/issues/1707)) ([8eba760](https://github.com/philipreese/baton/commit/8eba760d02abb6c3dbd43278562cd156e220e9e5))
* **dispatch:** Arrest on billed tokens and a tool-step cap instead of the context level ([#1686](https://github.com/philipreese/baton/issues/1686)) ([428098f](https://github.com/philipreese/baton/commit/428098f11254920775a4346de6517a38e7a6f520))
* **dispatch:** Evaluate agy's standing deny list on chained commands ([#1725](https://github.com/philipreese/baton/issues/1725)) ([113e406](https://github.com/philipreese/baton/commit/113e406b1a97f422a801a6b3d728c4269139ba1b))
* **dispatch:** Match shell command patterns on word boundaries and deny the flag-driven git and gh escapes ([#1683](https://github.com/philipreese/baton/issues/1683)) ([23c8654](https://github.com/philipreese/baton/commit/23c86542468984d645ea103fcfeba11871d8d0d1))
* **dispatch:** Settle an exit-0 contract failure Indeterminate instead of retrying on a mutated workspace ([#1664](https://github.com/philipreese/baton/issues/1664)) ([d35ca09](https://github.com/philipreese/baton/commit/d35ca09ec8bf0cd79f5ee26425174b1caff60068))
* **flow:** Exit-0 quota veto, work-product evidence, and a resolve path for verify/arrest dead ends ([#1720](https://github.com/philipreese/baton/issues/1720)) ([c5cfe08](https://github.com/philipreese/baton/commit/c5cfe0834e4caf490de0c679f0363d540e61cbb9))
* **flow:** Resolve the verify command from the workspace, always deliver --output ([#1708](https://github.com/philipreese/baton/issues/1708)) ([571f568](https://github.com/philipreese/baton/commit/571f5680a917f0985a1ee44571d6da05f7bb4946))
* **flow:** Stop decide's worktree provisioning from racing a live run --wait pump's flow.lock ([#1650](https://github.com/philipreese/baton/issues/1650)) ([8e20c85](https://github.com/philipreese/baton/commit/8e20c85026c6b9c3e700d2fbe00ae1c2002ab1f1))
* **store:** Keep throwaway repro rooms out of the room registry and off the glass ([#1661](https://github.com/philipreese/baton/issues/1661)) ([85c562e](https://github.com/philipreese/baton/commit/85c562e635c13da8f376cbf045c279dc940a3637))
* **vendors:** Verify agy's PreToolUse hook is live before trusting it as sole narrowing ([#1732](https://github.com/philipreese/baton/issues/1732)) ([0ddf15c](https://github.com/philipreese/baton/commit/0ddf15c3741db2957f5c4c63f640c54df748bbf4))


### Code Refactoring

* **vendors:** Fold the four [#1496](https://github.com/philipreese/baton/issues/1496)-exempt env readers into BatonEnvironmentSnapshot ([#1729](https://github.com/philipreese/baton/issues/1729)) ([e23fd8b](https://github.com/philipreese/baton/commit/e23fd8b738d9cf854c0be08572e8062ebf21d0be))

## [0.28.0](https://github.com/philipreese/baton/compare/cli-v0.27.0...cli-v0.28.0) (2026-09-02)


### Features

* **dispatch:** Add --workstream grouping field plus by-workstream junction links ([#1642](https://github.com/philipreese/baton/issues/1642)) ([17ce115](https://github.com/philipreese/baton/commit/17ce115f299a68735f64d67b4b2ae5f7372c1f48))


### Bug Fixes

* **flow:** Journal StepRebound on crash-recovery resubmit with divergent binding ([#1640](https://github.com/philipreese/baton/issues/1640)) ([0957c1c](https://github.com/philipreese/baton/commit/0957c1c826bc130046fbc641081c628b4b3937d1))
* **flow:** Prefer recorded Adapter and Model on fleet_status running bindings ([#1637](https://github.com/philipreese/baton/issues/1637)) ([8f8cd49](https://github.com/philipreese/baton/commit/8f8cd4927d346c535bff7b45fddd6e435c523ddf))

## [0.27.0](https://github.com/philipreese/baton/compare/cli-v0.26.1...cli-v0.27.0) (2026-09-01)


### Features

* **flow:** StepRetryForeclosed and the indeterminate terminal vocabulary (state-truth S1) ([#1628](https://github.com/philipreese/baton/issues/1628)) ([e7438a4](https://github.com/philipreese/baton/commit/e7438a4482e42a5521350aa246a223ad1238771c))
* **glass:** Live execution counts, honest staleness, terminal bindings, and terminal timelines ([#1624](https://github.com/philipreese/baton/issues/1624)) ([ea57852](https://github.com/philipreese/baton/commit/ea57852dab04f97c34d65137a575ae5b8a33aa5c))

## [0.26.1](https://github.com/philipreese/baton/compare/cli-v0.26.0...cli-v0.26.1) (2026-09-01)


### Miscellaneous

* **cli:** Synchronize core versions

## [0.26.0](https://github.com/philipreese/baton/compare/cli-v0.25.0...cli-v0.26.0) (2026-09-01)


### Features

* **cli:** A cancel request reaches a quota-parked lane without waiting out the park ([#1605](https://github.com/philipreese/baton/issues/1605)) ([ab717ec](https://github.com/philipreese/baton/commit/ab717ec0171e4151c30449b9f2434e4220e99ad3))
* **cli:** Dispatch accepts a --label so rooms are legible in every fleet view ([#1527](https://github.com/philipreese/baton/issues/1527)) ([ab695d7](https://github.com/philipreese/baton/commit/ab695d777bb22524cf9ae89481ea6b52ed5c1da8))
* **cli:** Dispatch ergonomics — spec/grant lint, --attach context files, --list-capabilities ([#1573](https://github.com/philipreese/baton/issues/1573)) ([9d6bc21](https://github.com/philipreese/baton/commit/9d6bc21e433e04bae836c12c9c93138db4af9015))
* **cli:** Reach a live lane with baton cancel through a room-scoped request channel ([#1528](https://github.com/philipreese/baton/issues/1528)) ([50c8a4b](https://github.com/philipreese/baton/commit/50c8a4ba157b70787a10535deb429e73b198ce03))
* **dispatch:** Surface the worker's skill roster at dispatch, and fix skill discovery on both adapters ([#1566](https://github.com/philipreese/baton/issues/1566)) ([b8c27dc](https://github.com/philipreese/baton/commit/b8c27dcdbaec1afc368e74ef5f71c0ba8beb4378))
* **flow:** Extend WorkerUsage with cache-read, cache-creation and thinking tokens ([#1587](https://github.com/philipreese/baton/issues/1587)) ([275c806](https://github.com/philipreese/baton/commit/275c806a48a00e3ae55a33a9aafeb7bf4b3616d1))
* **flow:** Persist a lifetime execution ordinal per step so RetryWithRevision can't reset the attempt count ([#1555](https://github.com/philipreese/baton/issues/1555)) ([cfcc8ba](https://github.com/philipreese/baton/commit/cfcc8ba13b33f2e984d5df58531a87626e86b6eb))
* **glass:** Show when an exhausted-until park lifts, honestly for stalled rooms too ([#1598](https://github.com/philipreese/baton/issues/1598)) ([dd71d32](https://github.com/philipreese/baton/commit/dd71d3237330b3519ce8a4ba910cf432907b4907))
* **mcp:** Fleet projection reads bindings.json for adapter, model, effort, role, and timeout ([#1504](https://github.com/philipreese/baton/issues/1504)) ([abfe460](https://github.com/philipreese/baton/commit/abfe4600b7d8f4ea4b686ab7305feb73947b4dda))
* **mcp:** Surface retry attempt and failure classification in fleet_status ([#1520](https://github.com/philipreese/baton/issues/1520)) ([3700b24](https://github.com/philipreese/baton/commit/3700b24590d73172fe8f2325ecd797428bfdf4c8))
* **vendors:** Stream claude dispatches so a running lane's stdout log fills incrementally ([#1559](https://github.com/philipreese/baton/issues/1559)) ([b28f5b3](https://github.com/philipreese/baton/commit/b28f5b310cf7bcac9cdf0db4c470b9896ef97395))


### Bug Fixes

* **cli:** Dead-pump rooms fail fast with the recovery pointer instead of refusing bare or hanging ([#1604](https://github.com/philipreese/baton/issues/1604)) ([ee13f98](https://github.com/philipreese/baton/commit/ee13f98f8b37e7eb522ab50d1d39eb839a8b0f46))
* **cli:** Render status/result envelopes, never swallow unknown ones ([#1585](https://github.com/philipreese/baton/issues/1585)) ([4b90189](https://github.com/philipreese/baton/commit/4b901895d09af4bdcb859bdd47dc3ad501ad2c1c))
* **dispatch:** A lane that writes its deliverable can hang without a terminal event ([#1582](https://github.com/philipreese/baton/issues/1582)) ([690b29e](https://github.com/philipreese/baton/commit/690b29e329f78738edce748b820e6bb27071db2b))
* **flow:** Freeze vendor attribution — record Adapter/Model on ExecutionRequest ([#1579](https://github.com/philipreese/baton/issues/1579)) ([cea2d16](https://github.com/philipreese/baton/commit/cea2d163d5e2aab1713622abebd9c1f9606f8e01))
* **flow:** terminal.json carries the same vendor usage baton status --json reports ([#1597](https://github.com/philipreese/baton/issues/1597)) ([01bef7f](https://github.com/philipreese/baton/commit/01bef7f1950b8b8fd54edafdadbdda813607472a))
* **hooks:** Wire the shell-pattern hook channel as a second enforcement layer for scoped grants ([#1506](https://github.com/philipreese/baton/issues/1506)) ([f1f34fa](https://github.com/philipreese/baton/commit/f1f34faa61e60295c5bc3eb97296853b0509b463))


### Code Refactoring

* **core:** Give the four room-identifying filenames one canonical home in BatonPaths ([#1489](https://github.com/philipreese/baton/issues/1489)) ([4307cb9](https://github.com/philipreese/baton/commit/4307cb921c989a0e940330d2ab80d1c58c8851ca))
* **vendors:** Freeze baton env config in an ambient snapshot instead of per-access re-reads ([#1526](https://github.com/philipreese/baton/issues/1526)) ([9a513aa](https://github.com/philipreese/baton/commit/9a513aa64a5a81951d72ad11b4a0c141f45ebc2a))

## [0.25.0](https://github.com/philipreese/baton/compare/cli-v0.24.0...cli-v0.25.0) (2026-08-31)


### Features

* **cli:** Add --wait-timeout so baton run --wait cannot block forever ([#1478](https://github.com/philipreese/baton/issues/1478)) ([7e8daeb](https://github.com/philipreese/baton/commit/7e8daebb91eb680ac80cdb592de61cf4a5de443b))
* **cli:** Operator can set KeepMarker via baton keep/unkeep ([#1481](https://github.com/philipreese/baton/issues/1481)) ([0d7f979](https://github.com/philipreese/baton/commit/0d7f979be4623e4fb040b17641dd43409d064403))
* **mcp:** fleet_status inherits liveness and rejected from the status projection ([#1477](https://github.com/philipreese/baton/issues/1477)) ([96248c3](https://github.com/philipreese/baton/commit/96248c38e5a71e5d879328d8c8bb3167dc9bf383))


### Miscellaneous

* **reset:** Baton everywhere -- the mechanical 1:1 token rename ([#1467](https://github.com/philipreese/baton/issues/1467)) ([fc08bac](https://github.com/philipreese/baton/commit/fc08bacc46968a0c94b82538c7a37ef74b142f1d))
* **reset:** Consolidate to one binary, five projects -- baton mcp and baton daemon verbs ([#1471](https://github.com/philipreese/baton/issues/1471)) ([1e7f297](https://github.com/philipreese/baton/commit/1e7f2971a7031329c58c8b81c8d8ac400c78a542))
* **reset:** Port native/core to C# and delete the Rust crate ([#1479](https://github.com/philipreese/baton/issues/1479)) ([444bcfe](https://github.com/philipreese/baton/commit/444bcfef5b7e77cb0fe7f8defc8e89ecadf69cde))

## [0.21.0](https://github.com/aer-works/baton/compare/cli-v0.20.0...cli-v0.21.0) (2026-08-27)


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
* Conversational permission gate (0022) — engine, adapters, desktop + mobile ([#1087](https://github.com/aer-works/baton/issues/1087)) ([9a9192a](https://github.com/aer-works/baton/commit/9a9192a4c9446ab4c12dea0379b4979d6b729ce6))
* **daemon,adapters,ui:** Milestone 24 — Interactive Sessions & Unified Task Creation ([#276](https://github.com/aer-works/baton/issues/276)) ([f7ab4fa](https://github.com/aer-works/baton/commit/f7ab4fad253e730735631d158d92f97b2fc22d03))
* **dispatch:** Agent-first dispatch -- worktree auto-provisioning, output override, dispatch-time facts ([#1380](https://github.com/aer-works/baton/issues/1380)) ([bd0c2a5](https://github.com/aer-works/baton/commit/bd0c2a54d0c7905a75520248b1db10d6c1d72d26))
* **dispatch:** Print the resolved grant profile; least-privilege review verified ([#1385](https://github.com/aer-works/baton/issues/1385)) ([9e88a6a](https://github.com/aer-works/baton/commit/9e88a6a9408a95ed47e7b02d0d01468856adf45e))
* **flow,adapters:** M23 Phase 3 — Project-Directory-Bound Tasks & Portable Bindings ([#275](https://github.com/aer-works/baton/issues/275)) ([2743172](https://github.com/aer-works/baton/commit/274317233a1f7c419f746c1868bec80b19944e8c))
* **flow,cli:** Engine identity recorded, liveness probed at read time -- a silence gets its provenance ([#796](https://github.com/aer-works/baton/issues/796)) ([5d4d914](https://github.com/aer-works/baton/commit/5d4d9147aeef2fec13bf449726e2db687c3617ea))
* **flow,cli:** Engine-provisioned git worktree workspaces (Rung 4, progresses [#669](https://github.com/aer-works/baton/issues/669)) ([#937](https://github.com/aer-works/baton/issues/937)) ([971f390](https://github.com/aer-works/baton/commit/971f39003253931e9f02304ea6e004d18e5bacb2))
* **flow:** Bounded room open via a projection checkpoint ([#903](https://github.com/aer-works/baton/issues/903) scope 1a) ([#961](https://github.com/aer-works/baton/issues/961)) ([89f556f](https://github.com/aer-works/baton/commit/89f556fcc2384d22a267c018fb639b98edcf4756))
* **flow:** First-class resume verb -- continue a worker session with a message ([#1388](https://github.com/aer-works/baton/issues/1388)) ([1453d04](https://github.com/aer-works/baton/commit/1453d042cf83b951b0b4ea053ac73458fd694297))
* **flow:** Machine completion contract -- --wait, exit codes, status --json, terminal state for pre-ledger failures ([#1374](https://github.com/aer-works/baton/issues/1374)) ([a6ca232](https://github.com/aer-works/baton/commit/a6ca2322b155aae37a46a26865e7ea7b1cf6ee0b))
* **flow:** Operator retry-now reaches a quota-parked step ([#815](https://github.com/aer-works/baton/issues/815)) ([#848](https://github.com/aer-works/baton/issues/848)) ([644d070](https://github.com/aer-works/baton/commit/644d070cfef1d9ea4589ed564bfaad439f43b598))
* **flow:** Split PausePoint into needs-input and ready-for-review kinds ([#435](https://github.com/aer-works/baton/issues/435)) ([82a9d95](https://github.com/aer-works/baton/commit/82a9d955da69be3bed3778d25415f9f2ec0185e7))
* **flow:** WriterUtcTimestamp on the journal envelope; aer status renders per-step times ([#824](https://github.com/aer-works/baton/issues/824)) ([267baf9](https://github.com/aer-works/baton/commit/267baf92f15e094f3efa7a2d90deb964be1b0d3f))
* **store:** Per-execution usage accounting -- wall-clock always, tokens where the vendor reports them ([#1389](https://github.com/aer-works/baton/issues/1389)) ([15b6519](https://github.com/aer-works/baton/commit/15b6519d55acd404ce938defd55f5f0e23a92d37))
* **ui,cli:** Running out of plan reads as a state with a reset time on every surface ([#1116](https://github.com/aer-works/baton/issues/1116)) ([#1123](https://github.com/aer-works/baton/issues/1123)) ([a7ded6a](https://github.com/aer-works/baton/commit/a7ded6a69dfb9c5d6119081bcba1a2e44888ef4f))
* **ui:** M15 Phase 1 — Mutation seam + start/resume a workflow ([#145](https://github.com/aer-works/baton/issues/145)) ([8b9c12e](https://github.com/aer-works/baton/commit/8b9c12e686e1a8676df75fbfb7d4ab40ec062e33))
* **ui:** M15 Phase 4 — Cancel: targeted live-execution cancel + host stop ([#148](https://github.com/aer-works/baton/issues/148)) ([f1a2361](https://github.com/aer-works/baton/commit/f1a2361b9ed887f17dbdd941f61034cb6bf63203))
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


### Miscellaneous

* **cli:** Add TryInvocation lines to CLI and validation errors ([#1382](https://github.com/aer-works/baton/issues/1382)) ([859dadc](https://github.com/aer-works/baton/commit/859dadc0b07fc7f6929a8dbef6eeff549bbf581e))
* release main ([#226](https://github.com/aer-works/baton/issues/226)) ([952a0bf](https://github.com/aer-works/baton/commit/952a0bfb546b2897798e7303600dd62d5ebebeec))
* release main ([#231](https://github.com/aer-works/baton/issues/231)) ([b2ddb02](https://github.com/aer-works/baton/commit/b2ddb0271e17077780ef12f7223e3f5b3410be5c))
* release main ([#253](https://github.com/aer-works/baton/issues/253)) ([e48f03b](https://github.com/aer-works/baton/commit/e48f03b14c6d25725b8301176d0002e176aa6de4))
* release main ([#257](https://github.com/aer-works/baton/issues/257)) ([a3431a6](https://github.com/aer-works/baton/commit/a3431a65003c4fa4b18df2273564df08f5923bc8))
* release main ([#280](https://github.com/aer-works/baton/issues/280)) ([c36a7f1](https://github.com/aer-works/baton/commit/c36a7f174b98209feb3805b8564236c05460993f))
* release main ([#309](https://github.com/aer-works/baton/issues/309)) ([0696b33](https://github.com/aer-works/baton/commit/0696b33a118244dfc33899d58b051740f13b1f9a))
* **release:** Split release-please into linked-versions groups (core, desktop) ([#225](https://github.com/aer-works/baton/issues/225)) ([86da732](https://github.com/aer-works/baton/commit/86da732bc469a1a86ba412b1863fb0506aa3e40b))

## [0.20.0](https://github.com/aer-works/baton/compare/cli-v0.19.0...cli-v0.20.0) (2026-08-26)


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
* Conversational permission gate (0022) — engine, adapters, desktop + mobile ([#1087](https://github.com/aer-works/baton/issues/1087)) ([9a9192a](https://github.com/aer-works/baton/commit/9a9192a4c9446ab4c12dea0379b4979d6b729ce6))
* **flow,cli:** Engine identity recorded, liveness probed at read time -- a silence gets its provenance ([#796](https://github.com/aer-works/baton/issues/796)) ([5d4d914](https://github.com/aer-works/baton/commit/5d4d9147aeef2fec13bf449726e2db687c3617ea))
* **flow,cli:** Engine-provisioned git worktree workspaces (Rung 4, progresses [#669](https://github.com/aer-works/baton/issues/669)) ([#937](https://github.com/aer-works/baton/issues/937)) ([971f390](https://github.com/aer-works/baton/commit/971f39003253931e9f02304ea6e004d18e5bacb2))
* **flow:** Bounded room open via a projection checkpoint ([#903](https://github.com/aer-works/baton/issues/903) scope 1a) ([#961](https://github.com/aer-works/baton/issues/961)) ([89f556f](https://github.com/aer-works/baton/commit/89f556fcc2384d22a267c018fb639b98edcf4756))
* **flow:** Operator retry-now reaches a quota-parked step ([#815](https://github.com/aer-works/baton/issues/815)) ([#848](https://github.com/aer-works/baton/issues/848)) ([644d070](https://github.com/aer-works/baton/commit/644d070cfef1d9ea4589ed564bfaad439f43b598))
* **flow:** Split PausePoint into needs-input and ready-for-review kinds ([#435](https://github.com/aer-works/baton/issues/435)) ([82a9d95](https://github.com/aer-works/baton/commit/82a9d955da69be3bed3778d25415f9f2ec0185e7))
* **flow:** WriterUtcTimestamp on the journal envelope; aer status renders per-step times ([#824](https://github.com/aer-works/baton/issues/824)) ([267baf9](https://github.com/aer-works/baton/commit/267baf92f15e094f3efa7a2d90deb964be1b0d3f))
* **ui,cli:** Running out of plan reads as a state with a reset time on every surface ([#1116](https://github.com/aer-works/baton/issues/1116)) ([#1123](https://github.com/aer-works/baton/issues/1123)) ([a7ded6a](https://github.com/aer-works/baton/commit/a7ded6a69dfb9c5d6119081bcba1a2e44888ef4f))
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
