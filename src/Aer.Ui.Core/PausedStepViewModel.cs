using Aer.Flow.Domain;
using Aer.RoomSession;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui.Core;

/// <summary>
/// Invokes a decision for <paramref name="stepId"/>'s <paramref name="executionId"/> (M15 Phase 2,
/// issue #138), now carrying Phase 3's (#139) artifact-carrying parameters: <paramref name="targetStepId"/>
/// (<see cref="DecisionType.Supersede"/> only), and the supplementary-artifact triple —
/// <paramref name="revisionFilePath"/>/<paramref name="supplementaryWorker"/>/
/// <paramref name="supplementaryOutputName"/> — <see langword="null"/> together whenever no
/// supplementary artifact rides this decision (always true for Resume/Reject; optional for
/// RetryWithRevision). The receiving end (<see cref="MainWindow"/>) is responsible for the
/// <c>aer supply</c> → <c>aer decide</c> two-call round trip this implies.
/// </summary>
public delegate Task DecideDelegate(
    StepId stepId,
    ExecutionId executionId,
    DecisionType decisionType,
    StepId? targetStepId,
    string? revisionFilePath,
    string? supplementaryWorker,
    string? supplementaryOutputName);

/// <summary>
/// One paused step's §7 action surface (M15 Phase 2, issue #138; extended for Phase 3, issue #139):
/// human-facing "Approve"/"Reject"/"Retry"/"Send back to X" labels on top of Flow's closed
/// <see cref="DecisionType"/> set — <c>Approve</c> records <see cref="DecisionType.Resume"/>,
/// <c>Reject</c> records <see cref="DecisionType.Reject"/>, <c>Retry</c> records
/// <see cref="DecisionType.RetryWithRevision"/>, and each <see cref="SendBackTargets"/> entry records
/// <see cref="DecisionType.Supersede"/> against its own declared <c>TargetStepId</c> — never a
/// UI-invented decision type (UI spec §6). Constructed from <see cref="RoomProjection"/> when a
/// (<see cref="StepId"/>, <see cref="ExecutionId"/>) pair first pauses; a later load whose pause set
/// still includes that same key reuses this exact instance rather than reconstructing it (#350) —
/// <see cref="RoomClient"/>'s <c>RebuildPausedSteps</c> reconciles by key instead of rebuilding
/// wholesale, so an operator's in-progress <see cref="RevisionFilePath"/>/<see cref="SupplementaryWorker"/>/
/// <see cref="SupplementaryOutputName"/> entry survives the next 2-second poll.
/// </summary>
public sealed partial class PausedStepViewModel : ObservableObject
{
    private readonly DecideDelegate _decide;

    public StepId StepId { get; }

    public ExecutionId ExecutionId { get; }

    public string Label { get; }

    /// <summary>
    /// One entry per this pause point's declared <c>PausePoint.SupersedeTargets</c> — §7's constraint
    /// that "send back to X" is offered *only* for a declared target, never offered-then-failed at
    /// the mutation interface. Empty when the pause point declares no targets, in which case no
    /// send-back option renders at all (Phase 3).
    /// </summary>
    public IReadOnlyList<SendBackTargetViewModel> SendBackTargets { get; }

    /// <summary>
    /// Whether this step's actions may be invoked — false while the UI's own pump holds the room's
    /// lock for any mutation (this decision or another one), driven by <see cref="MainWindowViewModel.IsMutationInFlight"/>.
    /// A <see cref="RelayCommandAttribute"/> <c>CanExecute</c> predicate, not a plain field, so the
    /// bound buttons' enabled state updates the moment it changes rather than only on the next render.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RejectCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    private bool isEnabled = true;

    /// <summary>
    /// The supplementary artifact's source file — optional for <c>Retry</c>, mandatory for every
    /// <see cref="SendBackTargets"/> entry (§7; §17.2 defines a Supersede without one as itself an
    /// invalid decision, so the UI never lets that reach the mutation interface at all). Shared across
    /// both actions rather than duplicated per action: at most one supplementary artifact rides any
    /// single decision this step can currently make.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    private string revisionFilePath = string.Empty;

    /// <summary>
    /// The worker role name <c>aer supply</c> mints the supplementary execution under. There is no
    /// durable, snapshot-declared name to infer this from (<c>WorkerBinding.NonProcess</c> is
    /// constructed directly from this value, never looked up in the bindings file — M12 Phase 3's
    /// decision of record) — "ask, don't infer" applies here exactly as it already does for the
    /// bindings/template file paths (Phase 1).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    private string supplementaryWorker = string.Empty;

    /// <summary>The single declared output name the minted supplementary execution produces — see <see cref="SupplementaryWorker"/>'s remarks.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    private string supplementaryOutputName = string.Empty;

    public PausedStepViewModel(
        StepId stepId, ExecutionId executionId, IReadOnlyList<StepId> supersedeTargets, DecideDelegate decide)
    {
        StepId = stepId;
        ExecutionId = executionId;
        _decide = decide;
        Label = $"{stepId.Value} ({executionId.Value})";
        SendBackTargets = supersedeTargets.Select(target => new SendBackTargetViewModel(target, this)).ToList();
    }

