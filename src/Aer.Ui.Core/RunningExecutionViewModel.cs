using Aer.Flow.Domain;
using Aer.RoomSession;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui.Core;

/// <summary>
/// Invokes a targeted Cancel for <paramref name="executionId"/> (M15 Phase 4, issue #140). The
/// receiving end (<see cref="MainWindow"/>) decides how to deliver it: in-process, via the retained
/// <c>InFlightExecutionRegistry</c>, when this window's own pump currently has it in flight, or a
/// brand-new <c>Aer.Cli.CancelCommand</c> mutation call otherwise (§15's guard is the only other way
/// to reach it) — the same split <see cref="RunningExecutionViewModel.IsLocallyHosted"/> renders.
/// </summary>
public delegate Task CancelDelegate(ExecutionId executionId);

/// <summary>
/// One currently-running (or cancellation-pending) execution's §7 Cancel action surface (M15 Phase 4,
/// issue #140) — process-bound steps still dispatched to Core, and step-less supplementary/human
/// executions still awaiting completion (spec §17.3), rendered uniformly since both are valid
/// <c>MutationInterface.RequestCancellationAsync</c> targets. Constructed from <see cref="RoomProjection"/>
/// when a (<see cref="StepId"/>, <see cref="ExecutionId"/>) pair first starts running; a later load
/// still reporting it running reuses this exact instance rather than reconstructing it (#350, the
/// same reconciliation <see cref="PausedStepViewModel"/> follows) — <see cref="CancellationRequested"/>
/// is refreshed on the reused instance each load, everything else about it can't change while it runs.
/// An execution that settles simply stops appearing next load.
/// </summary>
public sealed partial class RunningExecutionViewModel : ObservableObject
{
    private readonly CancelDelegate _cancel;

    /// <summary><see langword="null"/> for a step-less supplementary/human execution (spec §17.3).</summary>
    public StepId? StepId { get; }

    public ExecutionId ExecutionId { get; }

    public string Label { get; }

    /// <summary>
    /// Whether this window's own currently in-flight pump is the one that dispatched this execution
    /// — determined once, at render time, from whether this room directory is the one this window is
    /// actively pumping (<see cref="MainWindow"/>'s own retained registry). A non-process execution is
    /// never locally hosted: it never registers with <c>InFlightExecutionRegistry</c> in the first
    /// place (Phase 1's <c>NonProcessCancellationDetector</c> owns that tier directly). Drives both
    /// <see cref="MainWindow"/>'s delivery choice and this entry's enabled state while a mutation is
    /// in flight (see <see cref="UpdateEnabled"/>).
    /// </summary>
    public bool IsLocallyHosted { get; }

    /// <summary>
    /// Whether a <see cref="FlowEvent.CancellationRequested"/> is already recorded for this execution
    /// with no terminal outcome yet (<c>FlowState.CancellationRequestedExecutionIds</c>) — §7's first
    /// reflection phase, distinct from "stopped" (which this entry simply stops rendering once the
    /// terminal <c>ExecutionCancelled</c> lands). Disables the Cancel button once true: the intent is
    /// already durably recorded, so a second click has nothing left to add.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool cancellationRequested;

    /// <summary>
    /// Whether this entry's Cancel action may currently be invoked — true whenever
    /// <see cref="IsLocallyHosted"/> (this window's own pump can always be signalled, precisely while
    /// it is in flight) or whenever no mutation is in flight from this process at all (a fresh
    /// <c>CancelCommand</c> call otherwise). See <see cref="UpdateEnabled"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool isEnabled = true;

    public RunningExecutionViewModel(
        StepId? stepId, ExecutionId executionId, bool isLocallyHosted, bool cancellationRequested, CancelDelegate cancel)
    {
        StepId = stepId;
        ExecutionId = executionId;
        IsLocallyHosted = isLocallyHosted;
        _cancel = cancel;
        this.cancellationRequested = cancellationRequested;
        Label = stepId is { } knownStepId
            ? $"{knownStepId.Value} ({executionId.Value})"
            : $"(supplementary) — {executionId.Value}";
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private Task CancelAsync() => _cancel(ExecutionId);

    private bool CanCancel => IsEnabled && !CancellationRequested;

    /// <summary>
    /// Called from <see cref="MainWindowViewModel.OnIsMutationInFlightChanged"/> exactly like
    /// <see cref="PausedStepViewModel.IsEnabled"/> is — except a locally-hosted execution stays
    /// enabled precisely while a mutation (this window's own pump) is in flight, since that is the
    /// only time its Cancel button is meaningful at all.
    /// </summary>
    internal void UpdateEnabled(bool isMutationInFlight) => IsEnabled = IsLocallyHosted || !isMutationInFlight;
}
