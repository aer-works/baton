using Baton.Vendors;
using Baton.Cli.Daemon;
using Baton.Concurrency;
using Baton.Status;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// #1425: <see cref="DaemonHost.RunDaemonAsync(string[])"/> had no direct coverage of the three kept
/// startup behaviors -- the second-reader review of #1423's narrowing flagged it. The two tests that
/// need an isolated storage root use <see cref="BatonEnvironmentSnapshot.BeginScope"/> (#1496: this
/// project has an IVT grant from Baton for exactly that seam) so they never touch the real
/// <c>~/.baton</c> and never mutate process environment -- before #1496 they redirected
/// <see cref="BatonPaths.HomeEnvironmentVariable"/> directly, which stopped working once
/// <see cref="BatonPaths.Root"/> started resolving through a frozen, process-wide snapshot instead of
/// re-reading the environment per access. Every test also resets <see cref="ConcurrencySlotGate"/>'s
/// process-static caps to their documented defaults before running, the same way
/// <c>ConcurrencySlotGateTests</c> resets full state, since xunit runs one class's methods sequentially
/// by default (one collection per class) -- what makes that safe here too. This goes through the
/// public <see cref="ConcurrencySlotGate.SetCaps"/> rather than that type's test-only internal reset,
/// which this project has no IVT grant for; these tests never call <c>AcquireAsync</c>, so only the
/// two cap values need resetting.
/// </summary>
public class DaemonHostTests
{
    public DaemonHostTests() => ConcurrencySlotGate.SetCaps(ConcurrencySlotGate.DefaultGlobalCap, ConcurrencySlotGate.DefaultPerVendorCap);

    private static string CreateTempHome()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "baton_daemon_host_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempHome);
        return tempHome;
    }

    /// <summary>Registers a stop trigger the moment the host finishes starting -- <see cref="IHostApplicationLifetime.ApplicationStarted"/>
    /// only fires once every registered <see cref="IHostedService"/>'s StartAsync has returned, so this
    /// is the earliest point a test can stop the host without racing its own startup.</summary>
    private static void StopAsSoonAsStarted(IHost host)
    {
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Register(() => lifetime.StopApplication());
    }

    [Fact]
    public async Task RunDaemonAsync_SecondInstance_RefusesWithoutBuildingOrRunningAHost()
    {
        var mutexName = $"Global\\BatonDaemonMutex_{Environment.UserName}";
        using var heldByAnotherInstance = new Mutex(true, mutexName, out var thisTestOwnsIt);
        Assert.True(thisTestOwnsIt); // sanity: nothing else on this machine already holds it

        // Second-instance detection happens before Host.CreateApplicationBuilder/RunAsync, so a refused
        // instance must return promptly -- it must NOT fall through to host.RunAsync(), which blocks
        // forever absent an external stop signal. The timeout is a hang backstop, not an expected wait:
        // if the refusal branch regressed, this call would otherwise hang the whole test run.
        await DaemonHost.RunDaemonAsync(args: [])
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunDaemonAsync_LoadsSettingsBeforeApplyingCaps_SoSetCapsSeesTheFileNotDefaults()
    {
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            // Neither value is a default (3/2, see DaemonSettings/ConcurrencySlotGate) -- if RunDaemonAsync
            // called SetCaps before LoadAsync resolved (or dropped the load entirely), GlobalCap/PerVendorCap
            // would read the defaults below instead of these.
            var settings = new DaemonSettings { GlobalConcurrencyCap = 7, PerVendorConcurrencyCap = 5 };
            await DaemonSettingsStore.SaveAsync(settings, BatonPaths.SettingsFile, TestContext.Current.CancellationToken);

            await DaemonHost.RunDaemonAsync(["--no-mutex"], StopAsSoonAsStarted)
                .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

            Assert.Equal(7, ConcurrencySlotGate.GlobalCap);
            Assert.Equal(5, ConcurrencySlotGate.PerVendorCap);
        }
        finally
        {
            if (Directory.Exists(tempHome))
            {
                Directory.Delete(tempHome, true);
            }
        }
    }

    [Fact]
    public async Task RunDaemonAsync_RegistersRoomRetentionSweepAsAHostedServiceAndStartsIt()
    {
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            List<IHostedService>? hostedServices = null;
            var applicationStarted = false;

            await DaemonHost.RunDaemonAsync(
                ["--no-mutex"],
                host =>
                {
                    // Resolved from the built container: proves RoomRetentionSweep is actually registered
                    // in DI, not merely referenced somewhere in RunDaemonAsync's source.
                    hostedServices = [.. host.Services.GetServices<IHostedService>()];

                    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
                    lifetime.ApplicationStarted.Register(() =>
                    {
                        // ApplicationStarted fires only after every registered IHostedService's StartAsync
                        // has returned, so reaching this callback is itself proof RoomRetentionSweep (a
                        // BackgroundService) was started, not just added to the collection above.
                        applicationStarted = true;
                        lifetime.StopApplication();
                    });
                }).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

            Assert.Contains(hostedServices!, s => s is RoomRetentionSweep);
            Assert.True(applicationStarted);
        }
        finally
        {
            if (Directory.Exists(tempHome))
            {
                Directory.Delete(tempHome, true);
            }
        }
    }

    /// <summary>#1488: <see cref="WatchSweep"/> registered the same way as
    /// <see cref="RoomRetentionSweep"/> above — on the same daemon host, not a second process.</summary>
    [Fact]
    public async Task RunDaemonAsync_RegistersWatchSweepAsAHostedServiceAndStartsIt()
    {
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            List<IHostedService>? hostedServices = null;
            var applicationStarted = false;

            await DaemonHost.RunDaemonAsync(
                ["--no-mutex"],
                host =>
                {
                    hostedServices = [.. host.Services.GetServices<IHostedService>()];

                    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
                    lifetime.ApplicationStarted.Register(() =>
                    {
                        applicationStarted = true;
                        lifetime.StopApplication();
                    });
                }).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

            Assert.Contains(hostedServices!, s => s is WatchSweep);
            Assert.True(applicationStarted);
        }
        finally
        {
            if (Directory.Exists(tempHome))
            {
                Directory.Delete(tempHome, true);
            }
        }
    }
}
