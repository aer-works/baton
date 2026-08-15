using Aer.Adapters;
using Aer.Ui.Core;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// #1272: the desktop surface for a room's standing permissions. Exercises
/// <see cref="StandingPermissionsViewModel"/> against injected delegates rather than a real daemon —
/// the wiring to <c>RoomClient</c> is in <c>MainWindow</c>'s codebehind and is not testable at this
/// layer (Avalonia-free assembly); see <see cref="MainWindowViewModel.StandingPermissions"/> for why.
/// <para>
/// Every arm calls <see cref="StandingPermissionsViewModel.LoadAsync"/> directly and awaits it,
/// rather than calling <c>ToggleOpen</c> (which fires the load without awaiting it, correct for a
/// real UI but not something a test should sleep after to "settle" — that is the exact construction
/// this repo spent tonight deleting two instances of).
/// </para>
/// </summary>
public class StandingPermissionsViewModelTests
{
    private static StandingPermissionsResult Configured(
        bool runShellCommands = false,
        IReadOnlyList<string>? allowed = null,
        IReadOnlyList<string>? denied = null) =>
        new(
            StandingPermissionReadOutcome.Configured.ToString(),
            runShellCommands,
            allowed ?? [],
            denied ?? []);

    private static (StandingPermissionsViewModel Vm, List<(string Room, string Kind, string? Pattern)> Revokes) Build(
        Func<string, string?, CancellationToken, Task<StandingPermissionsResult>> getPermissions,
        RoomClient.MutationOutcome? revokeOutcome = null)
    {
        var revokes = new List<(string, string, string?)>();
        var vm = new StandingPermissionsViewModel(
            (roomDirectoryPath, workerName, cancellationToken) => getPermissions(roomDirectoryPath, workerName, cancellationToken),
            (roomDirectoryPath, revokeKind, shellCommandPattern, workerName, cancellationToken) =>
            {
                revokes.Add((roomDirectoryPath, revokeKind, shellCommandPattern));
                return Task.FromResult(revokeOutcome ?? new RoomClient.MutationOutcome(null));
            });
        return (vm, revokes);
    }

