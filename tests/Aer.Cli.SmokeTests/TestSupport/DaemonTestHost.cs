using Aer.Adapters;
using Aer.Daemon;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Aer.Cli.SmokeTests.TestSupport;

/// <summary>
/// Starts a real <see cref="DaemonHost"/> instance in-process on an OS-assigned dynamic port
/// (<c>--port 0</c>) and resolves the actual bound port straight off the running
/// <see cref="DaemonHost.App"/>'s <see cref="IServer"/>, rather than a hardcoded port like
/// <c>5099</c> (issue #296): a fixed port collides whenever two test runs happen to overlap — two
/// worktrees, or a local run overlapping CI — since both bind the identical address. Reading the
/// port back off this process's own <see cref="Microsoft.AspNetCore.Builder.WebApplication"/> also
/// sidesteps a second, subtler collision the daemon's own <c>daemon.port</c> discovery file would
/// reintroduce: that file lives at a single fixed per-user path (<c>~/.aer/daemon.port</c>), so two
/// daemons racing to write it concurrently would still stomp each other.
/// </summary>
internal static class DaemonTestHost
{
    // Raised from 10s to 60s for #846: a healthy daemon that binds slowly under full-suite CI load was
    // being called a failure, and a larger backstop loses no failure detection. (Until #1412 deleted
    // it, Aer.Ui.Tests carried its own duplicate copy of this file with the same rationale.)
    private static readonly TimeSpan DefaultBindTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    public static async Task<DaemonTestInstance> StartAsync(
        IReadOnlyDictionary<string, IWorkerAdapter>? adapters = null, TimeSpan? bindTimeout = null)
    {
        // Captured via RunDaemonAsync's onBuilt callback rather than read back off the static
        // DaemonHost.App -- that field is shared process-wide and never cleared on shutdown, so
        // immediately after this call kicks off a new daemon there's a window (before this
        // invocation's own "App = app" assignment runs) where DaemonHost.App can still be a
        // *previous* test's already-disposed instance. Polling that stale reference throws
        // ObjectDisposedException instead of just "not ready yet". A dedicated TaskCompletionSource
        // tied to this specific RunDaemonAsync call sidesteps the race entirely -- and the captured
        // app is what the returned handle stops on dispose, so teardown targets exactly this daemon
        // rather than whatever DaemonHost.App happens to point at by then.
        var appBuilt = new TaskCompletionSource<WebApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        var daemonTask = DaemonHost.RunDaemonAsync(["--port", "0", "--no-mutex"], adapters, onBuilt: app => appBuilt.TrySetResult(app));
        var baseUrl = await WaitForBoundBaseUrlAsync(daemonTask, appBuilt.Task, bindTimeout ?? DefaultBindTimeout);
        return new DaemonTestInstance(await appBuilt.Task, daemonTask, baseUrl);
    }

    private static async Task<string> WaitForBoundBaseUrlAsync(Task daemonTask, Task<WebApplication> appBuiltTask, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            // A daemon that faulted during startup (bad args, a genuine bind failure, etc.) never
            // populates the addresses feature -- polling for addresses alone would just spin until
            // this method's own TimeoutException fires, masking the real exception. Surface it
            // immediately instead: `await`ing the already-faulted task rethrows it with its original
            // stack trace.
            if (daemonTask.IsFaulted)
            {
                await daemonTask;
            }

            var addresses = appBuiltTask.IsCompletedSuccessfully
                ? appBuiltTask.Result.Services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses
                : null;
            if (addresses is { Count: > 0 })
            {
                // Use the actually-bound address as reported (e.g. "http://127.0.0.1:52341") rather
                // than assuming "localhost" resolves to it -- DaemonHost binds loopback-only (IPv4)
                // for a dynamic port (Program.cs), so this sidesteps any dependence on the test
                // machine's "localhost" DNS/hosts-file resolution order entirely.
                return addresses.First().TrimEnd('/');
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the test daemon to bind its dynamically-assigned port.");
            }

            await Task.Delay(PollInterval);
        }
    }
}

/// <summary>
/// A running test daemon and its deterministic teardown. Disposing stops <b>this instance's own</b>
/// captured <see cref="WebApplication"/> — not the process-wide static <see cref="DaemonHost.App"/>,
/// which <see cref="DaemonTestHost"/> documents can already point at a different (superseded) daemon
/// by teardown time. Per-run storage isolation is handled at process scope by the <c>AER_HOME</c>
/// redirect (see <c>tests/Shared/AerHomeRedirect.cs</c>), so this handle owns daemon lifetime only,
/// not the storage root.
/// </summary>
internal sealed class DaemonTestInstance : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _disposed;

    internal DaemonTestInstance(WebApplication app, Task daemonTask, string baseUrl)
    {
        _app = app;
        DaemonTask = daemonTask;
        BaseUrl = baseUrl;
    }

    /// <summary>The daemon's run loop; awaited to completion on dispose.</summary>
    public Task DaemonTask { get; }

    /// <summary>The dynamically-assigned base URL the daemon actually bound (issue #296).</summary>
    public string BaseUrl { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _app.StopAsync();
        }
        catch (ObjectDisposedException)
        {
            // The app was already torn down (e.g. the daemon faulted and disposed itself); nothing
            // left to stop, and awaiting DaemonTask below surfaces any real fault.
        }

        await DaemonTask;
    }
}
