using System.Globalization;
using System.Text.Json;
using Baton.Cli.Mcp;
using Baton.Domain;
using Baton.Status;
using Baton.Store;
using Microsoft.Extensions.Hosting;

namespace Baton.Cli.Daemon;

/// <summary>
/// #734 (spec/baton.md §7): a slow-cadence, outbound-only <c>gh</c> poll of every room whose declared
/// outputs resolve a <see cref="DeliveryReference"/> with a PR number
/// (<see cref="DeliveryReferenceOutputNames"/> has the two recognized names). Records
/// <see cref="FlowEvent.DeliveryPrOpened"/>/<see cref="FlowEvent.DeliveryChecksGreen"/>/
/// <see cref="FlowEvent.DeliveryChecksRed"/>/<see cref="FlowEvent.DeliveryMerged"/> as room facts and
/// nothing else — it never merges, retries, comments, or otherwise acts on what it observes (spec/baton.md
/// §7's "facts, never actions" rule). Registered alongside <see cref="WatchSweep"/>/
/// <see cref="FleetProjectionWriter"/> on the same daemon host. Runs unconditionally, no
/// <c>BATON_*_ENABLED</c> gate, matching <see cref="WatchSweep"/>'s own reasoning: a room with no
/// resolved delivery output makes each iteration cheap regardless.
/// </summary>
public sealed class DeliveryPoller : BackgroundService
{
    public const string IntervalSecondsEnvironmentVariable = "BATON_DELIVERY_POLL_INTERVAL_SECONDS";

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    // Same overflow/hot-loop rationale as RoomRetentionSweep.MinInterval/MaxInterval: the upper bound
    // keeps a pathological env value from overflowing TimeSpan.FromSeconds, the lower bound keeps a
    // typo from hot-looping a poll that hits the network on every iteration.
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromDays(1);

