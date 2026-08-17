using Aer.Flow.Artifacts;
using Aer.Flow.Domain;
using Aer.Flow.Outcomes;
using Aer.Flow.Projection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui.Core;

/// <summary>
/// M19 Phase 3 (issue #188): the plain-language vocabulary map (docs/archive/ux/ux-principles.md) applied
/// to the room view's primary text. Total for primary labels — a spec term reaching a primary
/// label is a defect (Phase 1 decision of record); the precise engine vocabulary survives one
/// disclosure away (ids as handles, the Details section, tooltips).
/// </summary>
public static class PlainLanguage
{
    /// <summary>
    /// Maps step status to plain-language text.
    /// Extended (#1116, 0026 §5): an <see cref="FailureClassification.ExhaustedUntil"/> step
    /// reads "Out of plan — resumes {local time}" with a known instant, or "Out of plan — reset unknown" without.
    /// </summary>
    public static string ForStep(
        StepStatus status,
        FailureClassification? failureClassification = null,
        DateTimeOffset? retryNotBefore = null)
    {
        if (status == StepStatus.Failed && failureClassification == FailureClassification.ExhaustedUntil)
        {
            return ForExhaustion(retryNotBefore);
        }

        return status switch
        {
            StepStatus.Pending => "Not started yet",
            StepStatus.Running => "Working",
            StepStatus.Succeeded => "Done",
            StepStatus.Failed => "Failed",
            // #461: was "Stopped". The token file's label for this state is "Cancelled", and a step
            // saying one word while the room card says another is the collage this milestone is undoing.
            StepStatus.Cancelled => "Cancelled",
            StepStatus.Paused => "Waiting for your review",
            StepStatus.Rejected => "Rejected",
            // #616: the discard throws instead of answering — same posture as the generated
            // AerStatusPresentation. A new member reaches no silent word: StatusDerivationTests'
            // golden map iterates every member, so the gap is a red test, never a shipped label.
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped step status."),
        };
    }

    /// <summary>
    /// The single 0026 §5 exhaustion sentence -- "Out of plan — resumes {local time}" with a known
    /// reset instant, or "Out of plan — reset unknown" without (an honest gap, never a fabricated
    /// estimate). This is the ONE derivation (#1180, record-once): <see cref="ForStep"/> calls it
    /// for the room/step surface (#1116), and the interactive-session chat card
    /// (<c>ChatViewModel.AddTurnMessages</c>) calls it for an exhausted turn -- neither restates the
    /// strings.
    /// </summary>
    public static string ForExhaustion(DateTimeOffset? retryNotBefore) =>
        retryNotBefore is { } instant
            ? $"Out of plan — resumes {instant.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)}"
            : "Out of plan — reset unknown";

    /// <summary>
    /// "claude · 14:02" — the room Files section's version vocabulary (#1340, 0021 §2): worker plus
    /// local time, or an honest gap ("time not recorded") when <see cref="FileVersion.ProducedAt"/>
    /// is null, the same rule <see cref="ForExhaustion"/> already applies to a reset time nobody
    /// reported.
    /// </summary>
    public static string ForFileVersion(string worker, DateTimeOffset? producedAt) =>
        producedAt is { } instant
            ? $"{worker} · {instant.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)}"
            : $"{worker} · time not recorded";

    public static string ForDecision(DecisionType decisionType) => decisionType switch
    {
        DecisionType.Resume => "Approved",
        DecisionType.Reject => "Rejected",
        DecisionType.RetryWithRevision => "Retry requested",
        DecisionType.Supersede => "Sent back",
        // #616: throws, never a raw enum name — the golden map in StatusDerivationTests reddens on a new member.
        _ => throw new ArgumentOutOfRangeException(nameof(decisionType), decisionType, "Unmapped decision type."),
    };

    /// <summary>
    /// Plain-language sentence for a recorded decision moment in a transcript history row (#1196/#1199).
    /// The verb comes from <see cref="ForDecision"/> rather than a second phrasing of the same choice.
    /// </summary>
    /// <remarks>
    /// Only <see cref="DecisionType.Supersede"/> names a target — `FlowEvent.ExternalDecisionRecorded`
    /// says so of its own `TargetStepId` — and only its verb ("Sent back") takes one grammatically.
    /// Appending the target to whatever verb happened to arrive would read "Approved to review" the
    /// day another decision type carries one.
    /// </remarks>
    public static string ForRecordedDecision(RecordedDecisionMoment moment)
    {
        ArgumentNullException.ThrowIfNull(moment);
        var verb = ForDecision(moment.DecisionType);
        return moment is { DecisionType: DecisionType.Supersede, TargetStepId: { } targetStepId }
            ? $"{verb} to {targetStepId.Value}"
            : verb;
    }

