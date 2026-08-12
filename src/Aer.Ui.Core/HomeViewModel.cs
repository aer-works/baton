using Aer.Adapters;
using Aer.Flow;
using Aer.Flow.Artifacts;
using Aer.Flow.Domain;
using Aer.Flow.Projection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui.Core;

/// <summary>
/// The ▤ front door's first-run/empty read model (originally M19 Phase 2, #187 — Home). Its recent
/// room cards and cross-room decision inbox moved to the permanent switcher and its "needs you"
/// filter (#1071/#1072); what remains here is whether any room exists at all
/// (<see cref="HasNoRooms"/>, for the "No rooms yet." empty state) and the shared
/// <see cref="BuildInboxItem"/> derivation the switcher's filter reuses so both surfaces phrase and
/// preview a paused step identically. The card/inbox value types (<see cref="RoomCardViewModel"/>,
/// <see cref="RoomCardStatus"/>, <see cref="PauseKind"/>, <see cref="InboxItemViewModel"/>) live on
/// below — they outlived Home and are consumed across the switcher, the fleet loader, and mobile.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private const int InboxPreviewMaxLength = 400;

    /// <summary>True when there is no room history at all — the ▤ front door's first-run empty state (#1071/#190) shows "No rooms yet." instead of a blank page.</summary>
    [ObservableProperty]
    private bool hasNoRooms = true;

    /// <summary>
    /// Reads whether any room exists, for the first-run empty state (#1071). The room cards and the
    /// decision inbox this used to also build from the recents list moved to the permanent switcher
    /// and its "needs you" filter (#1072), so this now only asks whether there is history at all — a
    /// listed directory that no longer loads still counts as history (the user recorded it).
    /// </summary>
    public async Task RefreshAsync(RoomClient session, CancellationToken cancellationToken = default)
    {
        var recents = await session.LoadRecentRoomDirectoriesAsync(cancellationToken).ConfigureAwait(true);
        HasNoRooms = recents.Count == 0;
    }

    /// <summary>
    /// Builds one paused step's decision-inbox item (#334's reply-vs-review wording, an inline output
    /// preview). Shared: the retired Home inbox built these from every recent room; the switcher's
    /// "needs you" filter (#1072) now builds them per expanded row from the same derivation, so the
    /// two surfaces can never phrase or preview a gate differently.
    /// </summary>
    internal static InboxItemViewModel BuildInboxItem(
        string roomDirectoryPath, RoomProjection projection, StepState stepState, Func<string, Task> openRoomAsync)
    {
        // Lead with the thing to review (ux-principles §4): the paused execution's first durable
        // output, previewed inline. Best-effort by design — a pause with no readable output still
        // renders an honest item, just without a preview.
        var previewText = string.Empty;
        var previewFileName = string.Empty;

        if (stepState.LatestExecutionId is { } executionId)
        {
            var execution = projection.Lineage.Executions.FirstOrDefault(e => e.ExecutionId == executionId);
            if (execution is { OutputFiles.Count: > 0 })
            {
                previewFileName = execution.OutputFiles[0];
                var outputDirectory = ArtifactManager.ResolveOutputDirectory(
                    Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName), executionId);
                try
                {
                    var content = File.ReadAllText(Path.Combine(outputDirectory, previewFileName));
                    previewText = content.Length > InboxPreviewMaxLength
                        ? content[..InboxPreviewMaxLength] + "…"
                        : content;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    previewText = string.Empty;
                }
            }
        }

        // #334: needs-input (a chat turn) asks for your reply, not your approval — the "{file} ready"
        // approval framing is wrong for it. Ready-for-review keeps its exact wording (test-pinned).
        var kind = PauseKind.ForStep(projection, stepState.StepId);
        var statusText = kind == PausePointKind.NeedsInput
            ? "Waiting for your reply"
            : previewFileName.Length > 0
                ? $"Waiting for your review — {previewFileName} ready"
                : "Waiting for your review";

        return new InboxItemViewModel(
            roomDirectoryPath,
            RoomCardViewModel.TitleFor(roomDirectoryPath),
            stepState.StepId.Value,
            statusText,
            previewText,
            kind,
            openRoomAsync,
            stepState.LatestExecutionId?.Value ?? string.Empty);
    }
}

