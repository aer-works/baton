# AER Flow — Milestone history

**This is provenance, not authority.** It is the append-only record of completed milestones: a
one-paragraph **summary** of what each shipped, and the reasoning it left behind — read when
something in the code or a runbook makes you ask *"why is this here?"*. Newest first.

> **It is never the justification for current work.** A rule you must follow today is a numbered
> record in [`docs/decisions/`](decisions/); what is being built now is [`docs/plan.md`](plan.md).
> If something in this file is still binding, promote it to a numbered record and cite that instead.
>
> *(Renamed from `decisions-of-record.md` — the old name read as a rival to `docs/decisions/`, and
> nothing said which of the two you were supposed to consult. See
> [`decisions/README.md`](decisions/README.md) for the full split.)*

Entries are extracted verbatim from each milestone's phase work as it completed and are never
rewritten to change meaning. Each milestone's phase checklist lives in its closed GitHub milestone;
the full phase plans live in the linked issues.

## Milestone summaries

What each completed milestone shipped, newest first.

**M25: Re-architecture** — the milestone that stopped adding surfaces and went back to what the product is. It shipped in two halves. The **structural half** paid down what M20–M24 had accumulated: one storage root for sessions and tasks with migration (#333) behind an `AerPaths` seam (#317), per-session host state so the daemon can hold more than one room at a time (#335) with its turns serialised against re-materialization (#393), the three god files decomposed ahead of the rebuild (#381, #425, #426), `PausePoint` split into *needs input* and *ready for review* (#334), Home/Task/Tasks/Chat collapsed into one switcher shell (#336), architecture invariants made enforceable as tests (#370), a token file generating both toolkits' themes with real shipped typefaces (#345, #453, #456), journeys established as a first-class spec artifact driven by real UI (#312, #313) with the UI driving harness landed as supported tooling (#328), tests isolated from the real `~/.aer` (#318), and a long run of daemon/mobile correctness fixes found by actually using the thing — desktop work never broadcasting (#330), a chat turn deleting a live event log (#354), a restart stranding every paired phone (#347), a session send reporting success while stalling the chat forever (#341). The **design half** was a ground-up pause: seven artifacts (`docs/design/`) that redefined the product as *a room you talk in, with more than one model in it, on subscriptions you already pay for* — landing decision records [0012](decisions/0012-what-aer-flow-is.md)–[0019](decisions/0019-consulting-is-not-deciding.md), and a live probe of what each vendor CLI can actually enforce and ask (#472) that corrected a load-bearing wrong belief: both CLIs fail closed headless, and a blocking MCP tool really does hold a vendor turn open, which is what makes a permission gate implementable at all. M25 shipped real UI — the switcher shell, both toolkits' themes, mobile parity, the activity surface — and then decided that surface is **what the rebuild replaces**, not what it builds on: the room-model UI is deliberately unbuilt at close, because the design half arrived after the presentation half and judged it. The rebuild is M26–M30, sequenced by what you can *do* rather than by capability, per [0012](decisions/0012-what-aer-flow-is.md).

> M25's durable decisions are **numbered records, not entries in this file** — that is the rule at
> the top of this document working as intended, and it is why this milestone has no section below.
> Start at [`decisions/README.md`](decisions/README.md). What the design pass settled but the
> records did not carry is tracked in [`design/coverage-audit.md`](design/coverage-audit.md).

**M24: Interactive Sessions & Unified Task Creation** — turned AER into a full replacement for the core Claude Code / Antigravity loop: a daemon-orchestrated **chat primitive** (a one-step workflow `Supersede`d once per human turn, reusing M9's human worker and M23's hardened supersede chains, with each vendor's own native session resumption carrying context and a per-session safety ceiling bounding runaway quota and event-log growth), **live in-turn progress streaming** (Claude's `stream-json` partial messages forwarded over the existing WebSocket; a generic running indicator for `agy`, which surfaces no equivalent), **worker capability discovery and in-session compact**, **codebase sessions** bound to a real project directory plus a Known Projects registry so a phone can pick one without a raw path, **Unified Task Creation** (Chat / Codebase Session / Two-Vendor Dialogue as the zero-authoring front door on desktop and mobile, with dedicated desktop `ChatView` and mobile `ChatScreen`; DAG authoring demoted to an Advanced path, not removed), and **task/session lifecycle management** (archive/unarchive/delete behind a managed-roots containment guard, plus the first fleet-status list of all active work either app has had). Planned in issue #258.

**M23: Generic Dialogue & Project Packaging** — generalized the Dialogue Worker from a fixed two-party exchange to an N-party, condition-stoppable loop (safe by default regardless of configured `TurnBudget`), authored as a first-class Template Editor step type instead of wizard-only; resolved, with test coverage, that an already-superseded step's target may legally be named in a second Supersede — the load-bearing prerequisite for M24's repeatedly-superseded chat primitive; and let a task's worker invocation point at a real project directory (`AerTask.WithCwd` wired through `WorkerInvocation` → the adapters → `CoreDispatchTarget` → `CoreDispatcher.DispatchAsync`), bundling a `WorkerBindingConfigEntry.PromptTemplate` absolute-path portability fix and a per-machine profile mapping so the resulting bindings survive a move to another machine.

**M22: Workflow Template Library** — shipped a built-in template catalog (`solo-run` and `review-run`) materialized against PATH-probed vendor CLIs (`claude`, `gemini`), daemon template endpoints (`GET /api/templates`, `POST /api/templates/run`), desktop (`TemplatePickerWindow`) and mobile (`inbox_screen.dart`) template picker UIs allowing zero-path task creation, and artifact-referenced supply enabling remote mobile send-back (Supersede) with no host filesystem access.

**M21: Zero-Config Remote Control & Permission Scopes** — shipped structured vendor-neutral permission scope models (`PermissionGrant` for file and command access), `Aer.Mobile` (Flutter remote client) with QR code pairing and live task streaming over WebSockets, zero-config embedded Tailscale transport (`aer-sidecar` Go node on desktop, embedded `tsnet` via CGO and RFC 6455 `ws_codec.dart`/`ws_client.dart` over `tcp.dial` on mobile), and closed M20's deferred token management (60s pairing code countdown and interactive paired client revocation).

**M20: Daemonization & Remote Control** — extracted the scheduling pump out of the UI process into a lightweight background runner (daemon) that runs continuously. Exposed task read models, mutation interfaces, and WebSocket real-time updates over a loopback and remote-accessible API. Added single-instance mutex enforcement, constant-time authentication token validation, and process lifecycle supervision. Hardened tool invocation security by replacing command shell spawners (`cmd.exe /c` / `sh -c`) with shell-less direct binary execution and C#-side environment variable expansion. Designed and implemented the client-pairing gateway protocol allowing secure LAN/Wi-Fi remote clients to connect and authenticate against the daemon.

**M19: Product UX** — the product-UX overhaul the owner scoped at M18's completion: a navigation shell (Home's decision inbox + task cards, Task, Author) built behind a new Avalonia-free `Aer.Ui.Core` seam that completes M15's deferred MVVM migration; the Task view rebuilt needs-you-first with plain-language drill-in tabs and the precise engine record one disclosure away; guided authoring that writes durable workflow/binding files with zero hand-edited config, vendor CLI knowledge owned by its adapters/workers; a token-driven visual design pass (property-level restyling, status as border+tint, one accent per surface, a generated app icon) judged against an owner-adopted reference set (Linear, GitLab, Dagster, Stately.ai, GitButler/Neovide, n8n, Raycast) under a one-product-not-a-collage directive; and a headless completion gate driving the entire non-expert path — author, run, review, send back, finish — through the real UI controls over stub CLIs, in default CI on all three OSes. `docs/archive/walkthroughs/first-real-workflow.md`'s UI section rewritten to match.

**M18: Conversation View** — UI spec §10's conversation view over M17's durable transcript: the tolerant `Aer.Ui` read seam honoring §10.1's reader contract (discovery by `transcript.jsonl` presence alone, producing schema worker-owned per Flow spec §18.2), conversation rows plus per-execution rendering in the main window (file order, prompt on demand, malformed lines marked in place, selection re-rendered on every load so refresh follows a live exchange), gated by `ConversationRoundTripTests`: the real dialogue worker run to terminal over stub CLIs and the view asserted over its artifacts — including a failed exchange's forensic prefix. No live gate: nothing in the milestone touches a vendor CLI.

**M17: Dialogue Worker** — the first Case 2 encapsulated multi-model worker (Flow spec §18.2): `Aer.Workers.Dialogue`, a single executable running a bounded, multi-turn Claude ↔ Gemini (`agy`) exchange — each model's turn threaded into the other's next prompt — writing a durable `transcript.jsonl` plus its declared output, dispatched by Flow like any other worker through a third adapter registry entry (`"dialogue"`), runnable from CLI and UI, with the stub-vendor round trip proven in default CI on all three OSes and the live exchange gated by `pixi run smoke-dialogue` (permanently a human action item, not yet recorded). Opened with the real-use walkthrough the project had been missing (`docs/archive/walkthroughs/first-real-workflow.md`).

**M16: UI Authoring** — the last milestone of the original UI track: template and worker-bindings authoring in `Aer.Ui` — create/edit steps, dependencies, retry policies, metadata, `PausePoint`s/`SupersedeTargets`, and bindings entries, with live structural validation through Flow's own `WorkflowDefinitionValidator`, the stack's first template and bindings writers held to round-trip fidelity through the engine's own parsers/validators, and full authoring round trips (author from blank → save → run to terminal; edit a bound task's template → the diff view shows the divergence while the bound rendering stays byte-identical) proven in default CI on all three OSes.

**M15: UI Control Surface** — the second UI-track milestone: every §7 user action — start/resume a workflow, Approve/Reject, Retry-with-revision, Send-back, and Cancel (targeted and host stop) — exposed in `Aer.Ui` exclusively through Flow's mutation interface, via in-process reuse of the CLI command layer, mapped onto Flow's closed `DecisionType` set, and proven by UI-driven round trips over shell-stub workers on all three CI OSes in default CI.

**M14: UI Projection** — the first UI-track milestone: `Aer.Ui`, an Avalonia desktop app consuming `Aer.Flow`'s read model in-process — task/execution/decision projection with live polling, the DAG view, artifact lineage, the snapshot-vs-template diff, and a golden-projection determinism gate in default CI. Read-only throughout: no mutations (M15), no authoring (M16).

**M13: Distribution** — turned `aer` from a checkout-only build into an installable `dotnet tool`: single-platform packing, version wiring from `release-please`, multi-RID native-lib bundling, and an unattended CI round-trip check proving install → run → uninstall works with no live vendor auth (`pixi run verify-pack`, `scripts/verify-pack-roundtrip.sh`).