    /// <summary>
    /// The room-level headline — the shared room-status derivation (<c>RoomCardViewModel.DeriveStatus</c>), by delegation rather than by copy. This
    /// method used to restate the mapping and claim it was shared; the copy drifted (#976: no
    /// Cancelled arm, so a cancelled run'headline said "Finished" — the 0020 worked example,
    /// alive on a second surface) and hard-coded the review wording for every pause, missing
    /// #334's reply/review split. Delegating is what makes "can never drift" true.
    /// </summary>
    /// <param name="isFlowLockHeld">
    /// #1219: threaded through rather than probed here, since this is handed a projection and no
    /// directory. <b>Deliberately not defaulted.</b> The first draft gave it <c>= true</c> on the
    /// reasoning that a caller which cannot answer should reproduce the pre-#1219 reading — and a
    /// second reader found that the one production caller simply omitted it, so the Task view's
    /// headline went on saying "Working — …" for a room whose process had died. That is the same
    /// disagreement this issue exists to remove, one surface over. A default here does not make a
    /// caller honest; it makes the dishonest case invisible.
    /// </param>
    public static string ForWorkflow(RoomProjection projection, bool isFlowLockHeld)
        // #1296: the legacy Task view this feeds is already slated for retirement (see
        // RebuildRoomSteps's remarks) and has no directory path here to probe the concurrency
        // gate with, so it never threads the real signal -- always false, same as any other
        // surface that genuinely cannot answer.
        => RoomCardViewModel.DeriveStatus(projection, projection.PendingPermission, isFlowLockHeld, isWaitingToStart: false).StatusText;

    /// <summary>
    /// #215: real execution/decision ids are 32-char generated Guids — pure visual noise to a
    /// non-expert user inline in the drill-in's primary text. Truncated to a short prefix, still
    /// enough to distinguish attempts by eye; the untruncated id remains in the Details disclosure
    /// (§12 traceability), which this projection never touches.
    /// </summary>
    public static string ShortId(string id) => id.Length > 8 ? id[..8] : id;

    /// <summary>
    /// #1176: The user-facing wording seam for an app-level unhandled exception state sentence.
    /// </summary>
    public static string ForUnexpectedAppError() => InteractionState.UnexpectedAppError.DisplayName();
}

/// <summary>
/// One step of the open room as the drill-in's read model (M19 Phase 3, issue #188;
/// docs/archive/ux/information-architecture.md's Task view): the plain status up front, and everything
/// that used to sprawl as separate stacked sections — attempts, output files, conversations,
/// recorded decisions — sliced per step. Rebuilt wholesale on every refresh like every other
/// projection surface; selection is re-anchored by <see cref="StepId"/> across rebuilds.
/// </summary>
public sealed partial class StepItemViewModel : ObservableObject
{
    private readonly Action<StepItemViewModel> _select;

    public StepItemViewModel(
        string stepId,
        string worker,
        StepStatus status,
        IReadOnlyList<string> attemptLines,
        IReadOnlyList<ArtifactFileViewModel> outputFiles,
        IReadOnlyList<ConversationRefViewModel> conversations,
        IReadOnlyList<string> decisionLines,
        PausedStepViewModel? pausedStep,
        Action<StepItemViewModel> select,
        string? adapter = null,
        IReadOnlyList<ArtifactFileViewModel>? promptFiles = null,
        FailedStepBannerViewModel? failedBanner = null,
        FailureClassification? latestFailureClassification = null,
        DateTimeOffset? retryNotBefore = null,
        AerEffortTier? effortTier = null,
        AerDepthTier? depthTier = null)
    {
        StepId = stepId;
        Worker = worker;
        Status = status;
        AttemptLines = attemptLines;
        OutputFiles = outputFiles;
        Conversations = conversations;
        DecisionLines = decisionLines;
        PausedStep = pausedStep;
        _select = select;
        Adapter = adapter;
        PromptFiles = promptFiles ?? [];
        FailedBanner = failedBanner;
        LatestFailureClassification = latestFailureClassification;
        RetryNotBefore = retryNotBefore;
        EffortTier = effortTier;
        DepthTier = depthTier;
    }

    public string StepId { get; }
    public string Worker { get; }
    public string? Adapter { get; }
    public StepStatus Status { get; }