/// <summary>
/// The shared room-status derivation (originally Home's card read model, #187). Home's recents cards
/// retired with #1071, and the instance side (the card object and its Open command) went with them;
/// what remains is static — the one place a <see cref="RoomProjection"/> becomes a plain status line
/// and a <see cref="RoomCardStatus"/>, consumed by the switcher rows, the fleet loader, and mobile.
/// </summary>
public static class RoomCardViewModel
{
    /// <summary>The room's handle — the directory's leaf name (ux-principles §3), via the one canonical <see cref="RoomProjectionLoader.FriendlyNameFor"/> derivation, so the switcher, the inbox items, and the chat header can never show a room three different names (#461/#976).</summary>
    public static string TitleFor(string roomDirectoryPath)
        => RoomProjectionLoader.FriendlyNameFor(roomDirectoryPath);

    /// <summary>
    /// The one place a <see cref="RoomProjection"/> becomes a human status line and a
    /// <see cref="RoomCardStatus"/>. Shared with the #336 switcher's rows rather than duplicated:
    /// the same surfaces that made #458's marks disagree across toolkits would make two copies of
    /// this disagree across views — Home would say "Cancelled" while the switcher said "Finished",
    /// which is the exact defect #461 had just fixed in one place.
    /// Extended (#1112): receives <paramref name="pendingPermission"/> projected from <c>room.jsonl</c>
    /// so a live permission ask derives <see cref="RoomCardStatus.NeedsYou"/> and "Permission requested".
    /// Extended (#1116, 0026 §1/§3/§5): an exhausted step must NOT rank the room NeedsYou (0026 §1).
    /// A room whose only blocker is exhaustion carries the 0026 sentence shape ("Out of plan — resumes ...")
    /// with rank/color non-alarming (<see cref="RoomCardStatus.Cancelled"/>, band 4 background room state).
    /// </summary>
    public static (string StatusText, RoomCardStatus Status) DeriveStatus(
        RoomProjection projection, PendingPermission? pendingPermission)
    {
        var failedOrRejectedSteps = projection.State.Steps
            .Where(s => s.Status is StepStatus.Failed or StepStatus.Rejected)
            .ToList();

        var isOnlyBlockerExhaustion = failedOrRejectedSteps.Count > 0 &&
            failedOrRejectedSteps.All(s => s.Status == StepStatus.Failed && s.LatestFailureClassification == FailureClassification.ExhaustedUntil);

        return projection.State.Status switch
        {
            // Running-scoped on purpose (#1112 review): a live answerable gate only exists while a
            // turn is executing. Revocation (#1102) is best-effort and reconcile is a single startup
            // pass (#1113), so an orphaned ask CAN sit in room.jsonl beside a Paused/Terminal flow
            // state — and headlining "Permission requested" there would mask the room's true status
            // with a gate no worker is left to be released by.
            WorkflowStatus.Running when pendingPermission != null => ("Permission requested", RoomCardStatus.NeedsYou),
            WorkflowStatus.Paused => (PausedCardStatusText(projection), RoomCardStatus.NeedsYou),
            WorkflowStatus.Running when projection.State.Steps.FirstOrDefault(s => s.Status == StepStatus.Running) is { } runningStep
                => ($"Working — {runningStep.StepId.Value}", RoomCardStatus.Running),
            _ when isOnlyBlockerExhaustion => FormatExhaustedRoomStatus(failedOrRejectedSteps[0]),
            WorkflowStatus.Running => ("Working", RoomCardStatus.Running),
            _ when failedOrRejectedSteps.Count > 0
                => ("Failed", RoomCardStatus.Failed),
            // #461: a cancelled run has no WorkflowStatus of its own — it reaches Terminal like any
            // other, which is exactly why it used to fall through to "Finished" and tell you a room
            // you had just stopped had completed. Cancellation is only visible in the steps. Ordered
            // after Failed on purpose: if something failed *and* something was cancelled, the
            // failure is the more important truth about the run.
            _ when projection.State.Steps.Any(s => s.Status == StepStatus.Cancelled)
                => ("Cancelled", RoomCardStatus.Cancelled),
            _ => ("Finished", RoomCardStatus.Finished),
        };
    }

