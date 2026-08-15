using System.Collections.Concurrent;
using System.Text.Json;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Adapters.Tests;

/// <summary>
/// #667's reader-side measurement, through the production path: concurrent resolves must not cost an
/// unretried reader its read. That the skip itself happens is asserted directly, and without any
/// shared state, in <see cref="AtomicLaunchConfigWriterTests"/>.
/// </summary>
/// <remarks>
/// Why the write is skipped, what the skip does and does not close, and the numbers measured before
/// it all live on <see cref="AtomicLaunchConfigWriter"/>. This file is the instrument, not a second
/// copy of the reasoning.
/// </remarks>
[Collection(LaunchConfigCollection.Name)]
public class LaunchConfigRewriteTests
{
    private static readonly WorkerContract ArchitectContract = new(
        "architect", ["goal"], [new ProducedOutput("plan.md")], []);

    private static string SettingsPath =>
        Path.Combine(AerPaths.WorkerLaunchConfig, "claude-settings.json");

    /// <summary>The once-only file, written by <c>EnsureFileExists</c> and never rewritten.</summary>
    private static string McpConfigPath =>
        Path.Combine(AerPaths.WorkerLaunchConfig, "claude-mcp.json");

    private static void Resolve() =>
        new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

    /// <summary>
    /// The reader-side measurement. Readers deliberately carry no retry, because the vendor CLI has
    /// none: it opens <c>--settings</c> once at spawn and a sharing violation there is a worker with
    /// no gate, not a transient it recovers from.
    /// </summary>
    /// <remarks>
    /// The once-only <c>claude-mcp.json</c> is the control arm: same directory, same reader code, same
    /// contention, differing only in not being rewritten. Scoped to the <i>settled</i> file — the
    /// seeding resolve runs before any reader, so the first-write window (#682) is excluded on
    /// purpose. Control run, not assumed: against the pre-#667 rewrite-always behaviour this fails on
    /// <c>settingsFailures</c>, 4239 reads of 424091 lost with the control clean.
    /// <para>
    /// <b>The overlap is established rather than assumed (#1274).</b> The writers do not start until
    /// all four reader loops have announced themselves. Before that, two of them could sit unstarted
    /// for the whole run while the writers finished and cancelled them — measured on a loaded macOS
    /// runner, where the read counts came back 1451/0 and the assertion below refused the vacuous
    /// pass. `Task.Run` having been called is not evidence a loop is running, and with eight tight
    /// synchronous loops competing for the pool it frequently is not.
    /// </para>
    [Fact]
    public async Task Concurrent_resolves_leave_a_settled_settings_file_readable_to_unretried_readers()
    {
        Resolve();

        using var writersDone = new CancellationTokenSource();
        var settingsFailures = new ConcurrentBag<Exception>();
        var controlFailures = new ConcurrentBag<Exception>();
        var settingsReads = 0;
        var controlReads = 0;

        // Each reader announces that it is actually running, and the writers wait for all four
        // (#1274). Eight tight synchronous loops do not fit the thread pool's initial slots, and a
        // reader that never got one reports zero — which the control assertion below correctly fails
        // on, having caught exactly that on a loaded macOS runner. LongRunning takes the loops off
        // the pool entirely, since a spin loop is what that flag is for; the handshake is what makes
        // the overlap a fact rather than a hope.
        var readerIsLive = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();

        var readers = Enumerable.Range(0, 4).Select(reader => Task.Factory.StartNew(
            () =>
        {
            var settings = reader % 2 == 0;
            var path = settings ? SettingsPath : McpConfigPath;

            while (!writersDone.IsCancellationRequested)
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    if (settings)
                    {
                        Interlocked.Increment(ref settingsReads);
                    }
                    else
                    {
                        Interlocked.Increment(ref controlReads);
                    }
                }
                catch (Exception ex)
                {
                    (settings ? settingsFailures : controlFailures).Add(ex);
                }

                // Signalled after the first attempt whether it read or failed: the point is that
                // this loop is running, and a reader whose every read fails is still contending.
                readerIsLive[reader].TrySetResult();
            }
        },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)).ToArray();

        await Task.WhenAll(readerIsLive.Select(live => live.Task));

        var writerFailures = new ConcurrentBag<Exception>();
        var writers = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() =>
            {
                for (var round = 0; round < 250; round++)
                {
                    // Collected rather than thrown: a resolve that dies here would abort the test
                    // before the reader measurement below could be reported, and would leave the
                    // reader loops spinning until process exit.
                    try
                    {
                        Resolve();
                    }
                    catch (Exception ex)
                    {
                        writerFailures.Add(ex);
                    }
                }
            }))
            .ToArray();

        try
        {
            await Task.WhenAll(writers);
        }
        finally
        {
            await writersDone.CancelAsync();
            await Task.WhenAll(readers);
        }

        Assert.True(
            settingsReads > 0 && controlReads > 0,
            $"Control: the reader loops observed the files {settingsReads}/{controlReads} times, so an " +
            "absence of failures below would prove nothing about the product.");
        Assert.True(
            controlFailures.IsEmpty,
            $"Control: {controlFailures.Count} reader(s) failed on the never-rewritten claude-mcp.json, " +
            $"so this run measures the harness rather than the rewrite. First: {controlFailures.FirstOrDefault()?.Message}");
        Assert.True(
            settingsFailures.IsEmpty,
            $"{settingsFailures.Count} of {settingsReads + settingsFailures.Count} readers could not load " +
            $"claude-settings.json while resolves ran. First: {settingsFailures.FirstOrDefault()?.Message}");
        Assert.True(
            writerFailures.IsEmpty,
            $"{writerFailures.Count} resolve(s) threw rather than writing. Under enough concurrency the " +
            "writer's own five-attempt retry budget is exhaustible, which a resolve surfaces as a failed " +
            $"dispatch. First: {writerFailures.FirstOrDefault()?.Message}");
    }
}
