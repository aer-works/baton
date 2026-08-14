using Aer.Flow.Concurrency;
using Aer.Flow.Domain;
using Aer.Ui.Core;

namespace Aer.Ui.Tests;

/// <summary>
/// #1215: which rooms offer something on their transcript, and which do not. The header Run button
/// this replaced was the only desktop path that resumed a non-terminal room, so getting the
/// predicate wrong in the permissive direction offers a resume on a room that is already running,
/// and getting it wrong in the strict direction strands a room that crashed with no way back.
/// </summary>
public class RoomStoppedCardTests
{
    private static FlowState StateWith(WorkflowStatus status, params StepStatus[] stepStatuses) =>
        new(
            new WorkflowDefinitionSnapshotId("snap-1"),
            [.. stepStatuses.Select((stepStatus, index) => new StepState(
                new StepId($"step-{index}"),
                stepStatus,
                LatestExecutionId: null,
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>()))],
            status);

    /// <summary>
    /// The heart of it: <see cref="WorkflowStatus.Running"/> is the same journal state for a live room
    /// and a crashed one, by that member's own definition. Nothing but the lock separates the two
    /// cases, which is why the arms below differ in exactly one argument and in nothing else.
    /// </summary>
    [Fact]
    public void A_running_room_is_stopped_only_when_no_live_pump_holds_its_lock()
    {
        var running = StateWith(WorkflowStatus.Running, StepStatus.Running);

        Assert.Equal(RoomStoppedReason.StoppedMidRun, RoomClient.DeriveRoomStoppedReason(running, isFlowLockHeld: false));
        Assert.Null(RoomClient.DeriveRoomStoppedReason(running, isFlowLockHeld: true));
    }

    /// <summary>
    /// A room waiting on a person is not stopped in this sense, lock or no lock — see
    /// <see cref="RoomClient.DeriveRoomStoppedReason"/> for why. Both polarities, because
    /// "non-terminal" alone would wrongly claim both.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_room_waiting_on_a_decision_offers_nothing(bool isFlowLockHeld)
    {
        var paused = StateWith(WorkflowStatus.Paused, StepStatus.Succeeded, StepStatus.Paused);

        Assert.Null(RoomClient.DeriveRoomStoppedReason(paused, isFlowLockHeld));
    }

    /// <summary>
    /// A mixed room — one branch still running, another paused on a gate — is the case
    /// <c>Status == Paused</c> alone would miss, since one running step makes the whole workflow
    /// <see cref="WorkflowStatus.Running"/>. The person still has an action on screen.
    /// </summary>
    [Fact]
    public void A_room_with_one_branch_paused_and_another_running_offers_nothing()
    {
        var mixed = StateWith(WorkflowStatus.Running, StepStatus.Running, StepStatus.Paused);

        Assert.Null(RoomClient.DeriveRoomStoppedReason(mixed, isFlowLockHeld: false));
    }

    /// <summary>A finished room offers a re-run, and never consults the lock — nothing pumps a terminal room.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_finished_room_offers_a_re_run(bool isFlowLockHeld)
    {
        var terminal = StateWith(WorkflowStatus.Terminal, StepStatus.Succeeded);

        Assert.Equal(RoomStoppedReason.Finished, RoomClient.DeriveRoomStoppedReason(terminal, isFlowLockHeld));
    }

    /// <summary>
    /// The control arm, and the reason the rest is not just a test of my own boolean: the
    /// <c>isFlowLockHeld</c> argument the arms above pass by hand is produced in production by
    /// <see cref="ConcurrencyGuard.IsHeld"/>, so this holds a real guard and reads the real probe.
    /// Asserted in both directions against the same directory — held while the guard lives, free the
    /// instant it is disposed, which is the same release the OS performs when a holder crashes.
    /// </summary>
    [Fact]
    public void The_lock_probe_the_derivation_is_fed_reports_a_real_guard_in_both_directions()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-stopped-card-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            Assert.False(ConcurrencyGuard.IsHeld(roomDirectory));

            using (ConcurrencyGuard.Acquire(roomDirectory, "room stopped card test"))
            {
                Assert.True(ConcurrencyGuard.IsHeld(roomDirectory));
                Assert.Null(RoomClient.DeriveRoomStoppedReason(
                    StateWith(WorkflowStatus.Running, StepStatus.Running),
                    ConcurrencyGuard.IsHeld(roomDirectory)));
            }

            Assert.False(ConcurrencyGuard.IsHeld(roomDirectory));
            Assert.Equal(
                RoomStoppedReason.StoppedMidRun,
                RoomClient.DeriveRoomStoppedReason(
                    StateWith(WorkflowStatus.Running, StepStatus.Running),
                    ConcurrencyGuard.IsHeld(roomDirectory)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// The card says different things for the two reasons, and its action is what a person clicks —
    /// so the label is part of the claim, not decoration. Resume must never read as "run again":
    /// one picks the room up where it left off, the other starts a fresh room.
    /// </summary>
    [Fact]
    public void Each_reason_offers_its_own_action_and_says_which_it_is()
    {
        var stalled = new RoomStoppedCardViewModel(RoomStoppedReason.StoppedMidRun, () => Task.CompletedTask);
        var finished = new RoomStoppedCardViewModel(RoomStoppedReason.Finished, () => Task.CompletedTask);

        Assert.Equal("Resume", stalled.ActionLabel);
        Assert.Contains("stopped mid-run", stalled.Headline);

        Assert.Equal("Run it again", finished.ActionLabel);
        Assert.Contains("finished", finished.Headline);
    }

    /// <summary>A click cannot post a second run while the first is still going.</summary>
    [Fact]
    public async Task The_action_disables_itself_while_it_is_in_flight()
    {
        var release = new TaskCompletionSource();
        var enabledDuringRun = true;

        var card = new RoomStoppedCardViewModel(RoomStoppedReason.StoppedMidRun, async () =>
        {
            await release.Task;
        });

        Assert.True(card.IsEnabled);

        var run = card.RunCommand.ExecuteAsync(null);
        enabledDuringRun = card.IsEnabled;
        release.SetResult();
        await run;

        Assert.False(enabledDuringRun);
        Assert.True(card.IsEnabled);
    }
}
