using Baton.Accounting;
using Baton.Status;

namespace Baton.Cli.Tests;

/// <summary>
/// The grammar of the reading form of <c>baton ledger</c> (#1849 phase B) — every malformed
/// invocation is a <see cref="CliArgumentException"/>, and the two instant spellings the contract
/// promises both land in the UTC frame the ledger records in.
/// </summary>
public sealed class LedgerViewOptionsParserTests
{
    [Fact]
    public void No_arguments_is_the_fleet_view_over_everything_in_the_file()
    {
        var options = LedgerViewOptionsParser.Parse([]);

        Assert.Null(options.RoomDirectoryPath);
        Assert.Null(options.Query.Room);
        Assert.Equal(new LedgerQuery(), options.Query);
        Assert.Equal(LedgerOutputFormat.Text, options.Format);
        Assert.False(options.Drill);
    }

    [Fact]
    public void A_room_directory_sets_the_room_facet_as_a_record_key()
    {
        var room = Path.Combine(Path.GetTempPath(), "baton-1849b-parse", "room");
        var options = LedgerViewOptionsParser.Parse([room]);

        Assert.Equal(BatonPaths.RecordKey(room), options.Query.Room);
        Assert.Equal(options.RoomDirectoryPath, options.Query.Room);
    }

    /// <summary>
    /// The date shorthand is the operator's LOCAL midnight, converted to UTC — asserted against a
    /// FIXED non-zero zone the test injects, not against the machine's (#1893 review M3). Writing the
    /// expectation as <c>SpecifyKind(…, Local).ToUniversalTime()</c> would restate the implementation's
    /// own expression, and on a UTC+0 runner that expression cannot tell <c>Local</c> from <c>Utc</c> —
    /// which is the whole claim the shorthand makes.
    /// </summary>
    [Fact]
    public void A_bare_date_is_local_midnight_and_an_ISO_instant_is_taken_as_written()
    {
        var kathmandu = TimeZoneInfo.CreateCustomTimeZone(
            "baton-test-+0545", TimeSpan.FromMinutes(345), "UTC+05:45", "UTC+05:45");

        // Midnight on the 4th THERE is 18:15 UTC on the 3rd. A UTC reading would say 2026-09-04T00:00Z.
        Assert.Equal(
            new DateTime(2026, 9, 3, 18, 15, 0, DateTimeKind.Utc),
            LedgerViewOptionsParser.ParseInstant("2026-09-04", "--since", kathmandu));

        // The other polarity, over the same zone: an instant that says which frame it is in is taken
        // as written, so the injected zone must NOT move it.
        Assert.Equal(
            new DateTime(2026, 9, 4, 14, 0, 0, DateTimeKind.Utc),
            LedgerViewOptionsParser.ParseInstant("2026-09-04T14:00:00Z", "--since", kathmandu));

        // The production default is still the machine's own zone.
        Assert.Equal(
            LedgerViewOptionsParser.ParseInstant("2026-09-04", "--since", TimeZoneInfo.Local),
            LedgerViewOptionsParser.ParseInstant("2026-09-04", "--since"));

        Assert.Equal(new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc), LedgerViewOptionsParser.ParseInstant("2026-09-04T14:00:00+02:00", "--since"));
    }

    [Fact]
    public void An_unparseable_instant_names_both_spellings_it_would_have_accepted()
    {
        var refusal = Assert.Throws<CliArgumentException>(
            () => LedgerViewOptionsParser.Parse(["--since", "last tuesday"]));

        Assert.Contains("ISO-8601", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("yyyy-MM-dd", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_window_is_refused_rather_than_silently_returning_nothing()
    {
        var refusal = Assert.Throws<CliArgumentException>(
            () => LedgerViewOptionsParser.Parse(["--since", "2026-09-05", "--until", "2026-09-04"]));

        Assert.Contains("--since is inclusive and --until is exclusive", refusal.Message, StringComparison.Ordinal);

        // The control: the boundary case one second wide is legal, so the refusal is about emptiness
        // rather than about any two-bound window at all.
        Assert.NotNull(LedgerViewOptionsParser.Parse(["--since", "2026-09-04T00:00:00Z", "--until", "2026-09-04T00:00:01Z"]));
    }

    [Fact]
    public void Every_facet_lands_on_the_query_it_names()
    {
        var options = LedgerViewOptionsParser.Parse(
        [
            "--vendor", "claude", "--model", "claude-opus-5", "--role", "implement",
            "--project", "github.com/aer-works/baton", "--outcome", "Succeeded", "--workflow", "wf1",
            "--pr", "1883", "--issue", "1849", "--source-kind", "codex-session", "--drill",
            "--format", "csv", "--repo-identity", "github.com/aer-works/baton",
        ]);

        Assert.Equal("claude", options.Query.Vendor);
        Assert.Equal("claude-opus-5", options.Query.Model);
        Assert.Equal("implement", options.Query.Role);
        Assert.Equal("github.com/aer-works/baton", options.Query.Project);
        Assert.Equal("Succeeded", options.Query.Outcome);
        Assert.Equal("wf1", options.Query.Workflow);
        Assert.Equal("1883", options.Query.PullRequest);
        Assert.Equal("1849", options.Query.Issue);
        Assert.Equal(CostSourceKind.CodexSession, options.Query.SourceKind);
        Assert.Equal("github.com/aer-works/baton", options.RepositoryIdentityKey);
        Assert.Equal(LedgerOutputFormat.Csv, options.Format);
        Assert.True(options.Drill);
    }

    [Theory]
    [InlineData("--vendor")]
    [InlineData("--since")]
    [InlineData("--format")]
    [InlineData("--repo-identity")]
    public void An_option_missing_its_value_is_refused_by_name(string option)
    {
        var refusal = Assert.Throws<CliArgumentException>(() => LedgerViewOptionsParser.Parse([option]));
        Assert.Contains($"Option '{option}' requires a value", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_option_format_or_source_kind_is_refused_rather_than_ignored()
    {
        Assert.Throws<CliArgumentException>(() => LedgerViewOptionsParser.Parse(["--rooms"]));
        Assert.Throws<CliArgumentException>(() => LedgerViewOptionsParser.Parse(["--format", "yaml"]));
        Assert.Throws<CliArgumentException>(() => LedgerViewOptionsParser.Parse(["--source-kind", "gemini-session"]));
        Assert.Throws<CliArgumentException>(() => LedgerViewOptionsParser.Parse(["room-one", "room-two"]));
    }
}