    private static (string StatusText, RoomCardStatus Status) FormatExhaustedRoomStatus(StepState exhaustedStep)
    {
        var resetInstant = exhaustedStep.RetryNotBefore ?? exhaustedStep.LatestExecutionFailedRetryNotBefore;
        var text = resetInstant is { } instant
            ? $"Out of plan — resumes {instant.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)}"
            : "Out of plan — reset unknown";
        return (text, RoomCardStatus.Cancelled);
    }

    // #334: a paused chat turn is "your turn to reply", not an approval gate. A card whose only
    // paused steps are NeedsInput says so; any genuine ReadyForReview gate among them keeps the
    // established approval wording (and its exact string, which NavigationShellTests pins).
    private static string PausedCardStatusText(RoomProjection projection)
        => projection.State.Steps.Any(step =>
               step.Status == StepStatus.Paused &&
               PauseKind.ForStep(projection, step.StepId) == PausePointKind.ReadyForReview)
            ? "Waiting for your review"
            : "Waiting for your reply";
}

/// <summary>
/// Resolves a paused step's declared <see cref="PausePointKind"/> from the bound snapshot (#334) —
/// the single lookup every Home surface shares. Defaults to <see cref="PausePointKind.ReadyForReview"/>
/// for any step lacking a pause point, so a pause persisted before the kind existed keeps the
/// approval-gate meaning every pause historically carried.
/// </summary>
internal static class PauseKind
{
    public static PausePointKind ForStep(RoomProjection projection, StepId stepId)
        => projection.Snapshot.Steps.FirstOrDefault(step => step.StepId == stepId)?.PausePoint?.Kind
           ?? PausePointKind.ReadyForReview;
}

/// <summary>The one status system's card-level states — carried as data so the skin styles them consistently (color + icon + word, never color alone).</summary>
public enum RoomCardStatus
{
    Running,
    NeedsYou,
    Finished,
    Failed,

    /// <summary>
    /// The run was stopped on purpose (#461). Previously absent, which meant a cancelled room fell
    /// through to <see cref="Finished"/> — the UI told you a room you had just stopped had finished.
    /// Deliberately distinct from <see cref="Failed"/>: "you stopped it" is not "it broke", and a
    /// list that renders them alike reads far more alarming than reality.
    /// </summary>
    Cancelled,

    /// <summary>§3's stale list state: recorded in Local UI Configuration but no longer loadable — greyed, never an error.</summary>
    Unavailable,
}

/// <summary>
/// One paused step across the recent rooms, as a decision-inbox item: the plain status, the
/// artifact preview beside it, and Review — which opens the room at its decision surface, the
/// same mutation path as deciding anywhere else (the inbox is a projection, never a second
/// authority).
/// </summary>
public sealed partial class InboxItemViewModel(
    string roomDirectoryPath, string roomTitle, string stepName, string statusText, string previewText,
    PausePointKind kind, Func<string, Task> openRoomAsync, string executionId = "")
{
    public string RoomDirectoryPath { get; } = roomDirectoryPath;
    public string RoomTitle { get; } = roomTitle;
    public string StepName { get; } = stepName;
    public string ExecutionId { get; } = executionId;
    public string StatusText { get; } = statusText;
    public string PreviewText { get; } = previewText;
    public bool HasPreview => PreviewText.Length > 0;

    /// <summary>Which human act this pause demands (#334) — carried so #319 can filter the inbox into "Needs input" / "Ready for review" states without re-deriving it.</summary>
    public PausePointKind Kind { get; } = kind;

    /// <summary>#334: a needs-input turn wants your next message, so the action reads "Reply"; a review gate reads "Review". Both open the room — the label names the act, not a second authority.</summary>
    public string ActionLabel => Kind == PausePointKind.NeedsInput ? "Reply" : "Review";

    [RelayCommand]
    private Task Review() => openRoomAsync(RoomDirectoryPath);
}