    /// <summary>
    /// The canonical effort word this step's worker is bound to, or null for a null, raw, or unmapped
    /// value (#1318, decision 0058's scope ruling — see <see cref="EffortTierParsing"/>). The
    /// workflow-room chip's effort mark; null renders no mark, never an empty frame (ruling 2).
    /// </summary>
    public AerEffortTier? EffortTier { get; }

    /// <summary>
    /// The canonical depth (model-tier) word this step's worker resolves to, or null for a null,
    /// unrecognized-adapter, or unmapped-model value (#1339, decision 0058's scope ruling — see
    /// <see cref="EffortTierParsing.TryParseDepth"/>). The workflow-room chip's depth mark; null
    /// renders no mark, never an empty frame (ruling 2) — the common case for an agy-vendored worker,
    /// since #1330 deliberately left agy's column unrecorded.
    /// </summary>
    public AerDepthTier? DepthTier { get; }
    public FailureClassification? LatestFailureClassification { get; }
    public DateTimeOffset? RetryNotBefore { get; }
    public string PlainStatusText => PlainLanguage.ForStep(Status, LatestFailureClassification, RetryNotBefore);
    public IReadOnlyList<string> AttemptLines { get; }
    public IReadOnlyList<ArtifactFileViewModel> OutputFiles { get; }
    public IReadOnlyList<ConversationRefViewModel> Conversations { get; }
    public IReadOnlyList<string> DecisionLines { get; }

    /// <summary>
    /// M25 Clause 4 (issue #617): banner rendering for failed steps. Non-null only when <see cref="Status"/> is <see cref="StepStatus.Failed"/>.
    /// </summary>
    public FailedStepBannerViewModel? FailedBanner { get; }
    public bool HasFailedBanner => FailedBanner is not null;

    /// <summary>
    /// One entry per attempt whose execution durably captured its resolved prompt (issue #292) —
    /// <see cref="ArtifactManager.PromptFileName"/> found in that execution's output directory,
    /// exactly the same discovery-by-artifact-presence pattern <see cref="HasConversations"/>'
    /// transcript check already uses, never a declared-outputs lookup (an ordinary step's contract
    /// never names this file). Reuses <see cref="ArtifactFileViewModel"/>/the shared output-file
    /// preview mechanism rather than a bespoke rendering path — parity with dialogue's transparency,
    /// not a new surface.
    /// </summary>
    public IReadOnlyList<ArtifactFileViewModel> PromptFiles { get; }
    public bool HasPromptFiles => PromptFiles.Count > 0;

    /// <summary>
    /// Normalized to exactly the vendors <c>VendorCliPresence</c> probes for (<c>claude</c>,
    /// <c>gemini</c>), or <see langword="null"/> for anything else — the presentation layer's
    /// <c>VendorIconGeometryConverter</c>/<c>VendorIconBrushConverter</c> map this to a glyph and
    /// brush by name (design-language.md's "named resource, not a raw value in a view" rule; a
    /// prior pass hardcoded geometry/hex strings here instead, which also meant it invented icon
    /// branches for adapters — "shell", "stub", "codex", "openai" — nothing in this codebase
    /// registers).
    /// </summary>
    public string? VendorKey
    {
        get
        {
            var target = (Adapter ?? Worker).ToLowerInvariant();
            if (target.Contains("claude")) return "claude";
            if (target.Contains("agy") || target.Contains("gemini")) return "agy"; // vocabulary-ok: engine key
            return null;
        }
    }

    public string VendorDisplay => VendorKey ?? (Adapter != null ? $"{Worker} ({Adapter})" : Worker); // vocabulary-ok: technical adapter setting

    /// <summary>Non-null exactly while this step waits at its review gate — the inline decision actions (§17 via M15's <see cref="PausedStepViewModel"/>, unchanged semantics, plain words on the buttons).</summary>
    public PausedStepViewModel? PausedStep { get; }
    public bool IsPaused => PausedStep is not null;

    public bool HasOutputFiles => OutputFiles.Count > 0;
    public bool HasConversations => Conversations.Count > 0;
    public bool HasDecisions => DecisionLines.Count > 0;

    [ObservableProperty]
    private bool isSelected;

    [RelayCommand]
    private void Select() => _select(this);
}

/// <summary>
/// M25 / issue #618, promoted into the canonical machine by #1299: the waiting-on-lock banner.
/// Renders <see cref="RoomCardStatus.WaitingOnLock"/>; the ruling behind why it has nothing to link
/// to lives on that member's own doc comment.
/// </summary>
public sealed partial class WaitingOnLockBannerViewModel : ObservableObject
{
    private readonly Func<Task>? _tryAgainAsyncAction;

