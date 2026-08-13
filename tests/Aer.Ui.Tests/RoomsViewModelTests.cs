using Aer.Flow.Domain;
using Aer.Flow.Templates;
using Aer.Ui.Core;

namespace Aer.Ui.Tests;

/// <summary>
/// Bulk select (issue #288) — the ViewModel-layer unit-test level for <see cref="RoomsViewModel"/>
/// and <see cref="RoomFleetItemViewModel"/>'s selection bookkeeping, mirroring
/// <see cref="PausedStepViewModelTests"/>'s "plain unit test, no headless Avalonia session, no live
/// daemon" approach. There was no pre-existing <c>RoomsViewModelTests</c> file (the issue's
/// description of one is stale) — this is the first ViewModel-level coverage for
/// <see cref="RoomsViewModel"/>; the fan-out/refresh mutation surface itself is covered at the
/// endpoint level by <c>DaemonIntegrationTests</c>' single-item archive/unarchive/delete round trip,
/// the same level the pre-existing single-item actions were already tested at.
/// </summary>
public class RoomsViewModelTests
{
    private static RoomFleetItem NewItem(string path, bool isArchived = false) =>
        new(path, FriendlyName: path, TypeLabel: "solo-run-template", StatusText: "Idle", PausedStepCount: 0,
            IsArchived: isArchived, Created: DateTimeOffset.UnixEpoch, Updated: DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_freshly_constructed_RoomsViewModel_has_no_selection()
    {
        var viewModel = new RoomsViewModel();

        Assert.Equal(0, viewModel.SelectedCount);
        Assert.False(viewModel.HasSelection);
    }

    [Fact]
    public void Selecting_a_row_updates_the_parents_SelectedCount_and_HasSelection()
    {
        var viewModel = new RoomsViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));

        row.IsSelected = true;

