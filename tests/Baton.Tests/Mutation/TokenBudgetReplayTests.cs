using Baton.Domain;
using Baton.Mutation;
using Baton.Status;
using Xunit;

namespace Baton.Tests.Mutation;

/// <summary>
/// #1682 acceptance: "A replay of room `dispatch-implement-f7b24a80`'s stream (or a fixture shaped
/// like it) arrests before 600k billed." This fixture is shaped like `dispatch-implement-38c24d11`
/// instead (the OTHER #1682 evidence room) — its 70 (input_tokens, output_tokens) pairs copied
/// verbatim, per usage line, from that room's own real `.stdout.log`
/// (`dispatch-implement-38c24d11/artifacts/execution_a6de5637dbae488581ad3848729a0c1b/.stdout.log`),
/// in the same order they were emitted. Regenerating the fixture: filter that file's lines for
/// `"event":"step_update"` with `step_update.state == "DONE"` and read `step_update.usage.input_tokens`/
/// `output_tokens` off each.
/// </summary>
public sealed class TokenBudgetReplayTests
{
    /// <summary>(input_tokens, output_tokens) per usage line, room order.</summary>
    private static readonly (long In, long Out)[] Room38c24d11Turns =
    [
        (14205, 443), (16203, 1128), (17941, 180), (2994, 436), (36793, 116), (3212, 102), (3399, 100),
        (3583, 110), (7147, 135), (3308, 120), (11862, 104), (9438, 134), (5573, 108), (5765, 143),
        (5993, 167), (5302, 120), (5252, 97), (5453, 92), (5640, 78), (2842, 91), (12250, 1631),
        (5660, 138), (2847, 147), (8337, 134), (3470, 130), (7148, 28286), (31505, 973), (4092, 21413),
        (21613, 376), (5858, 284), (2158, 1072), (3407, 23509), (2443, 359), (2897, 498), (3619, 611),
        (4336, 22864), (23163, 182), (3013, 369), (3548, 336), (4112, 103), (4408, 177), (4811, 109),
        (5306, 550), (5952, 7330), (9276, 3681), (4874, 10766), (11627, 3347), (6891, 7359), (10244, 535),
        (2704, 248), (3352, 708), (4155, 7353), (7503, 169), (3683, 573), (4564, 7611), (96546, 3679),
        (6401, 10551), (12936, 655), (2049, 7612), (9739, 3655), (5315, 10281), (11584, 173), (3684, 459),
        (4252, 562), (4920, 235), (5613, 872), (2648, 278), (3239, 1455), (4863, 100), (5164, 754),
    ];

    // #1686 review F3: this was named `OldAndNewDefaultBudget` and its comment claimed 600,000 was
    // "still-in-force" -- both false as of this diff. `implement`'s SHIPPED budget is 1,200,000
    // (`ShippedImplementBudget` below); 600,000 is the SUPERSEDED figure the original #1682 acceptance
    // criterion asked a replay to arrest under, kept only so that criterion still has a runnable test.
    private const long SupersededPre1682AcceptanceBudget = 600_000;

    // #1686 review F3: the configuration this PR actually ships for `implement`
    // (`src/Baton.Vendors/WorkerRoles.json`) -- what the "honest replay result" tests below exercise.
    private const long ShippedImplementBudget = 1_200_000;
    private const int ShippedImplementMaxToolSteps = 322;

    // #1686 review F1: the OLD (pre-#1682, i.e. pre-THIS-PR) `implement` tool-step cap, in the OLD
    // double-counted unit (ACTIVE + terminal lifecycle line per real call) -- kept only for the
    // discriminating control below, which proves the F2 unit fix is what changed the replay's outcome,
    // not an unrelated change.
    private const int PreF2ImplementMaxToolSteps = 80;

