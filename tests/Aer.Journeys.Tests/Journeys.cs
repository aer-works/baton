namespace Aer.Journeys.Tests;

/// <summary>
/// Which in-process runner drives a journey leg. The two UI runners render the <em>real</em>
/// surface — not a mock, not the daemon API behind it — so a leg passes only when the thing a
/// person actually looks at behaves. <see cref="Attest"/> legs cannot run in process at all and
/// stand on a dated human sign-off (see the runbook).
/// </summary>
public enum Runner
{
    /// <summary>Avalonia view-mounted headless (this project). Desktop legs.</summary>
    DesktopHeadless,

    /// <summary>Flutter widget test (<c>src/Aer.Mobile/test/journeys</c>). Phone legs.</summary>
    PhoneWidget,

    /// <summary>Adapter / Flow-level .NET test, no UI (this project). Engine legs.</summary>
    Engine,

    /// <summary>Real cross-device / live-vendor walk — a human gate, never CI. See the runbook.</summary>
    Attest,
}

/// <summary>How much of a journey's promise is under an executable test today.</summary>
public enum Coverage
{
    /// <summary>A test drives the real surface and currently fails — the promise is not kept yet.</summary>
    DrivenRed,

    /// <summary>A test drives the real surface and currently passes — the promise is kept for this leg.</summary>
    DrivenGreen,

    /// <summary>The surface exists and is driveable, but no test is written yet (a fast-follow).</summary>
    Pending,

    /// <summary>Not in-process testable; stands on a human sign-off.</summary>
    HumanAttested,
}

/// <summary>One surface a journey crosses, and how #313 covers it.</summary>
/// <param name="Surface">The human-readable surface this leg is, e.g. "desktop first-run".</param>
/// <param name="Runner">Which runner drives it.</param>
/// <param name="Coverage">Its coverage state in this repo today.</param>
/// <param name="Note">What the covering test asserts, or why it's pending / attested.</param>
public sealed record JourneyLeg(string Surface, Runner Runner, Coverage Coverage, string Note);

/// <summary>
/// One product journey (<c>spec/journeys.md</c>) as the harness sees it. The registry declares
/// only what is code-side-original — the id, the legs, the served issues; the title and status are
/// read from the spec at load (<see cref="SpecJourneys"/>, #952), so there is no second copy to
/// drift. <see cref="ReconcileTests"/> keeps the one direction a join cannot catch: a spec journey
/// with no registry entry. (#314 extends this to also compare the declared status against the
/// journey tests' actual pass/fail.)
/// </summary>
/// <param name="Id">The journey's stable id, e.g. "J6". Matches the <c>[Trait("Journey", …)]</c> on its tests.</param>
/// <param name="Legs">The surfaces it crosses and how each is covered here.</param>
/// <param name="Serves">The issues the journey serves, for cross-reference.</param>
public sealed record Journey(
    string Id,
    IReadOnlyList<JourneyLeg> Legs,
    IReadOnlyList<int> Serves)
{
    /// <summary>The spec's header title after the em dash, joined from the spec at load — never re-declared here.</summary>
    public string Title { get; init; } = "";

    /// <summary>The spec's <c>**Status:**</c> line, byte-for-byte, joined from the spec at load.</summary>
    public string DeclaredStatus { get; init; } = "";
}

/// <summary>
/// The journey registry. Adding a journey means adding it here and to the spec in the same change;
/// that coupling is the point. Titles and statuses are joined from the spec at load (#952) — a
/// declared id the spec does not carry fails loudly here, and a spec journey this registry does not
/// carry fails <see cref="ReconcileTests"/>.
/// </summary>
public static class Journeys
{
    /// <summary>The trait key every journey test carries, so <c>--filter Journey=J6</c> selects one.</summary>
    public const string TraitKey = "Journey";

    public static IReadOnlyList<Journey> All => LazyAll.Value;