    /// <summary>Arm 1: the list renders standing permissions, including a denied pattern.</summary>
    [Fact]
    public async Task LoadAsync_RendersEntries_IncludingADeniedPattern()
    {
        var (vm, _) = Build((_, _, _) => Task.FromResult(Configured(
            runShellCommands: true,
            allowed: ["git status"],
            denied: ["rm -rf *"])));

        await vm.LoadAsync("/rooms/r1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, vm.Entries.Count);
        Assert.Contains(vm.Entries, e => e.RevokeKind == PermissionRevokeKind.RoomShell && e.CanRevoke);
        Assert.Contains(vm.Entries, e => e.ShellCommandPattern == "git status" && e.CanRevoke);
        Assert.Contains(vm.Entries, e => e.ShellCommandPattern == "rm -rf *" && e.IsDeniedPattern && !e.CanRevoke);
    }

    /// <summary>
    /// Arm 2: revoking calls the route with the right kind and pattern, and the list reflects it
    /// afterward (re-fetch).
    /// </summary>
    [Fact]
    public async Task RevokingACommand_CallsTheRouteWithItsPattern_AndTheEntryIsGoneAfterReload()
    {
        var stillGranted = true;
        var (vm, revokes) = Build((_, _, _) => Task.FromResult(
            stillGranted ? Configured(allowed: ["git status"]) : Configured()));

        await vm.LoadAsync("/rooms/r1", cancellationToken: TestContext.Current.CancellationToken);
        var entry = Assert.Single(vm.Entries);

        stillGranted = false; // the fixture's next GET reflects the revoke that is about to happen
        await entry.RevokeCommand.ExecuteAsync(null);

        var revoke = Assert.Single(revokes);
        Assert.Equal("/rooms/r1", revoke.Room);
        Assert.Equal(PermissionRevokeKind.CommandInRoom, revoke.Kind);
        Assert.Equal("git status", revoke.Pattern);
        Assert.Empty(vm.Entries);
    }

    /// <summary>
    /// Arm 2b: revoking whole-room shell access needs confirmation first — the wide action, per the
    /// brief's caution around widening/narrowing operations. A single command's revoke (above) needs
    /// none.
    /// </summary>
    [Fact]
    public async Task RevokingRoomShell_AsksForConfirmationFirst_AndDoesNothingUntilConfirmed()
    {
        var (vm, revokes) = Build((_, _, _) => Task.FromResult(Configured(runShellCommands: true)));

        await vm.LoadAsync("/rooms/r1", cancellationToken: TestContext.Current.CancellationToken);
        var entry = Assert.Single(vm.Entries);

        await entry.RevokeCommand.ExecuteAsync(null);

        Assert.Empty(revokes);
        Assert.True(vm.IsConfirmingRevokeRoomShell);

        await vm.ConfirmRevokeRoomShellCommand.ExecuteAsync(null);

        var revoke = Assert.Single(revokes);
        Assert.Equal(PermissionRevokeKind.RoomShell, revoke.Kind);
        Assert.False(vm.IsConfirmingRevokeRoomShell);
    }

    /// <summary>
    /// Arm 3: a room with no worker setup and a room with worker setup but nothing granted render
    /// differently — the discriminating control for the three-way outcome. Collapsing them would let
    /// "this room grants nothing" and "this room has no worker setup at all" read as the same fact.
    /// </summary>
    [Fact]
    public async Task NoWorkerSetup_AndConfiguredButEmpty_RenderDifferentOutcomes()
    {
        var (noSetupVm, _) = Build((_, _, _) => Task.FromResult(
            new StandingPermissionsResult(StandingPermissionReadOutcome.NoWorkerSetup.ToString(), false, [], [])));
        await noSetupVm.LoadAsync("/rooms/r1", cancellationToken: TestContext.Current.CancellationToken);

        var (emptyVm, _) = Build((_, _, _) => Task.FromResult(Configured()));
        await emptyVm.LoadAsync("/rooms/r2", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(noSetupVm.IsNoWorkerSetup);
        Assert.False(noSetupVm.IsConfiguredEmpty);

        Assert.False(emptyVm.IsNoWorkerSetup);
        Assert.True(emptyVm.IsConfiguredEmpty);
    }

    /// <summary>Arm 4: a busy-room 503 on revoke surfaces as a legible message, not a silent failure.</summary>
    [Fact]
    public async Task ABusyRoom503OnRevoke_SurfacesAsAMessage_RatherThanSilentlySucceeding()
    {
        var (vm, _) = Build(
            (_, _, _) => Task.FromResult(Configured(allowed: ["git status"])),
            revokeOutcome: new RoomClient.MutationOutcome("Could not take back that permission: the room was busy. Try again."));

        await vm.LoadAsync("/rooms/r1", cancellationToken: TestContext.Current.CancellationToken);
        var entry = Assert.Single(vm.Entries);

        await entry.RevokeCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("busy", vm.ErrorMessage);
        // The entry is still there — a 503 must not be read as a successful revoke.
        Assert.Single(vm.Entries);
    }

    /// <summary>
    /// <c>ToggleOpen</c> flips the flag on both directions. Does not assert anything about whether a
    /// load ran — that would mean either sleeping after a fire-and-forget call (the construction this
    /// repo spent tonight removing two instances of) or relying on synchronous continuation of an
    /// already-completed task, which is an implementation detail of the TPL, not a contract worth
    /// pinning. The delegate is wired correctly per arm 1, which awaits <c>LoadAsync</c> directly.
    /// </summary>
    [Fact]
    public void ToggleOpen_FlipsTheFlagBothWays()
    {
        var vm = new StandingPermissionsViewModel(
            (_, _, _) => Task.FromResult(Configured()),
            (_, _, _, _, _) => Task.FromResult(new RoomClient.MutationOutcome(null)));

        Assert.False(vm.IsStandingPermissionsOpen);
        vm.ToggleOpen("/rooms/r1");
        Assert.True(vm.IsStandingPermissionsOpen);
        vm.ToggleOpen("/rooms/r1");
        Assert.False(vm.IsStandingPermissionsOpen);
    }

    /// <summary>
    /// Closed-to-open with no room path (nothing open yet) still flips the flag but starts no load —
    /// the null-path guard in <c>ToggleOpen</c> exists so a stray toggle before any room is open
    /// cannot call the delegate with an empty path.
    /// </summary>
    [Fact]
    public void ToggleOpen_WithNoRoomPath_StillFlipsButNeverCallsTheDelegate()
    {
        var calls = 0;
        var vm = new StandingPermissionsViewModel(
            (_, _, _) => { calls++; return Task.FromResult(Configured()); },
            (_, _, _, _, _) => Task.FromResult(new RoomClient.MutationOutcome(null)));

        vm.ToggleOpen(roomDirectoryPath: null);

        Assert.True(vm.IsStandingPermissionsOpen);
        Assert.Equal(0, calls);
    }
}
