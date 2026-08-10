using Aer.Cli;

namespace Aer.Cli.Tests;

/// <summary>
/// #1094: the foreground vendor-quota park notice (<see cref="RunCommand.FormatVendorQuotaParkNotice"/>).
/// Pins that it renders the reset instant in the operator's local time and names the auto-resume and
/// the Ctrl-C escape, so a day-long paced wait reads as a state rather than a hang.
/// </summary>
public class QuotaParkNoticeTests
{
    [Fact]
    public void The_notice_renders_the_reset_instant_in_local_time_and_names_the_escape_hatch()
    {
        var resumesAt = new DateTimeOffset(2026, 8, 11, 17, 48, 0, TimeSpan.Zero);
        var notice = RunCommand.FormatVendorQuotaParkNotice(resumesAt);

        // Local time, not UTC — computed the same way so the assertion holds in any host time zone.
        var expectedLocal = resumesAt.ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(expectedLocal, notice);

        Assert.Contains("vendor quota", notice);
        Assert.Contains("resumes automatically", notice);
        Assert.Contains("Ctrl-C", notice);
    }
}