    // Declared precedes LazyAll so nullable analysis can see it is initialized before the lazy's
    // factory could ever read it (reviewer catch on #952: the reverse order emitted CS8604).
    private static readonly IReadOnlyList<Journey> Declared =
    [
        new("J1",
        [
            new("desktop start → daemon → paired phone approve", Runner.Attest, Coverage.HumanAttested,
                "Cross-device: the phone's inbox scope (#335) and the desk→phone broadcast need a real paired device."),
        ], [335, 319, 330]),

        new("J2",
        [
            new("desktop room (spawn / host / gate)", Runner.DesktopHeadless, Coverage.Pending,
                "The room model (decisions 0001/0008/0009) isn't built yet; the spawn/host/gate legs get driven as they land."),
            new("live-vendor review quality", Runner.Attest, Coverage.HumanAttested,
                "A review's live quality needs authenticated vendors — a live-smoke check."),
        ], [333, 335, 340]),

        new("J3",
        [
            new("desktop inbox / cards (Home)", Runner.DesktopHeadless, Coverage.Pending,
                "Home already segregates waiting/running/finished and labels failed on the card; the remaining red edge (tracked on umbrella #752, former #355) is a fast-follow view-mounted assertion."),
            new("phone inbox — client-injection seam (#753, former #337)", Runner.PhoneWidget, Coverage.Pending,
                "InboxScreen builds its own DaemonClient from stored credentials, so it needs a client-injection seam before a widget test can drive it (phone lands on switcher since PR #1046; tracked on umbrella #753, former #337)."),
        ], [337, 355, 334]),

        new("J4",
        [
            new("fresh-device LAN pairing", Runner.Attest, Coverage.HumanAttested,
                "A physical phone on a real LAN, per the pairing runbook (#347, #349)."),
        ], [347, 349, 346]),

        new("J5",
        [
            new("desktop ↔ daemon ↔ phone broadcast", Runner.Attest, Coverage.HumanAttested,
                "The broadcast path (#330, #348) is a cross-process/device concern — a real second surface."),
        ], [330, 348, 335]),

        new("J6",
        [
            new("engine — Claude grant enforcement at the dispatch boundary", Runner.Engine, Coverage.DrivenGreen,
                "J6_DeniedToolEnforcementTests: a shell-denied grant produces a dispatch carrying --disallowedTools. Green since #331 (2026-07-23) — and green is NOT the same as J6 being kept. The vendor audit (#527) measured that --allowedTools/--disallowedTools pre-approve rather than restrict: a model denied Write writes the file through Bash (#529). So this leg asserts the dispatch shape, which holds, and NOT the promise, which does not. It is the reason J6 went Partial -> Fails on 2026-07-25 while its test stayed green."),
            new("engine — a PreToolUse hook is the actual capability boundary", Runner.Engine, Coverage.Pending,
                "Decision 0029: a hook is the only measured enforcement point covering vendor tools, exit-2 blocks even against an explicit allow rule, and it is now mandatory on every worker AER spawns. Nothing ships it yet, which is what makes J6 Fails rather than Partial. #530 gates it: hooks may fail SILENTLY on Windows, and a gate that silently does not fire looks exactly like one that works."),
            new("engine — Gemini fails closed when a denial is unenforceable", Runner.Engine, Coverage.Pending,
                "agy has no deny-list flag, so AgyWorkerAdapter throws PermissionGrantUnsupportedException rather than running under-enforced — decision 0004's fail-closed floor. Correct and untested: J6's test only exercises ClaudeWorkerAdapter. agy's permission rules are also global-only (#527), so a workspace hook is its only per-worker gate."),
            new("live worker actually refuses the tool", Runner.Attest, Coverage.HumanAttested,
                "The end-to-end refusal (worker attempts the tool, is blocked, it's recorded) needs a live vendor — a smoke check. Per #529 this must include the substitution route: withhold Write, then confirm the worker cannot write through Bash either."),
        ], [331, 529, 530]),

        new("J7",
        [
            new("phone disconnected state + recovery action", Runner.PhoneWidget, Coverage.Pending,
                "InboxScreen renders _connectionError with a Reconnect button, but isn't client-injectable yet; the truthful-state assertion waits on the same seam as J3-phone (#346, #349)."),
            new("real network-drop walk", Runner.Attest, Coverage.HumanAttested,
                "A real device losing the daemon, per the recovery runbook."),
        ], [346, 347, 349]),

        new("J8",
        [
            new("desktop first-run empty state", Runner.DesktopHeadless, Coverage.DrivenGreen,
                "J8_DesktopFirstRunTests: an empty Home renders \"No rooms yet.\" with real Start-from-template / Create-workflow actions (#190), not a blank wall. Green — this leg is kept."),
            new("phone empty rooms surface offers a first action (#337)", Runner.PhoneWidget, Coverage.DrivenGreen,
                "j8_first_run_phone_test: an empty RoomsScreen shows \"No rooms yet.\" and a real \"New room\" start action, not just a dead-end message. Green — this leg is kept."),
        ], [337, 338, 339]),

        new("J9",
        [
            new("cross-vendor usage view", Runner.DesktopHeadless, Coverage.Pending,
                "No usage surface exists yet (#360, #338); the aggregation/display leg gets driven when the surface lands."),
        ], [360, 338]),

        // J10–J18 — the M25 design corpus's nine claims (docs/design/07-whats-new.md), which states
        // they are "journey-shaped on purpose". Every leg is Pending or HumanAttested on purpose:
        // nothing here is built, and DrivenRed would assert a test exists and fails.

        new("J10",
        [
            new("desktop gate — consult without closing it", Runner.DesktopHeadless, Coverage.Pending,
                "Decision 0019's centrepiece. Needs a gate that is a long-lived object surviving consultation turns; no such object exists yet."),
            new("a consulted worker's answer actually contradicts", Runner.Attest, Coverage.HumanAttested,
                "Whether a second opinion genuinely disagrees is a live-vendor quality question, not something a stub can stage."),
        ], [424, 385, 367]),

        new("J11",
        [
            new("both vendors acting on plan auth", Runner.Attest, Coverage.HumanAttested,
                "Permanently human: the adapters own no key-handling code and shell out to whatever is authenticated on the host, so there is no headless way to provision this (CLAUDE.md, live-vendor smoke)."),
        ], [478, 391]),

        new("J12",
        [
            new("room memory is visible, attributed, editable", Runner.DesktopHeadless, Coverage.Pending,
                "Decision 0016. Memory falls out of cwd and splits per vendor today (#442); the surface does not exist."),
            new("a fact crosses vendors in one room", Runner.Attest, Coverage.HumanAttested,
                "Whether a second vendor actually uses the fact is a live-model behaviour, not a stub assertion."),
        ], [442, 386]),

        new("J13",
        [
            new("two same-vendor chips, distinct model and effort", Runner.DesktopHeadless, Coverage.Pending,
                "Decisions 0017 + 0023. A worker is pinned to a vendor and nothing below it today; the chip must render AER's own vocabulary, never a vendor flag value."),
            new("both actually answer, at different cost", Runner.Attest, Coverage.HumanAttested,
                "Requires two live models on one subscription."),
        ], [391, 479]),

        new("J14",
        [
            new("artifact versions, attribution and diff", Runner.DesktopHeadless, Coverage.Pending,
                "Decision 0021. The engine stores artifacts per execution, but they are not objects a person can version, attribute or hand over (#377)."),
            new("a second vendor edits the first's document", Runner.Attest, Coverage.HumanAttested,
                "Cross-vendor authorship needs both CLIs authenticated."),
        ], [377, 455]),

        new("J15",
        [
            new("quit desktop → permission on phone → reopen continued", Runner.Attest, Coverage.HumanAttested,
                "Cross-device and cross-process. Distinct from J1, whose bar is a decision gate on a still-running app; the permission kind is 0015's genuinely-new one and is gated on #445."),
        ], [445, 337, 434]),

        new("J16",
        [
            new("the ladder at the moment of asking, then Settings", Runner.DesktopHeadless, Coverage.Pending,
                "Decision 0022 over 0004's scopes. The grant path, distinct from J6's deny-enforcement. No ladder and no Settings surface exist (#338)."),
            new("the grant actually suppresses the next ask", Runner.Attest, Coverage.HumanAttested,
                "Enforcement differs per vendor — agy matches command rules literally, so a family-shaped rung is not expressible there (docs/vendor-capabilities.md)."),
        ], [445, 481, 338]),

        new("J17",
        [
            new("phone shape editor — four steps, reorder, instructions", Runner.PhoneWidget, Coverage.Pending,
                "Decisions 0014 + 0025. The step model has no instruction field at all today, and authoring is a desktop canvas behind Advanced (#327)."),
            new("desktop starts it and renders the run", Runner.DesktopHeadless, Coverage.Pending,
                "Same renderer for authoring and watching, per 0014."),
        ], [339, 327, 340]),

        new("J18",
        [
            new("/ask-all and side-by-side answers", Runner.DesktopHeadless, Coverage.Pending,
                "Decision 0024. No command palette, no namespacing and no multi-worker room to broadcast into yet."),
            new("two models genuinely disagree", Runner.Attest, Coverage.HumanAttested,
                "The disagreement is the point and cannot be staged with stubs."),
        ], [386, 424]),

        new("J19",
        [
            new("daemon event→notification pipeline and the decision round-trip", Runner.Engine, Coverage.Pending,
                "The wake-bridge (#799) and AER's own notifier (decision 0030) do not exist yet; the automated legs get driven as they land."),
            new("real device, real notification, desk untouched", Runner.Attest, Coverage.HumanAttested,
                "The phone half is a human walk: notifications reaching a physical pocket and a decision answered there advancing the room. This journey is docs/plan.md §M26's demo bar."),
        ], [799, 806, 337]),
    ];

    private static readonly Lazy<IReadOnlyList<Journey>> LazyAll = new(() =>
    {
        var spec = SpecJourneys.Parse().ToDictionary(e => e.Id);
        return Declared.Select(j =>
            spec.TryGetValue(j.Id, out var e)
                ? j with { Title = e.Title, DeclaredStatus = e.Status }
                : throw new InvalidOperationException(
                    $"Journey {j.Id} is declared in this registry but spec/journeys.md has no such journey."))
            .ToList();
    });
}