    private static string AgyDoneLine(long inputTokens, long outputTokens) =>
        "{\"event\":\"step_update\",\"step_update\":{\"state\":\"DONE\",\"step_type\":\"agent_response\",\"usage\":{"
        + "\"input_tokens\":" + inputTokens + ",\"output_tokens\":" + outputTokens
        + ",\"total_tokens\":" + (inputTokens + outputTokens) + "}}}";

    private static string AgyToolLine(string state) =>
        "{\"event\":\"step_update\",\"step_update\":{\"state\":\"" + state + "\",\"step_type\":\"tool\",\"tool_name\":\"run_command\"}}";

    /// <summary>
    /// #1686 review F3: the REAL interleaved tool-step lines from room `38c24d11`'s own
    /// `.stdout.log`, positioned relative to the 70 usage lines above -- extracted by filtering the
    /// same capture for every `step_update` line (usage OR tool) in emitted order and reading off the
    /// shape: this room's stream is maximally regular -- each of the 70 turns is exactly
    /// [usage DONE/agent_response line, ACTIVE tool line, terminal (DONE) tool line], back to back,
    /// with NO trailing tool pair after the 70th (final) usage line, because the room was cancelled
    /// before that turn's own tool call reached ACTIVE. Regenerating: filter the room's `.stdout.log`
    /// for every `"event":"step_update"` line in order and classify each by
    /// `step_update.step_type`/`step_update.state` the same way <see cref="TokenBudgetReplayTests"/>'s
    /// class doc already documents regenerating the usage-only fixture above.
    /// #1686 review F8: unlike <see cref="Room38c24d11Turns"/>, which is copied verbatim per line, the
    /// tool lines this method emits (<see cref="AgyToolLine"/>) are RECONSTRUCTED from the counted
    /// [usage, ACTIVE, DONE] shape above, not copied literal lines -- the room's real tool_name and
    /// exact JSON are not reproduced, only the state/step_type/count regularity that governs what this
    /// monitor counts.
    /// </summary>
    /// <param name="monitor">Fed one real interleaved line at a time, in the room's own emitted order.</param>
    /// <returns>The 0-based usage-line index (matching <see cref="Room38c24d11Turns"/>) at which the monitor first arrested, or -1 if it never did.</returns>
    private static int ReplayRoom38c24d11(TokenBudgetMonitor monitor)
    {
        var arrestedAtLine = -1;
        for (var i = 0; i < Room38c24d11Turns.Length; i++)
        {
            var (inTokens, outTokens) = Room38c24d11Turns[i];
            monitor.OnStdoutLine(AgyDoneLine(inTokens, outTokens));
            if (monitor.Arrested && arrestedAtLine == -1)
            {
                arrestedAtLine = i;
            }

            if (i == Room38c24d11Turns.Length - 1)
            {
                // The room's capture ends right here -- no tool pair follows the final usage line.
                break;
            }

            monitor.OnStdoutLine(AgyToolLine("ACTIVE"));
            if (monitor.Arrested && arrestedAtLine == -1)
            {
                arrestedAtLine = i;
            }

            monitor.OnStdoutLine(AgyToolLine("DONE"));
            if (monitor.Arrested && arrestedAtLine == -1)
            {
                arrestedAtLine = i;
            }
        }

        return arrestedAtLine;
    }

    [Fact]
    public void GREEN_the_room_38c24d11_shaped_replay_arrests_before_600k_billed_under_the_new_reading()
    {
        // The original #1682 acceptance criterion, still runnable against the superseded budget --
        // NOT the shipped configuration (see the honest-replay tests below for that).
        var monitor = new TokenBudgetMonitor(budget: SupersededPre1682AcceptanceBudget, maxToolSteps: null, new AgyUsageParser());

        var arrestedAtLine = -1;
        for (var i = 0; i < Room38c24d11Turns.Length; i++)
        {
            var (inTokens, outTokens) = Room38c24d11Turns[i];
            monitor.OnStdoutLine(AgyDoneLine(inTokens, outTokens));
            if (monitor.Arrested && arrestedAtLine == -1)
            {
                arrestedAtLine = i;
            }
        }

        Assert.True(monitor.Arrested);
        Assert.Equal(ArrestReason.TokenBudget, monitor.ArrestReasonValue);
        // Measured: billed crosses 600,000 on usage line 56 (0-indexed 55) of this room's real stream --
        // long before the room's own eventual total of 794,940.
        Assert.Equal(55, arrestedAtLine);
        Assert.True(monitor.SnapshotUsage().BilledTokens >= SupersededPre1682AcceptanceBudget);
    }

