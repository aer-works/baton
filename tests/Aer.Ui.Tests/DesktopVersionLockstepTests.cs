using Aer.Daemon;
using Aer.Ui;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// #1260's enforcement clause. `RoomClient` decides whether to shut the daemon down and respawn it by
/// comparing the desktop's assembly version against the one `/api/version` reports, so those two
/// versions moving together is not a tidiness preference — it is what keeps that comparison capable
/// of succeeding at all. It was not, for as long as the client read a version that never moves.
/// </summary>
/// <remarks>
/// Deliberately not in <c>DaemonIntegrationTests</c>: this needs no daemon, and every test in that
/// class starts one. It is also deliberately not a test of the comparison — that branch cannot be
/// discriminated from outside the client under #998's constraint, which the comment at the end of
/// that file records. This is the instrument that can fail on the defect.
/// </remarks>
public class DesktopVersionLockstepTests
{
    [Fact]
    public void The_desktop_and_the_daemon_ship_the_same_version()
    {
        var desktop = typeof(MainWindow).Assembly.GetName().Version;
        var daemon = typeof(DaemonHost).Assembly.GetName().Version;

        // Read, never transcribed: the number itself is release-please's to own, and pinning a
        // literal here would go stale on the next release rather than catch anything.
        Assert.True(
            desktop == daemon,
            $"The desktop ships {desktop} and the daemon ships {daemon}. These are one release-please "
            + "linked group ('desktop' in release-please-config.json), so a divergence means the group "
            + "was split or a member stopped being patched — not that this test wants loosening. While "
            + "they differ, RoomClient's version check can never succeed and every launch against an "
            + "idle daemon shuts it down and respawns it (#1260).");
    }
}