    public string Title => "Waiting on another process's lock";
    public string HolderText { get; }

    public WaitingOnLockBannerViewModel(string? holderDescription, DateTime? acquiredAtUtc, Func<Task>? tryAgainAsyncAction = null)
    {
        var holder = string.IsNullOrWhiteSpace(holderDescription)
            ? "another process — it did not say which"
            : holderDescription;

        HolderText = acquiredAtUtc is { } acquired
            ? $"Held by {holder} for {FormatDuration(DateTime.UtcNow - acquired)}"
            : $"Held by {holder}";

        _tryAgainAsyncAction = tryAgainAsyncAction;
    }

    /// <summary>Whole seconds/minutes/hours — this is a wait indicator, not a stopwatch (#483's own reasoning for coarse units applies here too).</summary>
    private static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
            : elapsed.TotalMinutes >= 1
                ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
                : $"{(int)elapsed.TotalSeconds}s";
    }

    [RelayCommand]
    private async Task TryAgain()
    {
        if (_tryAgainAsyncAction != null)
        {
            await _tryAgainAsyncAction();
        }
    }
}

/// <summary>
/// Why a room is not going anywhere on its own, and therefore which offer its transcript carries
/// (#1215). Deliberately closed and deliberately not "not running": a room waiting on a decision is
/// also not running, and it is neither of these — it already has the person's action on screen.
/// </summary>
public enum RoomStoppedReason
{
    /// <summary>
    /// Non-terminal, nothing paused, and no live pump owns the directory — the process died mid-run.
    /// <see cref="WorkflowStatus.Running"/> cannot say this on its own; read its own summary, which
    /// covers a live attempt and a crashed one in one clause. What separates them is
    /// <see cref="Aer.Flow.Concurrency.ConcurrencyGuard.IsHeld"/> — a kernel-held lock the OS drops
    /// the instant its holder exits, crashed or not, held by <c>MutationInterface</c> across the
    /// whole of <c>PumpToFixedPointAsync</c> (a #1094 vendor-quota park included, which is why a
    /// day-long paced wait does not read as stopped).
    /// </summary>
    StoppedMidRun,

    /// <summary>
    /// Terminal. Resuming this directory is a proven silent no-op (see
    /// <see cref="MainWindowViewModel.IsRoomFinished"/>), so the offer is a fresh room cloned from it.
    /// </summary>
    Finished,

    /// <summary>
    /// Terminal because someone stopped it (#1219). The same offer as <see cref="Finished"/> and
    /// deliberately not the same words: telling a room you had just stopped that it "finished" is the
    /// exact sentence #461 was filed to delete, and it would come straight back if this shared
    /// <see cref="Finished"/>'s copy.
    /// </summary>
    Cancelled,
}

/// <summary>
/// #1215: the offer a stopped room's own transcript carries. Replaces the header Run button, whose
/// only unique job was resuming a room no other desktop path resumes. (Worth knowing while reading
/// this: <c>RoomWakeBridge</c> is not that path — #799 wakes a room whose <em>delegated</em> workflow
/// reached terminal, which is the other direction entirely, and it starts dormant on restart.)
/// It sits on the turn rather than in chrome. What this replaced, which precedents it follows, and
/// what was rejected on the way — including why it is a click and not something that happens when a
/// room is opened — is recorded in <c>docs/design/02-screens.md</c>'s 2026-08-14 amendment.
/// </summary>
public sealed partial class RoomStoppedCardViewModel : ObservableObject
{
    private readonly Func<Task> _runAsyncAction;

    public RoomStoppedReason Reason { get; }

    public string Headline => Reason switch
    {
        RoomStoppedReason.Finished => "This room has finished",
        RoomStoppedReason.Cancelled => "You stopped this room",
        _ => "This room stopped mid-run",
    };

    public string BodyText => Reason switch
    {
        RoomStoppedReason.Finished =>
            "Run it again and its work starts in a fresh room cloned from this one — this room's own history is left as it is.",
        RoomStoppedReason.Cancelled =>
            "Run it again and its work starts in a fresh room cloned from this one — what this room did up to the stop is left as it is.",
        _ => "Nothing is running it and it is not waiting on you. Resume picks it up where it left off.",
    };

    public string ActionLabel => Reason == RoomStoppedReason.StoppedMidRun ? "Resume" : "Run it again";

    /// <summary>
    /// False while this card's own action is in flight, so a second click cannot post a second run.
    /// A competing <em>external</em> pump is a different question and is refused by the room's own
    /// §15 lock, surfacing as the waiting-on-lock banner — this flag neither can nor tries to.
    /// </summary>
    [ObservableProperty]
    private bool isEnabled = true;