    [Fact]
    public void HONEST_the_shipped_implement_configuration_does_NOT_arrest_the_real_room_38c24d11_replay()
    {
        // #1686 review F3 / "Replay verdict": the review's own bar is that the SHIPPED configuration
        // must be proven to arrest the real runaway room. Measured here rather than assumed: it does
        // NOT. Room 38c24d11's real capture makes only 69 real tool calls in its entire 70-turn, 794,940
        // -billed-token stream -- far under a tool-step cap wide enough to avoid false-arresting normal
        // `implement` traffic (322, and even that is exceeded by 2 of 26 real normal rooms measured for
        // this PR -- spec/baton.md §3). And 794,940 sits under the recalibrated 1,200,000 token budget
        // by the SAME sound "2x normal" method. Neither trigger fires. This is not a bug in this
        // replay -- it is the honest result of fixing F1/F2 correctly, and it is recorded here as a
        // durable fact rather than left to only exist in a PR body that will drift out of sync with the
        // code the moment either constant changes again.
        var monitor = new TokenBudgetMonitor(budget: ShippedImplementBudget, maxToolSteps: ShippedImplementMaxToolSteps, new AgyUsageParser());

        var arrestedAtLine = ReplayRoom38c24d11(monitor);

        Assert.False(monitor.Arrested);
        Assert.Equal(-1, arrestedAtLine);
        Assert.Null(monitor.ArrestReasonValue);
        Assert.Equal(794_940, monitor.SnapshotUsage().BilledTokens);
        Assert.Equal(69, monitor.SnapshotToolStepCount());
    }

    [Fact]
    public void HONEST_the_token_trigger_alone_does_NOT_fire_on_room_38c24d11_at_the_shipped_1_2M_budget()
    {
        // #1686 review F3's second, independently satisfiable assertion: isolated from the tool-step
        // axis entirely (maxToolSteps: null), the token trigger alone never crosses 1,200,000 on this
        // room's real 794,940-token total.
        var monitor = new TokenBudgetMonitor(budget: ShippedImplementBudget, maxToolSteps: null, new AgyUsageParser());

        foreach (var (inTokens, outTokens) in Room38c24d11Turns)
        {
            monitor.OnStdoutLine(AgyDoneLine(inTokens, outTokens));
        }

        Assert.False(monitor.Arrested);
        Assert.Equal(794_940, monitor.SnapshotUsage().BilledTokens);
        Assert.True(monitor.SnapshotUsage().BilledTokens < ShippedImplementBudget);
    }

    [Fact]
    public void DISCRIMINATING_the_real_monitor_at_the_old_cap_value_does_NOT_arrest_under_the_fixed_unit()
    {
        // #1686 review F8: the cheap, genuinely discriminating half the review's own DISCRIMINATING_
        // test above was missing -- that one reimplements the old unit inline and never touches
        // AgyUsageParser/TokenBudgetMonitor at all, so it would pass even if both classes were deleted.
        // This runs the REAL monitor (real AgyUsageParser.CountToolSteps, real TokenBudgetMonitor) over
        // the real interleaved replay at the OLD cap value (80), isolated from the budget axis. Under
        // the fixed unit the room made only 69 real calls, so even the pre-F2 cap number does not fire --
        // proving the unit fix, not the recalibrated cap, is what changed this replay's outcome.
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: PreF2ImplementMaxToolSteps, new AgyUsageParser());

        var arrestedAtLine = ReplayRoom38c24d11(monitor);

