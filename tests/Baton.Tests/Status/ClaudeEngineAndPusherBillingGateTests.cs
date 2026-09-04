using System.Text.Json;
using Baton.Mutation;
using Baton.Status;
using Xunit;

namespace Baton.Tests.Status;

/// <summary>
/// #1706 review M5. The claude billing rule now has three implementations — this engine,
/// <c>tools/fleet-glass/pusher.py</c>'s <c>extract_live_counts</c>, and
/// <c>tools/room-rate-sweep/sweep.py</c>. <see cref="AgyEngineAndPusherUsageGateTests"/> exists because
/// #1686 review F4 caught two of them disagreeing on the AGY side; this closes the same hole on the
/// claude side, and does it properly: both consumers read the SAME file
/// (<c>Fixtures/claude-billing-gate.json</c>) rather than each carrying its own transcription of the
/// same line, which is how the agy pair still drifts if someone edits one comment.
/// <para>
/// The live divergence it was created to settle is named on the pusher's own claude branch (search
/// <c>#1706 review M5</c> in <c>extract_live_counts</c>). It resolves to null — "no reading", never a
/// fabricated zero — and the fixture carries an explicit-zero arm right next to it so the two answers
/// stay distinguishable.
/// </para>
/// </summary>
public sealed class ClaudeEngineAndPusherBillingGateTests
{
    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in LoadCases().Keys)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void The_engine_agrees_with_the_shared_fixture_pusher_py_s_selftest_reads(string caseName)
    {
        var gateCase = LoadCases()[caseName];
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: null, billedRateLimit: null, new ClaudeUsageParser());

        foreach (var line in gateCase.Lines)
        {
            monitor.OnStdoutLine(line);
        }

        var usage = monitor.SnapshotUsage();
        Assert.Equal(gateCase.ExpectedBilledTokens, usage.BilledTokens);
        Assert.Equal(gateCase.ExpectedBilledIsFloor, usage.BilledIsFloor);
    }

    [Fact]
    public void The_fixture_discriminates_absent_from_zero()
    {
        // The control on the FIXTURE itself, read first: without both an absent case and a measured-zero
        // case in the file, an implementation that collapsed the two -- which is precisely the defect
        // this gate closes -- would pass every arm above.
        var cases = LoadCases();

        Assert.Contains(cases.Values, c => c.ExpectedBilledTokens is null);
        Assert.Contains(cases.Values, c => c.ExpectedBilledTokens == 0);
        Assert.Contains(cases.Values, c => c.ExpectedBilledIsFloor is false);
    }

    private sealed record GateCase(string[] Lines, long? ExpectedBilledTokens, bool ExpectedBilledIsFloor);

    private static IReadOnlyDictionary<string, GateCase> LoadCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-billing-gate.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var cases = new Dictionary<string, GateCase>(StringComparer.Ordinal);
        foreach (var element in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var lines = element.GetProperty("lines").EnumerateArray().Select(l => l.GetString()!).ToArray();
            var billedElement = element.GetProperty("expectedBilledTokens");
            long? billed = billedElement.ValueKind == JsonValueKind.Null ? null : billedElement.GetInt64();
            cases[element.GetProperty("name").GetString()!] =
                new GateCase(lines, billed, element.GetProperty("expectedBilledIsFloor").GetBoolean());
        }

        return cases;
    }
}
