namespace Aer.Architecture.Tests;

/// <summary>
/// #336's one correctness trap, guarded at the source level because it is a *structural* invariant
/// that no behavioural test can see.
/// <para>
/// The switcher needs every projection push, including pushes for sessions this client is not
/// currently viewing. The obvious way to get that — widening
/// <c>RoomClient.ShouldApplyProjectionPush</c> — would silently un-fix #262, where one client
/// opening a different task corrupted every other connected client's detail view with that task's
/// data, mislabeled under whatever directory the victim had open. So the fan-out is expressed as
/// *two consumers of one frame*: the list is notified unconditionally, and the detail pane keeps
/// the unchanged directory-equality filter.
/// </para>
/// <para>
/// <c>RoomClientProjectionFilterTests</c> already proves the filter itself still rejects foreign
/// directories. What it cannot prove is that the list's notification did not later get nested back
/// *inside* that filter — which would compile, keep every existing test green, and quietly restore
/// the stale-switcher bug this issue exists to fix. That is what this asserts.
/// </para>
/// </summary>
public class ProjectionPushFanOutTests
{
    private const string ReceiveLoopFile = "src/Aer.Ui.Core/RoomClient.Connection.cs";
    private const string FanOutCall = "RaiseFleetProjectionReceived(";
    private const string DetailFilter = "if (ShouldApplyProjectionPush(";

    [Fact]
    public void The_fleet_fan_out_stays_outside_the_detail_panes_directory_filter()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), ReceiveLoopFile));

        var fanOutIndex = source.IndexOf(FanOutCall, StringComparison.Ordinal);
        var filterIndex = source.IndexOf(DetailFilter, StringComparison.Ordinal);

        Assert.True(fanOutIndex >= 0, $"{ReceiveLoopFile} must still notify the switcher's list of every push ({FanOutCall}).");
        Assert.True(filterIndex >= 0, $"{ReceiveLoopFile} must still gate the detail pane on {DetailFilter} — that filter is what fixed #262.");

        Assert.True(
            fanOutIndex < filterIndex,
            $"'{FanOutCall}' must appear before '{DetailFilter}' in {ReceiveLoopFile}, so the switcher's list is " +
            "notified of every push while the detail pane still takes only pushes for the directory it has open. " +
            "Moving the fan-out inside the filter makes the permanently-visible session list go stale for every " +
            "session except the one being viewed — and widening the filter instead would resurrect #262.");
    }

    [Fact]
    public void The_detail_panes_filter_still_compares_directories_rather_than_accepting_everything()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src/Aer.Ui.Core/RoomClient.cs"));

        Assert.Contains("incomingDirectoryPath == CurrentRoomDirectoryPath", source, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AerFlow.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate the repository root (no AerFlow.slnx found above the test binary).");
    }
}