        Assert.False(monitor.Arrested);
        Assert.Equal(-1, arrestedAtLine);
        Assert.Equal(69, monitor.SnapshotToolStepCount());
    }

    [Fact]
    public void DISCRIMINATING_the_pre_F2_double_counted_unit_WOULD_have_arrested_this_replay_the_fixed_unit_does_not()
    {
        // #1686 review F1/F2/F3, a control so the F2 tradeoff is checkable in the suite rather than only
        // in prose: same real interleaved replay, but counting BOTH the ACTIVE and terminal lifecycle
        // line per real tool call (the OLD unit AgyUsageParser.CountToolSteps used before this PR)
        // against the OLD cap of 80 in that unit. This DOES arrest -- the old cap's catch on this room
        // was genuine, but only because of the double count, not because the underlying real-call rate
        // was actually high (spec/baton.md §3 has the retraction this proves).
        var oldUnitToolStepCount = 0;
        var arrestedAtLine = -1;
        var billedTokens = 0L;
        var billedAtArrest = -1L;
        var arrestedOnToolCap = false;

        for (var i = 0; i < Room38c24d11Turns.Length; i++)
        {
            var (inTokens, outTokens) = Room38c24d11Turns[i];
            billedTokens += inTokens + outTokens;

            if (!arrestedOnToolCap && i == Room38c24d11Turns.Length - 1)
            {
                break;
            }

            oldUnitToolStepCount += 2; // ACTIVE + terminal, the pre-#1686-F2 unit.
            if (!arrestedOnToolCap && oldUnitToolStepCount > PreF2ImplementMaxToolSteps)
            {
                arrestedOnToolCap = true;
                arrestedAtLine = i;
                billedAtArrest = billedTokens;
            }
        }

        Assert.True(arrestedOnToolCap);
        Assert.Equal(40, arrestedAtLine); // cumulative old-unit count crosses 81 mid-turn 41 (0-indexed 40).
        // spec/baton.md §3's own prior calibration figure, corroborated independently by the #1686
        // review's own arithmetic: cumulative billed at usage line 41 (index 40) is 439,385 -- well
        // under the room's own eventual 794,940, and under even the OLD 600,000 ceiling.
        Assert.Equal(439_385, billedAtArrest);
    }

    [Fact]
    public void RED_the_same_replay_does_NOT_arrest_at_any_point_under_the_pre_1682_level_based_reading()
    {
        // #1682's own root cause, reproduced directly: the pre-#1682 TokenBudgetMonitor tracked
        // (LEVEL of input+cache_read+cache_creation, REPLACED every turn) + (SUM of output), checked
        // for a crossing after EVERY line -- never SUM of input. Replaying the identical 70-turn stream
        // against that formula, turn by turn, must never cross 600,000 at ANY point, which is exactly
        // what let this real evidence room run away unarrested. TokenBudgetMonitor itself no longer
        // implements this formula (that IS the fix) -- reproduced inline here, turn by turn, as the red
        // control, since the class this replaces no longer exists in the tree to run directly.
        long sumOutput = 0;
        long maxTrackedAtAnyTurn = 0;
        foreach (var (inTokens, outTokens) in Room38c24d11Turns)
        {
            sumOutput += outTokens;
            var level = inTokens; // #1623: the input side was a LEVEL -- each new reading REPLACES it, never adds.
            var trackedThisTurn = level + sumOutput;
            maxTrackedAtAnyTurn = Math.Max(maxTrackedAtAnyTurn, trackedThisTurn);

            Assert.True(trackedThisTurn < SupersededPre1682AcceptanceBudget,
                $"pre-#1682 formula crossed the budget mid-replay at tracked={trackedThisTurn} -- the bug this fixture exists to demonstrate did not reproduce.");
        }

        // Pins the measured peak so a change to the fixture data is caught here too, not just by the
        // per-turn assertion above never firing.
        Assert.Equal(258_160, maxTrackedAtAnyTurn);
    }
}
