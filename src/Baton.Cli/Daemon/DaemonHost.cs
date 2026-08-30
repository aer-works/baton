using Baton.Vendors;
using Baton.Concurrency;
using Baton.Status;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Baton.Cli.Daemon;

public static class DaemonHost
{
    public static Task RunDaemonAsync(string[] args) => RunDaemonAsync(args, onHostBuilt: null);

    /// <summary>Test-only seam (Baton.Cli.Tests, via <c>InternalsVisibleTo</c>): <paramref name="onHostBuilt"/>
    /// runs after the host is built but before <c>RunAsync</c>, so a test can inspect DI registrations and/or
    /// register a stop trigger. Without it, <c>RunAsync</c> blocks until an external process signal that never
    /// arrives in-process, so a test calling this method directly would hang forever.</summary>
    internal static async Task RunDaemonAsync(string[] args, Action<IHost>? onHostBuilt)
    {
        var noMutex = args.Contains("--no-mutex");
        Mutex? mutex = null;
        if (!noMutex)
        {
            var username = Environment.UserName;
            mutex = new Mutex(true, $"Global\\BatonDaemonMutex_{username}", out var createdNew);
            if (!createdNew)
            {
                Console.WriteLine("Another instance of the Baton daemon is already running.");
                mutex.Dispose();
                return;
            }
        }

        // Setup local data directory ~/.baton
        var batonDir = BatonPaths.Root;
        Directory.CreateDirectory(batonDir);

        // #1298: daemon-wide settings (currently just the concurrency caps) apply from the moment
        // the daemon comes up, before any room can dispatch a turn.
        var daemonSettings = await DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile);
        ConcurrencySlotGate.SetCaps(daemonSettings.GlobalConcurrencyCap, daemonSettings.PerVendorConcurrencyCap);

        var builder = Host.CreateApplicationBuilder(args);

        // #1025: room retention sweep (journal compaction)
        builder.Services.AddHostedService<RoomRetentionSweep>();

        var host = builder.Build();
        onHostBuilt?.Invoke(host);
        await host.RunAsync();
        mutex?.Dispose();
    }
}