    private static readonly HashSet<string> FailingConclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "FAILURE", "CANCELLED", "TIMED_OUT", "ACTION_REQUIRED", "STARTUP_FAILURE",
    };

    private readonly IGhCliRunner _gh;
    private bool _ghUnavailableWarned;

    public DeliveryPoller()
        : this(new GhCliRunner())
    {
    }

    public DeliveryPoller(IGhCliRunner gh)
    {
        _gh = gh;
    }

    public static TimeSpan GetInterval()
    {
        var val = BatonEnvironmentSnapshot.Current.DeliveryPollIntervalSecondsOverride;
        if (!string.IsNullOrWhiteSpace(val) &&
            double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds) &&
            seconds > 0)
        {
            return TimeSpan.FromSeconds(Math.Clamp(seconds, MinInterval.TotalSeconds, MaxInterval.TotalSeconds));
        }

        return DefaultInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"DeliveryPoller: sweep iteration failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(GetInterval(), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One tick's worth of work over every discovered room — public entry point for tests.</summary>
    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        var discovered = await FleetStatusTool.DiscoverRoomsAsync([], cancellationToken).ConfigureAwait(false);
        foreach (var room in discovered)
        {
            try
            {
                await PollRoomAsync(room, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"DeliveryPoller: room '{room.RoomDir}' failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// One room's worth of work — internal so tests can drive a single fixture directly rather than
    /// standing up a full fleet scan. <paramref name="warningSink"/> defaults to <see cref="Console.Error"/>;
    /// a test supplies its own to avoid the shared-stream race a global <c>Console.Error</c> capture
    /// invites across parallel tests (the same reason <c>RoomRetentionSweep.PruneRoomAsync</c> takes
    /// one).
    /// </summary>
    internal async Task PollRoomAsync(
        FleetStatusTool.DiscoveredRoom room, CancellationToken cancellationToken, TextWriter? warningSink = null)
    {
        var view = await FleetStatusTool.ProcessRoomAsync(room.RoomDir, includeTerminal: true, cancellationToken)
            .ConfigureAwait(false);
        if (view is null)
        {
            return;
        }

        var reference = DeliveryReferenceResolver.Resolve(view.Outputs);
        if (reference?.PullRequestNumber is not { } pullRequestNumber)
        {
            // No declared delivery output resolved yet (or only a branch, no PR number yet) -- the
            // poller never starts for this room until a PR number is there to poll against.
            return;
        }

        var logPath = Path.Combine(room.RoomDir, BatonPaths.FlowLogFileName);
        var events = await new FlowEventLogReader(logPath).ReadAllAsync(cancellationToken).ConfigureAwait(false);

        if (events.Any(e => e is FlowEvent.DeliveryMerged))
        {
            // Terminal: merged or closed-unmerged already recorded once. Never polled again.
            return;
        }

        if (room.Project is null)
        {
            // No registered project root for this room (spec/baton.md §8) -- `gh` has no repo
            // context to run in without one, and this poller never guesses at a cwd.
            return;
        }

        var result = await _gh.RunAsync(
                room.Project,
                ["pr", "view", pullRequestNumber.ToString(CultureInfo.InvariantCulture), "--json", "state,mergedAt,statusCheckRollup"],
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Started || result.ExitCode != 0)
        {
            if (!_ghUnavailableWarned)
            {
                _ghUnavailableWarned = true;
                var why = result.Started ? result.Stderr.Trim() : "gh was not found on PATH";
                (warningSink ?? Console.Error).WriteLine(
                    $"DeliveryPoller: 'gh pr view' unavailable ({why}) -- delivery polling is disabled "
                    + "until it is. A missing or unauthenticated forge must not fail the daemon. "
                    + "Reported once per daemon process.");
            }

            return;
        }

        var observed = ParsePrView(result.Stdout);
        if (observed is null)
        {
            return;
        }

        var alreadyOpened = events.Any(
            e => e is FlowEvent.DeliveryPrOpened opened && opened.PullRequestNumber == pullRequestNumber);
        var lastChecksState = events.LastOrDefault(
            e => (e is FlowEvent.DeliveryChecksGreen green && green.PullRequestNumber == pullRequestNumber)
                || (e is FlowEvent.DeliveryChecksRed red && red.PullRequestNumber == pullRequestNumber));

        var toAppend = new List<FlowEvent>();
        if (!alreadyOpened)
        {
            toAppend.Add(new FlowEvent.DeliveryPrOpened(pullRequestNumber, reference.Branch));
        }

        if (observed.Checks == DeliveryCheckState.Green && lastChecksState is not FlowEvent.DeliveryChecksGreen)
        {
            toAppend.Add(new FlowEvent.DeliveryChecksGreen(pullRequestNumber));
        }
        else if (observed.Checks == DeliveryCheckState.Red && lastChecksState is not FlowEvent.DeliveryChecksRed)
        {
            toAppend.Add(new FlowEvent.DeliveryChecksRed(pullRequestNumber));
        }

        if (observed.Merged is { } merged)
        {
            toAppend.Add(new FlowEvent.DeliveryMerged(pullRequestNumber, merged));
        }

        if (toAppend.Count == 0)
        {
            return;
        }

        await using var writer = new FlowEventLogWriter(logPath);
        foreach (var flowEvent in toAppend)
        {
            await writer.AppendAsync(flowEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    private enum DeliveryCheckState { Pending, Green, Red }

    private sealed record ObservedPrState(DeliveryCheckState Checks, bool? Merged);

    /// <summary>
    /// Parses <c>gh pr view --json state,mergedAt,statusCheckRollup</c>'s stdout. Lenient by design:
    /// an unrecognized or empty shape reads as Pending/not-terminal rather than throwing, since a
    /// malformed response this tick is retried next tick regardless.
    /// </summary>
    private static ObservedPrState? ParsePrView(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            bool? merged = null;
            if (root.TryGetProperty("state", out var stateElem) && stateElem.ValueKind == JsonValueKind.String)
            {
                var state = stateElem.GetString();
                if (string.Equals(state, "MERGED", StringComparison.OrdinalIgnoreCase))
                {
                    merged = true;
                }
                else if (string.Equals(state, "CLOSED", StringComparison.OrdinalIgnoreCase))
                {
                    merged = false;
                }
            }

            var checks = DeliveryCheckState.Pending;
            if (root.TryGetProperty("statusCheckRollup", out var rollup)
                && rollup.ValueKind == JsonValueKind.Array
                && rollup.GetArrayLength() > 0)
            {
                var sawFailure = false;
                var allComplete = true;
                foreach (var check in rollup.EnumerateArray())
                {
                    var conclusion = check.TryGetProperty("conclusion", out var c) ? c.GetString() : null;
                    var status = check.TryGetProperty("status", out var s) ? s.GetString() : null;

                    if (conclusion is not null && FailingConclusions.Contains(conclusion))
                    {
                        sawFailure = true;
                    }

                    if (!string.IsNullOrEmpty(status) && !string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
                    {
                        allComplete = false;
                    }
                }

                checks = sawFailure ? DeliveryCheckState.Red
                    : allComplete ? DeliveryCheckState.Green
                    : DeliveryCheckState.Pending;
            }

            return new ObservedPrState(checks, merged);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
