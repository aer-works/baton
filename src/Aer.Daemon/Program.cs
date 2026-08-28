using System.Threading;
using Aer.Adapters;
using Aer.Flow.Concurrency;
using Aer.Flow.Status;
using Microsoft.Extensions.Hosting;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Aer.Daemon.Tests")]

await Aer.Daemon.DaemonHost.RunDaemonAsync(args);

namespace Aer.Daemon
{
    public static class DaemonHost
    {
        public static async Task RunDaemonAsync(string[] args)
        {
            var noMutex = args.Contains("--no-mutex");
            Mutex? mutex = null;
            if (!noMutex)
            {
                var username = Environment.UserName;
                mutex = new Mutex(true, $"Global\\AerDaemonMutex_{username}", out var createdNew);
                if (!createdNew)
                {
                    Console.WriteLine("Another instance of Aer.Daemon is already running.");
                    mutex.Dispose();
                    return;
                }
            }

            // Setup local data directory ~/.aer
            var aerDir = AerPaths.Root;
            Directory.CreateDirectory(aerDir);

            // #1298: daemon-wide settings (currently just the concurrency caps) apply from the moment
            // the daemon comes up, before any room can dispatch a turn.
            var daemonSettings = await DaemonSettingsStore.LoadAsync(AerPaths.SettingsFile);
            ConcurrencySlotGate.SetCaps(daemonSettings.GlobalConcurrencyCap, daemonSettings.PerVendorConcurrencyCap);

            var builder = Host.CreateApplicationBuilder(args);

            // #1025: room retention sweep (journal compaction)
            builder.Services.AddHostedService<RoomRetentionSweep>();

            var host = builder.Build();
            await host.RunAsync();
            mutex?.Dispose();
        }
    }
}
