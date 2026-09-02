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

    private const long OldAndNewDefaultBudget = 600_000; // the pre-#1682, still-in-force `implement` default the issue's own test asks for

    private static string AgyDoneLine(long inputTokens, long outputTokens) =>
        "{\"event\":\"step_update\",\"step_update\":{\"state\":\"DONE\",\"step_type\":\"agent_response\",\"usage\":{"
        + "\"input_tokens\":" + inputTokens + ",\"output_tokens\":" + outputTokens
        + ",\"total_tokens\":" + (inputTokens + outputTokens) + "}}}";

    [Fact]
    public void GREEN_the_room_38c24d11_shaped_replay_arrests_before_600k_billed_under_the_new_reading()
    {
        var monitor = new TokenBudgetMonitor(budget: OldAndNewDefaultBudget, maxToolSteps: null, new AgyUsageParser());

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
        Assert.True(monitor.SnapshotUsage().BilledTokens >= OldAndNewDefaultBudget);
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

            Assert.True(trackedThisTurn < OldAndNewDefaultBudget,
                $"pre-#1682 formula crossed the budget mid-replay at tracked={trackedThisTurn} -- the bug this fixture exists to demonstrate did not reproduce.");
        }

        // Pins the measured peak so a change to the fixture data is caught here too, not just by the
        // per-turn assertion above never firing.
        Assert.Equal(258_160, maxTrackedAtAnyTurn);
    }
}
