using System.Text.RegularExpressions;

namespace Baton.Cli.Tests;

/// <summary>
/// M13 Phase 2 (#108): confirms the SDK actually stamped <c>Directory.Build.props</c>'s
/// <c>&lt;Version&gt;</c> onto the assembly release-please bumps it in — not just that
/// <see cref="VersionInfo.GetVersion"/> doesn't throw.
/// </summary>
public class VersionInfoTests
{
    [Fact]
    public void GetVersion_reads_the_SemVer_the_SDK_stamped_from_Directory_Build_props()
    {
        var version = VersionInfo.GetVersion(typeof(RunCommand).Assembly);

        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+$"), version);
    }

    [Fact]
    public void GetVersion_rejects_a_null_assembly()
    {
        Assert.Throws<ArgumentNullException>(() => VersionInfo.GetVersion(null!));
    }
}
