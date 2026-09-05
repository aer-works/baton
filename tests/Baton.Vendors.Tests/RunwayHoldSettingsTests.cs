using Baton.Vendors.Tests.TestSupport;

namespace Baton.Vendors.Tests;

/// <summary>
/// #1848's thresholds where the operator actually sets them — <c>~/.baton/settings.json</c>, the
/// config file baton already has. The round-trip arm is the one that matters: <see cref="DaemonSettingsStore"/>
/// serializes with default (PascalCase) naming, so a property that does not bind reads as the default
/// silently, and a gate that quietly ignores its configuration is worse than one with no configuration.
/// </summary>
public class RunwayHoldSettingsTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"baton-runway-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void Absent_configuration_leaves_the_operator_approved_defaults_in_force()
    {
        var thresholds = new DaemonSettings().RunwayHold.For("claude");

        Assert.Equal(RunwayThresholds.DefaultWeekHoldPct, thresholds.WeekHoldPct);
        Assert.Equal(RunwayThresholds.DefaultSessionHoldPct, thresholds.SessionHoldPct);
        Assert.Equal(TimeSpan.FromHours(RunwayThresholds.DefaultMaxSnapshotAgeHours), thresholds.EffectiveMaxSnapshotAge);
    }

    [Fact]
    public void A_per_vendor_entry_overrides_only_the_fields_it_names()
    {
        var settings = new RunwayHoldSettings
        {
            WeekHoldPct = 70,
            Vendors = new Dictionary<string, RunwayVendorHoldSettings>(StringComparer.Ordinal)
            {
                ["agy"] = new() { SessionHoldPct = 95 },
            },
        };

        var agy = settings.For("agy");
        var claude = settings.For("claude");

        Assert.Equal(70, agy.WeekHoldPct);
        Assert.Equal(95, agy.SessionHoldPct);
        Assert.Equal(70, claude.WeekHoldPct);
        Assert.Equal(RunwayThresholds.DefaultSessionHoldPct, claude.SessionHoldPct);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(150)]
    public void An_out_of_range_percentage_falls_back_to_the_default_rather_than_being_honoured(int configured)
    {
        var settings = new RunwayHoldSettings { WeekHoldPct = configured, MaxSnapshotAgeHours = 0 };

        var thresholds = settings.For("claude");

        Assert.Equal(RunwayThresholds.DefaultWeekHoldPct, thresholds.WeekHoldPct);
        Assert.Equal(TimeSpan.FromHours(RunwayThresholds.DefaultMaxSnapshotAgeHours), thresholds.EffectiveMaxSnapshotAge);
    }

    [Fact]
    public async Task Thresholds_written_to_the_settings_file_are_read_back_and_take_effect()
    {
        var path = TempPath();
        try
        {
            var original = new DaemonSettings
            {
                RunwayHold = new RunwayHoldSettings
                {
                    WeekHoldPct = 60,
                    SessionHoldPct = 65,
                    MaxSnapshotAgeHours = 2,
                    Vendors = new Dictionary<string, RunwayVendorHoldSettings>(StringComparer.Ordinal)
                    {
                        ["claude"] = new() { WeekHoldPct = 50 },
                    },
                },
            };

            await DaemonSettingsStore.SaveAsync(original, path, TestContext.Current.CancellationToken);
            var loaded = await DaemonSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken);

            var claude = loaded.RunwayHold.For("claude");
            Assert.Equal(50, claude.WeekHoldPct);
            Assert.Equal(65, claude.SessionHoldPct);
            Assert.Equal(TimeSpan.FromHours(2), claude.EffectiveMaxSnapshotAge);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// #1848 review: <c>"RunwayHold": null</c> in an otherwise well-formed file parses cleanly — the
    /// store's defaults-on-failure arm never fires for it — so before the null-coalescing accessor the
    /// first <c>For(vendor)</c> threw a NullReferenceException and an operator's typo took dispatch down
    /// instead of leaving the shipped thresholds in force. The honoured-value arm above is this one's
    /// control: it proves a settings file's contents reach <c>For</c> at all, so "gates at 85/90" here
    /// means the null fell back rather than the file being ignored.
    /// </summary>
    [Fact]
    public async Task An_explicit_null_runway_hold_still_gates_at_the_defaults()
    {
        var path = TempPath();
        try
        {
            await File.WriteAllTextAsync(
                path,
                """{ "GlobalConcurrencyCap": 3, "RunwayHold": null }""",
                TestContext.Current.CancellationToken);

            var loaded = await DaemonSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken);

            var thresholds = loaded.RunwayHold.For("claude");
            Assert.Equal(RunwayThresholds.DefaultWeekHoldPct, thresholds.WeekHoldPct);
            Assert.Equal(RunwayThresholds.DefaultSessionHoldPct, thresholds.SessionHoldPct);
            Assert.Equal(
                TimeSpan.FromHours(RunwayThresholds.DefaultMaxSnapshotAgeHours), thresholds.EffectiveMaxSnapshotAge);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task A_malformed_settings_file_still_gates_at_the_defaults()
    {
        var path = TempPath();
        try
        {
            await File.WriteAllTextAsync(path, "{ not json", TestContext.Current.CancellationToken);

            var loaded = await DaemonSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(RunwayThresholds.DefaultWeekHoldPct, loaded.RunwayHold.For("claude").WeekHoldPct);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }
}