    [RelayCommand(CanExecute = nameof(IsEnabled))]
    private Task ApproveAsync() => _decide(StepId, ExecutionId, DecisionType.Resume, null, null, null, null);

    [RelayCommand(CanExecute = nameof(IsEnabled))]
    private Task RejectAsync() => _decide(StepId, ExecutionId, DecisionType.Reject, null, null, null, null);

    /// <summary>
    /// Records <see cref="DecisionType.RetryWithRevision"/>. The revision file is optional (§7), so
    /// this only carries it — and therefore only requires <see cref="SupplementaryWorker"/>/
    /// <see cref="SupplementaryOutputName"/> too — when <see cref="RevisionFilePath"/> is non-blank;
    /// <see cref="CanRetry"/> is what keeps an incomplete supplementary triple from ever reaching
    /// <c>aer supply</c>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync() => _decide(
        StepId,
        ExecutionId,
        DecisionType.RetryWithRevision,
        null,
        string.IsNullOrWhiteSpace(RevisionFilePath) ? null : RevisionFilePath,
        string.IsNullOrWhiteSpace(RevisionFilePath) ? null : SupplementaryWorker,
        string.IsNullOrWhiteSpace(RevisionFilePath) ? null : SupplementaryOutputName);

    /// <summary>
    /// Retry is always offered while <see cref="IsEnabled"/>, since its supplementary artifact is
    /// optional — but if a revision file *is* entered, the worker/output-name pair it needs to reach
    /// <c>aer supply</c> must be entered too, or the command stays disabled rather than sending a
    /// half-filled triple that would fail deeper in the stack with a raw, unhandled exception.
    /// </summary>
    internal bool CanRetry =>
        IsEnabled &&
        (string.IsNullOrWhiteSpace(RevisionFilePath) ||
         (!string.IsNullOrWhiteSpace(SupplementaryWorker) && !string.IsNullOrWhiteSpace(SupplementaryOutputName)));

    /// <summary>
    /// Whether a <see cref="SendBackTargets"/> entry may currently submit — the supplementary triple
    /// is mandatory for Supersede (§7), so unlike <see cref="CanRetry"/> every one of the three fields
    /// must be filled in, not just filled-in-together.
    /// </summary>
    internal bool CanSendBack =>
        IsEnabled &&
        !string.IsNullOrWhiteSpace(RevisionFilePath) &&
        !string.IsNullOrWhiteSpace(SupplementaryWorker) &&
        !string.IsNullOrWhiteSpace(SupplementaryOutputName);

    internal Task SendBackAsync(StepId targetStepId) => _decide(
        StepId, ExecutionId, DecisionType.Supersede, targetStepId, RevisionFilePath, SupplementaryWorker, SupplementaryOutputName);

    partial void OnRevisionFilePathChanged(string value) => NotifySendBackTargetsCanExecuteChanged();

    partial void OnSupplementaryWorkerChanged(string value) => NotifySendBackTargetsCanExecuteChanged();

    partial void OnSupplementaryOutputNameChanged(string value) => NotifySendBackTargetsCanExecuteChanged();

    partial void OnIsEnabledChanged(bool value) => NotifySendBackTargetsCanExecuteChanged();

    /// <summary>
    /// <see cref="SendBackTargets"/> entries are a different <see cref="ObservableObject"/> each, so
    /// their generated <c>SendBackCommand</c>'s <c>CanExecute</c> re-evaluation can't be reached by
    /// <see cref="NotifyCanExecuteChangedForAttribute"/> the way <see cref="RetryCommand"/>'s can —
    /// this is the manual equivalent, fired whenever any field <see cref="CanSendBack"/> reads changes.
    /// </summary>
    private void NotifySendBackTargetsCanExecuteChanged()
    {
        foreach (var target in SendBackTargets)
        {
            target.SendBackCommand.NotifyCanExecuteChanged();
        }
    }
}

/// <summary>
/// One declared <c>PausePoint.SupersedeTargets</c> entry, rendered as its own "Send back to X" button
/// (Phase 3, issue #139). A thin child view model rather than a <see cref="DecideDelegate"/>-with-parameter
/// command on <see cref="PausedStepViewModel"/> itself: <c>ItemsControl</c> binding to a per-target
/// button is simpler as one small object per target than as one command needing a bound
/// <c>CommandParameter</c> threaded through the template. Reads the shared supplementary-artifact
/// fields (<see cref="PausedStepViewModel.RevisionFilePath"/> and friends) directly off
/// <paramref name="owner"/> rather than duplicating them per target — a paused step has exactly one
/// supplementary artifact in flight at a time, regardless of which target eventually consumes it.
/// </summary>
public sealed partial class SendBackTargetViewModel : ObservableObject
{
    private readonly PausedStepViewModel _owner;

    public StepId TargetStepId { get; }

    public string Label { get; }

    public SendBackTargetViewModel(StepId targetStepId, PausedStepViewModel owner)
    {
        TargetStepId = targetStepId;
        _owner = owner;
        Label = $"Send back to {targetStepId.Value}";
    }

    [RelayCommand(CanExecute = nameof(CanSendBack))]
    private Task SendBackAsync() => _owner.SendBackAsync(TargetStepId);

    private bool CanSendBack() => _owner.CanSendBack;
}
