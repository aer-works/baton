# Changelog

## [0.6.0](https://github.com/aer-works/aer-core/compare/aer-core-v0.5.0...aer-core-v0.6.0) (2026-07-10)


### Features

* **core:** Add environment and working-directory control to Task, the C ABI, and the .NET binding ([#87](https://github.com/aer-works/aer-core/issues/87)) ([4cf3f31](https://github.com/aer-works/aer-core/commit/4cf3f313434180cb289ffd2a07818d68f15fc068)), closes [#77](https://github.com/aer-works/aer-core/issues/77)
* **dotnet:** Bridge C callback to managed delegate (M5 [#61](https://github.com/aer-works/aer-core/issues/61)) ([#70](https://github.com/aer-works/aer-core/issues/70)) ([e80bc4c](https://github.com/aer-works/aer-core/commit/e80bc4cc746a03a24419f5e34681f136a8f5bc22))
* **dotnet:** High-level AerTask managed wrapper with CancellationToken integration ([#91](https://github.com/aer-works/aer-core/issues/91)) ([5dfb2fa](https://github.com/aer-works/aer-core/commit/5dfb2fa5f0df2604a029089715e8f82776188cad))
* **dotnet:** Safe handles for task and cancel pointers ([#69](https://github.com/aer-works/aer-core/issues/69)) ([32149fd](https://github.com/aer-works/aer-core/commit/32149fd3f4ceead0fb23210754df6fbd7be947b2))
* **dotnet:** Scaffold Aer.Core P/Invoke layer and xUnit project ([e4c744c](https://github.com/aer-works/aer-core/commit/e4c744c1d0a7e0f610cf67544c9130dcf68f57ab))
* **dotnet:** Scaffold Aer.Core P/Invoke layer and xUnit project (M5 issue [#59](https://github.com/aer-works/aer-core/issues/59)) ([a8ea939](https://github.com/aer-works/aer-core/commit/a8ea939abcc0b77d6ec1e6ec33a55a64cbab8e4e))


### Bug Fixes

* **core:** Deliver captured output chunks live during execution ([#83](https://github.com/aer-works/aer-core/issues/83)) ([03bcebe](https://github.com/aer-works/aer-core/commit/03bcebebd8546c0651010bd7f5ad4bc8e3c3ee6e)), closes [#72](https://github.com/aer-works/aer-core/issues/72)
* **core:** Kill the process tree when the event callback panics ([#86](https://github.com/aer-works/aer-core/issues/86)) ([0cfa9e3](https://github.com/aer-works/aer-core/commit/0cfa9e3909e463cb74dec99a39e1464927c8998e)), closes [#75](https://github.com/aer-works/aer-core/issues/75)
* **core:** Probe tree liveness before cancel/timeout kills to avoid misreporting natural exits ([#84](https://github.com/aer-works/aer-core/issues/84)) ([db17046](https://github.com/aer-works/aer-core/commit/db17046c85ad5f902f526676d4c7673b7de594f3)), closes [#73](https://github.com/aer-works/aer-core/issues/73)
* **os:** Kill the spawned child when job assignment fails on Windows ([#85](https://github.com/aer-works/aer-core/issues/85)) ([0246de8](https://github.com/aer-works/aer-core/commit/0246de8f840809ce3fdda0c21d744442ca7d7903)), closes [#74](https://github.com/aer-works/aer-core/issues/74)
* **os:** terminate job object at root exit so timeout/cancel paths cannot hang or misreport ([#81](https://github.com/aer-works/aer-core/issues/81)) ([dcb1042](https://github.com/aer-works/aer-core/commit/dcb104221c2d242dbcd1c4dfa83380605cf8746b)), closes [#71](https://github.com/aer-works/aer-core/issues/71)


### Performance Improvements

* **os:** Poll for tree death during the Unix kill grace window instead of sleeping it out ([#90](https://github.com/aer-works/aer-core/issues/90)) ([362155a](https://github.com/aer-works/aer-core/commit/362155a0d6bf961d8d5f9343f22905cdfb1f27b5)), closes [#76](https://github.com/aer-works/aer-core/issues/76)


### Documentation

* Add IMPLEMENTATION_PLAN.md and redirect AER Overview to aer-flow ([dad2d58](https://github.com/aer-works/aer-core/commit/dad2d58db5b517d20ef7a4dce6e6181c2058755b))
* Add IMPLEMENTATION_PLAN.md and redirect AER Overview to aer-flow ([850db7a](https://github.com/aer-works/aer-core/commit/850db7a29cdf258ff0ee7539aa7dba509f514c45))
* Land plan refinements and record M5 as complete ([#94](https://github.com/aer-works/aer-core/issues/94)) ([1686e52](https://github.com/aer-works/aer-core/commit/1686e52c6912e25c7df827acf35e90e0a658f231)), closes [#93](https://github.com/aer-works/aer-core/issues/93)
* **spec:** Correct stale M4 status and qualify the Unix no-orphans guarantee ([#82](https://github.com/aer-works/aer-core/issues/82)) ([a56adcc](https://github.com/aer-works/aer-core/commit/a56adccf8c44cd2480ccfae1abeac5e88ffa944e)), closes [#80](https://github.com/aer-works/aer-core/issues/80)


### Continuous Integration

* Kill dotnet processes before cleanup to fix Windows EPERM on post-job ([44f78cb](https://github.com/aer-works/aer-core/commit/44f78cb0f32308f2bab5d2c89e0418290632ce4a))
* Remove dotnet-sdk from pixi, use actions/setup-dotnet in CI ([7389794](https://github.com/aer-works/aer-core/commit/738979478beabdd8a00b73d8b28b9db8e1338350))
* Shut down Roslyn build server before cleanup to fix Windows EBUSY ([b34a9b4](https://github.com/aer-works/aer-core/commit/b34a9b42a58fffff2b721b7cd40104f54b0e3f2e))


### Tests

* **dotnet:** Behavioral-contract integration tests and C# usage docs ([#92](https://github.com/aer-works/aer-core/issues/92)) ([848e9f7](https://github.com/aer-works/aer-core/commit/848e9f7ad41630454f7490dc13653880653db35a)), closes [#64](https://github.com/aer-works/aer-core/issues/64)


### Miscellaneous

* **core:** FFI and code hygiene bundle ([#88](https://github.com/aer-works/aer-core/issues/88)) ([66e2259](https://github.com/aer-works/aer-core/commit/66e22596b5b81c159aa558e340bd9e934f7b74f7)), closes [#78](https://github.com/aer-works/aer-core/issues/78)

## [0.5.0](https://github.com/aer-works/aer-core/compare/aer-core-v0.4.0...aer-core-v0.5.0) (2026-06-29)


### Features

* **core:** M4 FFI boundary — C-compatible ABI ([#43](https://github.com/aer-works/aer-core/issues/43)) ([d44ad57](https://github.com/aer-works/aer-core/commit/d44ad57380af1aa50de3dfa658a82a1a005bd936))
* **core:** M4b — Observation Tier (StdoutChunk/StderrChunk capture) ([#47](https://github.com/aer-works/aer-core/issues/47)) ([259060f](https://github.com/aer-works/aer-core/commit/259060f7635a27ae9ebd2b79e51fe61dc767c473)), closes [#44](https://github.com/aer-works/aer-core/issues/44)
* **core:** M4c — Cancellation and ExitReason ([#48](https://github.com/aer-works/aer-core/issues/48)) ([8d32071](https://github.com/aer-works/aer-core/commit/8d3207173593a9ff5671576de1fc56dc93e626a6)), closes [#45](https://github.com/aer-works/aer-core/issues/45)
* **core:** Process tree cleanup — Windows Job Objects + Unix setsid ([#36](https://github.com/aer-works/aer-core/issues/36)) ([c9792a4](https://github.com/aer-works/aer-core/commit/c9792a44f7e4295aabbbda6a508630875cc2c5bd))
* **core:** Timeout and kill escalation (M2) ([#27](https://github.com/aer-works/aer-core/issues/27)) ([a97e623](https://github.com/aer-works/aer-core/commit/a97e623c96bfaedf3ab8a3fb60be2122c6305602))


### Bug Fixes

* **ci:** guard against null projectItems when issue/PR not on board ([#19](https://github.com/aer-works/aer-core/issues/19)) ([aebdfaf](https://github.com/aer-works/aer-core/commit/aebdfaf42e98f3b989e5f357b59e5b501bc034db))
* **ci:** use PROJECT_TOKEN for project board automation ([#18](https://github.com/aer-works/aer-core/issues/18)) ([f072a4c](https://github.com/aer-works/aer-core/commit/f072a4c5e9eb3dfeb525a396c288c065fa4c3c2b))
* **core:** Concurrent pipe drain, Unix orphan cleanup on wait error, M3 docs ([#40](https://github.com/aer-works/aer-core/issues/40)) ([cab0d44](https://github.com/aer-works/aer-core/commit/cab0d441db46d30da956b6576ba4f4c7878636b5))
* **core:** Process tree deadlock fix, pre_exec safety, and M3 tests ([#39](https://github.com/aer-works/aer-core/issues/39)) ([a64c682](https://github.com/aer-works/aer-core/commit/a64c682a10c0b956b5b0f99ab7c0a75ddbb1709d))


### Documentation

* Commit M4 spec, reorganize spec folder, update CLAUDE.md for dotnet structure ([#52](https://github.com/aer-works/aer-core/issues/52)) ([9f67f83](https://github.com/aer-works/aer-core/commit/9f67f83a933fc2bdd74e7661d3d92419d90ea5c3))
* **examples:** Add capture and cancel examples for M4b/M4c ([#50](https://github.com/aer-works/aer-core/issues/50)) ([e8af587](https://github.com/aer-works/aer-core/commit/e8af587f91c854a8361ac8a9a40e274b5849411c)), closes [#49](https://github.com/aer-works/aer-core/issues/49)
* **examples:** Add M2 timeout and M3 process tree examples ([#41](https://github.com/aer-works/aer-core/issues/41)) ([3d2a116](https://github.com/aer-works/aer-core/commit/3d2a116d54c2c83f9aa3a54d279695b1961a8991))
* expand README title to include full name (Agent Execution Runtime) ([f072a4c](https://github.com/aer-works/aer-core/commit/f072a4c5e9eb3dfeb525a396c288c065fa4c3c2b))
* Mark M2 as in progress in README roadmap ([#29](https://github.com/aer-works/aer-core/issues/29)) ([5eeaf4f](https://github.com/aer-works/aer-core/commit/5eeaf4fd45867b44d0e5381bc5f5aeb79002d24f))
* **spec:** Add M2 timeout & kill escalation contract ([#26](https://github.com/aer-works/aer-core/issues/26)) ([68587a4](https://github.com/aer-works/aer-core/commit/68587a4e14980f001f28c930f81eebeb1bbe168d))
* **spec:** Add M3 process tree cleanup contract ([#35](https://github.com/aer-works/aer-core/issues/35)) ([b37393c](https://github.com/aer-works/aer-core/commit/b37393cee7bb87a687161158796b0ad7f94fee55)), closes [#31](https://github.com/aer-works/aer-core/issues/31)
* update README and CLAUDE for aer-core restructure and M4 examples ([13055bf](https://github.com/aer-works/aer-core/commit/13055bfdd2d2f18e50c99039cc667491c0ef7b03))


### Continuous Integration

* fix org name in project automation ([c1cec16](https://github.com/aer-works/aer-core/commit/c1cec16616b9fca6eda52c555f664e0977c94775))
* fix org name in project automation ([2b5cc36](https://github.com/aer-works/aer-core/commit/2b5cc365b2378c1e08944c774b636c71d5d74142))


### Tests

* **core:** M2 timeout and kill escalation integration tests ([#28](https://github.com/aer-works/aer-core/issues/28)) ([c063674](https://github.com/aer-works/aer-core/commit/c06367466d9a4f9d90baa793eaf7de3ca48a809d))


### Miscellaneous

* **core:** Milestone 1 — core scaffold and process lifecycle ([#14](https://github.com/aer-works/aer-core/issues/14)) ([abf11ff](https://github.com/aer-works/aer-core/commit/abf11ff32bede0adfd64cf8cef9952cd5ca2caae))
* flatten core/ directory for aer-core repository rename ([e09065c](https://github.com/aer-works/aer-core/commit/e09065cd400799c669b47b12747195734be17b05))
* flatten core/ directory to root for aer-core repository rename ([f9fd01f](https://github.com/aer-works/aer-core/commit/f9fd01fab6dc6afe91d31f5410576f59d6bc287f))
* **main:** release core 0.1.1 ([#16](https://github.com/aer-works/aer-core/issues/16)) ([0396983](https://github.com/aer-works/aer-core/commit/039698397a6decb5a6ee25e787469cf2373f03a9))
* **main:** release core 0.2.0 ([#30](https://github.com/aer-works/aer-core/issues/30)) ([3ad67ff](https://github.com/aer-works/aer-core/commit/3ad67ff1c6e8a52d256767064c0825d189966658))
* **main:** release core 0.3.0 ([#38](https://github.com/aer-works/aer-core/issues/38)) ([cf42064](https://github.com/aer-works/aer-core/commit/cf4206464337aec8c1d18e6e3724f8a1904f7ebc))
* **main:** release core 0.4.0 ([2ec5ac9](https://github.com/aer-works/aer-core/commit/2ec5ac9aa3aa314e6427fffac924f1f48149c381))
* **main:** release core 0.4.0 ([a093422](https://github.com/aer-works/aer-core/commit/a0934227211412d672596540dde49ece7a8e20a8))
* update release-please config for flat repo ([5baf08f](https://github.com/aer-works/aer-core/commit/5baf08f68e6c5979f03cfd10e434f855318a165d))
* update release-please config for flat repo ([7cefe08](https://github.com/aer-works/aer-core/commit/7cefe08694b61185ae862e48d77690c9bd62f285))

## [0.4.0](https://github.com/aer-runtime/aer/compare/core-v0.3.0...core-v0.4.0) (2026-06-28)


### Features

* **core:** M4 FFI boundary — C-compatible ABI ([#43](https://github.com/aer-runtime/aer/issues/43)) ([d44ad57](https://github.com/aer-runtime/aer/commit/d44ad57380af1aa50de3dfa658a82a1a005bd936))
* **core:** M4b — Observation Tier (StdoutChunk/StderrChunk capture) ([#47](https://github.com/aer-runtime/aer/issues/47)) ([259060f](https://github.com/aer-runtime/aer/commit/259060f7635a27ae9ebd2b79e51fe61dc767c473)), closes [#44](https://github.com/aer-runtime/aer/issues/44)
* **core:** M4c — Cancellation and ExitReason ([#48](https://github.com/aer-runtime/aer/issues/48)) ([8d32071](https://github.com/aer-runtime/aer/commit/8d3207173593a9ff5671576de1fc56dc93e626a6)), closes [#45](https://github.com/aer-runtime/aer/issues/45)


### Documentation

* **examples:** Add capture and cancel examples for M4b/M4c ([#50](https://github.com/aer-runtime/aer/issues/50)) ([e8af587](https://github.com/aer-runtime/aer/commit/e8af587f91c854a8361ac8a9a40e274b5849411c)), closes [#49](https://github.com/aer-runtime/aer/issues/49)

## [0.3.0](https://github.com/aer-runtime/aer/compare/core-v0.2.0...core-v0.3.0) (2026-06-26)


### Features

* **core:** Process tree cleanup — Windows Job Objects + Unix setsid ([#36](https://github.com/aer-runtime/aer/issues/36)) ([c9792a4](https://github.com/aer-runtime/aer/commit/c9792a44f7e4295aabbbda6a508630875cc2c5bd))


### Bug Fixes

* **core:** Concurrent pipe drain, Unix orphan cleanup on wait error, M3 docs ([#40](https://github.com/aer-runtime/aer/issues/40)) ([cab0d44](https://github.com/aer-runtime/aer/commit/cab0d441db46d30da956b6576ba4f4c7878636b5))
* **core:** Process tree deadlock fix, pre_exec safety, and M3 tests ([#39](https://github.com/aer-runtime/aer/issues/39)) ([a64c682](https://github.com/aer-runtime/aer/commit/a64c682a10c0b956b5b0f99ab7c0a75ddbb1709d))


### Documentation

* **examples:** Add M2 timeout and M3 process tree examples ([#41](https://github.com/aer-runtime/aer/issues/41)) ([3d2a116](https://github.com/aer-runtime/aer/commit/3d2a116d54c2c83f9aa3a54d279695b1961a8991))

## [0.2.0](https://github.com/aer-runtime/aer/compare/core-v0.1.1...core-v0.2.0) (2026-06-26)


### Features

* **core:** Timeout and kill escalation (M2) ([#27](https://github.com/aer-runtime/aer/issues/27)) ([a97e623](https://github.com/aer-runtime/aer/commit/a97e623c96bfaedf3ab8a3fb60be2122c6305602))


### Tests

* **core:** M2 timeout and kill escalation integration tests ([#28](https://github.com/aer-runtime/aer/issues/28)) ([c063674](https://github.com/aer-runtime/aer/commit/c06367466d9a4f9d90baa793eaf7de3ca48a809d))

## [0.1.1](https://github.com/aer-runtime/aer/compare/core-v0.1.0...core-v0.1.1) (2026-06-26)


### Miscellaneous

* **core:** Milestone 1 — core scaffold and process lifecycle ([#14](https://github.com/aer-runtime/aer/issues/14)) ([abf11ff](https://github.com/aer-runtime/aer/commit/abf11ff32bede0adfd64cf8cef9952cd5ca2caae))
