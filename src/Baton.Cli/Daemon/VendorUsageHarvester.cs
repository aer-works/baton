using System.Text.Json;
using Baton.Cli.Mcp;
using Baton.Status;
using Baton.Vendors;
using Microsoft.Extensions.Hosting;

namespace Baton.Cli.Daemon;

/// <summary>
/// Issue #1391's daemon-side harvester: reads each vendor's own headless <c>/usage</c> report on the
/// cadence <see cref="VendorUsageHarvestScheduler"/> decides, and persists the latest snapshot per
/// vendor to <see cref="BatonPaths.VendorUsageSnapshotFile"/> — that property's own doc comment has
/// the restart-survival reasoning. Advisory only — nothing here gates dispatch (#1848 owns that);
/// this type only ever reads and writes, never blocks a worker.
/// </summary>
/// <remarks>
/// Live-lane counts are read from the SAME room scan <see cref="FleetStatusTool.DiscoverRoomsAsync"/>/
/// <see cref="FleetStatusTool.ProcessRoomAsync"/> already do for <c>fleet_status</c> and
/// <see cref="FleetProjectionWriter"/> — a second, independent scan on this service's own tick rather
/// than threaded through from <see cref="FleetProjectionWriter"/>'s tick, so the two background
/// services stay decoupled (one's failure or interval change cannot affect the other's cadence).
/// </remarks>
public sealed class VendorUsageHarvester : BackgroundService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PeriodicInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Jitter = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan PostExitDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(60);

    private readonly IReadOnlyList<IVendorUsageSource> _sources;
    private readonly VendorUsageHarvestScheduler _scheduler;

    public VendorUsageHarvester()
        : this([new ClaudeUsageSlashCommandSource(), new AgyUsageSlashCommandSource()])
    {
    }

    internal VendorUsageHarvester(IReadOnlyList<IVendorUsageSource> sources, VendorUsageHarvestScheduler? scheduler = null)
    {
        _sources = sources;
        _scheduler = scheduler ?? new VendorUsageHarvestScheduler(PeriodicInterval, Jitter, PostExitDelay, CoalesceWindow);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickOnceAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"VendorUsageHarvester: iteration failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One tick's worth of work — public entry point for tests, and what <see cref="ExecuteAsync"/> loops.</summary>
    internal async Task TickOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var liveLanesByVendor = await CountLiveLanesByVendorAsync(cancellationToken).ConfigureAwait(false);

        foreach (var source in _sources)
        {
            var anyLive = liveLanesByVendor.TryGetValue(source.Vendor, out var count) && count > 0;
            if (!_scheduler.OnTick(source.Vendor, now, anyLive))
            {
                continue;
            }

            VendorUsageSnapshot? snapshot;
            try
            {
                snapshot = await source.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"VendorUsageHarvester: harvest failed for {source.Vendor}: {ex.Message}");
                continue;
            }

            if (snapshot is null)
            {
                continue;
            }

            Persist(source.Vendor, snapshot);
        }
    }

    private static async Task<Dictionary<string, int>> CountLiveLanesByVendorAsync(CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var discovered = await FleetStatusTool.DiscoverRoomsAsync([], cancellationToken).ConfigureAwait(false);
        foreach (var room in discovered)
        {
            var view = await FleetStatusTool.ProcessRoomAsync(room.RoomDir, includeTerminal: false, cancellationToken)
                .ConfigureAwait(false);
            if (view is null || view.State != "Running" || view.Adapter is not { } adapter)
            {
                continue;
            }

            counts[adapter] = counts.GetValueOrDefault(adapter) + 1;
        }

        return counts;
    }

    /// <summary>
    /// Serializes with the DEFAULT (PascalCase) options -- this file is machine-local persisted state
    /// this same process reads back (<see cref="VendorUsageProjectionReader"/>), never a wire contract,
    /// so it does not need the lowerCamelCase <c>JsonPropertyName</c> shape the fleet projection's own
    /// <c>vendors[]</c> block uses.
    /// </summary>
    private static void Persist(string vendor, VendorUsageSnapshot snapshot)
    {
        try
        {
            var path = BatonPaths.VendorUsageSnapshotFile(vendor);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(snapshot);
            var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"VendorUsageHarvester: failed to persist snapshot for {vendor}: {ex.Message}");
        }
    }
}