    public RoomStoppedCardViewModel(RoomStoppedReason reason, Func<Task> runAsyncAction)
    {
        ArgumentNullException.ThrowIfNull(runAsyncAction);
        Reason = reason;
        _runAsyncAction = runAsyncAction;
    }

    [RelayCommand]
    private async Task Run()
    {
        IsEnabled = false;
        try
        {
            await _runAsyncAction();
        }
        finally
        {
            IsEnabled = true;
        }
    }
}

/// <summary>
/// Issue #994: the room turn-host status card / banner — values and live usage. Dormancy left this
/// surface in #1178: it renders as a transcript turn (with Wake) in the chat, so the banner keeps
/// only the meter presentation.
/// </summary>
public sealed partial class RoomTurnHostBannerViewModel : ObservableObject
{
    public string MeterText { get; }
    public string ValuesText { get; }
    public string? LoadErrorText { get; }

    public RoomTurnHostBannerViewModel(RoomTurnHostStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        MeterText = $"machine turns {status.TurnsInTrailingHourCount}/{status.MachineTurnsPerHourCap} this hour";

        var sourceText = string.Equals(status.ThrottlesSource, "file", StringComparison.OrdinalIgnoreCase)
            ? "turn-throttles.json"
            : "defaults";
        ValuesText = $"{status.Throttles.MachineTurnMinimumGapSeconds}s gap · {status.Throttles.MachineTurnsPerHour}/h cap · limit {status.Throttles.ConsecutiveFailureLimit} ({sourceText})";

        LoadErrorText = status.LoadError;
    }
}

/// <summary>
/// M25 Clause 4 (issue #617): the failed-step banner — errors are content.
/// Shows the reason sentence and stderr excerpt in place, with affordances for "Try again",
/// "Ask <worker> to fix it", and "Show full output".
/// </summary>
public sealed partial class FailedStepBannerViewModel : ObservableObject
{
    private readonly Action? _tryAgainAction;
    private readonly Action<string, string, string>? _askWorkerToFixAction;
    private readonly Action? _showFullOutputAction;

    public string StepId { get; }
    public string Worker { get; }
    public string Adapter { get; }
    public string Headline { get; }
    public string ReasonSentence { get; }
    public string? StderrExcerpt { get; }
    public bool HasStderrExcerpt => !string.IsNullOrWhiteSpace(StderrExcerpt);

    public string TryAgainLabel { get; }
    public string AskWorkerLabel { get; }
    public bool CanShowFullOutput => _showFullOutputAction != null;

    public FailedStepBannerViewModel(
        string stepId,
        string worker,
        string adapter,
        string? rawReason,
        Action? reRunAction,
        Action<string, string, string>? askWorkerToFixAction,
        Action? showFullOutputAction)
    {
        StepId = stepId;
        Worker = worker;
        Adapter = adapter;
        _askWorkerToFixAction = askWorkerToFixAction;
        _showFullOutputAction = showFullOutputAction;

        // The split lives beside its writer (see OutcomeClassifier.SplitReasonAndStderr) so the
        // separator has one home; this surface only renders the two halves.
        var (reasonSentence, stderrExcerpt) = OutcomeClassifier.SplitReasonAndStderr(rawReason);
        ReasonSentence = reasonSentence;
        StderrExcerpt = stderrExcerpt;

        Headline = $"Failed · {worker} · {ReasonSentence}";
        AskWorkerLabel = $"Ask {worker} to fix it";

        // A banner exists only for StepStatus.Failed, and a Failed step is never in the paused set
        // (paused-after-exhausted-retries is Status Paused, with its own decision surface) — so the
        // only honest retry here is the re-run clone flow, and the label says so. That flow exists
        // only for a Terminal room: while a sibling branch is still running or paused, Run resumes
        // the same directory in place, and for a step that is Failed with no pending obligation the
        // pump reaches its fixed point immediately — a silent no-op wearing a "Try again" label.
        // The projector therefore passes reRunAction only when the workflow is Terminal, and
        // CanTryAgain hides the button in the meantime rather than offering a click that does
        // nothing.
        TryAgainLabel = "Try again (re-run room)";
        _tryAgainAction = reRunAction;
    }

    public bool CanTryAgain => _tryAgainAction != null;

    [RelayCommand]
    private void TryAgain() => _tryAgainAction?.Invoke();

    [RelayCommand]
    private void AskWorkerToFix() => _askWorkerToFixAction?.Invoke(Adapter, StepId, ReasonSentence);

