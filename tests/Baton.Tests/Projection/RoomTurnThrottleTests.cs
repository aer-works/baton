using Baton.Projection;
using Baton.Tests.Shared;
using Xunit;

namespace Baton.Tests.Projection;

public class RoomTurnThrottleTests
{
    private static string CreateTempRoomDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_throttle_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    [Fact]
    public void Defaults_when_absent()
    {
        var roomDir = CreateTempRoomDir();
        try
        {
            var throttleLoad = RoomTurnThrottleStore.LoadWithDiagnostics(roomDir);
            var usageLoad = RoomTurnUsageStore.LoadWithDiagnostics(roomDir);

            Assert.Equal(TimeSpan.FromSeconds(60), throttleLoad.Throttles.MinMachineTurnInterval);
            Assert.Equal(10, throttleLoad.Throttles.MaxMachineTurnsPerHour);
            Assert.Equal(3, throttleLoad.Throttles.FailedTurnsBeforeDormancy);

            Assert.Empty(usageLoad.Usage.RecentMachineTurnTimestamps);
            Assert.Null(usageLoad.Usage.LastMachineTurnAt);
            Assert.Equal(0, usageLoad.Usage.ConsecutiveFailedTurns);

            // Absence is normal state -> no loud output
            Assert.Empty(throttleLoad.Warnings);
            Assert.Empty(usageLoad.Warnings);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public void Invalid_file_logs_loudly_and_returns_defaults()
    {
        var roomDir = CreateTempRoomDir();
        try
        {
            var throttlePath = RoomTurnThrottleStore.GetThrottleFilePath(roomDir);
            File.WriteAllText(throttlePath, "{ corrupt json ... }}}");

            var usagePath = RoomTurnUsageStore.GetUsageFilePath(roomDir);
            Directory.CreateDirectory(Path.GetDirectoryName(usagePath)!);
            File.WriteAllText(usagePath, "{ corrupt json ... }}}");

            var throttleLoad = RoomTurnThrottleStore.LoadWithDiagnostics(roomDir);
            var usageLoad = RoomTurnUsageStore.LoadWithDiagnostics(roomDir);

            Assert.Equal(RoomTurnThrottles.Default, throttleLoad.Throttles);
            Assert.Equal(RoomTurnUsage.Empty, usageLoad.Usage);

            Assert.Contains(throttleLoad.Warnings, w => w.Contains("[RoomTurnThrottles] Loud fallback to defaults"));
            Assert.Contains(usageLoad.Warnings, w => w.Contains("[RoomTurnUsage] Loud fallback to empty usage"));

            // Single file cleanup test (#918)
            FileCleanup.Delete(throttlePath);
            FileCleanup.Delete(usagePath);

            Assert.False(File.Exists(throttlePath));
            Assert.False(File.Exists(usagePath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public void Nonsensical_throttle_values_log_loudly_and_return_defaults()
    {
        var roomDir = CreateTempRoomDir();
        try
        {
            var throttlePath = RoomTurnThrottleStore.GetThrottleFilePath(roomDir);
            File.WriteAllText(throttlePath, """
            {
              "minMachineTurnIntervalSeconds": 0,
              "maxMachineTurnsPerHour": -5,
              "failedTurnsBeforeDormancy": 0
            }
            """);

            var loaded = RoomTurnThrottleStore.LoadWithDiagnostics(roomDir);

            Assert.Equal(RoomTurnThrottles.Default, loaded.Throttles);
            Assert.Contains(loaded.Warnings, w => w.Contains("non-positive values"));

            FileCleanup.Delete(throttlePath);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public void Each_cap_refuses_machine_turn_with_its_named_reason()
    {
        var throttles = RoomTurnThrottles.Default;
        var now = DateTimeOffset.UtcNow;

        // MinInterval refusal
        var usageMinInterval = RoomTurnUsage.Empty.RecordMachineTurnStarted(now);
        var permMinInterval = RoomTurnDecider.Decide(throttles, usageMinInterval, TurnWakeSource.Machine, now.AddSeconds(30));
        Assert.False(permMinInterval.IsAllowed);
        Assert.Equal(TurnRefusalReason.MinInterval, permMinInterval.RefusalReason);

        // HourlyCap refusal
        var usageHourly = RoomTurnUsage.Empty;
        for (int i = 0; i < 10; i++)
        {
            usageHourly = usageHourly.RecordMachineTurnStarted(now.AddMinutes(-30 + i));
        }
        var permHourly = RoomTurnDecider.Decide(throttles, usageHourly, TurnWakeSource.Machine, now);
        Assert.False(permHourly.IsAllowed);
        Assert.Equal(TurnRefusalReason.HourlyCap, permHourly.RefusalReason);

        // Dormant refusal
        var usageDormant = RoomTurnUsage.Empty.RecordTurnFailed().RecordTurnFailed().RecordTurnFailed();
        var permDormant = RoomTurnDecider.Decide(throttles, usageDormant, TurnWakeSource.Machine, now);
        Assert.False(permDormant.IsAllowed);
        Assert.Equal(TurnRefusalReason.Dormant, permDormant.RefusalReason);
    }

    [Fact]
    public void User_wake_bypasses_interval_and_hourly_caps()
    {
        var throttles = RoomTurnThrottles.Default;
        var now = DateTimeOffset.UtcNow;

        var maxedUsage = RoomTurnUsage.Empty.RecordMachineTurnStarted(now);
        for (int i = 1; i < 10; i++)
        {
            maxedUsage = maxedUsage.RecordMachineTurnStarted(now.AddSeconds(i * 10));
        }

        // Machine turn is refused due to min interval
        var machinePerm = RoomTurnDecider.Decide(throttles, maxedUsage, TurnWakeSource.Machine, now.AddSeconds(5));
        Assert.False(machinePerm.IsAllowed);

        // User turn bypasses both caps
        var userPerm = RoomTurnDecider.Decide(throttles, maxedUsage, TurnWakeSource.UserMessage, now.AddSeconds(5));
        Assert.True(userPerm.IsAllowed);
        Assert.Null(userPerm.RefusalReason);
    }

    [Fact]
    public void Breaker_threshold_trips_dormancy_for_user_turns_too()
    {
        var throttles = RoomTurnThrottles.Default; // 3 failed turns -> dormancy
        var now = DateTimeOffset.UtcNow;

        var dormantUsage = RoomTurnUsage.Empty
            .RecordTurnFailed()
            .RecordTurnFailed()
            .RecordTurnFailed();

        var userPerm = RoomTurnDecider.Decide(throttles, dormantUsage, TurnWakeSource.UserMessage, now);
        Assert.False(userPerm.IsAllowed);
        Assert.Equal(TurnRefusalReason.Dormant, userPerm.RefusalReason);
    }

    [Fact]
    public void Counters_roll_the_hour_correctly()
    {
        var now = DateTimeOffset.UtcNow;
        var usage = RoomTurnUsage.Empty;

        // Record a turn 65 minutes ago and a turn 10 minutes ago
        usage = usage.RecordMachineTurnStarted(now.AddMinutes(-65));
        usage = usage.RecordMachineTurnStarted(now.AddMinutes(-10));

        // Turn from 65m ago rolled out; only turn from 10m ago is in the hour window
        Assert.Equal(1, usage.GetMachineTurnsThisHour(now));

        // When a new turn starts at now
        usage = usage.RecordMachineTurnStarted(now);
        Assert.Equal(2, usage.GetMachineTurnsThisHour(now));
    }

    [Fact]
    public void Atomic_counter_write_and_round_trip()
    {
        var roomDir = CreateTempRoomDir();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var usage = RoomTurnUsage.Empty
                .RecordMachineTurnStarted(now)
                .RecordTurnFailed();

            RoomTurnUsageStore.Save(roomDir, usage);

            var loaded = RoomTurnUsageStore.Load(roomDir);
            Assert.Equal(1, loaded.ConsecutiveFailedTurns);
            Assert.Equal(1, loaded.GetMachineTurnsThisHour(now));
            Assert.NotNull(loaded.LastMachineTurnAt);
            Assert.Equal(now.ToUnixTimeSeconds(), loaded.LastMachineTurnAt!.Value.ToUnixTimeSeconds());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public void Boundaries_sit_exactly_where_the_addendum_numbers_say()
    {
        // The addendum's exact numbers (60s / 10 / 3) invite off-by-one bugs at precisely these
        // three edges (#778 review) -- each pinned on its boundary, both sides where they differ.
        var now = DateTimeOffset.UtcNow;
        var throttles = RoomTurnThrottles.Default;

        // Exactly at the min interval: allowed ("60s min" means >= 60s is fine).
        var atInterval = RoomTurnUsage.Empty with { LastMachineTurnAt = now - TimeSpan.FromSeconds(60) };
        Assert.True(RoomTurnDecider.Decide(throttles, atInterval, TurnWakeSource.Machine, now).IsAllowed);

        // One tick under: refused.
        var underInterval = RoomTurnUsage.Empty with { LastMachineTurnAt = now - TimeSpan.FromSeconds(59) };
        Assert.Equal(
            TurnRefusalReason.MinInterval,
            RoomTurnDecider.Decide(throttles, underInterval, TurnWakeSource.Machine, now).RefusalReason);

        // A turn exactly one hour old has left the rolling window; 9 in-window turns + it = still
        // under the cap of 10, so the 10th fresh turn is allowed.
        var oneHourOld = now - TimeSpan.FromHours(1);
        var nineRecentPlusExpired = RoomTurnUsage.Empty with
        {
            RecentMachineTurnTimestamps =
                Enumerable.Range(1, 9).Select(i => now - TimeSpan.FromMinutes(i)).Append(oneHourOld).ToList().AsReadOnly(),
            LastMachineTurnAt = now - TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(-1),
        };
        Assert.Equal(9, nineRecentPlusExpired.GetMachineTurnsThisHour(now));

        // The 10th in-window turn fills the cap: an 11th is refused.
        var tenRecent = RoomTurnUsage.Empty with
        {
            RecentMachineTurnTimestamps =
                Enumerable.Range(1, 10).Select(i => now - TimeSpan.FromMinutes(i + 10)).ToList().AsReadOnly(),
            LastMachineTurnAt = now - TimeSpan.FromMinutes(11),
        };
        Assert.Equal(
            TurnRefusalReason.HourlyCap,
            RoomTurnDecider.Decide(throttles, tenRecent, TurnWakeSource.Machine, now).RefusalReason);

        // Two failures: not yet dormant. Three: dormant.
        Assert.True(RoomTurnDecider.Decide(
            throttles, RoomTurnUsage.Empty with { ConsecutiveFailedTurns = 2 }, TurnWakeSource.Machine, now).IsAllowed);
        Assert.Equal(
            TurnRefusalReason.Dormant,
            RoomTurnDecider.Decide(
                throttles, RoomTurnUsage.Empty with { ConsecutiveFailedTurns = 3 }, TurnWakeSource.Machine, now).RefusalReason);
    }

    [Fact]
    public void A_partial_throttle_file_overrides_only_the_field_it_names()
    {
        var roomDir = CreateTempRoomDir();
        try
        {
            File.WriteAllText(
                Path.Combine(roomDir, RoomTurnThrottleStore.ThrottleFileName),
                """{ "maxMachineTurnsPerHour": 5 }""");

            var loaded = RoomTurnThrottleStore.LoadWithDiagnostics(roomDir);

            Assert.Equal(5, loaded.Throttles.MaxMachineTurnsPerHour);
            Assert.Equal(TimeSpan.FromSeconds(60), loaded.Throttles.MinMachineTurnInterval);
            Assert.Equal(3, loaded.Throttles.FailedTurnsBeforeDormancy);
            // Deliberately partial = deliberately silent: overriding one knob is the operator's
            // normal move, not a fault.
            Assert.Empty(loaded.Warnings);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public void A_misspelled_throttle_key_is_loudly_named_not_silently_ignored()
    {
        var roomDir = CreateTempRoomDir();
        try
        {
            File.WriteAllText(
                Path.Combine(roomDir, RoomTurnThrottleStore.ThrottleFileName),
                """{ "minMachineTurnInterval": 120 }""");

            var loaded = RoomTurnThrottleStore.LoadWithDiagnostics(roomDir);

            // The typo'd key changed nothing -- and said so.
            Assert.Equal(TimeSpan.FromSeconds(60), loaded.Throttles.MinMachineTurnInterval);
            Assert.Contains(loaded.Warnings, w => w.Contains("minMachineTurnInterval") && w.Contains("IGNORED"));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public void An_inconsistent_usage_file_is_reconciled_loudly_toward_the_newest_turn()
    {
        var roomDir = CreateTempRoomDir();
        try
        {
            var newest = DateTimeOffset.UtcNow;
            Directory.CreateDirectory(Path.Combine(roomDir, ".baton"));
            File.WriteAllText(
                Path.Combine(roomDir, ".baton", "turn-usage.json"),
                $$"""{ "recentMachineTurnTimestamps": ["{{newest:O}}"], "lastMachineTurnAt": null, "consecutiveFailedTurns": 0 }""");

            var loaded = RoomTurnUsageStore.LoadWithDiagnostics(roomDir);

            Assert.NotNull(loaded.Usage.LastMachineTurnAt);
            Assert.Equal(newest.ToUnixTimeSeconds(), loaded.Usage.LastMachineTurnAt!.Value.ToUnixTimeSeconds());
            Assert.Contains(loaded.Warnings, w => w.Contains("RECONCILED"));

            // The reconciled state decides conservatively: a machine turn right now is refused
            // on the interval, exactly as if the two fields had agreed all along.
            Assert.Equal(
                TurnRefusalReason.MinInterval,
                RoomTurnDecider.Decide(
                    RoomTurnThrottles.Default, loaded.Usage, TurnWakeSource.Machine, newest.AddSeconds(5)).RefusalReason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public void Throttle_settings_round_trip_through_save_and_load()
    {
        var roomDir = CreateTempRoomDir();
        try
        {
            var custom = new RoomTurnThrottles(TimeSpan.FromSeconds(90), 4, 2);
            RoomTurnThrottleStore.Save(roomDir, custom);

            var loaded = RoomTurnThrottleStore.Load(roomDir);
            Assert.Equal(custom, loaded);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }
}
