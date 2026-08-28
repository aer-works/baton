using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Aer.Adapters;
using Aer.Flow;
using Aer.Flow.Concurrency;
using Aer.Flow.Domain;
using Aer.RoomSession;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui.Core;

/// <summary>
/// The Rooms view's state (M24 Phase 5, #278) — every known room directory, not just
/// Home's capped 10-item recents cards, with archive/unarchive/delete. Deliberately its own child
/// ViewModel rather than fields on <see cref="MainWindowViewModel"/> (the pattern <see cref="RemoteViewModel"/>/<see cref="ChatViewModel"/>
/// already establish) — a real fleet management surface is a distinct concern from the mutation/decision
/// surface <see cref="MainWindowViewModel"/> was introduced for.
/// </summary>
public sealed partial class RoomsViewModel : ObservableObject
{
    [ObservableProperty]
    private bool includeArchived;

    /// <summary>
    /// #1072: the switcher's "Needs you" filter — the design's "'Needs you' is a filter, not the front
    /// door" (docs/design/02-screens.md:806). On, the list narrows to rooms with a paused step (the
    /// needs-you band the switcher already orders first, 0018/#1051) and those rows expand in place to
    /// their paused step(s). Off, every room shows and nothing is expanded.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNeedsYouEmpty))]
    private bool needsYouOnly;