    [RelayCommand]
    private void ShowFullOutput() => _showFullOutputAction?.Invoke();
}

/// <summary>
/// One durable output file of one execution, previewable in place — the same file-listing +
/// plain-text-preview ceiling as M14 (issue #121), re-sliced per step. <see cref="IsSelected"/>
/// (#211) tracks which file's content the step's single preview surface currently shows, so the
/// chip that produced it stays visually indicated instead of the previous selection lingering.
/// </summary>
public sealed partial class ArtifactFileViewModel : ObservableObject
{
    private readonly Func<string, Task> _previewAsync;
    private readonly Action<ArtifactFileViewModel> _select;

    public ArtifactFileViewModel(
        string label, string filePath, Func<string, Task> previewAsync, Action<ArtifactFileViewModel> select)
    {
        Label = label;
        FilePath = filePath;
        _previewAsync = previewAsync;
        _select = select;
    }

    public string Label { get; }
    public string FilePath { get; }

    [ObservableProperty]
    private bool isSelected;

    [RelayCommand]
    private Task Preview()
    {
        _select(this);
        return _previewAsync(FilePath);
    }
}

/// <summary>One execution of the step that recorded a durable transcript — opens M18's conversation view unchanged in behavior (discovery by transcript presence alone, §10.1).</summary>
public sealed partial class ConversationRefViewModel(string label, string outputDirectory, Action<string, string> show)
{
    public string Label { get; } = label;
    public string OutputDirectory { get; } = outputDirectory;

    [RelayCommand]
    private void Show() => show(OutputDirectory, Label);
}

/// <summary>
/// Builds the per-step drill-in items from one <see cref="RoomProjection"/> — pure projection
/// re-slicing (§11): every fact here already renders in the Details section's room-level panels;
/// this groups it by step, in plain language, nothing new asserted.
/// </summary>
public static class StepItemProjector
{
    public static IReadOnlyList<StepItemViewModel> Build(
        RoomProjection projection,
        string roomDirectoryPath,
        IReadOnlyList<PausedStepViewModel> pausedSteps,
        Func<string, Task> previewFileAsync,
        Action<string, string> showConversation,
        Action<StepItemViewModel> select,
        IReadOnlyDictionary<string, string>? workerAdapters = null,
        Action? reRunAction = null,
        Action<string, string, string>? askWorkerToFixAction = null,
        IReadOnlyDictionary<string, string>? workerEffortTiers = null,
        IReadOnlyDictionary<string, string>? workerDepthTiers = null)
    {
        var artifactsRootPath = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);
        var pausedByStepId = pausedSteps.ToDictionary(paused => paused.StepId);
        var executionsByStepId = projection.Lineage.Executions
            .Where(execution => execution.StepId is not null)
            .ToLookup(execution => execution.StepId!);
        var stepIdByExecutionId = projection.Lineage.Executions
            .Where(execution => execution.StepId is not null)
            .ToDictionary(execution => execution.ExecutionId, execution => execution.StepId!);

        // #1340 (0021 §2 fix): the file chip's version number, keyed by the exact same
        // outputDirectory/fileName path RoomFilesProjector built its FileVersion.FilePath from — the
        // one canonical version count, not a second count re-derived here. prompt.txt has no entry
        // (RoomFilesProjector excludes it), which is why the prompt chip below carries no version.
        var versionByFilePath = projection.Files.Files
            .SelectMany(file => file.Versions.Select((version, index) => (version.FilePath, Version: index + 1)))
            .ToDictionary(pair => pair.FilePath, pair => pair.Version, StringComparer.Ordinal);

