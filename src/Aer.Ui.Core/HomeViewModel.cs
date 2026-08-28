using Aer.Adapters;
using Aer.Flow;
using Aer.Flow.Artifacts;
using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.RoomSession;
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
                // #1191: OutputFiles is every file in the execution's output directory, ordinal-sorted
                // (ArtifactLineageProjector), so prompt.txt — the worker's own instructions, absolute
                // artifact paths included — sorted ahead of review.md and became the preview. Ask what
                // the execution was contracted to produce instead (ExecutionArtifacts.DeclaredOutputs
                // records where that list comes from and why it is the right one). Exact match:
                // declared outputs carry their extension, and fuzzier matching would let a stray
                // review.md.bak outrank the real thing.
                //
                // When the execution declared outputs and none of them is on disk, this previews
                // NOTHING rather than falling back to the first file: that case is the bug — an
                // arbitrary file dressed as the thing you are being asked to approve — and the card
                // already renders honestly without a preview (see above). Only an execution that
                // declared nothing at all falls back.
                previewFileName = execution.DeclaredOutputs.Count > 0
                    ? execution.OutputFiles.FirstOrDefault(file => execution.DeclaredOutputs.Any(
                        declared => string.Equals(file, declared, StringComparison.OrdinalIgnoreCase))) ?? string.Empty
                    : execution.OutputFiles[0];

                if (previewFileName.Length > 0)
                {
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