    partial void OnNeedsYouOnlyChanged(bool value)
    {
        ApplyNeedsYouFilter();
        _ = RefreshPausedStepsAsync();
    }

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorText))]
    private string? errorText;

    /// <summary>
    /// How many of <see cref="Items"/> currently have <see cref="RoomFleetItemViewModel.IsSelected"/>
    /// set (bulk select, issue #288) — recomputed by <see cref="OnItemSelectionChanged"/> rather than
    /// tracked independently, since the source of truth is each row's own checkbox state.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(BulkDeleteConfirmText))]
    [NotifyCanExecuteChangedFor(nameof(RequestBulkDeleteCommand))]
    private int selectedCount;

    /// <summary>
    /// Bulk delete's own two-step confirm (issue #288) — the same in-place idiom
    /// <see cref="RoomFleetItemViewModel.IsConfirmingDelete"/> already uses for a single row, scaled
    /// to "Delete N rooms?" instead of one confirm per item.
    /// </summary>
    [ObservableProperty]
    private bool isConfirmingBulkDelete;

    public ObservableCollection<RoomFleetItemViewModel> Items { get; } = [];

    /// <summary>
    /// The switcher's current row (#336) — which record the permanently-visible list has highlighted,
    /// and therefore what the detail pane is showing. Distinct from
    /// <see cref="RoomFleetItemViewModel.IsSelected"/>, which is bulk-select's checkbox: you can tick
    /// five rows for a bulk archive while looking at a sixth, so "checked" and "open" are genuinely
    /// two different things and share no state.
    /// </summary>
    [ObservableProperty]
    private RoomFleetItemViewModel? currentItem;

    public bool HasNoItems => !IsBusy && Items.Count == 0;
    public bool HasErrorText => !string.IsNullOrEmpty(ErrorText);
    public bool HasSelection => SelectedCount > 0;

    /// <summary>#1072: the "Needs you" filter is on over a non-empty fleet but nothing is waiting — the honest empty state ("Nothing needs you."). Requires at least one room so it never doubles up with the switcher's own "No rooms yet." on a truly empty fleet.</summary>
    public bool ShowNeedsYouEmpty => NeedsYouOnly && !IsBusy && Items.Count > 0 && Items.All(i => !i.HasPausedSteps);

    public string BulkDeleteConfirmText =>
        $"Really delete {SelectedCount} selected room{(SelectedCount == 1 ? "" : "s")}? This can't be undone.";

    /// <summary>Re-fetches the fleet list (activation, after archive/unarchive/delete, and the "Show archived" toggle).</summary>
    public async Task RefreshAsync(RoomClient session, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorText = null;
        IsConfirmingBulkDelete = false;

        try
        {
            var (items, error) = await session.GetFleetAsync(IncludeArchived, cancellationToken).ConfigureAwait(true);
            if (items == null)
            {
                ErrorText = error ?? "Could not load rooms.";
                return;
            }

            // A rebuild replaces every row object, so the open record has to be re-found by identity
            // afterwards (#336) — otherwise any refresh would silently deselect whatever the user is
            // looking at, which on a permanently-visible switcher is far more disruptive than it was
            // on a view you had to navigate to. Null if it was archived away or deleted meanwhile.
            var openDirectoryPath = CurrentItem?.RoomDirectoryPath;

            Items.Clear();
            foreach (var item in InFleetOrder(items))
            {
                Items.Add(new RoomFleetItemViewModel(
                    item,
                    i => ArchiveAsync(session, i, cancellationToken),
                    i => UnarchiveAsync(session, i, cancellationToken),
                    i => DeleteAsync(session, i, cancellationToken),
                    OnItemSelectionChanged));
            }

            CurrentItem = openDirectoryPath == null ? null : FindRow(openDirectoryPath);

            // #1072: a rebuild replaced every row, so the "Needs you" filter (and its inline paused
            // steps) has to be re-applied to the new row objects.
            ApplyNeedsYouFilter();
            if (NeedsYouOnly)
            {
                await RefreshPausedStepsAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
            OnItemSelectionChanged();
            OnPropertyChanged(nameof(HasNoItems));
            OnPropertyChanged(nameof(ShowNeedsYouEmpty));
        }
    }

    /// <summary>Every row's selection checkbox reports back through this (rather than <see cref="Items"/> itself being observed) — see <see cref="RoomFleetItemViewModel"/>'s own <c>selectionChanged</c> callback.</summary>
    private void OnItemSelectionChanged() => SelectedCount = Items.Count(i => i.IsSelected);

    /// <summary>
    /// #1072: applies the "Needs you" filter to every row without rebuilding the list — a row with no
    /// paused step is collapsed out while the filter is on, and the row objects (selection, sort,
    /// scroll) are untouched. Called on toggle, after a refresh, and after a projection push moves a
    /// room in or out of the needs-you band.
    /// </summary>
    private void ApplyNeedsYouFilter()
    {
        foreach (var row in Items)
        {
            row.IsFilteredOut = NeedsYouOnly && !row.HasPausedSteps;
        }

        OnPropertyChanged(nameof(ShowNeedsYouEmpty));
    }

    /// <summary>
    /// #1072: loads each needs-you row's paused steps from its projection and expands it, so the
    /// filtered list shows the retired inbox's per-step previews inline (expand-in-place, 0007). Clears
    /// them when the filter is off. The projection is read from the room directory directly
    /// (<see cref="RoomProjectionLoader"/>), so this needs no <see cref="RoomClient"/> — a filter
    /// toggle can drive it without a session.
    /// </summary>
    private async Task RefreshPausedStepsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var row in Items)
        {
            if (NeedsYouOnly && row.HasPausedSteps)
            {
                await LoadRowPausedStepsAsync(row, cancellationToken).ConfigureAwait(true);
                row.IsExpanded = true;
            }
            else
            {
                row.PausedSteps.Clear();
                row.IsExpanded = false;
            }
        }
    }

    private async Task LoadRowPausedStepsAsync(RoomFleetItemViewModel row, CancellationToken cancellationToken)
    {
        // #1072 review: both callers are fire-and-forget (a toggle flip and a live projection push),
        // so two loads for one row can overlap. Stamp this load's generation before the await; if a
        // newer load has since started, discard this one's result instead of appending on top of it.
        var generation = ++row.PausedStepsLoadGeneration;

        RoomProjection projection;
        try
        {
            projection = await RoomProjectionLoader.LoadAsync(row.RoomDirectoryPath, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is AerFlowException or IOException or UnauthorizedAccessException)
        {
            // A room that no longer loads (deleted/moved/locked mid-read) carries no inline steps this
            // pass — its own row status handles the stale case (HomeViewModel's stale-list rule). Caught
            // here rather than escaping as an unobserved task exception through the fire-and-forget call.
            // The existing list is left as-is on purpose: a swallowed reload must not blank a row that a
            // prior good load had populated (second-reader finding — the clear used to run before this).
            return;
        }

        if (generation != row.PausedStepsLoadGeneration)
        {
            return;
        }

        // Only now that this load has both succeeded and won its generation do we replace the list.
        // Clearing earlier (before the await, or before either check) blanked a validly-displayed row
        // whenever an overlapping load then failed or was superseded.
        row.PausedSteps.Clear();
        foreach (var step in projection.State.Steps)
        {
            if (step.Status == StepStatus.Paused)
            {
                // Opening a paused step is selecting its room: the switcher's existing
                // selection-is-opening wiring (#336) opens the room where the gate is answered inline,
                // so this reuses that one open path rather than a second route in (one decision
                // surface — 02-screens.md:798).
                row.PausedSteps.Add(HomeViewModel.BuildInboxItem(
                    row.RoomDirectoryPath, projection, step, _ => { CurrentItem = row; return Task.CompletedTask; }));
            }
        }
    }

    /// <summary>
    /// Test seam (#1072 second-reader): awaits one row's inline-step reload directly, so
    /// <c>RoomsViewModelTests</c> can assert the failed-reload path leaves an already-populated list
    /// intact — the fire-and-forget callers (<see cref="ApplyProjectionPush"/>, the filter toggle)
    /// give a test nothing to await. Same reasoning as <see cref="AddTestItem"/>.
    /// </summary>
    internal Task ReloadRowPausedStepsForTestAsync(RoomFleetItemViewModel row) =>
        LoadRowPausedStepsAsync(row, CancellationToken.None);

    /// <summary>
    /// #1072: the retired Home inbox's <c>RetireInboxItem</c>, relocated — a gate answered anywhere
    /// drops its paused-step item from the matching switcher row's expanded list (<see cref="RoomClient"/>
    /// calls this on decision resolution). Keyed by room + step + execution, the same identity #618 used.
    /// </summary>
    public void RetireInboxItem(string roomDirectoryPath, StepId stepId, ExecutionId executionId)
    {
        var row = FindRow(roomDirectoryPath);
        if (row is null)
        {
            return;
        }

        row.RetirePausedStep(stepId, executionId);

        // Retiring the last gate makes the row no longer needs-you: collapse it out of the filtered
        // list and drop its (now empty) expansion, so the switcher never shows a needs-you row with
        // nothing in it, and "Nothing needs you." appears if that was the last one.
        if (NeedsYouOnly && !row.HasPausedSteps)
        {
            row.IsFilteredOut = true;
            row.IsExpanded = false;
        }

        OnPropertyChanged(nameof(ShowNeedsYouEmpty));
    }

    /// <summary>
    /// The fleet list's ordering rule: rooms that need you first (J3, #1051), then most recently
    /// updated, ties broken by name so the order is *stable* rather than merely sorted — two sessions
    /// touched in the same second must not swap places on an unrelated refresh, which in a
    /// permanently-visible switcher would move a row out from under the pointer. Matches the phone's
    /// waiting-on-you-first order.
    /// </summary>
    /// <remarks>
    /// The recency key (#336) previously ordered by <c>FriendlyName</c> descending, which silently
    /// discarded the recency order the daemon had already applied (<c>Aer.Daemon.Program</c>'s
    /// <c>OrderByDescending(i =&gt; i.Updated)</c>) and contradicted <see cref="RoomFleetItem.Updated"/>'s
    /// own contract ("the key the fleet list orders by"). Sorting here rather than trusting the
    /// transport keeps local (non-daemon) loads and push-updated rows in the same order as remote ones.
    /// </remarks>
    private static IEnumerable<RoomFleetItem> InFleetOrder(IEnumerable<RoomFleetItem> items) =>
        items.OrderBy(i => StateRank(i.Status))
            .ThenByDescending(i => i.LastActivityAt ?? i.Updated)
            .ThenBy(i => i.FriendlyName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Decision 0018's (<see href="docs/decisions/0018-attention-is-the-primary-signal.md">attention is the primary signal</see>)
    /// four bands: <b>needs you</b> (0), <b>working</b> (1), <b>idle/finished/failed</b> (2), and muted <b>quiet states</b>
    /// (cancelled/unavailable/out-of-plan, 3). Recency orders within each group. This is the tier key;
    /// <see cref="InFleetOrder"/> breaks ties by recency then name so the list is stable.
    /// The stress test's lesson (02-screens.md): at a hundred rooms, sorting by recency alone buries
    /// the three that need you among ninety-one finished ones — grouping is what keeps it usable.
    /// </summary>
    private static int StateRank(RoomCardStatus? status) => status switch
    {
        RoomCardStatus.NeedsYou => 0,
        RoomCardStatus.Running => 1,
        RoomCardStatus.Finished => 2,
        RoomCardStatus.Failed => 2,
        RoomCardStatus.Cancelled => 3,
        RoomCardStatus.Unavailable => 3,
        RoomCardStatus.OutOfPlan => 3,
        // #1219: named rather than left to the discard, which a second reader caught silently filing
        // it beside Finished. Band 3 with Cancelled, its nearest sibling: a room whose process died is
        // quiet — it is not competing for attention with a gate or a live run — and the person finds
        // it when they go looking, with Resume on its own transcript.
        RoomCardStatus.Stopped => 3,
        // #1299 second-reader finding: both of these were missing from this switch and threw the
        // instant a real room reached either status — WaitingToStart since #1296 shipped, silently
        // uncaught until now because no fleet-order test exercised either member. Same band as
        // Stopped/Cancelled for the same reason: a queued-or-blocked room is quiet, not competing
        // for attention with a live run or a gate.
        RoomCardStatus.WaitingToStart => 3,
        RoomCardStatus.WaitingOnLock => 3,
        // A never-run room the fleet reports no state for (see the property's own remarks) genuinely
        // has no band; it sits with the settled outcomes. Split from the discard so that a NEW status
        // member cannot inherit a tier by accident — the #616 lesson every other status switch in this
        // file's neighbourhood already applies, and which this one was quietly missing.
        null => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unranked card status."),
    };

    /// <summary>
    /// Test seam for <see cref="InFleetOrder"/> — same reasoning as <see cref="AddTestItem"/>: the
    /// rule is worth asserting directly, and reaching it through <see cref="RefreshAsync"/> would
    /// need the sealed <see cref="RoomClient"/> and a live fleet fetch to test a pure sort.
    /// </summary>
    internal static IEnumerable<RoomFleetItem> InFleetOrderForTests(IEnumerable<RoomFleetItem> items) =>
        InFleetOrder(items);

    /// <summary>
    /// Applies one live projection push to the row it belongs to (#336). The switcher's list is
    /// permanently visible, so it can no longer rely on rebuilding itself when its section is
    /// activated — before this, <see cref="RefreshAsync"/> on activation was the *only* thing keeping
    /// it current, and making the list permanent removes that trigger.
    /// </summary>
    /// <remarks>
    /// Updates the existing row in place rather than going through <see cref="RefreshAsync"/>'s
    /// clear-and-rebuild: a rebuild on every frame would discard selection and scroll position, and
    /// would re-fetch the whole fleet to apply news the push already carried. A push for a directory
    /// with no row (a session created by another client since the last refresh) is ignored rather than
    /// synthesising a row — the push carries a projection, not the archived/created/updated fleet
    /// metadata a row needs, so a synthesised row would be wrong in exactly the fields the list sorts
    /// and filters on. Rows keyed by <see cref="AerPaths.RecordKey"/>, the shared normaliser from #335
    /// — two spellings of one directory must resolve to one row here for the same reason they must
    /// resolve to one lock there.
    /// </remarks>
    public void ApplyProjectionPush(string directoryPath, RoomProjection projection)
    {
        var row = FindRow(directoryPath);
        if (row is null)
        {
            return;
        }

        row.ApplyProjection(projection);

        // #1072: a push can move a room in or out of the needs-you band; keep the filter, the row's
        // inline steps, and the empty state honest without a full rebuild.
        if (NeedsYouOnly)
        {
            row.IsFilteredOut = !row.HasPausedSteps;
            if (row.HasPausedSteps)
            {
                _ = LoadRowPausedStepsAsync(row, CancellationToken.None);
                row.IsExpanded = true;
            }
            else
            {
                row.PausedSteps.Clear();
                row.IsExpanded = false;
            }

            OnPropertyChanged(nameof(ShowNeedsYouEmpty));
        }
    }

    /// <summary>
    /// The list's one row-identity rule (#336): a directory path resolves to at most one row, under
    /// #335's shared <see cref="AerPaths.RecordKey"/> normaliser. Two spellings of one directory must
    /// resolve to one row here for the same reason they must resolve to one lock there — #335's
    /// durable lesson was that the *second* primitive keyed on a record path is where normalisers
    /// drift apart, and this is that second primitive.
    /// </summary>
    private RoomFleetItemViewModel? FindRow(string directoryPath)
    {
        var key = AerPaths.RecordKey(directoryPath);
        return Items.FirstOrDefault(
            i => AerPaths.RecordKeyComparer.Equals(AerPaths.RecordKey(i.RoomDirectoryPath), key));
    }

    /// <summary>
    /// Test seam (issue #288): adds a row to <see cref="Items"/> wired with the real
    /// selection-changed callback <see cref="RefreshAsync"/> itself uses, but no-op
    /// archive/unarchive/delete delegates — lets <c>RoomsViewModelTests</c> exercise the actual
    /// selection bookkeeping (<see cref="SelectedCount"/>, <see cref="HasSelection"/>, the bulk-delete
    /// confirm gating) without constructing the sealed <see cref="RoomClient"/> that
    /// <see cref="RefreshAsync"/>'s real row construction needs. Same reasoning as
    /// <see cref="RoomClient.ShouldApplyProjectionPush"/>'s own internal test seam.
    /// </summary>
    internal RoomFleetItemViewModel AddTestItem(RoomFleetItem item)
    {
        var row = new RoomFleetItemViewModel(
            item, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, OnItemSelectionChanged);
        Items.Add(row);
        OnItemSelectionChanged();
        return row;
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items)
        {
            item.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RequestBulkDelete() => IsConfirmingBulkDelete = true;

    [RelayCommand]
    private void CancelBulkDelete() => IsConfirmingBulkDelete = false;

    /// <summary>
    /// Archives every selected, not-yet-archived row (issue #288) — the bulk counterpart of
    /// <see cref="ArchiveAsync"/>. Fans out sequentially against the same per-directory
    /// <c>/api/rooms/archive</c> endpoint (delete mutates the shared recents list and archive mutates
    /// the shared fleet index, so concurrent calls could race) rather than a new bulk daemon endpoint,
    /// per the issue's stated default. Calls <see cref="RoomClient.ArchiveRoomAsync"/> directly in the
    /// loop and refreshes exactly once at the end -- routing through the existing single-item
    /// <see cref="ArchiveAsync"/> would call <see cref="RefreshAsync"/> after every item, rebuilding
    /// <see cref="Items"/> (and clearing selection) mid-loop.
    /// </summary>
    public async Task BulkArchiveAsync(RoomClient session, CancellationToken cancellationToken = default)
    {
        var targets = Items.Where(i => i.IsSelected && !i.IsArchived).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        IsBusy = true;
        var failures = new List<string>();
        try
        {
            foreach (var item in targets)
            {
                var outcome = await session.ArchiveRoomAsync(item.RoomDirectoryPath, cancellationToken).ConfigureAwait(true);
                if (outcome.ErrorMessage != null)
                {
                    failures.Add($"{item.FriendlyName}: {outcome.ErrorMessage}");
                }
            }

            await RefreshAsync(session, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }

        // Set after RefreshAsync, which resets ErrorText to null on entry -- setting it before the
        // refresh would just be clobbered.
        if (failures.Count > 0)
        {
            ErrorText = $"{failures.Count} of {targets.Count} room(s) couldn't be archived: {string.Join("; ", failures)}";
        }
    }

    /// <summary>
    /// Deletes every selected row (issue #288) once <see cref="IsConfirmingBulkDelete"/>'s confirm has
    /// been accepted -- the bulk counterpart of <see cref="DeleteAsync"/>, with the same
    /// sequential-fan-out-then-single-refresh reasoning as <see cref="BulkArchiveAsync"/>.
    /// </summary>
    public async Task ConfirmBulkDeleteAsync(RoomClient session, CancellationToken cancellationToken = default)
    {
        var targets = Items.Where(i => i.IsSelected).ToList();
        if (targets.Count == 0)
        {
            IsConfirmingBulkDelete = false;
            return;
        }

        IsBusy = true;
        var failures = new List<string>();
        try
        {
            foreach (var item in targets)
            {
                var outcome = await session.DeleteRoomAsync(item.RoomDirectoryPath, cancellationToken).ConfigureAwait(true);
                if (outcome.ErrorMessage != null)
                {
                    failures.Add($"{item.FriendlyName}: {outcome.ErrorMessage}");
                }
            }

            await RefreshAsync(session, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }

        if (failures.Count > 0)
        {
            ErrorText = $"{failures.Count} of {targets.Count} room(s) couldn't be deleted: {string.Join("; ", failures)}";
        }
    }

    private async Task ArchiveAsync(RoomClient session, RoomFleetItemViewModel item, CancellationToken cancellationToken)
    {
        var outcome = await session.ArchiveRoomAsync(item.RoomDirectoryPath, cancellationToken).ConfigureAwait(true);
        if (outcome.ErrorMessage != null)
        {
            item.RowErrorText = outcome.ErrorMessage;
            return;
        }

        await RefreshAsync(session, cancellationToken).ConfigureAwait(true);
    }

    private async Task UnarchiveAsync(RoomClient session, RoomFleetItemViewModel item, CancellationToken cancellationToken)
    {
        var outcome = await session.UnarchiveRoomAsync(item.RoomDirectoryPath, cancellationToken).ConfigureAwait(true);
        if (outcome.ErrorMessage != null)
        {
            item.RowErrorText = outcome.ErrorMessage;
            return;
        }

        await RefreshAsync(session, cancellationToken).ConfigureAwait(true);
    }

    private async Task DeleteAsync(RoomClient session, RoomFleetItemViewModel item, CancellationToken cancellationToken)
    {
        var outcome = await session.DeleteRoomAsync(item.RoomDirectoryPath, cancellationToken).ConfigureAwait(true);
        if (outcome.ErrorMessage != null)
        {
            item.IsConfirmingDelete = false;
            item.RowErrorText = outcome.ErrorMessage;
            return;
        }

        await RefreshAsync(session, cancellationToken).ConfigureAwait(true);
    }
}

/// <summary>
/// One row in the Rooms view (M24 Phase 5, #278) — same closure-over-parent-actions shape as
/// <see cref="PairedClientItemViewModel"/>: the parent <see cref="RoomsViewModel"/> already has the
/// <see cref="RoomClient"/> this row's actions need, so each action closes over it at construction
/// rather than the row needing its own reference. Delete uses an inline two-step confirm
/// (<see cref="IsConfirmingDelete"/>) rather than a modal dialog — no modal-dialog precedent exists
/// anywhere in this codebase's Avalonia views (<see cref="TemplatePickerWindow"/>'s in-window
/// <c>ErrorText</c> is the closest thing, and this follows the same in-place idiom).
/// </summary>
public sealed partial class RoomFleetItemViewModel : ObservableObject
{
    private readonly Func<RoomFleetItemViewModel, Task> _archiveAsync;
    private readonly Func<RoomFleetItemViewModel, Task> _unarchiveAsync;
    private readonly Func<RoomFleetItemViewModel, Task> _deleteAsync;
    private readonly Action? _selectionChanged;

    public RoomFleetItemViewModel(
        RoomFleetItem item,
        Func<RoomFleetItemViewModel, Task> archiveAsync,
        Func<RoomFleetItemViewModel, Task> unarchiveAsync,
        Func<RoomFleetItemViewModel, Task> deleteAsync,
        Action? selectionChanged = null)
    {
        RoomDirectoryPath = item.RoomDirectoryPath;
        FriendlyName = item.FriendlyName;
        TypeLabel = item.TypeLabel;
        IsSession = item.IsSession;
        statusText = item.StatusText;
        status = item.Status;
        pausedStepCount = item.PausedStepCount;
        IsArchived = item.IsArchived;
        LastActivityAt = item.LastActivityAt;
        _archiveAsync = archiveAsync;
        _unarchiveAsync = unarchiveAsync;
        _deleteAsync = deleteAsync;
        _selectionChanged = selectionChanged;
    }

    public string RoomDirectoryPath { get; }
    public string FriendlyName { get; }
    public string TypeLabel { get; }
    public DateTimeOffset? LastActivityAt { get; }

    /// <summary>
    /// Whether this row is an interactive session (chat-shaped) rather than a workflow (DAG-shaped)
    /// — what the switcher routes the detail pane on (#336). See <see cref="RoomFleetItem.IsSession"/>
    /// for why this is carried structurally instead of read back off <see cref="TypeLabel"/>.
    /// </summary>
    public bool IsSession { get; }

    public bool IsArchived { get; }

    /// <summary>
    /// Live under projection pushes (#336) — see <see cref="RoomsViewModel.ApplyProjectionPush"/>.
    /// Observable rather than get-only because the switcher's list is permanently visible: it can no
    /// longer wait for a section activation to rebuild itself with a fresh value.
    /// </summary>
    [ObservableProperty]
    private string statusText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPausedSteps))]
    private int pausedStepCount;

    public bool HasPausedSteps => PausedStepCount > 0;

    /// <summary>
    /// Folds one live projection push into this row (#336), touching only what a projection actually
    /// knows: the workflow status and the paused-step count. Name, type, archived state and the
    /// timestamps are fleet metadata the push does not carry, so they are deliberately left alone
    /// rather than being guessed at from the projection.
    /// </summary>
    internal void ApplyProjection(RoomProjection projection)
    {
        // #1219: this row's own directory, so a push for a room nothing is pumping settles to
        // "Stopped" instead of leaving the spinner turning. Pushes only arrive for rooms something is
        // doing something to, so this is not a per-tick probe.
        var (statusText, status) = RoomCardViewModel.DeriveStatus(
            projection, projection.PendingPermission, ConcurrencyGuard.IsHeld(RoomDirectoryPath),
            ConcurrencySlotGate.IsWaiting(RoomDirectoryPath));
        StatusText = statusText;
        Status = status;
        PausedStepCount = projection.State.Steps.Count(s => s.Status == StepStatus.Paused);
    }

    /// <summary>
    /// This row's status as a mark-bearing state rather than a string (#461's vocabulary), so every
    /// surface draws the same silhouette for the same state — decision 0006's rule 2 is only worth
    /// anything if every surface honours it. Seeded on load from the fleet's
    /// canonical <see cref="RoomFleetItem.Status"/> (#1051), then kept live by projection pushes
    /// (<see cref="ApplyProjection"/>). Null only for a never-run room the fleet reports no state for.
    /// </summary>
    [ObservableProperty]
    private RoomCardStatus? status;

    /// <summary>Bulk select (issue #288) — this row's own checkbox state; <see cref="RoomsViewModel.SelectedCount"/> is recomputed from every row's value whenever any one of them changes.</summary>
    [ObservableProperty]
    private bool isSelected;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged?.Invoke();

    [ObservableProperty]
    private bool isConfirmingDelete;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRowErrorText))]
    private string? rowErrorText;

    public bool HasRowErrorText => !string.IsNullOrEmpty(RowErrorText);

    [RelayCommand]
    private Task Archive() => _archiveAsync(this);

    [RelayCommand]
    private Task Unarchive() => _unarchiveAsync(this);

    [RelayCommand]
    private void RequestDelete() => IsConfirmingDelete = true;

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private Task ConfirmDelete() => _deleteAsync(this);

    /// <summary>
    /// #1072: hidden by the switcher's "Needs you" filter — set by
    /// <see cref="RoomsViewModel.ApplyNeedsYouFilter"/> when the filter is on and this row has no
    /// paused step. The row object stays in <see cref="RoomsViewModel.Items"/> (selection and sort are
    /// preserved); only its container collapses, so toggling the filter never rebuilds the list.
    /// </summary>
    [ObservableProperty]
    private bool isFilteredOut;

    /// <summary>
    /// #1072: expand-in-place — decision 0007's middle disclosure level ("the row expands where it
    /// sits to show more of that item's activity and its outputs, without leaving the list"). A
    /// needs-you row expands to its paused step(s) rather than forcing a drill-in or a second surface.
    /// </summary>
    [ObservableProperty]
    private bool isExpanded;

    /// <summary>
    /// The paused steps this room is waiting on (#1072) — each with the plain status and an output
    /// preview, the retired Home decision inbox's per-step items (built by the same
    /// <see cref="HomeViewModel.BuildInboxItem"/> derivation), now shown inline when a needs-you row
    /// is expanded. Populated by <see cref="RoomsViewModel"/> when the filter is on; empty otherwise.
    /// </summary>
    public ObservableCollection<InboxItemViewModel> PausedSteps { get; } = [];

    /// <summary>
    /// #1072: the generation of the most recently *started* paused-step load for this row. Bumped at
    /// the head of each <see cref="RoomsViewModel.LoadRowPausedStepsAsync"/> so a load that awaited I/O
    /// while a newer one started can detect it and discard its result rather than appending on top —
    /// two overlapping loads (a projection push landing mid-toggle, a double-click) must not duplicate
    /// the list.
    /// </summary>
    internal int PausedStepsLoadGeneration;

    /// <summary>
    /// #1072 (0020 clause 3, the ex-#618 rule): answering a gate retires its item everywhere at once —
    /// here, from this row's expanded paused-step list, matched by gate identity (step + execution) so
    /// the switcher's needs-you view drops it immediately rather than waiting for the next projection.
    /// </summary>
    public void RetirePausedStep(StepId stepId, ExecutionId executionId)
    {
        var match = PausedSteps.FirstOrDefault(
            s => s.StepName == stepId.Value && s.ExecutionId == executionId.Value);
        if (match != null)
        {
            PausedSteps.Remove(match);
        }

        // A gate was answered, so this room has one fewer paused step. Decrement the *authoritative*
        // count (which HasPausedSteps and the filter read) so the row stops reading as needs-you the
        // instant its last gate is answered — not only when the next projection push re-derives it.
        // Floors at zero; the next push sets the exact value. Unconditional on the match above because
        // the filter may be off (the item never loaded) while the gate is real either way.
        if (PausedStepCount > 0)
        {
            PausedStepCount--;
        }
    }
}