        var items = new List<StepItemViewModel>(projection.State.Steps.Count);
        foreach (var stepState in projection.State.Steps)
        {
            var attempts = projection.History.AttemptsByStepId.GetValueOrDefault(
                stepState.StepId, (IReadOnlyList<ExecutionAttempt>)[]);
            var attemptLines = new List<string>(attempts.Count);
            for (var index = 0; index < attempts.Count; index++)
            {
                var attempt = attempts[index];
                var classificationSuffix = attempt.FailureClassification switch
                {
                    FailureClassification.Retryable => " — can be retried",
                    FailureClassification.Permanent => " — not retryable",
                    FailureClassification.ToolDenied => " — not retryable (a required tool was denied)",
                    _ => string.Empty,
                };

                // #597: the diagnostic OutcomeClassifier computed at classification time, shown to
                // the person reading the step rather than stopping at the event. Without this the
                // commonest failure shape — a worker that exits 0 having written none of its
                // declared outputs — reads here as a bare "Failed", which is the whole defect:
                // Flow knew which output was missing and said so nowhere a user looks. Absent for
                // attempts with no recorded failure and for those recorded before the field existed
                // — see ExecutionAttempt.Reason for why "non-failed status" is the wrong test: a
                // step paused after exhausting its retries is Paused and still carries the reason,
                // which is exactly when the person being asked to decide needs it.
                var reasonSuffix = string.IsNullOrWhiteSpace(attempt.Reason)
                    ? string.Empty
                    : $" — {attempt.Reason}";

                attemptLines.Add(
                    $"Attempt {index + 1} of {attempts.Count}: " +
                    $"{PlainLanguage.ForStep(attempt.Status)}{classificationSuffix} ({PlainLanguage.ShortId(attempt.ExecutionId.ToString())})" +
                    reasonSuffix);
            }

            // The banner quotes the newest attempt that recorded a reason, so its "Show full
            // output" must open that same execution's artifacts — the collections below are
            // chronological, and index 0 is the *first* attempt, which for a retried step is a
            // different (possibly successful) run than the one the headline describes.
            var reasonedAttempt = stepState.Status == StepStatus.Failed
                ? attempts.LastOrDefault(attempt => attempt.Reason != null) ?? attempts.LastOrDefault()
                : null;
            Action? reasonedExecutionShowOutput = null;
            Action? latestExecutionShowOutput = null;

            var outputFiles = new List<ArtifactFileViewModel>();
            var promptFiles = new List<ArtifactFileViewModel>();
            var conversations = new List<ConversationRefViewModel>();
            foreach (var execution in executionsByStepId[stepState.StepId])
            {
                var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootPath, execution.ExecutionId);
                var shortId = PlainLanguage.ShortId(execution.ExecutionId.ToString());
                ArtifactFileViewModel? firstOutputOfExecution = null;
                ArtifactFileViewModel? firstPromptOfExecution = null;
                foreach (var fileName in execution.OutputFiles)
                {
                    // #292: prompt.txt is durable capture of what the worker was asked, not
                    // something it produced — surfaced instead via PromptFiles' own collapsed
                    // affordance, matching dialogue's "Prompt" expander, not mixed into the
                    // always-visible output chips.
                    if (string.Equals(fileName, ArtifactManager.PromptFileName, StringComparison.Ordinal))
                    {
                        // #1340 (0021 §2 fix): author, not the execution's short id — prompt.txt
                        // carries no room-file version (RoomFilesProjector excludes it, #292), so
                        // there is no version number to show here, only who asked for it.
                        var promptFile = new ArtifactFileViewModel(
                            $"Prompt ({execution.Worker})",
                            Path.Combine(outputDirectory, fileName),
                            previewFileAsync,
                            select: file => SelectOutputFile(promptFiles, file));
                        promptFiles.Add(promptFile);
                        firstPromptOfExecution ??= promptFile;
                        continue;
                    }

                    // #1340 (0021 §2 fix): author + version — this slice's vocabulary — replaces the
                    // execution's short id an earlier pass put here.
                    var filePath = Path.Combine(outputDirectory, fileName);
                    var version = versionByFilePath.GetValueOrDefault(filePath, 1);
                    var outputFile = new ArtifactFileViewModel(
                        $"{fileName} ({execution.Worker} · v{version})",
                        filePath,
                        previewFileAsync,
                        select: file => SelectOutputFile(outputFiles, file));
                    outputFiles.Add(outputFile);
                    firstOutputOfExecution ??= outputFile;
                }

                ConversationRefViewModel? conversationOfExecution = null;
                if (TranscriptProjectionLoader.HasTranscript(outputDirectory))
                {
                    conversationOfExecution = new ConversationRefViewModel(
                        $"{stepState.StepId} — {shortId} ({execution.Worker})",
                        outputDirectory,
                        showConversation);
                    conversations.Add(conversationOfExecution);
                }

                Action? showThisExecution = null;
                if (conversationOfExecution is { } conversation)
                {
                    showThisExecution = () => conversation.ShowCommand.Execute(null);
                }
                else if (firstOutputOfExecution is { } output)
                {
                    showThisExecution = () => _ = output.PreviewCommand.ExecuteAsync(null);
                }
                else if (firstPromptOfExecution is { } prompt)
                {
                    showThisExecution = () => _ = prompt.PreviewCommand.ExecuteAsync(null);
                }

                if (showThisExecution != null)
                {
                    latestExecutionShowOutput = showThisExecution;
                    if (reasonedAttempt?.ExecutionId == execution.ExecutionId)
                    {
                        reasonedExecutionShowOutput = showThisExecution;
                    }
                }
            }