**M12: Full Control Surface** — the milestone that made the runnable library drivable: a second vendor (Gemini's `agy`) behind M11's unchanged protocol, and the mutation surface M9/M10 built exposed as `aer decide`/`aer cancel`, proven by a live mixed-vendor paused run decided from the terminal (`docs/runbooks/live-mixed-vendor-smoke.md`).

**M11: First Real Run** — the milestone that made the library runnable: the canonical worker-invocation protocol and adapter seam, the Claude adapter, the `aer run` pump, and a recorded green live two-step run (`docs/runbooks/live-claude-smoke.md`).

**M10: Cancellation & Edge Cases** — on-demand cancellation through the single mutation surface (intent recorded first), and crash-recovery made whole by reading back the Core half of the log.

**M9: External Decisions** — pause points, the four external decisions, the automatic invalidation cascade, human workers.

**M8: Reactive Scheduler** — fan-out/fan-in DAG with retries and concurrent dispatch.

**M7: Foundation** — linear A → B → C end-to-end, happy path only.

## Decisions of record

The durable decisions each milestone left behind, newest first — each entry cites the phase that decided it. Coverage is uneven: M20, M23, and M24 are represented by their summary above only, having shipped without a separate decisions pass into this file; their durable decisions live in their PRs and closed milestones. (Surfaced by the #367 docs audit.)

**M25 is absent from below by design, not omission** — it is the milestone that established [`docs/decisions/`](decisions/), so its durable decisions are numbered records 0001–0019 and belong there. It is the first milestone for which this section is correctly empty, and the intended state for every milestone after it.

## M22: Workflow Template Library

- **Pre-authored built-in template catalog (Phase 1)** — `BuiltInWorkflowTemplates` in `Aer.Adapters` exposes built-in template shapes (`solo-run`, `review-run`) with pre-authored, engine-valid `WorkflowDefinition` definitions and `WorkerBindingConfigEntry` dictionaries, materializing against whichever vendor CLIs (`claude`, `gemini`) are probed on system PATH via `VendorCliPresence`.
- **Daemon template materialization & execution endpoints (Phase 2)** — `GET /api/templates` lists available built-in templates and installed vendor CLIs. `POST /api/templates/run` materializes template files (`workflow.json`, `bindings.json`) into a daemon-managed directory (`~/.aer/tasks/task-{timestamp}`) and dispatches execution via `RoomClient.RunAsync` with zero caller-supplied path arguments, allowing clients with no host filesystem access to initiate tasks.
- **Desktop template picker (Phase 3)** — `TemplatePickerWindow` in `Aer.Ui` provides a visual dialog for picking built-in templates, selecting available vendor CLIs, specifying optional task names or initial prompts, and launching execution directly into the desktop Task view.
- **Mobile template picker (Phase 4)** — `DaemonClient` gains `listTemplates()` and `runTemplate(...)`. `Aer.Mobile` (`inbox_screen.dart`) surfaces a "Start from template" dialog in empty and active inbox states, giving the mobile app its first capability to start a new task from scratch over REST and observe live updates over WebSockets.
- **Artifact-referenced supply & mobile send-back (Phase 5)** — `POST /api/rooms/decide` accepts an optional `ArtifactReference` (`{ executionId, fileName }`). The daemon resolves the artifact path server-side using `ArtifactManager.ResolveOutputDirectory`, eliminating the need for remote clients to have host filesystem access. `Aer.Mobile` surfaces a "Send back to `<target>`" button on paused review steps, triggering Supersede with artifact-referenced feedback.

## M21: Zero-Config Remote Control & Permission Scopes

- **Permission grants are structured and vendor-neutral (Phase 1)** — `PermissionGrant` replaces opaque adapter flag strings (`ReadFiles`, `WriteFiles`, `RunShellCommands` with wildcard glob matching, `NetworkAccess`). Each adapter translates grants into vendor-native flags inside `Resolve()` (Adapter Isolation rule).
- **Mobile remote client is pure WebSocket + REST (Phase 2)** — `Aer.Mobile` (Flutter) pairs via QR code, listens to the daemon's WebSocket stream, and issues decisions (`Approve`, `Reject`, `Cancel`) over the daemon REST API.
- **Remote client artifact fetching and directory discovery (Phase 2)** — `GET /api/rooms/artifact` provides safe read-only access to step outputs for remote clients with no local filesystem access. WebSocket payloads carry `DirectoryPath` as a top-level property so clients listening to WS broadcasts can issue decision requests without prior host-side path knowledge.
- **Desktop pairing UX and single-scan auth (Phases 3, 7)** — `RemoteView` in `Aer.Ui` generates 60s transient pairing codes and renders the QR code containing connection info (`host`, `token`, `tsnetRouted`, `tskey`). Scanning the QR code embeds the Tailscale auth key (`tskey`) so pairing and tailnet joining happen in a single step with zero extra setup.
- **Zero-config Tailscale TCP splicing architecture (Phases 5, 7, 8)** — Desktop spawns `aer-sidecar.exe` (Go `tsnet`) when `--remote` is enabled, joining the tailnet as `aer-sidecar` and TCP-splicing incoming traffic directly to Kestrel loopback (`127.0.0.1`). Kestrel stays loopback-bound and protocol-agnostic. Mobile embeds `tsnet` via Go CGO (`tailscale` package), joining as `aer-mobile`. REST requests use `TailnetGateway().client` and WebSocket streams dial over `TailscaleWsSocket` (`tcp.dial`) with a pure Dart RFC 6455 codec (`ws_codec.dart`, `ws_client.dart`), requiring zero standalone Tailscale app installations on either device.
- **Client token management and revocation (Phase 6)** — M20's deferred token management is fully closed: pairing codes expire in 60s with auto-refresh and 5-attempt lockout, and paired devices can be revoked interactively from `RemoteView` via `DELETE /api/pairing/clients/{id}`.

## M19: Product UX

- **The reference set is the owner's, adopted verbatim (2026-07-17)** — Linear (inbox), GitLab
  (to-dos), Dagster (Launchpad), Stately.ai (visual↔config sync), GitButler and Neovim/Neovide
  (polished skin over presentation-agnostic core), n8n (DAG rendering), Raycast (chrome/tokens/
  keyboard gold standard), each mapped to the phase it informs in `docs/archive/ux/design-language.md`.
  Changing the set is an owner decision, not an implementation one; Phase 5's human gate is
  judged against it (Phase 1).
- **One product, not a collage (owner directive, 2026-07-17)** — the references calibrate the
  bar, they never supply the look: AER Flow has one identity of its own, and stitching together
  surfaces that each resemble their reference is the named failure mode. The identity is carried
  by the token system + status system + motion rules + vocabulary — defined once in
  `design-language.md` and implemented by every client (desktop now, remote/web later), so all
  surfaces read as the same product wearing different windows. Phase 5's review question is
  two-sided: holds up beside the reference *and* unmistakably the same product throughout.
  Corollaries: fit over fidelity (skip what doesn't suit the domain, without apology), and mine
  the references for capabilities, not just polish — a standout affordance we lack is adapted
  to our identity and folded into its natural phase, or surfaced to the owner if it would grow
  scope (Phase 1).
- **The vocabulary map is total for primary text and never renames semantics** — a spec term in
  a label/button/status line is a defect (Phase 6 checks for it), and a plain word that would
  imply behavior the engine doesn't have is wrong, not the engine ("send back" *is* supersede,
  mandatory feedback artifact included). Spec terms survive in tooltips/disclosure for §12
  traceability (Phase 1).
- **The non-expert audit generated zero engine requirements** — all twelve rows
  (`docs/archive/ux/non-expert-audit.md`) are organization, language, or authoring ergonomics,
  confirming the plan's first shaping fact. Two audit findings bind later phases beyond the
  plan's text: Phase 4 owes a **vendor-readiness surface** (read-only presence check, "Claude:
  available / Gemini: not found", never credential handling), and Phase 3's bindings pre-fill
  is recorded as **convenience, never remembered authority** (a visible, swappable picker
  default — §4's input-not-authority stance preserved) (Phase 1).
- **Tokens are a system, not values** — `design-language.md` fixes the token names, scales, and
  rules (semantic color only, 4px grid, two radii, three motion durations, status always
  color+icon+word); exact values are fixed when Phase 2 materializes the theme resource. A
  surface using a raw hex/size/pixel literal after Phase 2 is a defect (Phase 1).
- **The MVVM-completion constraint executed as written** — every re-homed surface's logic lives
  in an `Aer.Ui.Core` ViewModel (CommunityToolkit.Mvvm source generators, compiled bindings);
  `MainWindow` code-behind shrank to shell wiring plus a **transitional facade** of delegating
  control properties, kept deliberately so the not-yet-rebuilt rendering paths and every
  existing headless test compile unchanged — Phases 3–4 retire facade entries as they rebuild
  each surface properly, and a facade entry surviving past its surface's rebuild is a defect.
  No DI container, as recorded in the constraint (Phase 2).
- **The inbox scans all recent task directories** (the phase's named open question) — Home
  exists precisely for the moment no task is open yet; an inbox that only knew the open task
  would be empty exactly when it matters most. Bounded by the store-capped recents list;
  refreshes on Home activation plus the open task's poller tick — never its own timer (Phase 2).
- **§3's stale-recents rule renders as the greyed `Unavailable` card** — a recent that no
  longer loads stays visible ("Not available — moved, deleted, or not a task") with no inbox
  items and no live status: reflected, never an error, never silently pruned — the user
  recorded that history, and hiding it would misreport it (Phase 2).
- **The drill-in is a pure re-slice; the full record stays one disclosure away** — the per-step
  tabs (attempts / outputs / conversation / decisions) assert nothing the task-level panels
  don't already render; those precise, spec-vocabulary panels moved intact into a single
  collapsed **Details** expander (§12 traceability; ux-principles' progressive disclosure), not
  deleted. Their facade entries therefore stay until the phase that actually rebuilds them away
  — the retirement rule binds per surface, and these surfaces became the disclosure layer
  (Phase 3).
- **One decision authority, inline** — the needs-you-first decision cards bind the *same*
  `PausedStepViewModel` instances M15's decision surface rebuilds; the drill-in's paused step
  holds a reference, never a copy. Plain words on the buttons (Approve / Retry this step /
  Reject / Send back), spec terms in the tooltips (Phase 3).
- **The feedback-file picker is convenience over the same visible property** — the OS file
  dialog writes `RevisionFilePath`, the property the text box still binds and headless tests
  still set; a picker cannot be driven headlessly, and the path stays visible and swappable
  (the audit's convenience-never-authority stance, applied to files) (Phase 3).
- **Live-follow needed no new mechanism** — the M15 2-second poller already refreshes while
  the open task is non-terminal, and M18's conversation selection re-renders on every load;
  "on by default" was already the recorded behavior, now confirmed as the phase requirement
  (Phase 3).
- **Authored files live in a UI-managed default workspace** (the phase's first named open
  question) — `Documents/AER Flow/<workflow-name>`, visible in the flow and swappable via a
  folder picker, never required; task directories are created inside it per run
  (`task-<timestamp>`). Explicit-everywhere would put a path decision back at the start of the
  non-expert's very first action — the exact failure the audit's walkthrough recorded (Phase 4).
- **Vendor invocation knowledge lives with its owner, never in the UI** (the second named open
  question) — dialogue participants' command shapes moved from per-smoke-test duplication into
  `DialogueParticipantPresets` (Aer.Workers.Dialogue: the worker that invokes them), the
  read-only PATH probe lives in `VendorCliPresence` (Aer.Adapters: the layer that owns binary
  names), and the guided flow's presets pass **no explicit PermissionScope** — the adapter's own
  default governs, explained in plain words. The UI re-encodes nothing spike #21 isolated
  (Phase 4).
- **The readiness surface is informational, never a gate** — "Claude: available / Gemini: not
  found", refreshed on Author activation; nothing in the save/run path reads it (the audit's
  vendor-readiness finding, delivered read-only as recorded) (Phase 4).
- **Guided save goes through the editors' own writers** (`WorkflowDefinitionWriter`,
  `WorkerBindingConfigWriter`) — guided output and hand-authored files can never diverge in
  format, and the M16 editors remain in the Author view as the advanced disclosure (the Phase 3
  Details pattern, applied to authoring) (Phase 4).
- **Property-level restyling over Fluent's templates, not template replacement** — the custom
  control theme re-skins every stock control through token-valued property setters (color,
  radius, padding, focus, per-state brushes with Motion.Fast transitions); Fluent's control
  *shapes* are kept. Full re-templating buys nothing the identity needs and re-owns behavior
  (focus, accessibility) the framework already gets right (Phase 5).
- **Status renders as border + tint, never fill-only** — the `Status.*Bg` tint tokens joined
  both theme variants; the DAG node carries its status as a colored border over a tinted
  surface with the status word in the label (color+icon+word discipline, DAG tests re-pointed
  from named framework colors to token lookups — the only test change the phase needed) (Phase 5).
- **One accent button per surface** — Run, Approve, Save and run, Review, Create a workflow;
  everything else stays quiet. The nav rail marks the active section in accent (Phase 5).
- **The app icon is the mark, generated, committed** — a mini-DAG on the accent teal,
  multi-size PNG-in-ICO, produced by a scripted render (no design-tool dependency); regenerate
  by re-running the script if the accent ever changes (Phase 5). Refreshed post-M21 (issue #238,
  standalone follow-up, no milestone): a two-source fan-in mark on the real `Color.Accent` →
  `Color.AccentPressed` gradient token (this bullet's original render predated that token
  existing), curved Bezier edges instead of straight, tested down to 16×16 before being chosen
  over a fan-out alternative (illegible under 32px — the crowded single top node smears into the
  curve strokes; fan-in's two separate top nodes hold a readable silhouette at every size). Given
  to `Aer.Mobile` for the first time too (previously Flutter's unmodified stock icon) — one mark,
  shared across desktop and mobile launcher icons. An animated fan-in→fan-out→"A"→"AER Flow"
  wordmark treatment was considered for a splash/loading surface and is tracked as a future
  direction under M24, not built here — motion resolves the letterform ambiguity a static icon
  can't, so the animated and static marks intentionally differ.
- **The dialogue step's stub is a script at the adapter-registry boundary, not a fake vendor
  CLI** — `NonExpertPathGateTests` registers a `StubDialogueScriptAdapter` under the `"dialogue"`
  key that dispatches a local PowerShell/sh script writing a schema-valid transcript and the
  declared output directly, bypassing `DialogueWorkerAdapter`/`DialogueRunner` entirely.
  `DialogueParticipantPresets` hardcodes real `claude`/`agy` command shapes with no swap point
  reachable from the guided-authoring UI, so stubbing at that level isn't available to this test
  the way `ShellCommandWorkerAdapter` stubs the single-vendor step. This is judged acceptable
  because the live dialogue exchange itself is already M18's proven gate (`smoke-dialogue`); this
  gate's job is the UI/session/projection path around it — authoring, running, pausing, reading
  the conversation, sending back, finishing — which the stub exercises completely (Phase 6).
- **One headless test drives the entire non-expert path, not a suite of smaller ones** — author
  (zero hand-authored files, both a single-vendor and a dialogue step) → run → pause at the
  review gate → read the conversation through the Phase 3 drill-in → send back with a feedback
  file → resume to terminal, all through the real `MainWindow`/`NewWorkflowViewModel` controls.
  Splitting it would lose the property the gate exists to prove: that the whole path composes,
  not just its parts in isolation (Phase 6).
- **The walkthrough's CLI sections stay untouched; only its UI section is rewritten** — the CLI
  surface and durable file formats didn't change in M19, so `first-real-workflow.md`'s CLI
  commands and JSON stay exactly as a CLI user needs them; its UI section is rebuilt against
  Home/Task/Author and the guided authoring flow, closing the loop the Phase 1 audit opened
  against this same document (Phase 6).

## M18: Conversation View

- **A malformed transcript line projects as an explicit `TranscriptLine.Malformed` marker in
  place, never a silent skip and never a whole-projection failure** — resolving Phase 1's named
  open question in favor of the marker, per `DecisionRecord.Resolved`'s
  render-the-damage-honestly precedent. The marker carries only the 1-based line number: the raw
  bytes stay on disk for §12 traceability, and the view has nothing else it could honestly
  render. Blank/whitespace-only lines are skipped outright (they carry nothing to mark), and
  extra JSON fields beyond §10.1's five never malform a turn — the contract says "at least"
  (Phase 1).
- **File order is projection order** — `TranscriptProjection.Lines` preserves the file exactly;
  `Sequence` is projected data, never a sort key. §10.1 names file order as the order the turns
  happened, so reordering would let a buggy producer's claim override the durable record
  (Phase 1).
- **`TranscriptProjectionLoader` opens with `FileShare.ReadWrite`** — the producing worker holds
  the file open (`FileMode.Append` + `FileShare.Read`) for the whole exchange, so a
  load-on-refresh against a still-running execution must read what is durably there so far
  rather than fail on a sharing violation; whole lines are the writer's flush unit, so the only
  mid-write shape a reader can observe is a torn final line, which projects as `Malformed`
  (Phase 1).
- **The conversation view lives in the existing flat window, not a new top-level view** —
  resolving Phase 2's named open question: two sections directly after Execution history (entry
  rows derived from `Lineage.Executions` filtered by `TranscriptProjectionLoader.HasTranscript`,
  plus the rendered conversation), the anchor the existing surfaces made natural, with no new
  navigation chrome ahead of M19's design pass. Rendering stays code-behind per M15's MVVM
  decision of record — it is one-directional projection → controls, nothing to two-way-bind
  (Phase 2).
- **Which conversation is shown is local UI selection state (UI spec §4), never a projected
  fact** — every `LoadAsync` re-renders the selection from the durable transcript as it exists
  now (and clears it if the file is gone), so load-on-refresh follows a still-running exchange
  through the existing refresh/live-timer path; each turn's `Prompt` renders inside a collapsed
  per-turn `Expander`, on demand only (Phase 2).
- **The gate is `Aer.Ui.Tests.ConversationRoundTripTests`** — the real `Aer.Workers.Dialogue`
  executable dispatched through the real `WorkerAdapterRegistry.Default` via
  `RunCommand.ExecuteAsync` over stub vendor CLIs, then the conversation view asserted control by
  control over the resulting artifacts. This is the producer/consumer agreement check Phases 1–2
  deliberately did not make (their fixtures were hand-written to §10.1's reader contract); the
  second gate test proves a failed exchange's forensic prefix renders as a conversation — one
  intact turn, no malformed marker, because the failing turn is never appended (M17 Phase 3's
  decision). In `AerFlow.slnx`, so it runs on all three of `ci.yml`'s OSes by default; no live
  gate or runbook, since nothing in this milestone touches a vendor CLI (Phase 3).

## M17: Dialogue Worker

- **The walkthrough documents verified behavior, not intent** — every command in
  `docs/archive/walkthroughs/first-real-workflow.md` was executed end to end over stub vendor CLIs
  (run → pause → supply → supersede cascade → resume → terminal, exit 0) before being written
  down. Two facts the dry run corrected against the code: the default task directory is
  `.aer/<workflow-file-stem>` (the *file* stem, not the template id), and
  `AER_SUPPLEMENTARY_INPUT` names the supplementary execution's output *directory*
  (`ArtifactManager.ResolveSupplementaryInputPath` — "addressed the same way as any other
  execution's"), never the supplied file itself (Phase 1).
- **Requirements captured for the dialogue worker**, recorded in the walkthrough's §8: the
  supplementary path is not surfaced in any adapter's generated prompt — and can't be surfaced
  per-dispatch under the current seam, since `IWorkerAdapter.Resolve` runs once per role
  (M11's decision of record), so an unconditional env-var reference is the only available
  adapter-level shape (Phase 4's open question now has this constraint attached); a
  vendor-bound step that must *consume* a send-back needs a shell-capable `PermissionScope`
  (`"Bash,Read,Write"` for Claude) purely to discover the feedback path; and the live
  walkthrough run itself remains a human action item per CLAUDE.md's live-vendor rule —
  the stub dry run is the part an agent session can and did verify (Phase 1).
- **The worker lives at a new `Aer.Workers.Dialogue` leaf** (`src/Aer.Workers.Dialogue`, tested by
  `tests/Aer.Workers.Dialogue.Tests`), Overview §7's default, resolving Phase 2's first open
  question — not a `scripts/` shell script. It references neither `Aer.Flow` nor `Aer.Adapters`:
  per §18.2, a Case 2 worker is "indistinguishable from running `cargo test`" to the engine, so
  nothing above the worker boundary needs to change, and nothing inside this boundary needs to
  reach back across it. How it ships (riding `aer`'s existing `dotnet tool` package vs. its own) is
  left for Phase 4/5, when dispatch integration and packaging actually need an answer (Phase 2).
- **`transcript.jsonl`'s schema is `TranscriptTurn` (sequence, role, vendor, prompt, text), one per
  line** — documented on the record itself rather than a separate markdown file, this codebase's
  existing convention for spec-bearing types (`WorkerContract`, `WorkerInvocation`). `Role` is the
  configured participant's logical name (e.g. `"initiator"`/`"responder"`), never a vendor name, so
  a transcript reader can tell who argued which side independent of which vendor played it. Whether
  UI spec §10 names this schema is still open, per the ledger entry, for M18 planning to settle
  (Phase 2).
- **The worker's own config surface (`DialogueWorkerConfig`) is a JSON sidecar the executable reads
  from a config-file-path argument** — mirrors `WorkerBindingConfigParser`'s "parse, then validate
  structurally" shape and exception style, but is its own type family (`DialogueWorkerConfigException`
  extends `Exception` directly, not `Aer.Flow.AerFlowException`), since this worker depends on
  neither `Aer.Flow` nor `Aer.Adapters`. Carries a provisional `StopSentinel` field so the format
  does not change shape again once Phase 3 decides the real stop-signal mechanism; the skeleton
  itself ignores it and always runs the full `TurnBudget`. *How* this config path reaches the
  worker once Flow actually dispatches it remains Phase 4's open question, deliberately left
  unresolved here (Phase 2).
- **Per-turn vendor invocation is direct process spawning with no shell wrapper** — unlike
  `Aer.Adapters`'s vendor adapters, a per-turn call never touches `AER_INPUT_<n>`/`AER_OUTPUT_DIR`
  (those are Flow's top-level dispatch convention, meaningless to a call made entirely inside the
  worker's own process), so nothing needs shell-based environment-variable expansion. Each
  participant names a `Command` and an `Args` list containing one literal `"{PROMPT}"` token,
  substituted with the turn's prompt at spawn time and passed via `ProcessStartInfo.ArgumentList`
  — every argument reaches the child exactly once, correctly quoted by the runtime, with none of
  `ClaudeWorkerAdapter`/`GeminiWorkerAdapter`'s shell-quoting hazards. Real per-vendor argument
  shaping (the actual `claude`/`agy` flag vocabularies, spike #21's realities) stays out of this
  skeleton — Phase 3's turn loop is where that lands. Context threading is deliberately minimal for
  the same reason: each turn's prompt is its speaker's preamble plus only the immediately preceding
  turn's text, not the full transcript — enough to prove the loop and the schema, not a context-
  window design (Phase 2).
- **The stop-signal shape is a literal substring in a turn's own text, not a structured per-turn
  output file** — resolving Phase 3's named open question. Spike #21 already recorded that vendor
  CLIs are unreliable about writing extra files on cue (the walkthrough's §8 finding: `agy` asking
  a clarifying question and writing nothing at all) but reliably produce stdout text, which
  `DialogueRunner` already reads for every turn regardless — parsing that same text for a sentinel
  needs no new per-turn output-file contract each vendor CLI would separately have to honor, and is
  the more robust of the two shapes across two different vendors' output habits for exactly that
  reason. `DialogueWorkerConfig.StopSentinel`, carried as provisional config since Phase 2, is now
  live: `DialogueRunner` checks each turn's raw (post-empty-check, pre-recording) text for the
  configured sentinel substring; if present, it is stripped from the text recorded on the
  transcript and threaded forward — a transcript reader sees the participant's actual words, never
  the control token — and the exchange ends after that turn, before `TurnBudget` is necessarily
  exhausted (Phase 3).
- **Context threading is the full transcript so far, not a sliding window** — resolving the
  phase's other named question. `DialogueWorkerConfig.TurnBudget` is this worker's own config and
  deliberately small (the phase plan's "bounded" exchange), so a bounded turn count is what keeps
  the full transcript's size a non-issue for spike #21's CLI-argument-length realities, without
  this worker inventing a token-budget or summarization scheme of its own — the same reasoning
  that kept `OutputCondition` free of a general expression language (behavioral spec §4.1): a
  narrower mechanism sized to the actual bounded need, not a general one built ahead of a concrete
  requirement for it. `DialogueRunner.RunAsync` now builds each turn's prompt from the speaker's
  preamble, the exchange's `SeedPrompt`, and every prior turn's role and text in order — Phase 2's
  "only the immediately preceding turn's text" placeholder is gone (Phase 3).
- **Failure mapping: a non-zero vendor exit or an empty turn throws `DialogueExecutionException`
  mid-loop, deliberately before the failing turn is appended to the transcript and before
  `FinalOutputName` is ever written.** `Program` maps the exception to a non-zero process exit, so
  Flow's `OutcomeClassifier`/`ContractValidator` (spec §8) see a broken dialogue fail on both counts
  at once — non-zero exit *and* a missing declared output — deliberately redundant, not
  either-or, so the failure is unambiguous however a caller happens to check it, mirroring the
  `agy`-writes-nothing precedent `ContractValidator` already handles for any other worker.
  Whatever `transcript.jsonl` lines were appended for turns that succeeded *before* the failing one
  stay on disk as a forensic record; per §18.2's tradeoff, restated deliberately and not worked
  around, there is no resumption from them — the step's ordinary `RetryPolicy` (spec §10) restarts
  the whole exchange from turn one on retry, exactly like any other worker's retry (Phase 3).
- **`IVendorTurnClient.SendTurnAsync` returns a new `VendorTurnResult(Text, ExitCode, StandardError)`
  record instead of a bare string** — `DialogueRunner` needs the exit code to classify a turn as
  failed (the same "exit code alone is not success" split `OutcomeClassifier` applies one layer up
  in Flow) and captured stderr to put something a human can act on into the failure message;
  `ProcessVendorTurnClient` now redirects and reads stderr, concurrently with stdout via
  `Task.WhenAll` before `WaitForExitAsync`, avoiding the pipe deadlock a chatty CLI's unread stream
  would otherwise risk (Phase 3).
- **`DialogueWorkerAdapter` reuses `WorkerInvocation.PromptTemplate` to carry the dialogue-worker
  config file's static path**, resolving Phase 4's named open question against "a required input."
  `ArtifactManager.ResolveInputPaths` only ever resolves a step's declared `Inputs` from an ancestor
  step's declared `Outputs` — there is no static-file input kind in this codebase — so the required-
  input shape would force every workflow using this worker to add a step whose only job is producing
  a static file, for content (seed prompt, per-side preambles, stop condition) that is exactly as
  static and per-role as `Model`/`PermissionScope` already are. Reusing the already-"forwarded
  verbatim" `PromptTemplate` needs zero Flow or engine change, matching the milestone's first fact
  (Phase 4).
- **The dialogue executable is located via a `ProjectReference` (`Aer.Adapters` → `Aer.Workers.Dialogue`),
  invoked as `dotnet exec <dll>`** — the identical mechanism `tests/Aer.Flow.Tests/TestSupport/CrashTestHostLauncher`
  already proved for a different Exe-output `ProjectReference` (M10 Phase 4), now used in production
  code for the first time. Resolves Phase 2's "how it ships" open question in favor of "rides `aer`'s
  existing `dotnet tool` package": confirmed live via `dotnet pack`, the transitive reference alone
  puts `Aer.Workers.Dialogue.dll`/`.deps.json`/`.runtimeconfig.json` in the packed nupkg's
  `tools/net10.0/any/` next to `Aer.Cli.dll`, no separate package or extra packing step needed
  (Phase 4).
- **The registry key is `"dialogue"`, generalizing M12's "names the capability, not the binary" rule**
  one step further: `"claude"`/`"gemini"` name vendors, `"dialogue"` names a worker that isn't a
  vendor CLI at all (Phase 4).
- **Dispatch integration is proven by a real, unstubbed run**: `Aer.Cli.Tests.DialogueDispatchEndToEndTests`
  binds a step to `"dialogue"` through the real `WorkerAdapterRegistry.Default` (not a shell-stub test
  adapter, unlike every other `*EndToEndTests` in this project) and runs it to terminal via
  `RunCommand.ExecuteAsync` — the real `dotnet exec` spawn, the real `Aer.Workers.Dialogue` executable,
  and its own child spawns of two stub vendor scripts all actually execute. CI-safe on every OS: no
  real vendor CLI or network access involved (Phase 4).
- **Gate (a) — the unattended stub-vendor dialogue round trip — was already satisfied by Phase 4's
  own `DialogueDispatchEndToEndTests`**, not new work this phase adds: that test already binds a
  step to `"dialogue"` through the real registry and runs it to `Terminal` with a schema-asserted
  `transcript.jsonl`, and it already lives in `Aer.Cli.Tests` (part of `AerFlow.slnx`), so it already
  runs on all three of `ci.yml`'s matrix OSes as a side effect of `pixi run test` — reconfirmed this
  phase by re-running the full suite (`dotnet build -warnaserror`, `dotnet format
  --verify-no-changes`, `dotnet test`, all green, no `Aer.Flow`/`Aer.Adapters`/projection-semantics
  changes) rather than duplicated (Phase 5).
- **Gate (b) is `LiveDialogueSmokeTest` (`tests/Aer.Cli.SmokeTests`) + `pixi run smoke-dialogue` +
  `docs/runbooks/live-dialogue-smoke.md`**, built exactly on the `smoke-claude`/`smoke-mixed-vendor`
  precedent: excluded from `AerFlow.slnx` so it never builds/runs by default, its `Initiator`/
  `Responder` spawn the real `claude`/`agy` CLIs directly (`ProcessVendorTurnClient`'s no-shell
  invocation, M17 Phase 3) using the same one-shot-text-turn flags `ClaudeWorkerAdapter`/
  `GeminiWorkerAdapter` already build for a top-level dispatch, and its workflow/bindings/dialogue-
  config fixtures are generated at test run time (not static fixture files) since
  `WorkerInvocation.PromptTemplate` must carry a real absolute path to the generated config file, the
  same reason `DialogueDispatchEndToEndTests` generates its own fixtures rather than reading static
  ones. A short, cheap exchange (`TurnBudget: 2`, a one-sentence seed prompt) — the same "keep a live
  gate fast" reasoning `live-claude-smoke.md`'s single-sentence draft prompt uses (Phase 5).
- **The live run itself was not recorded this phase and stays a permanent human action item, per
  CLAUDE.md's live-vendor rule** — this session's host happened to carry an authenticated `claude`
  CLI (the same coincidence `live-claude-smoke.md` documents for M11) but no `agy`, so running
  `smoke-dialogue` here would prove only half the gate. Left un-run rather than worked around;
  `docs/runbooks/live-dialogue-smoke.md` records "none yet" until a host with both CLIs
  authenticated actually runs it (Phase 5).

## M16: UI Authoring

- **The template writer is `WorkflowDefinitionWriter`, beside its parser in `Aer.Flow.Templates`** —
  not inside `Aer.Ui`, resolving the phase plan's named seam decision. Round-trip fidelity
  (save → parse → validate through the exact code every other consumer uses) is a domain-layer
  property, guaranteed by construction only when both directions share the same
  `System.Text.Json` converters, and `SnapshotBinder.PersistAsync` already established that
  domain-record file writers live in this namespace. Flow's engine still never writes a template
  on any execution path — the writer has no caller inside `Aer.Flow` itself; the UI is the caller,
  exactly as UI spec §4 assigns. Output is indented (a template is a human-editable file, §11.1's
  own framing), and the round-trip bar is parse-level, never byte-level (Phase 1).
- **The writer validates before writing** — `WorkflowDefinitionWriter.Serialize` runs
  `WorkflowDefinitionValidator.Validate` first and writes nothing on failure, the same
  public-entry-point reasoning as `SnapshotBinder.Bind`. Phase 2's save-validity open question
  (may an invalid in-progress graph be saved as a draft?) can loosen this deliberately; until it
  does, a saved template file is engine-valid by construction (Phase 1).
- **The editing model is in-memory `WorkflowDefinition` + explicit Save with dirty tracking,
  as a separate editor surface riding the MVVM layer** — `TemplateEditorViewModel` (a child of
  `MainWindowViewModel`, the first two-way-bound surface, exactly the shape M15's
  notes-for-future-work entry anticipated) holds the metadata fields and the baseline they were
  loaded from; `MainWindow.NewTemplate`/`OpenTemplateInEditorAsync`/`SaveTemplateAsync` own all
  file I/O, the same state-in-VM/I-O-in-window split `PausedStepViewModel` established.
  M14 Phase 3's read-only template projection is untouched: `OpenAsync` still routes a template
  file straight to the read-only DAG view — inspecting and authoring are separate surfaces, so
  the read-only view never has to defend against a half-edited state (Phase 1).
- **§11.1's version-increment rule is implemented in `MainWindow.SaveTemplateAsync` exactly as the
  spec amendment settled it**: a content-changing save increments `WorkflowTemplateVersion` from
  the loaded baseline — unless the user explicitly set a different version themselves, which is
  respected as-is (a hand-editor may legitimately do the same); a no-op save writes nothing and
  increments nothing (`No changes to save.`); and a brand-new template's first save has no saved
  predecessor to distinguish from, so it saves the version as entered
  (`TemplateEditorViewModel.BaselineIsPersisted` is how Save tells the two apart). The incremented
  version is written back into the editor's fields after save, so the box never silently diverges
  from disk (Phase 1).
- **Template saves are deliberately not gated on `IsMutationInFlight`** — a template file is not
  durable task state, no §15 task lock is involved, and an edit is visible only to future
  instantiations regardless (UI spec §5), so the editor stays usable while a pump is in flight
  (Phase 1).
- **The save-validity discipline: Save stays blocked until the in-progress graph is valid — Phase
  1's rule is not loosened.** `TemplateEditorViewModel.BuildCandidate` returns every parse and
  `WorkflowDefinitionValidator` violation found; `SaveTemplateAsync` refuses to write while any
  remain, surfacing them verbatim in `StatusText`. There is no draft-storage concept elsewhere in
  the stack — a template file is the sole authoring artifact and exactly what instantiation reads —
  and a blocked Save loses no work: the in-memory `Steps` collection and every field persist for the
  whole editing session regardless of current validity, so the user can edit across a temporarily
  invalid intermediate state and only needs to reach validity once, at the moment they choose to
  save (Phase 2).
- **`DependsOn` is edited as a checkbox per declared step (`StepEditorViewModel.DependsOnOptions`),
  never free text** — candidates are offered from the template's other declared steps only,
  excluding the step itself (impossible-by-construction, not a validator rule tripped), the
  authoring counterpart of M15's "reflect, don't invent" send-back discipline. A step's
  `SelectedDependsOn` set is keyed by target `StepId` text and survives `DependsOnOptions` being
  rebuilt after an unrelated edit; a rename elsewhere is treated as a new identity — an old
  selection under stale text is not carried forward — the same way the validator itself would flag
  a stale reference as unresolved rather than silently repair it (Phase 2).
- **The DAG preview re-layout is gated on full validation passing, never attempted against an
  invalid in-progress graph.** `DagLayoutEngine.Layout` assumes an already-structurally-valid graph
  (acyclic, every `DependsOn` reference resolvable) and does not itself guard a cycle or a dangling
  reference — calling it on an invalid graph risks an unbounded recursion or a dictionary-lookup
  crash, not a graceful validator-style rejection. `TemplateEditorViewModel.PreviewLayout` is
  `null` whenever `ValidationErrors` is non-empty; the editor's dedicated `TemplateEditorDagCanvas`
  simply clears rather than rendering a stale layout (Phase 2).
- **Dirty tracking and the no-op-save check use a dedicated structural-equality helper
  (`TemplateEditorViewModel.DefinitionsAreEqual`), not `WorkflowDefinition`'s record `==`.** Once
  `Steps` is editable, every list-typed member compares by reference under default record equality,
  not by content, so `==` silently under-reports changes. `DependsOn` compares as a set (the
  validator never treats it as ordered); every other list compares in sequence (Phase 2).
- **`WorkflowDefinitionValidator` gains a public `ComputeTransitiveAncestors(WorkflowDefinition)`,
  reusing the exact ancestor walk `Validate` already runs internally for `SupersedeTargets`
  ancestry, rather than a second implementation in `Aer.Ui`.** The authoring counterpart of "Flow
  carries discipline": a `SupersedeTargets` candidate list needs the live in-edit graph's actual
  ancestor set, and re-deriving that in the UI would risk drifting from the validator's own rule.
  Carries the same precondition `DagLayoutEngine.Layout` already does — acyclic, every `DependsOn`
  reference resolvable — so it is only ever called once `WorkflowDefinitionValidator.Validate` has
  already succeeded (Phase 3).
- **`SupersedeTargets` is edited as a checkbox per this step's actual transitive ancestor
  (`StepEditorViewModel.SupersedeTargetOptions`), gated on the whole graph currently validating** —
  the same "reflect, don't invent" shape `DependsOn` established in Phase 2, extended with a
  validity gate `DependsOn`'s own candidate list didn't need (an ancestor walk, unlike "every other
  declared step," isn't safe to compute against a cyclic or dangling-reference graph). A
  `PausePoint`'s own toggle (`HasPausePoint`) is independent of its targets — turning it off writes
  `PausePoint = null` regardless of what remains selected, and turning it back on does not clear
  prior selections (Phase 3).
- **An edit that orphans an already-selected `SupersedeTargets` entry (removing the `DependsOn`
  path that made it an ancestor) is never silently dropped — it rides into the candidate unchanged
  and surfaces as a live `WorkflowDefinitionValidator` "not a transitive ancestor" violation**,
  exactly as the phase plan requires (live, not save-time). `StepEditorViewModel.SelectedSupersedeTargets`
  is the authoritative selection state independent of whatever `SupersedeTargetOptions` currently
  offers — an option list is allowed to go briefly stale while the graph is otherwise invalid, since
  ancestor computation isn't safe against invalid input, but the underlying selection is never
  touched by that staleness (Phase 3).
- **`PausePoint` equality for dirty tracking and the no-op-save check is content-based
  (`TemplateEditorViewModel.PausePointsAreEqual`), not the reference equality Phase 2's pass-through
  relied on** — `BuildCandidate` now constructs a fresh `PausePoint` from editor state on every call,
  so a loaded step's original instance is no longer threaded through untouched once `PausePoint`
  itself is editable (Phase 3).
- **The bindings writer is `WorkerBindingConfigWriter`, beside its parser in `Aer.Adapters`** — not
  inside `Aer.Ui` or `Aer.Flow.Templates`, resolving the phase plan's named seam decision the same
  way Phase 1 resolved it for templates. The bindings shape (adapter names, `WorkerContract`,
  prompt/timeout/model/permission scope) lives entirely in `Aer.Adapters` already (Adapter
  Isolation, CLAUDE.md's own architecture rule) — putting the writer anywhere else would split a
  format's read and write sides across the isolation boundary the rule exists to prevent. `Aer.Ui`
  is the writer's only caller, exactly as UI spec §4 assigns (Phase 4).
- **The writer validates by round-tripping through the parser, not a separate validator** — there is
  no `WorkerBindingConfigValidator`; `WorkerBindingConfigParser.Parse`'s own field checks (non-blank
  `Adapter`, a present `Contract`, non-blank `PromptTemplate`) are this format's only validation.
  `WorkerBindingConfigWriter.Serialize` proves them by parsing its own serialized output before ever
  returning it, and writes nothing on failure — the same "public entry point re-validates, saved
  state is always engine-valid" discipline `WorkflowDefinitionWriter.Serialize` established via
  `WorkflowDefinitionValidator`, adapted to a format whose only validation already lives in its
  parser (Phase 4).
- **Adapter names are offered per-row, from the registry `MainWindow` was constructed with** — each
  `WorkerBindingEntryViewModel` carries its own `AdapterCandidates` list (set once from
  `MainWindow`'s `IReadOnlyDictionary<string, IWorkerAdapter>` constructor argument, M15 Phase 1's
  decision of record) rather than a shared binding to a root-level list, because inside an
  `ItemsControl.ItemTemplate` the bound `DataContext` is the row itself — an ancestor/relative-source
  binding back to `MainWindowViewModel` is the awkward path in Avalonia, a per-item list is the
  established one (`PausedStepViewModel.SendBackTargets` already does this). Not a hard gate: the
  `Adapter` box is an editable `ComboBox` seeded with these candidates, since nothing in
  `WorkerBindingConfigParser.Parse` validates an entry's `Adapter` against any registry either
  (Phase 4).
- **Structured vs. opaque editing on `WorkerContract`**: `Adapter`, `PromptTemplate`, `Timeout`,
  `Model`, `PermissionScope` (the entry's scalars) and `RequiredInputs`/`OptionalMetadata` (its two
  plain-string lists, edited as comma-separated text) all get real structured editing.
  `ProducedOutputs` does not — each entry is a small record of its own (`Name` plus an optional
  `OutputCondition` carrying a `JsonScalar` sum type: string/number/bool/null), and a safe small
  editable surface for that shape (per-item add/remove, a scalar-type picker) is new list-editing
  machinery this phase's scope doesn't call for. It round-trips opaquely instead, as a raw JSON text
  box using the same `System.Text.Json` converters the parser/writer use, so fidelity — including
  `OutputCondition` — is guaranteed by construction rather than by a hand-written mapping this phase
  would otherwise have to get right (Phase 4).
- **Dirty tracking cannot reuse Phase 1's `==`-on-record trick** — `TemplateEditorViewModel`'s Save
  builds its candidate via `baseline with { ... }`, which keeps the very same `Steps` list reference
  when steps are untouched, so record equality (which does not deep-compare `IReadOnlyList` fields)
  already happens to be correct there. A bindings save always rebuilds a fresh `Dictionary` from the
  editable rows, so two structurally-identical configs are never reference-equal.
  `BindingsEditorViewModel` uses a manual deep-equality check (`ConfigEquals`/`EntryEquals`,
  `SequenceEqual` on the list fields) instead, recomputed via `PropertyChanged` subscriptions on
  every row rather than the per-field `OnXChanged` partial methods `TemplateEditorViewModel` and
  `PausedStepViewModel` use — one central subscription per row instead of ten near-identical partial
  methods (Phase 4).
  Note: Phase 2 later gave `TemplateEditorViewModel` its own dedicated structural-equality helper
  (`DefinitionsAreEqual`) once `Steps` became editable there too, for the same underlying reason —
  see Phase 2's own decision above.
- **The template↔bindings advisory cross-check reads `TemplateEditorViewModel.Baseline`** — "the
  currently-open template" (the phase's own open-question wording) is read from the template
  *editor's* in-memory state, not the read-only DAG view's `LoadTemplateAsync`, which never retains
  its loaded definition as a field at all. This is a read-only consultation of already-computed
  state, not a change to template-editing code: nothing here writes to, or is called from,
  `TemplateEditorViewModel` or `OpenTemplateInEditorAsync`, honoring the phase's exclusion of
  touching Phases 1-3's surface. `MainWindow.RefreshBindingsTemplateCrossCheck` is called explicitly
  (New/Open/Save bindings, adding a row, or a dedicated "Check against open template" button) rather
  than wired to any template-editor change notification, for the same reason. Strictly one-directional
  (template workers missing a binding, never the reverse) and never consulted by
  `SaveBindingsAsync` — advisory display only, per UI spec §9 (Phase 4).
- **Unlike M15 Phase 5, the gate needed genuinely new test code, not a relabeling of each earlier
  phase's own end-to-end proof.** Grepping Phases 1–4's test classes for `RunAsync`/
  `CompareToTemplateAsync` returned zero matches — every prior M16 test drives `SaveTemplateAsync`/
  `SaveBindingsAsync` and stops at the saved file, never the Run action or the diff view. The three
  round trips the phase plan names live in a new `AuthoringRoundTripTests`, each stitching an
  authoring surface (Phases 1–4) to a surface an earlier milestone shipped: a template built from
  blank through `TemplateEditorViewModel` (Phase 2's own walking-skeleton shape), saved, and run to
  `Terminal` through `MainWindow.RunAsync` (M15 Phase 1) over a directly-written shell-stub bindings
  file; a template file a bound task's snapshot already reflects, edited and saved through the same
  editor (adding a step, `WorkflowTemplateVersion` incrementing per §11.1), then compared back
  through `MainWindow.CompareToTemplateAsync` (M14 Phase 4) — asserting both the diff panel reports
  the added step and the *bound task's own* `StepsPanel` rendering is byte-identical before and
  after, since `RenderDiff` only ever touches `DiffPanel`; and a bindings file built entirely from
  blank rows through `BindingsEditorViewModel.AddEntry`/`SaveBindingsAsync` (Phase 4), then driving
  the same `RunAsync` call to `Terminal` with zero bindings content written by hand (Phase 5).
- **No new CI workflow or job — the same "`Aer.Ui.Tests` is already a leaf" precedent M14 Phase 5
  and M15 Phase 5 established holds a third time.** `AuthoringRoundTripTests` runs unattended in
  `pixi run test`'s plain `dotnet test` on all three of `ci.yml`'s matrix OSes. Verified green
  end to end for this phase: `dotnet build -warnaserror` (lint), `dotnet format --verify-no-changes`
  (fmt-check), and the full `dotnet test` run — all four `AerFlow.slnx` test projects, 527 tests
  total (`Aer.Ui.Tests` 138, up from 135), including every M14 Phase 5 golden-projection fact —
  pass with no changes to `Aer.Flow`, `Aer.Adapters`, or `Aer.Cli`: this phase adds only test code
  (Phase 5).
- **The UI spec v0.9 → v1.0 promotion question the phase plan flags is deliberately left open by
  this phase, not answered by it.** The phase plan says milestone completion "owes the ledger" the
  answer, not that this PR must execute a promotion — renaming a canonical spec and declaring its
  status is a different kind of change than the round-trip tests this phase exists to add, and
  bundling it into a test-only PR would put a doc-status call on the same merge-on-green path as
  mechanical, easily-verified test code. The recommendation (worth a deliberate follow-up, not
  silently dropped): M14 + M15 + M16 together now cover every UI-track capability the roadmap
  named (projection, control surface, authoring), so v1.0 looks earned on the same terms the Flow
  spec itself reached v1.0 on — not "every hypothetical covered," but "no known gap blocking
  current capabilities." Conversation/live-streaming views (blocked on Case 2 multi-model workers)
  and scheduling simulation/cost display (spec "may"s with no concrete need naming them) stay
  deliberately unassigned to any milestone either way (Phase 5).

## M15: UI Control Surface

- **The mutation seam is in-process reuse of `Aer.Cli.RunCommand.ExecuteAsync`** — `Aer.Ui` now
  references `Aer.Cli` and `Aer.Adapters` directly (new `ProjectReference`s), the same static,
  adapter-registry-as-argument call `Program.cs` makes for `aer run`, rather than spawning the
  installed `aer` binary. This is the seam every later phase's decision command builds on the same
  way (Phase 1).
- **The worker-adapter registry is a `MainWindow` constructor argument**, defaulting to
  `WorkerAdapterRegistry.Default` through the existing parameterless/one-argument constructors so
  no production caller has to name it — the same "production wiring is the caller's decision" seam
  `LocalUiConfigurationStore` established in M14 Phase 2. `Aer.Ui.Tests` substitutes a deterministic
  shell-stub registry (`MainWindowRunTests`) instead of resolving a live vendor CLI (Phase 1).
- **`RunOptions.WorkflowFilePath` is nullable** — a resume of an already-bound task directory never
  reads it (`RunCommand.ExecuteAsync` only binds a fresh snapshot when none is persisted yet), so
  `MainWindow.RunAsync` never has to ask the user for a template unless the task directory is
  actually starting fresh. A fresh start with no template given is a `CliArgumentException`, not a
  silent no-op (Phase 1).
- **Bindings and template file paths are asked for on every Run, never inferred** — bindings are
  never persisted in a task directory (M14 Phase 2's decision of record; **superseded 2026-08-14 by
  [decision 0056](decisions/0056-a-room-carries-its-own-worker-bindings.md) — a room now keeps its own
  copy, and the "asked for on every Run, never inferred" half of this line stands unchanged: Run still
  asks, and the copy records the answer**) and a template is only
  ever relevant on a fresh start. `LocalUiConfigurationStore` gained `LastBindingsFilePath`/
  `LastWorkflowTemplateFilePath` purely to pre-fill that ask, the same non-authoritative,
  rebuildable-convenience treatment as the existing recents list (Phase 1).
- **The pump runs via `Task.Run` inside `MainWindow.RunAsync`, and the UI thread never awaits it
  directly** — a real dispatch can take however long a worker takes; the existing 2-second
  `DispatcherTimer` poller (M14 Phase 2) is what renders progress while a Run is in flight.
  `RunAsync` itself only touches projection controls once, after the pump has already reached its
  fixed point (Phase 1).
- **`RunCommand`/`MutationInterface` were not given the caller-retained `InFlightExecutionRegistry`
  this phase** — deliberately deferred to Phase 4, which already owns that additive signature
  change per the phase plan above; Phase 1's Run action has nothing yet to target a cancel at
  (Phase 1).
- **MVVM enters now, scoped to the decision surface only** — `CommunityToolkit.Mvvm`
  (source-generator `[ObservableProperty]`/`[RelayCommand]`, no reactive-extensions dependency) is
  the new `Aer.Ui` `PackageReference`. `MainWindowViewModel`/`PausedStepViewModel` own exactly the
  surface M14 Phase 1 named as the potential second concrete need — buttons whose enabled state is
  tied jointly to projected state and an in-flight mutation — set as `MainWindow.DataContext`. The
  rest of the window's read-only rendering (DAG, history, lineage, diff) is untouched, still direct
  code-behind control manipulation; migrating it is a future decision this phase's Approve/Reject
  surface does not need to force (Phase 2).
- **§7's Approve/Reject label mapping**: `PausedStepViewModel.ApproveCommand` records
  `DecisionType.Resume`, `RejectCommand` records `DecisionType.Reject` — never a UI-invented decision
  type (UI spec §6). `MainWindow.RebuildPausedSteps` re-derives one `PausedStepViewModel` per step
  whose latest attempt is `StepStatus.Paused`, from `StepState.LatestExecutionId`, on every load —
  a projected fact, not retained handler state, so a step that resumes simply stops appearing next
  load (Phase 2).
- **One shared `IsMutationInFlight` flag, not a per-action one**, gates every mutation this UI
  process can start — `RunButton`'s bound `IsEnabled` and every `PausedStepViewModel`'s command
  `CanExecute` all read it, since the underlying §15 lock could not support two concurrent
  in-process mutations regardless. A `WorkflowLockedException` from a *competing external* pump
  still renders via the in-window-message precedent (M14 Phase 1) — this flag only ever prevents a
  second mutation from this same process, never claims to reach across processes (Phase 2).
- **The decision's worker-bindings path is read from `BindingsFilePathBox` at decide-time, not
  cached in a field** — the same "ask, don't infer" box `RunAsync` already asks for (Phase 1's
  decision of record); `RunAsync` now also writes its own `bindingsFilePath` argument back into that
  box so a decision has something to read even when `RunAsync` was invoked directly rather than
  through the Run button's click handler (Phase 2).
- **The supplementary-artifact worker role and output name are asked for, never inferred or
  defaulted** — `WorkerBinding.NonProcess` is constructed directly from these two strings (M12
  Phase 3's decision of record: never looked up in the bindings file), and no snapshot-declared field
  names an expected value for either, so `PausedStepViewModel.SupplementaryWorker`/
  `SupplementaryOutputName` are the same "ask, don't infer" discipline as the bindings/template file
  paths, just promoted into the MVVM layer Phase 2 introduced rather than a named code-behind control
  — a paused step is a dynamically-templated `ItemsControl` row, not a fixed named control (Phase 3).
- **`DecideDelegate` replaced the three-argument decide callback**, carrying `TargetStepId` and the
  supplementary-artifact triple (`RevisionFilePath`/`SupplementaryWorker`/`SupplementaryOutputName`,
  `null` together whenever no artifact rides the decision) alongside the original
  `StepId`/`ExecutionId`/`DecisionType`. `MainWindow`'s private `DecideAsync` is the one place that
  runs the `aer supply` → `aer decide` two-call round trip M12 Phase 3 established for the CLI: it
  mints/populates/settles the supplementary execution first (only when a revision file path is
  non-null) and passes the resulting `ExecutionId` to `DecideCommand` as `SupplementaryExecutionId`
  — both calls share one `IsMutationInFlight` window and one poller start, since together they are
  one user-facing action, not two (Phase 3).
- **Retry's supplementary artifact is optional, Send-back's is mandatory — enforced by each
  command's own `CanExecute`, never by letting an incomplete call reach the mutation interface.**
  `PausedStepViewModel.CanRetry` allows a blank revision file (Retry proceeds with no supplementary
  artifact) but requires the worker/output-name pair *together* with a non-blank one, so a half-filled
  triple can never reach `aer supply` with an empty string argument. Every `SendBackTargets` entry's
  `CanSendBack` requires all three fields unconditionally — §17.2 defines a `Supersede` without a
  `SupplementaryExecutionId` as itself invalid, so the UI never offers a submittable button until one
  is guaranteed (Phase 3).
- **"Send back to X" is a small child view model (`SendBackTargetViewModel`) per declared
  `PausePoint.SupersedeTargets` entry, not a single parameterized command on `PausedStepViewModel`.**
  One object per target keeps the `ItemsControl` binding simple (`Command="{Binding SendBackCommand}"`
  needs no `CommandParameter` threaded through a nested template); it reads the shared
  `RevisionFilePath`/`SupplementaryWorker`/`SupplementaryOutputName` directly off its owning
  `PausedStepViewModel` rather than duplicating them per target, since a paused step has exactly one
  supplementary artifact in flight regardless of which target eventually consumes it. An empty
  `SendBackTargets` list (no declared targets) renders no send-back option at all — never
  offered-then-failed at the mutation interface (Phase 3).
- **`SendBackTargetViewModel`'s `SendBackCommand` `CanExecute` re-evaluation is pushed manually, not
  via `NotifyCanExecuteChangedFor`** — that attribute only reaches commands generated on the *same*
  class, and each target is its own `ObservableObject`. `PausedStepViewModel` calls
  `NotifyCanExecuteChanged()` on every target's `SendBackCommand` from `On<Field>Changed` partial
  methods whenever `RevisionFilePath`/`SupplementaryWorker`/`SupplementaryOutputName`/`IsEnabled`
  changes (Phase 3).
- **`SupplyCommand`'s `FileNotFoundException` (a plain BCL exception, not `AerFlowException`) is
  caught alongside `AerFlowException` in `MainWindow.DecideAsync`** — a mistyped revision file path
  is exactly the kind of input error the in-window-message precedent (M14 Phase 1) exists for, and
  letting it propagate uncaught would crash the window, the one thing that precedent forbids (Phase 3).
- **`RunCommand.ExecuteAsync`/`DecideCommand.ExecuteAsync` gained an additive, optional
  `InFlightExecutionRegistry? inFlightExecutions = null` parameter**, forwarded unchanged to
  `MutationInterface.StartWorkflowAsync`/`RecordDecisionAsync` — the signature change the phase plan
  named as sitting on the critical path. `null` for every existing caller (the CLI included, which
  still lets `MutationInterface` default one internally); `Aer.Ui.MainWindow` is the first caller with
  a reason to retain one (Phase 4).
- **`MainWindow` retains a fresh `InFlightExecutionRegistry` and a host-stop `CancellationTokenSource`
  (linked to the caller's own token) per `RunAsync`/`DecideAsync` pump call**, cleared in that same
  call's `finally` — the same call-scoped lifetime the registry itself already has inside
  `MutationInterface`, just now reachable from outside it. `DecideAsync`'s `aer supply` half shares
  the same host-stop token as its `aer decide` half, since together they are one user-facing action
  (Phase 3's own precedent), even though only the decide call ever registers a process dispatch
  (Phase 4).
- **`MainWindowViewModel.RunningExecutions` (`RunningExecutionViewModel`) is the §7 targeted-Cancel
  surface**, rebuilt from `RoomProjection` on every load exactly like `PausedSteps` — one entry per
  step whose latest attempt is `StepStatus.Running`, plus one per pending step-less/human execution
  (`FlowState.StepLessExecutions`), both valid `RequestCancellationAsync` targets. Two-phase
  reflection (§7) reuses `FlowState.CancellationRequestedExecutionIds` directly rather than adding a
  new UI-owned field — the Flow layer already derives exactly that fact (Phase 4).
- **`IsLocallyHosted` is derived once, at render time, from whether `MainWindow`'s own retained
  `InFlightExecutionRegistry` is currently driving the exact task directory being rendered** — never
  a per-execution registry membership check (which would need `InFlightExecutionRegistry`'s internal
  `RegisteredExecutionIds()` visible outside `Aer.Flow`). Since only one mutation can be in flight
  from this process at a time (the shared `IsMutationInFlight` flag, Phase 2's decision of record),
  "this window's retained registry is non-null and its task directory matches" is unambiguous. A
  step-less/non-process execution is never locally hosted: it never registers with
  `InFlightExecutionRegistry` in the first place (Phase 1's `NonProcessCancellationDetector` finalizes
  it directly, in-round) (Phase 4).
- **Targeted Cancel delivery is a two-way split, `MainWindow.CancelExecutionAsync`'s own decision, not
  offered as a single always-available button**: a locally-hosted execution is signalled in-process via
  `InFlightExecutionRegistry.RequestCancellationAsync` — fast, idempotent, no new mutation call, since
  §15's guard is already held for that pump's entire duration (M10's decision of record). Anything
  else is the only remaining path: a brand-new `Aer.Cli.CancelCommand` mutation call, wrapped exactly
  like `RunAsync` wraps `RunCommand`, including a possible `WorkflowLockedException` from whatever
  process (or pump) currently holds the task's lock — rendered via the in-window-message precedent
  (M14 Phase 1), never a button that pretends to work (the phase's own named open question) (Phase 4).
- **`RunningExecutionViewModel`'s enabled state is the one deliberate exception to the shared
  `IsMutationInFlight` gate**: a locally-hosted execution's Cancel command stays enabled *exactly*
  while `IsMutationInFlight` is true, since that is the only time signalling it is meaningful at all;
  every other entry (not locally hosted) follows the same `!IsMutationInFlight` rule
  `PausedStepViewModel`/`RunButton` already do. `RunningExecutionViewModel.UpdateEnabled` encodes this;
  `MainWindowViewModel.OnIsMutationInFlightChanged` calls it for every entry, the same fan-out
  `PausedSteps` already gets (Phase 4).
- **`MainWindow.StopAsync` (bound to the new `StopButton`) only cancels the retained host-stop
  `CancellationTokenSource` — it does not itself await the pump.** `RunAsync`/`DecideAsync`'s own
  already-awaited pump task is what actually drives §9's intent-first record for every execution still
  in flight and clears `IsMutationInFlight` once `MutationInterface`'s existing host-stop machinery
  (M10 Phase 2) reaches its fixed point; `StopAsync` is fire-and-forget by design, mirroring
  `Aer.Cli.Program.cs`'s `Console.CancelKeyPress` handler, which is exactly as thin (Phase 4).
- **Window-close semantics: the first `Window.Closing` while a pump is in flight is deferred
  (`e.Cancel = true`), triggers the same host stop, and the window closes for real only once the
  retained pump task has settled.** A `_closeConfirmed` flag distinguishes that second, programmatic
  `Close()` from the first user-initiated one so the stop sequence never re-enters. This is the
  "Ctrl+C equivalent" applied to the one exit path a GUI has that a terminal's SIGINT handler doesn't:
  closing the window mid-pump is never a silent abandonment of a live execution (Phase 4).
- **No new gate mechanism was needed — the milestone's three named round trips already existed as
  each earlier phase's own end-to-end proof.** `MainWindowDecisionTests.Approve_resolves_the_pause_...`
  (Phase 2) *is* run → pause → Approve → terminal; `MainWindowRetryAndSendBackTests.Send_back_offers_
  only_declared_SupersedeTargets_...` (Phase 3) *is* pause → supply + Send-back → invalidation cascade
  → terminal; `MainWindowCancelAndStopTests.Targeted_cancel_of_a_locally_hosted_execution_...`
  (Phase 4) *is* running → targeted Cancel → cancelled — each already driving the real `MainWindow`
  through a deterministic shell-stub `IWorkerAdapter`, never a live vendor CLI. Writing a duplicate
  Phase-5-named test class over the same three scenarios would be ceremony, not coverage. The
  `ShellCommandWorkerAdapter`-placement question the phase plan named was likewise already settled,
  in Phase 1: `Aer.Ui.Tests` grew its own copy (`TestSupport/ShellCommandWorkerAdapter.cs`) rather
  than sharing `Aer.Cli.Tests`'s, because Phase 1's own `MainWindowRunTests` needed a stub registry
  immediately and this project's established convention (`ShellWorkerCommands`'s own remarks) is to
  own its minimal shell-stub set rather than reach into another test project's `TestSupport`.
- **"Wired into default CI" needed no new CI step, because `Aer.Ui.Tests` already is one.** It has
  been a leaf in `AerFlow.slnx` since M14 Phase 1, so `pixi run test`'s plain `dotnet test` already
  runs it — headless, offscreen, no display server — on all three of `ci.yml`'s matrix OSes
  (win/linux/mac) on every PR and every push to `main`, the same unattended placement M13 Phase 4
  and M14 Phase 5 established for gates that need no live vendor auth. Verified green end to end for
  this phase: `dotnet build -warnaserror` (lint), `dotnet format --verify-no-changes` (fmt-check),
  and the full `dotnet test` run — all four `AerFlow.slnx` test projects, 480 tests total, including
  every M14 Phase 5 golden-projection fact — pass with no changes to `Aer.Flow`, `Aer.Adapters`, or
  `Aer.Cli`: the control surface added mutation callers across Phases 1–4, never projection semantics
  (Phase 5).

## M14: UI Projection

- **Stack: Avalonia, in this repo/solution, referencing `Aer.Flow` directly.** UI spec §13 treats
  the form factors as behaviorally equivalent, so the criteria were Overview §6 (single-developer
  tool — "run the exe" is the whole deployment story) and §11's determinism, which in-process
  read-model reuse inherits by construction, the same seam `Aer.Cli` proved for the write side.
  Avalonia over WPF/MAUI for genuine cross-platform (the existing three-OS CI matrix) and real
  vector graphics. Nothing needed a cross-language/cross-solution boundary, so Overview §7's
  default held: `Aer.Ui.csproj`/`Aer.Ui.Tests.csproj` are new leaves in `AerFlow.slnx` (Phase 1).
- **Project name is `Aer.Ui`, not `Aer.Flow.Ui`** — the UI is architecturally outside the trusted
  execution stack (UI spec §2) and must never read as part of Flow's namespace; `Aer.Cli` set the
  flat-naming precedent (Phase 1).
- **No ViewModel/data-binding layer** — code-behind against named controls is the simplest thing
  that renders the projection; an MVVM layer waits for a second concrete need, which M15's
  interactive control surface may be (Phase 1).
- **Async entry points are public and directly awaitable** (`LoadAsync`, `RefreshAsync`), never
  fired only from constructors, `Loaded` events, or timer ticks — the only way a test drives them
  deterministically without pumping the dispatcher or racing elapsed time. `OpenAsync` is the
  richer production entry (load + recents + live-refresh timer) the Open button, recents clicks,
  and CLI-argument launch all go through (Phases 1–2).
- **A failed load renders as an in-window message, not a crash** — a GUI has no stderr/exit-code
  convention to fail into; `MainWindow` catches `AerFlowException` itself (Phase 1).
- **UI tests drive the real `App`/`MainWindow` through `Avalonia.Headless`/`Avalonia.Headless.XUnit`**,
  offscreen, no display server — which forced `Aer.Ui.Tests` onto xunit v3, an isolated exception
  to the repo's xunit v2 convention, confined to this one project (Phase 1).
- **`ExecutionHistory`/`ExecutionHistoryProjector` is an `Aer.Ui`-only read-model type, not an
  addition to `FlowState`.** `StateProjector` deliberately collapses each step to its latest
  attempt (§12); full per-execution history is a presentation-layer fact re-derived from the same
  event list, never a dispatch-affecting one. `RoomProjection` carries `Snapshot`/`State`/`History`
  (Phase 2).
- **A non-process/human execution is identified by `ExecutionRequest.Timeout is null`** — the only
  signal already durable on disk once the read side has nothing but the event log and snapshot
  (bindings were, at the time, never persisted to the task directory — superseded by
  [decision 0056](decisions/0056-a-room-carries-its-own-worker-bindings.md); the `Timeout is null`
  signal it justifies is unaffected) (Phase 2).
- **Task-directory discovery is "ask the user, or pick a remembered one" — never a scanned root**
  (UI spec §3.1's implementation choice). `LocalUiConfigurationStore` is a small explicit JSON
  file store, deliberately non-authoritative per §3.1: missing/corrupt loads as empty, vanished
  paths silently drop, capped at 10 (Phase 2).
- **`MainWindow` takes its `LocalUiConfigurationStore` as a constructor argument** — production
  wiring is the caller's decision, the same seam as `RunCommand`'s adapter-registry argument;
  it's what points tests at a temp config file (Phase 2).
- **Change observation is polling via a 2-second `DispatcherTimer`, not `FileSystemWatcher`** —
  identical behavior across the three-OS matrix, re-read cost known cheap (M8's ~3.8ms finding);
  polling stops once `WorkflowStatus` reaches `Terminal` (Phase 2).
- **A separate release-please package for `Aer.Ui` was tried and reverted** — a same-repo
  `exclude-paths` split can't work while every phase commit also touches this file (a root-level
  path), and upstream `exclude-paths` reliability is poor in manifest mode
  ([release-please#2301](https://github.com/googleapis/release-please/issues/2301), [#2230](https://github.com/googleapis/release-please/issues/2230)).
  The real fix is a separate repo, not worth reopening the placement decision for; `Aer.Ui` stays
  on the shared root version.
- **`DagLayoutEngine.Layout` takes `IReadOnlyList<WorkflowStepDefinition>` directly** — the shape
  both a raw template and a bound snapshot expose, so one graph view covers both and only the
  status overlay branches. Layering is longest-path-from-root, columns in declaration order; all
  output order derives from walking the input lists, never `Dictionary`/`HashSet` enumeration
  order — as deterministic as §11 requires, assertable by the golden gate (Phase 3).
- **`TemplateProjectionLoader` is a separate loader, not a branch inside `RoomProjectionLoader`**
  (different durable-state shapes); `MainWindow.OpenAsync` routes on `File.Exists` vs.
  `Directory.Exists` — a template file and a task directory are never ambiguous on disk. Opening a
  template records no recents and starts no live-refresh timer (Phase 3).
- **`ArtifactLineageProjector` walks each recorded `ExecutionRequest`'s `UpstreamExecutionIds`
  directly — never `ArtifactManager.ResolveInputPaths` or the current `FlowState`.** Which
  execution fed an input is recorded once, at dispatch time; re-deriving against today's state
  would substitute a step's current latest execution for the one actually consumed. Producers are
  found by matching the snapshot's declared `Inputs` names against each `DependsOn` step's
  declared `Outputs` (Phase 4).
- **A `WorkflowTemplateId` mismatch is `TemplateIdMismatch`, never folded into `HasDiverged`** —
  divergence means the *same* template changed, not that the wrong file was compared;
  `WorkflowTemplateVersion` is informational, never part of the predicate (Phase 4).
- **There is no durable link from a bound task back to its template file** (snapshot carries
  id/version only — confirmed against `WorkflowDefinitionSnapshot`, `SnapshotBinder`, and every
  `FlowEvent`), so the diff surface takes the template path from the user — ask, don't infer
  (Phase 4).
- **`GoldenProjectionCanonicalizer` tokenizes runtime-minted IDs by first appearance and sorts
  only the `Dictionary`/`HashSet`-backed fields** — every List-backed field stays in its natural
  walk-derived order, because that order *is* the §11 determinism property the gate exists to
  check; re-sorting would hide real ordering bugs (Phase 5).
- **Golden files are bootstrapped/refreshed only via opt-in `AER_UPDATE_GOLDEN_FILES=1`**, writing
  to the source-tree fixture path so a reviewable diff is the only way a golden changes (Phase 5).
- **Fixture hazard, for anyone authoring pumped fixtures:** steps sharing one `Worker` name but
  declaring different `Outputs` are safe as never-pumped templates but dispatch-unsafe once
  pumped — one shared `WorkerBinding` makes `OutcomeClassifier` check the wrong step's output.
  Give every step a distinct worker name (`paused-run-workflow.json` vs. the older
  `diamond-workflow-with-pause.json`) (Phase 5).

## M13: Distribution

- **The version's single source of truth is a root `Directory.Build.props` `<Version>`**, bumped
  directly by a release-please `extra-files` XML entry on every release PR merge — visible to
  every local build, not just CI. `IncludeSourceRevisionInInformationalVersion` is explicitly
  `false`, so the tool's reported version equals `CHANGELOG.md`'s plain entry (Phase 2).
- **A single fat package, not three RID-qualified packages**: the CI `test` matrix (now including
  a real `macos-latest` job) uploads each OS's own `cargo build` output; the `pack` job gathers
  the other two platforms into `artifacts/native-libs/<rid>/` (gitignored, a hand-off point only).
  The pack items are `Condition="Exists(...)"`, so a plain local `pixi run pack` with no gathered
  artifacts still works (Phases 1, 3).
- **No runtime OS-detection/P/Invoke-resolution code was needed**: `DllImport` references the bare
  name `aer_core` and .NET's default probing appends the host-appropriate prefix/extension, so all
  three platform binaries coexist in the flat `tools/.../any/` directory (Phase 3).
- **No extra MSBuild plumbing for the native lib**: `PackAsTool` packs from a *publish* output,
  which already folds in `Aer.Core.csproj`'s existing `Content` copy of `aer_core` (Phase 1).
- **`PackageId`/`ToolCommandName` are both `aer`** — no public feed exists to collide with
  (Phase 1).
- **The round-trip check is a plain bash script in the CI `pack` job** (not a `dotnet test`, not a
  gated runbook — nothing needs live vendor auth): it drives the literal `README.md` install/run/
  uninstall commands, so the script *is* the documentation, verified. Its "no live vendor" trick
  is stubbing the `claude` binary itself ahead on `PATH` — `WorkerAdapterRegistry.Default` (what
  an installed `aer` actually wires) only resolves `claude`/`gemini`, so the test-only `shell`
  adapter is unreachable from a real installed tool; the stub satisfies the output contract by
  reading `AER_OUTPUT_DIR` directly (Phase 4).

## M12: Full Control Surface

- **`aer supply` mints, populates, and settles a supplementary execution in one call.**
  `RecordSupplementaryExecutionAsync` deliberately never runs the pump (§17.3: minting alone
  changes no readiness), so `aer supply` calls `StartWorkflowAsync` itself after copying `--file`
  into the assigned output directory — the supply → decide round trip is two CLI invocations, and
  the transient `WorkerContract` a supplementary role needs never has to be reconstructed across
  invocations (Phase 3).
- **The non-process `WorkerBinding` a supplementary execution dispatches under is constructed
  directly from `--worker`/`--output`, never looked up in the bindings file** — worker-binding
  config entries only ever resolve to `WorkerBinding.Process` (M11's decision of record), and this
  phase didn't reopen that. `aer supply` is scoped to a single declared output from a single
  `--file`; a multi-output supplementary execution is a hypothetical it declines to design for
  (Phase 3).
- **`aer run`/`aer cancel`/`aer decide` all return a `CommandResult` (`FlowState` + the bound
  snapshot), not a bare `FlowState`** — pause-aware reporting (a paused step's `SupersedeTargets`)
  is only resolvable against the snapshot; `FlowStateReporter` is the one shared formatter
  (Phase 3).
- **The input-directory grant is one vendor-neutral env var**: `ArtifactManager.BuildEnvironment`
  emits `AER_ARTIFACTS_ROOT` unconditionally (inputs and output are sibling directories under one
  root, §16); `GeminiWorkerAdapter` grants it once via `--add-dir`; `ClaudeWorkerAdapter` simply
  never references it (Phase 1).
- **The registry key is the vendor name, not the binary name** (`"gemini"`, though the binary is
  `agy`); `agy` is shell-wrapped with stdin redirected exactly like Claude (free insurance against
  the same stall class), and its scoped-permission flag is `--mode`, default `"accept-edits"` —
  further confirmation `PermissionScope` stays an opaque, adapter-interpreted string (Phase 1).
- **Phase 4's live gate recorded green 2026-07-13** (a host that happened to carry both vendors
  authenticated — a coincidence, not a capability; see CLAUDE.md). The first live attempt caught a
  real Windows-only bug in *both* adapters: each built one pre-quoted `cmd /c "..."` string, which
  aer-core's Windows spawn re-quoted and corrupted — fixed by passing each token as its own `Args`
  element on Windows (see `live-mixed-vendor-smoke.md`) (Phase 4).

## M11: First Real Run

- **Live gates live in `Aer.Cli.SmokeTests`, deliberately absent from `AerFlow.slnx`** — default
  CI never discovers them, with no trait-based filtering; `pixi run smoke-*` targets the project
  directly, and a runbook per gate documents prerequisites and triage (Phase 4).
- **A worker role that reads an upstream artifact needs `Read` in its `PermissionScope`, not just
  `Write`** — a per-worker config fact (`PermissionScope` is opaque and adapter-interpreted), not
  engine behavior; the runbook calls it out for config authors (Phase 4).
- **`RunCommand.ExecuteAsync` takes the adapter registry as a plain argument, never constructing
  one** — `Program.cs`'s only production wiring decision is passing `WorkerAdapterRegistry.Default`;
  this is what lets tests reach the real adapter/bindings seam with a deterministic
  `ShellCommandWorkerAdapter` instead of a live LLM, with zero test-only production code (Phase 3).
- **`snapshot.json` existence is the fresh-vs-resumed signal**: `RunCommand` binds and persists a
  new snapshot only when absent, otherwise loads the persisted one and never re-reads the workflow
  file — `aer run` again resumes the same task (§21) while staying bound per §11.2. `--task-dir`
  defaults to `.aer/<workflow-file-stem>` under the current directory (Phase 3).
- **Malformed CLI arguments are `CliArgumentException : AerFlowException`**, parsed before any
  file is touched; `Program.cs`'s `Main` is the one place any `AerFlowException` becomes a stderr
  message + non-zero exit (Phase 3).
- **Adapters shell-wrap every invocation and never rely on cwd**: `sh -c`/`cmd /c` around the real
  vendor binary, both for explicit stdin redirection (spike #21's stall finding) and so
  per-execution paths reach the prompt as live `$AER_INPUT_<n>`/`$AER_OUTPUT_DIR` expansions.
  Config-authored text is escaped; the adapter's own generated env-var references deliberately
  aren't (Phase 2).
- **`WorkerInvocation` cannot carry a resolved, execution-specific file path.** `IWorkerAdapter.Resolve`
  runs once per worker-binding resolution, not once per execution — one `CoreDispatchTarget` per
  role is reused across every dispatch; per-execution dynamism stays in the env vars the unchanged
  `ArtifactManager` resolves fresh per dispatch (M7 Phase 6) (Phase 1).
- **Worker-binding config is a flat JSON object keyed by worker role name**, living entirely in
  `Aer.Adapters` (Adapter Isolation), deserialized with the repo's one case-sensitive
  no-naming-policy convention — and **every config entry resolves to `WorkerBinding.Process`**;
  `NonProcess` is constructed directly by whatever caller needs one (Phase 1).

## M10: Cancellation & Edge Cases

- **The pump's own host process is the only delivery point for a live execution, by construction**:
  §15's guard is held for a mutation call's entire duration, so a second call — even from the same
  process — cannot acquire it while a pump is in flight. `InFlightExecutionRegistry` is an
  in-process handle the caller retains *before* calling the mutation surface, so cancellation of
  one specific live execution — or a host stop of everything in flight — reaches the pump with no
  second mutation-surface call and no daemon (Phase 2).
- **Every process dispatch is registered under its own `CancellationTokenSource`, never the ambient
  host token directly**: a host stop mints `CancellationRequested` for every in-flight execution
  (fsync'd) *before* any is signalled; a targeted cancel does the identical record-then-signal for
  exactly one (Phase 2).
- **Once a host stop is detected, the pump's own I/O switches to an uncancellable token** — the
  ambient token firing stops new dispatches, never the pump's ability to write its way to a
  consistent fixed point (Phase 2).
- **`IEventLogReader.ReadAllCoreEventsAsync` is additive** — every existing `ReadAllAsync` caller
  already treats it as Flow-events-only (Phase 3).
- **A dispatch the same call already registered is excluded from crash-recovery consideration,
  checked before any of the four crash states** — otherwise a genuinely in-flight stub dispatch
  looks like "never started" and gets wrongly resubmitted (Phase 3).
- **The orphan's best-effort cancellation re-issue is a documented no-op**: a crashed pump's
  `AerCancelHandle` cannot survive its process (no cross-process re-attach or kill-by-`Pid` in the
  binding); §7's "best-effort" phrasing accommodates this (Phase 3).

## M9: External Decisions

- **Pause follows only settled outcomes**: automatic §10 retry runs first; `WorkflowPaused` is a **derived obligation** appended after `ExecutionSucceeded`, terminal failure, or `ExecutionCancelled` — evaluated from projected state at the top of each round, never welded into the dispatch continuation, so the outcome→pause crash window re-derives on the next call (Phase 1).
- **One resolving decision per pause**: supplementary executions occupy §17's "zero or more decisions" window without being decisions; each recorded decision resolves its pause, a second decision naming the same execution is invalid, and a step that pauses again does so under a new `ExecutionId` (Phase 2).
- **`Reject` is externally triggered exhaustion**: the step projects terminally failed with retry foreclosed regardless of remaining budget — and it applies to a *successful* paused outcome too (the approval-gate "no") (Phase 2).
- **Decision consequences are projected facts, not handler state**: an unfulfilled `RetryWithRevision`/`Supersede` (decision recorded, no newer accept for the affected step) is re-derived by any later pump, so the record→dispatch crash window loses nothing (Phase 3).
- **The supplementary artifact reaches workers via `AER_SUPPLEMENTARY_INPUT`**, a dedicated variable that can never collide with declared `AER_INPUT_<n>` names (Phase 3).
- **The resume race is recorded, not fixed**: a dependent of the pausing step that dispatches at resume against the pre-supersede result goes stale and reruns through the same cascade once the superseding rerun lands; preventing it would need the holding mechanism §17.5 declines to introduce (Phase 3).
- **Non-process executions are pending until satisfied, never `Failed`** — there is no exit signal to classify against; completion is detected at the top of every mutation call by full contract satisfaction (existence + §4.1 conditions). `ExecutionRequest.StepId` is optional: step-less supplementary executions are tracked execution-level and ignored for step state (Phase 4).

## M8: Reactive Scheduler

- **Attempt counting is per round**: `ConsecutiveFailureCount` counts trailing consecutive failures *since the last success*, so a step re-run after M9's `Supersede` starts with a fresh retry budget — matching §11.3's "only the latest attempt per step matters" framing (Phase 1).
- **Retry decisions live in `Aer.Flow.Scheduling.RetryEngine`**, a pure predicate (`MayRetry`) consulted by the Dependency Resolver; "terminally failed" is a derived fact (`Failed` ∧ ¬`MayRetry`), never a stored event, per §5.2. `Cancelled` is never retried (§9, §10); `MaxAttempts` is total attempts per round and validated `>= 1` (Phase 2).
- **Determinism under concurrency (§13)**: `ExecutionRequestAccepted` events are appended and fsync'd sequentially in snapshot declaration order *before* their dispatches are awaited; completion order only influences *when* the next projection happens, never *what* it concludes (Phase 3).
- **No concurrency cap in M8**, recorded deliberately: `ExecutionRequestRejected` stays unexercised until an admission cap is a real, scoped design decision (rejection is durable; what re-admits a rejected step?) (Phase 3).
- **Manifest cache deferred** per §21's expectation: a 400-event log re-reads in ~3.8ms, dwarfed by real dispatch latency; revisit only if a per-task log grows large enough for this to show up in practice (Phase 4).

## M7: Foundation

- **Workflow definition files are plain JSON** (`.json`, one document — not `.jsonl`), deserialized through the same `System.Text.Json` converters as every other domain record and `flow.jsonl` itself (Phase 3).
- **Paths reach workers via environment variables**: `AER_INPUT_<n>` and `AER_OUTPUT_DIR`. `ArtifactManager.ResolveInputPaths` matches a step's declared `Inputs` names against its direct dependencies' declared `Outputs` names (Phase 6).
- **A single `flow.jsonl` records both Flow- and Core-originated events** (allowed because §5 leaves the storage backend implementation-defined); §5.1's dual-log ownership is enforced in the type system (`LogEntry.FlowLogEntry` vs. `LogEntry.CoreLogEntry`), not by physical file separation (Phase 6).
- **aer-core is consumed as a pinned git submodule** (`external/aer-core`), built from source via `pixi run build-core`. Revisit with a real package feed only once a second consumer exists (Phase 6; AER Overview §6).
- **Worker resolution shape**: the Mutation Interface takes `Worker`-name → `WorkerBinding` (the `WorkerContract`, the concrete `CoreDispatchTarget`, and a per-worker `Timeout`). The timeout deliberately lives on the binding, not the step, keeping the frozen `WorkflowDefinitionSnapshot` shape (§11.2) unchanged (Phase 7).
- **Where `FailureClassification` (§8.1) lives**: the first of the contract's declared `OptionalMetadata` file names (checked in order) that exists in the output directory, parses as JSON, and has a top-level `FailureClassification` field wins; absent or unrecognized is `null`, which every consumer treats as `Retryable` (Phase 7).
- **The concurrency guard is held by the Mutation Interface** for the full duration of the mutation call — the single mutation surface (§14) is the one place §15's guarantee needs enforcing. `flow.lock` is left on disk on release; its existence is deliberately meaningless — only the live `FileShare.None` hold signals "locked" (Phase 8).
