# Changelog

## [0.3.0](https://github.com/aer-works/baton/compare/mobile-v0.2.0...mobile-v0.3.0) (2026-08-08)


### Features

* **daemon,ui:** Switcher orders by last activity, derived from the journal ([#640](https://github.com/aer-works/baton/issues/640)) ([#1009](https://github.com/aer-works/baton/issues/1009)) ([439cb61](https://github.com/aer-works/baton/commit/439cb618c3ef87038faa5c20b3f601305c8b244f))
* **mobile:** First-run rooms screen gets a "New room" primary action (J8) ([#1043](https://github.com/aer-works/baton/issues/1043)) ([ec1e2b7](https://github.com/aer-works/baton/commit/ec1e2b768813678c24e30228736d061feeb1ad10))
* **mobile:** Make the switcher the phone landing, with tap-to-open rooms (front-door rebuild, slice 1) ([#1046](https://github.com/aer-works/baton/issues/1046)) ([7946bab](https://github.com/aer-works/baton/commit/7946bab12f7cbd9ef3cdb70f7e1d6343974466cf))
* **mobile:** Truthful room states on the switcher — reply vs review, waiting-on-you first (J3, slice 2a) ([#1050](https://github.com/aer-works/baton/issues/1050)) ([c089190](https://github.com/aer-works/baton/commit/c089190c00d07e5805ccedbf86005f213efa3c64))
* **ui,mobile:** Generate both toolkits' themes from one token file ([#450](https://github.com/aer-works/baton/issues/450)) ([c4666a2](https://github.com/aer-works/baton/commit/c4666a29796bbe0f76d4b7803b09b8835fa4b026))
* **ui,mobile:** Point both apps at the shipped typefaces ([#457](https://github.com/aer-works/baton/issues/457)) ([360cd81](https://github.com/aer-works/baton/commit/360cd8189a1e69f6f4eb278501dc99b3c91c614a))
* **ui,mobile:** Ship Source Sans 3 + JetBrains Mono and give code its own surface ([#454](https://github.com/aer-works/baton/issues/454)) ([f9b742e](https://github.com/aer-works/baton/commit/f9b742eb79ec5be5e30dfa0d277c8a75ada6e26e))


### Bug Fixes

* **mobile:** Rename Forget pairing to honest sign-out with confirmation ([#399](https://github.com/aer-works/baton/issues/399)) ([34f5a58](https://github.com/aer-works/baton/commit/34f5a586e6915ae7279b695fbe9b090c17431b01))
* **mobile:** Starting a non-chat template leaves the phone on the empty state ([#389](https://github.com/aer-works/baton/issues/389)) ([2405bf6](https://github.com/aer-works/baton/commit/2405bf65c862440f37d5ecfee36fed614bbf26eb))
* **ui,mobile:** Complete the status vocabulary and make fill agree across toolkits ([#463](https://github.com/aer-works/baton/issues/463)) ([6dd8c87](https://github.com/aer-works/baton/commit/6dd8c874e18e3b11211b349905f346a7fd144d3a))
* **ui,mobile:** Draw status marks as shapes instead of codepoints ([#460](https://github.com/aer-works/baton/issues/460)) ([8f74855](https://github.com/aer-works/baton/commit/8f7485588c8cd7cd1bc9c2e3ebbf87002d56ee68))


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