        Assert.Equal(1, viewModel.SelectedCount);
        Assert.True(viewModel.HasSelection);
    }

    [Fact]
    public void Deselecting_a_row_decrements_SelectedCount_back_to_zero()
    {
        var viewModel = new RoomsViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));
        row.IsSelected = true;

        row.IsSelected = false;

        Assert.Equal(0, viewModel.SelectedCount);
        Assert.False(viewModel.HasSelection);
    }

    [Fact]
    public void SelectedCount_reflects_however_many_of_several_rows_are_selected()
    {
        var viewModel = new RoomsViewModel();
        var a = viewModel.AddTestItem(NewItem("/tasks/a"));
        var b = viewModel.AddTestItem(NewItem("/tasks/b"));
        viewModel.AddTestItem(NewItem("/tasks/c"));

        a.IsSelected = true;
        b.IsSelected = true;

        Assert.Equal(2, viewModel.SelectedCount);
    }

    [Fact]
    public void SelectAllCommand_selects_every_row()
    {
        var viewModel = new RoomsViewModel();
        viewModel.AddTestItem(NewItem("/tasks/a"));
        viewModel.AddTestItem(NewItem("/tasks/b"));

        viewModel.SelectAllCommand.Execute(null);

        Assert.Equal(2, viewModel.SelectedCount);
        Assert.All(viewModel.Items, item => Assert.True(item.IsSelected));
    }

    [Fact]
    public void ClearSelectionCommand_deselects_every_row()
    {
        var viewModel = new RoomsViewModel();
        viewModel.AddTestItem(NewItem("/tasks/a"));
        viewModel.AddTestItem(NewItem("/tasks/b"));
        viewModel.SelectAllCommand.Execute(null);

        viewModel.ClearSelectionCommand.Execute(null);

        Assert.Equal(0, viewModel.SelectedCount);
        Assert.All(viewModel.Items, item => Assert.False(item.IsSelected));
    }

    [Fact]
    public void RequestBulkDeleteCommand_is_disabled_with_no_selection_and_enabled_once_something_is_selected()
    {
        var viewModel = new RoomsViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));

        Assert.False(viewModel.RequestBulkDeleteCommand.CanExecute(null));

        row.IsSelected = true;

        Assert.True(viewModel.RequestBulkDeleteCommand.CanExecute(null));
    }

    [Fact]
    public void RequestBulkDeleteCommand_sets_IsConfirmingBulkDelete()
    {
        var viewModel = new RoomsViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));
        row.IsSelected = true;

        viewModel.RequestBulkDeleteCommand.Execute(null);

        Assert.True(viewModel.IsConfirmingBulkDelete);
    }

    [Fact]
    public void CancelBulkDeleteCommand_clears_the_confirm_without_touching_the_selection()
    {
        var viewModel = new RoomsViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));
        row.IsSelected = true;
        viewModel.RequestBulkDeleteCommand.Execute(null);

        viewModel.CancelBulkDeleteCommand.Execute(null);

        Assert.False(viewModel.IsConfirmingBulkDelete);
        Assert.Equal(1, viewModel.SelectedCount);
        Assert.True(row.IsSelected);
    }

    [Fact]
    public void BulkDeleteConfirmText_pluralizes_the_count()
    {
        var viewModel = new RoomsViewModel();
        var a = viewModel.AddTestItem(NewItem("/tasks/a"));
        var b = viewModel.AddTestItem(NewItem("/tasks/b"));

        a.IsSelected = true;
        Assert.Equal("Really delete 1 selected room? This can't be undone.", viewModel.BulkDeleteConfirmText);

        b.IsSelected = true;
        Assert.Equal("Really delete 2 selected rooms? This can't be undone.", viewModel.BulkDeleteConfirmText);
    }

    // ---- #336: the switcher's push-driven liveness ----
    //
    // The switcher list is permanently visible, so it no longer gets a section activation to rebuild
    // on — before this, RoomsViewModel.RefreshAsync on activation was the *only* thing keeping it
    // current. These cover the replacement: a live projection push folded into the right row.

    private static RoomProjection ProjectionWith(WorkflowStatus status, params StepStatus[] stepStatuses)
    {
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("switcher-fixture"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(new StepId("only"), "worker", ["in"], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]));

        var steps = stepStatuses
            .Select((s, i) => new StepState(new StepId($"step-{i}"), s, LatestExecutionId: null, UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>()))
            .ToList();

        return new RoomProjection(
            snapshot,
            new FlowState(snapshot.WorkflowDefinitionSnapshotId, steps, status),
            new ExecutionHistory(new Dictionary<StepId, IReadOnlyList<ExecutionAttempt>>(), [], []),
            new ArtifactLineage([]));
    }

    [Fact]
    public void A_projection_push_updates_the_row_it_is_for()
    {
        var viewModel = new RoomsViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));
        Assert.Equal("Idle", row.StatusText);

        viewModel.ApplyProjectionPush("/tasks/a", ProjectionWith(WorkflowStatus.Terminal));

        Assert.Equal("Finished", row.StatusText);
        Assert.Equal(RoomCardStatus.Finished, row.Status);
    }

    [Fact]
    public void A_push_for_one_session_leaves_every_other_rows_status_alone()
    {
        var viewModel = new RoomsViewModel();
        var a = viewModel.AddTestItem(NewItem("/tasks/a"));
        var b = viewModel.AddTestItem(NewItem("/tasks/b"));

        viewModel.ApplyProjectionPush("/tasks/a", ProjectionWith(WorkflowStatus.Terminal));

        Assert.Equal("Finished", a.StatusText);
        Assert.Equal("Idle", b.StatusText);
        Assert.Null(b.Status);
    }

    [Fact]
    public void A_cancelled_session_reads_as_cancelled_on_the_switcher_too()
    {
        // #461 fixed "a cancelled task reports itself as Finished" on Home's cards. The switcher is a
        // second surface showing the same fact, so it shares Home's one derivation rather than
        // growing a copy that could drift back into the same defect.
        var viewModel = new RoomsViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));

        viewModel.ApplyProjectionPush(
            "/tasks/a", ProjectionWith(WorkflowStatus.Terminal, StepStatus.Cancelled));

        Assert.Equal("Cancelled", row.StatusText);
        Assert.Equal(RoomCardStatus.Cancelled, row.Status);
    }

    [Fact]
    public void A_push_carries_the_paused_step_count_the_row_shows()
    {
        var viewModel = new RoomsViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));
        Assert.False(row.HasPausedSteps);

        viewModel.ApplyProjectionPush(
            "/tasks/a", ProjectionWith(WorkflowStatus.Running, StepStatus.Paused, StepStatus.Paused, StepStatus.Succeeded));

        Assert.Equal(2, row.PausedStepCount);
        Assert.True(row.HasPausedSteps);
    }

    [Fact]
    public void A_push_for_a_directory_with_no_row_is_ignored_rather_than_synthesising_one()
    {
        var viewModel = new RoomsViewModel();
        viewModel.AddTestItem(NewItem("/tasks/a"));

        viewModel.ApplyProjectionPush("/tasks/never-seen", ProjectionWith(WorkflowStatus.Terminal));

        // A push carries a projection, not the archived/created/updated fleet metadata a row needs —
        // a synthesised row would be wrong in exactly the fields the list sorts and filters on.
        Assert.Single(viewModel.Items);
        Assert.Equal("Idle", viewModel.Items[0].StatusText);
    }

    [Fact]
    public void Two_spellings_of_one_directory_resolve_to_the_same_row()
    {
        // Built from Path rather than written as a literal: AerPaths.RecordKey runs
        // Path.GetFullPath, so a Windows-shaped literal ("C:\tasks\Alpha") is an absolute path on
        // Windows and a *relative* one on Linux — and '\' is not a separator there, so a trailing
        // one never gets trimmed. A hardcoded path would make this assert a different thing per OS.
        var viewModel = new RoomsViewModel();
        var directoryPath = Path.Combine(Path.GetTempPath(), "aer-switcher-key", "Alpha");
        var row = viewModel.AddTestItem(NewItem(directoryPath));

        // The two spellings that must collapse to one row: different casing, and a trailing
        // separator. #335's durable lesson is that the *second* primitive keyed on a record path is
        // where normalisers drift apart — this is that second primitive, so it shares RecordKey.
        var sameRecordSpeltDifferently = directoryPath.ToUpperInvariant() + Path.DirectorySeparatorChar;

        viewModel.ApplyProjectionPush(sameRecordSpeltDifferently, ProjectionWith(WorkflowStatus.Terminal));

        Assert.Equal("Finished", row.StatusText);
    }

    // ---- #336: ordering ----

    [Fact]
    public void The_list_orders_by_most_recent_last_activity_not_by_name()
    {
        // #640: recency means LAST ACTIVITY — when the room last did something (derived from journal events).
        var viewModel = new RoomsViewModel();
        var olderActivity = NewItem("/tasks/zulu") with
        {
            Updated = DateTimeOffset.UnixEpoch.AddHours(10),
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(1)
        };
        var newerActivity = NewItem("/tasks/alpha") with
        {
            Updated = DateTimeOffset.UnixEpoch.AddHours(2),
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(9)
        };

        var ordered = RoomsViewModel.InFleetOrderForTests([olderActivity, newerActivity]).ToList();

        Assert.Equal("/tasks/alpha", ordered[0].RoomDirectoryPath);
        Assert.Equal("/tasks/zulu", ordered[1].RoomDirectoryPath);
    }

    [Fact]
    public void Rows_with_same_last_activity_instant_order_by_name_so_the_list_is_stable()
    {
        // Ties must not resolve arbitrarily: on a permanently-visible switcher, a row that swaps
        // places on an unrelated refresh moves out from under the pointer.
        var viewModel = new RoomsViewModel();
        var sameInstant = DateTimeOffset.UnixEpoch.AddHours(3);
        var b = NewItem("/tasks/bravo") with { LastActivityAt = sameInstant, FriendlyName = "bravo" };
        var a = NewItem("/tasks/alpha") with { LastActivityAt = sameInstant, FriendlyName = "alpha" };

        var ordered = RoomsViewModel.InFleetOrderForTests([b, a]).ToList();

        Assert.Equal("alpha", ordered[0].FriendlyName);
        Assert.Equal("bravo", ordered[1].FriendlyName);
    }

    // ---- #1051: waiting-on-you first (J3), matching the phone ----

    [Fact]
    public void Rooms_that_need_you_sort_before_others_even_when_less_recently_active()
    {
        // Waiting-on-you is the PRIMARY key: a needs-you room outranks a more recently active room
        // that does not need you. Discriminates the needs-you key from the recency key beneath it.
        var needsYouButOlder = NewItem("/tasks/needs") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(1),
            Status = RoomCardStatus.NeedsYou,
        };
        var finishedButNewer = NewItem("/tasks/finished") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(9),
            Status = RoomCardStatus.Finished,
        };

        var ordered = RoomsViewModel.InFleetOrderForTests([finishedButNewer, needsYouButOlder]).ToList();

        Assert.Equal("/tasks/needs", ordered[0].RoomDirectoryPath);
        Assert.Equal("/tasks/finished", ordered[1].RoomDirectoryPath);
    }

    [Fact]
    public void Among_rooms_that_need_you_the_more_recently_active_still_comes_first()
    {
        // The needs-you key partitions; it does not flatten recency inside a partition.
        var olderNeedsYou = NewItem("/tasks/older") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(2),
            Status = RoomCardStatus.NeedsYou,
        };
        var newerNeedsYou = NewItem("/tasks/newer") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(8),
            Status = RoomCardStatus.NeedsYou,
        };

        var ordered = RoomsViewModel.InFleetOrderForTests([olderNeedsYou, newerNeedsYou]).ToList();

        Assert.Equal("/tasks/newer", ordered[0].RoomDirectoryPath);
        Assert.Equal("/tasks/older", ordered[1].RoomDirectoryPath);
    }

    [Fact]
    public void A_working_room_sorts_above_a_more_recently_active_finished_room()
    {
        // Design "State first, then recency": working is its own tier, above "earlier" (finished etc.),
        // so a working room outranks a finished one even when the finished one was touched more recently.
        var workingButOlder = NewItem("/tasks/working") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(1),
            Status = RoomCardStatus.Running,
        };
        var finishedButNewer = NewItem("/tasks/finished") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(9),
            Status = RoomCardStatus.Finished,
        };

        var ordered = RoomsViewModel.InFleetOrderForTests([finishedButNewer, workingButOlder]).ToList();

        Assert.Equal("/tasks/working", ordered[0].RoomDirectoryPath);
        Assert.Equal("/tasks/finished", ordered[1].RoomDirectoryPath);
    }

    [Fact]
    public void A_cancelled_room_more_recent_than_a_finished_one_sorts_below_it()
    {
        var cancelledButNewer = NewItem("/tasks/cancelled") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(9),
            Status = RoomCardStatus.Cancelled,
        };
        var finishedButOlder = NewItem("/tasks/finished") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(1),
            Status = RoomCardStatus.Finished,
        };

        var ordered = RoomsViewModel.InFleetOrderForTests([cancelledButNewer, finishedButOlder]).ToList();

        Assert.Equal("/tasks/finished", ordered[0].RoomDirectoryPath);
        Assert.Equal("/tasks/cancelled", ordered[1].RoomDirectoryPath);
    }

    [Fact]
    public void An_out_of_plan_room_sorts_below_a_finished_one()
    {
        var outOfPlanButNewer = NewItem("/tasks/outofplan") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(9),
            Status = RoomCardStatus.OutOfPlan,
        };
        var finishedButOlder = NewItem("/tasks/finished") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(1),
            Status = RoomCardStatus.Finished,
        };

        var ordered = RoomsViewModel.InFleetOrderForTests([outOfPlanButNewer, finishedButOlder]).ToList();

        Assert.Equal("/tasks/finished", ordered[0].RoomDirectoryPath);
        Assert.Equal("/tasks/outofplan", ordered[1].RoomDirectoryPath);
    }

    [Fact]
    public void An_unavailable_room_sorts_below_a_finished_one()
    {
        var unavailableButNewer = NewItem("/tasks/unavailable") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(9),
            Status = RoomCardStatus.Unavailable,
        };
        var finishedButOlder = NewItem("/tasks/finished") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(1),
            Status = RoomCardStatus.Finished,
        };

        var ordered = RoomsViewModel.InFleetOrderForTests([unavailableButNewer, finishedButOlder]).ToList();

        Assert.Equal("/tasks/finished", ordered[0].RoomDirectoryPath);
        Assert.Equal("/tasks/unavailable", ordered[1].RoomDirectoryPath);
    }

    [Fact]
    public void Failed_ties_with_finished_by_recency()
    {
        var failedButNewer = NewItem("/tasks/failed") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(9),
            Status = RoomCardStatus.Failed,
        };
        var finishedButOlder = NewItem("/tasks/finished") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(1),
            Status = RoomCardStatus.Finished,
        };

        var ordered = RoomsViewModel.InFleetOrderForTests([finishedButOlder, failedButNewer]).ToList();

        Assert.Equal("/tasks/failed", ordered[0].RoomDirectoryPath);
        Assert.Equal("/tasks/finished", ordered[1].RoomDirectoryPath);
    }

    [Fact]
    public void Recency_still_orders_within_each_band()
    {
        var olderCancelled = NewItem("/tasks/older-cancelled") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(1),
            Status = RoomCardStatus.Cancelled,
        };
        var newerOutOfPlan = NewItem("/tasks/newer-outofplan") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(5),
            Status = RoomCardStatus.OutOfPlan,
        };
        var oldestUnavailable = NewItem("/tasks/oldest-unavailable") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(0),
            Status = RoomCardStatus.Unavailable,
        };

        var ordered = RoomsViewModel.InFleetOrderForTests([olderCancelled, oldestUnavailable, newerOutOfPlan]).ToList();

        Assert.Equal("/tasks/newer-outofplan", ordered[0].RoomDirectoryPath);
        Assert.Equal("/tasks/older-cancelled", ordered[1].RoomDirectoryPath);
        Assert.Equal("/tasks/oldest-unavailable", ordered[2].RoomDirectoryPath);
    }

    [Fact]
    public void A_row_seeds_its_mark_from_the_fleets_status_on_load_not_only_after_a_push()
    {
        // The switcher must draw the correct silhouette immediately; before #1051 the row's Status
        // was null until ApplyProjection fired on the first projection push.
        var viewModel = new RoomsViewModel();

        var row = viewModel.AddTestItem(NewItem("/tasks/needs") with { Status = RoomCardStatus.NeedsYou });

        Assert.Equal(RoomCardStatus.NeedsYou, row.Status);
    }

    // ---- #336: the detail router's discriminator ----

    [Fact]
    public void A_row_carries_whether_it_is_a_session_structurally_not_as_a_label()
    {
        // The switcher routes the detail pane on this. TypeLabel is a *display* string, so routing on
        // it would mean string-matching a rendered label.
        var viewModel = new RoomsViewModel();

        var session = viewModel.AddTestItem(NewItem("/tasks/chat") with { IsSession = true, TypeLabel = "interactive session" });
        var workflow = viewModel.AddTestItem(NewItem("/tasks/dag"));

        Assert.True(session.IsSession);
        Assert.False(workflow.IsSession);
    }

    // ---- #1072: the "Needs you" filter ----

    private static RoomFleetItem NeedsYouItem(string path) =>
        new(path, FriendlyName: path, TypeLabel: "solo-run-template", StatusText: "Waiting for your review",
            PausedStepCount: 1, IsArchived: false, Created: DateTimeOffset.UnixEpoch, Updated: DateTimeOffset.UnixEpoch,
            Status: RoomCardStatus.NeedsYou);

    [Fact]
    public void NeedsYouOnly_collapses_only_the_rows_with_no_paused_step()
    {
        var viewModel = new RoomsViewModel();
        var waiting = viewModel.AddTestItem(NeedsYouItem("/tasks/waiting"));
        var idle = viewModel.AddTestItem(NewItem("/tasks/idle"));

        viewModel.NeedsYouOnly = true;

        // A room that needs you stays; a room that doesn't is filtered out (its container collapses).
        Assert.False(waiting.IsFilteredOut);
        Assert.True(idle.IsFilteredOut);

        viewModel.NeedsYouOnly = false;

        // The polarity control the other way: filter off, every row shows again.
        Assert.False(waiting.IsFilteredOut);
        Assert.False(idle.IsFilteredOut);
    }

    [Fact]
    public void ShowNeedsYouEmpty_is_true_only_when_the_filter_is_on_and_nothing_is_waiting()
    {
        var viewModel = new RoomsViewModel();
        viewModel.AddTestItem(NewItem("/tasks/idle"));

        // Filter off: never the empty-state, whatever the rooms are.
        Assert.False(viewModel.ShowNeedsYouEmpty);

        viewModel.NeedsYouOnly = true;
        Assert.True(viewModel.ShowNeedsYouEmpty);

        // A room that needs you is present → not the empty state, even with the filter on.
        viewModel.AddTestItem(NeedsYouItem("/tasks/waiting"));
        Assert.False(viewModel.ShowNeedsYouEmpty);
    }

    [Fact]
    public void ShowNeedsYouEmpty_is_false_on_a_truly_empty_fleet_so_it_never_doubles_up_with_no_rooms_yet()
    {
        // Review finding #5: with zero rooms, the switcher already shows "No rooms yet." (HasNoItems);
        // "Nothing needs you." must not render on top of it when the filter is on.
        var viewModel = new RoomsViewModel { NeedsYouOnly = true };

        Assert.True(viewModel.HasNoItems);
        Assert.False(viewModel.ShowNeedsYouEmpty);
    }

    [Fact]
    public async Task A_failed_reload_leaves_an_already_loaded_rows_inline_steps_intact_rather_than_blanking_them()
    {
        // Second-reader finding: LoadRowPausedStepsAsync used to Clear() the inline list before the
        // load resolved, so a fire-and-forget reload that then failed (room deleted/locked mid-read)
        // blanked a row a prior good load had populated. The fix defers the clear until the load has
        // both succeeded and won its generation. Control arm: the reload targets a directory with no
        // snapshot, so RoomProjectionLoader.LoadAsync throws and the catch is the path under test.
        var viewModel = new RoomsViewModel();
        var row = viewModel.AddTestItem(
            NeedsYouItem(Path.Combine(Path.GetTempPath(), $"ui-missing-{Guid.NewGuid():N}")));

        // Stand in for a prior successful load: the expanded row already shows one paused step.
        row.PausedSteps.Add(new InboxItemViewModel(
            row.RoomDirectoryPath, "room", "architect", "Waiting for your review", "preview",
            PausePointKind.ReadyForReview, _ => Task.CompletedTask));
        Assert.Single(row.PausedSteps);

        await viewModel.ReloadRowPausedStepsForTestAsync(row);

        // The reload failed (no snapshot on disk), so the previously-displayed step must remain. Before
        // the fix this was zero — the up-front Clear() had already blanked it.
        Assert.Single(row.PausedSteps);
    }
}