            var decisionLines = projection.History.Decisions
                .Where(decision =>
                    (stepIdByExecutionId.TryGetValue(decision.ReferencedExecutionId, out var decidedStepId) &&
                     decidedStepId == stepState.StepId) ||
                    decision.TargetStepId == stepState.StepId)
                .Select(decision =>
                {
                    var target = decision.TargetStepId is { } targetStepId ? $" to {targetStepId}" : string.Empty;
                    var pending = decision.Resolved ? string.Empty : " — not carried out yet";
                    return $"{PlainLanguage.ForDecision(decision.DecisionType)}{target} " +
                           $"({PlainLanguage.ShortId(decision.DecisionId.ToString())} on {PlainLanguage.ShortId(decision.ReferencedExecutionId.ToString())}){pending}";
                })
                .ToList();

            var stepDefinition = projection.Snapshot.Steps.First(step => step.StepId == stepState.StepId);
            var adapter = workerAdapters?.GetValueOrDefault(stepDefinition.Worker);

            // #1318 (decision 0058's scope ruling): the canonical word travels forward into this same
            // string field (ruling 4), and EffortTierParsing is the UI's only map from it to a mark
            // parameter. A raw vendor value or an unmapped word simply fails the parse -- rendering
            // absence, not fabricating a tier (ruling 2).
            var effortTier = EffortTierParsing.TryParseEffort(workerEffortTiers?.GetValueOrDefault(stepDefinition.Worker), out var parsedEffort)
                ? parsedEffort
                : (AerEffortTier?)null;

            // #1339 (decision 0058's scope ruling): the depth twin of effortTier above. workerDepthTiers
            // already carries only canonical words -- DepthTierMapping resolved the adapter+model pair
            // to one upstream of here (Aer.Daemon.DaemonBroadcast / MainWindow.GetWorkerDepthTiers) --
            // so this is the same vocabulary-only parse EffortTierParsing does for effort, never a
            // second look at vendor knowledge.
            var depthTier = EffortTierParsing.TryParseDepth(workerDepthTiers?.GetValueOrDefault(stepDefinition.Worker), out var parsedDepth)
                ? parsedDepth
                : (AerDepthTier?)null;

            FailedStepBannerViewModel? failedBanner = null;
            // #1116 review must-fix: no failed banner for an ExhaustedUntil step. The banner says
            // "Failed" with a red cross and a live ask-the-worker-to-fix button — for a step that
            // is not broken and must not have dispatches spent against it (0026 §1), directly
            // beside the step wording that already says "Out of plan — resumes …". The calm word
            // is the whole 0026 point; the banner would un-say it.
            if (stepState.Status == StepStatus.Failed
                && stepState.LatestFailureClassification != FailureClassification.ExhaustedUntil)
            {
                var reasonText = reasonedAttempt?.Reason ?? stepState.LatestFailureReason;

                // The reasoned attempt's own artifacts first; if it recorded none (a worker that
                // died before writing anything), the newest execution that has any is the closest
                // honest stand-in — never the chronological first.
                var showFullOutputAction = reasonedExecutionShowOutput ?? latestExecutionShowOutput;

                failedBanner = new FailedStepBannerViewModel(
                    stepState.StepId.Value,
                    stepDefinition.Worker,
                    adapter ?? stepDefinition.Worker,
                    reasonText,
                    reRunAction,
                    askWorkerToFixAction,
                    showFullOutputAction);
            }

            var resetInstant = stepState.RetryNotBefore ?? stepState.LatestExecutionFailedRetryNotBefore;
            items.Add(new StepItemViewModel(
                stepState.StepId.Value,
                stepDefinition.Worker,
                stepState.Status,
                attemptLines,
                outputFiles,
                conversations,
                decisionLines,
                pausedByStepId.GetValueOrDefault(stepState.StepId),
                select,
                adapter,
                promptFiles,
                failedBanner,
                stepState.LatestFailureClassification,
                resetInstant,
                effortTier,
                depthTier));
        }

        return items;
    }

    /// <summary>#211: marks exactly one output file of the step selected — mirrors <see cref="StepItemViewModel"/>'s own sibling-clearing selection pattern, scoped to one step's file list.</summary>
    private static void SelectOutputFile(IReadOnlyList<ArtifactFileViewModel> files, ArtifactFileViewModel selected)
    {
        foreach (var file in files)
        {
            file.IsSelected = ReferenceEquals(file, selected);
        }
    }
}

