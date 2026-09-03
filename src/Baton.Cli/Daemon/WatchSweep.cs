using Baton.Status;
using Microsoft.Extensions.Hosting;

namespace Baton.Cli.Daemon;

/// <summary>
/// The sweep half of <c>baton watch</c> (#1488, spec/baton.md §2): fires every still-pending watch
/// whose room has since reached Terminal. Registered alongside <see cref="RoomRetentionSweep"/> as a
/// hosted service on the same <c>baton daemon</c> host — reusing that already-running process rather
/// than starting a second one, per the design note the issue itself calls for. Unlike
/// <see cref="RoomRetentionSweep"/>, this runs unconditionally (no <c>BATON_*_ENABLED</c> gate): a
/// registered watch that never fires because the operator forgot an env flag is exactly the silent
/// failure this feature exists to remove, and an empty or all-pending <see cref="BatonPaths.Watches"/>
/// directory makes each iteration cheap regardless.
/// </summary>
public sealed class WatchSweep : BackgroundService
{
    /// <summary>Deliberately much shorter than <see cref="RoomRetentionSweep.PlaceholderDefaultInterval"/>
    /// (5 minutes): a conductor waiting on this notification to resume is the entire point of the
    /// feature, so the poll cadence is tuned for "soon", not for housekeeping.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly IWatchNotifier _notifier;

    public WatchSweep()
        : this(new WatchNotifier())
    {
    }

    public WatchSweep(IWatchNotifier notifier)
    {
        _notifier = notifier;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WatchFireService.SweepAsync(BatonPaths.Watches, _notifier, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WatchSweep: sweep iteration failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
