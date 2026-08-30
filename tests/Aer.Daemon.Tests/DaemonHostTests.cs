using Aer.Adapters;
using Aer.Daemon;
using Aer.Flow.Concurrency;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aer.Daemon.Tests;

/// <summary>
/// #1425: <see cref="DaemonHost.RunDaemonAsync(string[])"/> had no direct coverage of the three kept
/// startup behaviors -- the second-reader review of #1423's narrowing flagged it. Each test isolates
/// AER's storage root via <see cref="AerPaths.HomeEnvironmentVariable"/> (the seam that type's own doc
/// comment names for exactly this: "per-run test isolation (#318)") so it never touches the real
/// <c>~/.aer</c>, and resets <see cref="ConcurrencySlotGate"/>'s process-static caps to their documented
/// defaults before every test the same way <c>ConcurrencySlotGateTests</c> resets full state, since
/// xunit runs one class's methods sequentially by default (one collection per class) -- what makes that
/// safe here too. This project has no <c>InternalsVisibleTo</c> grant from Aer.Flow, so it goes through
/// the public <see cref="ConcurrencySlotGate.SetCaps"/> rather than that type's test-only internal reset;
/// these tests never call <c>AcquireAsync</c>, so only the two cap values need resetting.
/// </summary>
public class DaemonHostTests
{
    public DaemonHostTests() => ConcurrencySlotGate.SetCaps(ConcurrencySlotGate.DefaultGlobalCap, ConcurrencySlotGate.DefaultPerVendorCap);

    private static string CreateTempHome()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "aer_daemon_host_test_" + Guid.NewGuid().ToString("n"));
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
        var mutexName = $"Global\\AerDaemonMutex_{Environment.UserName}";
        using var heldByAnotherInstance = new Mutex(true, mutexName, out var thisTestOwnsIt);
        Assert.True(thisTestOwnsIt); // sanity: nothing else on this machine already holds it

        // Second-instance detection happens before Host.CreateApplicationBuilder/RunAsync, so a refused
        // instance must return promptly -- it must NOT fall through to host.RunAsync(), which blocks
        // forever absent an external stop signal. The timeout is a hang backstop, not an expected wait:
        // if the refusal branch regressed, this call would otherwise hang the whole test run.
        await DaemonHost.RunDaemonAsync(args: [])
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunDaemonAsync_LoadsSettingsBeforeApplyingCaps_SoSetCapsSeesTheFileNotDefaults()
    {
        var tempHome = CreateTempHome();
        var priorHome = Environment.GetEnvironmentVariable(AerPaths.HomeEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AerPaths.HomeEnvironmentVariable, tempHome);

            // Neither value is a default (3/2, see DaemonSettings/ConcurrencySlotGate) -- if RunDaemonAsync
            // called SetCaps before LoadAsync resolved (or dropped the load entirely), GlobalCap/PerVendorCap
            // would read the defaults below instead of these.
            var settings = new DaemonSettings { GlobalConcurrencyCap = 7, PerVendorConcurrencyCap = 5 };
            await DaemonSettingsStore.SaveAsync(settings, AerPaths.SettingsFile, TestContext.Current.CancellationToken);

            await DaemonHost.RunDaemonAsync(["--no-mutex"], StopAsSoonAsStarted)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Equal(7, ConcurrencySlotGate.GlobalCap);
            Assert.Equal(5, ConcurrencySlotGate.PerVendorCap);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AerPaths.HomeEnvironmentVariable, priorHome);
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
        var priorHome = Environment.GetEnvironmentVariable(AerPaths.HomeEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AerPaths.HomeEnvironmentVariable, tempHome);

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
                }).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Contains(hostedServices!, s => s is RoomRetentionSweep);
            Assert.True(applicationStarted);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AerPaths.HomeEnvironmentVariable, priorHome);
            if (Directory.Exists(tempHome))
            {
                Directory.Delete(tempHome, true);
            }
        }
    }
}
