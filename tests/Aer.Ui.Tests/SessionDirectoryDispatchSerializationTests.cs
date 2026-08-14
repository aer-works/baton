using System.Net.Http.Json;
using System.Text.Json;
using Aer.Adapters;
using Aer.Daemon;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Ui.Core;
using Aer.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// #590: the vendor CLI's own <c>--session-id</c> guard is an existence check, not a lock
/// (vendor-doc-audit.md, "`--session-id` is guarded by an existence check, not a lock") -- two
/// concurrent dispatches of the same persisted vendor session id both succeed and both write.
/// <c>/api/rooms/run</c> and <c>/api/rooms/decide</c> dispatch whatever <c>bindings.json</c> says
/// with no serialisation of their own, unlike the chat pipeline's <c>SessionTurnLockFor</c>
/// (Program.cs).
///
/// <para>
/// Flow's own readiness is monotonic within one directory's pump: once a step dispatches and
/// settles, nothing makes it "ready" again for a second, independent call to pick up. So "two
/// concurrent calls against one directory" can NEVER observe two completions the way "two calls
/// against two different directories" can -- that would be true even with a perfect lock. What the
/// lock changes is whether the second call's request is silently lost to Flow's own
/// <c>ConcurrencyGuard</c> throwing <c>WorkflowLockedException</c> (pre-#590: swallowed by
/// <c>RoomClient.RunAsync</c>/<c>DecideAsync</c>'s <c>catch (AerFlowException)</c>, invisible to the
/// caller since both endpoints already return 200 before dispatch runs) or cleanly waits its turn.
/// Every test below asserts exactly the number of completions the *serialised* pump can produce, and
/// separately asserts no dispatch overlapped (the collision file) and, via #828's dispatch-failure log,
/// that a losing racer was recorded rather than silently dropped where one exists.
/// </para>
///
/// <see cref="SlowCollisionStubAdapter"/>'s dispatched process itself detects overlap (a marker file
/// left in the invocation's working directory), rather than this test measuring wall-clock timing --
/// timing is inherently flaky under CI load, a marker file left by another live process is not.
///
/// Shares the <c>DaemonIntegrationTests</c> collection for the same reason every other class here
/// does: each spins up a real Kestrel daemon and points a config store at the same per-user file.
/// </summary>
[Collection("DaemonIntegrationTests")]
public class SessionDirectoryDispatchSerializationTests : IAsyncLifetime
{
    private static readonly StepId WorkerStep = new("worker-step");

    private DaemonTestInstance? _daemon;
    private string _baseUrl = "";
    private readonly HttpClient _client = new();

    public async ValueTask InitializeAsync()
    {
        IReadOnlyDictionary<string, IWorkerAdapter> stubAdapters = new Dictionary<string, IWorkerAdapter>
        {
            ["slow-collision"] = new SlowCollisionStubAdapter(),
        };

        _daemon = await DaemonTestHost.StartAsync(stubAdapters);
        _baseUrl = _daemon.BaseUrl;

        for (int i = 0; i < 30; i++)
        {
            try
            {
                var response = await _client.GetAsync($"{_baseUrl}/api/version", TestContext.Current.CancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }

        var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
        if (File.Exists(tokenFile))
        {
            var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_daemon != null)
        {
            await _daemon.DisposeAsync();
        }

        _client.Dispose();
    }

    [Fact]
    public async Task ConcurrentRuns_OnTheSameDirectory_NeverDispatchOverlappingWorkers()
    {
        var (roomDirectory, bindingsFilePath) = await CreateReadyRoomDirectoryAsync();

        // No await between the two POSTs: both endpoints return 200 before their fire-and-forget
        // dispatch runs, so this genuinely races the two Task.Run bodies against one another.
        var run1 = _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/run",
            new RunRoomRequest(roomDirectory, null, bindingsFilePath),
            TestContext.Current.CancellationToken);
        var run2 = _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/run",
            new RunRoomRequest(roomDirectory, null, bindingsFilePath),
            TestContext.Current.CancellationToken);

        var responses = await Task.WhenAll(run1, run2);
        foreach (var response in responses)
        {
            Assert.True(response.IsSuccessStatusCode);
        }

        await WaitForCompletionsAsync(roomDirectory, expectedCompletions: 1);

        // Settle grace: readiness is monotonic (see class doc), so only one of the two calls can
        // ever dispatch -- the whichever-arrives-second call finds the step already Succeeded and
        // does nothing. This grace period is what tells "exactly one, ever" apart from "one has
        // landed but a second may still be racing in" -- reading immediately after the first
        // completion lands would only prove the latter.
        await Task.Delay(SlowCollisionStubAdapter.DispatchDelay * 2, TestContext.Current.CancellationToken);

        AssertNoCollision(roomDirectory);
        Assert.Equal(1, ReadCompletionsCount(roomDirectory));
    }

    [Fact]
    public async Task ConcurrentRunAndDecide_OnTheSameDirectory_NeverDispatchOverlappingWorkers()
    {
        // #590 review finding: the original version of this test posted /api/rooms/run twice and
        // never called /api/rooms/decide at all, despite its name and doc comment claiming run+decide
        // coverage -- decide's lock wrapper (Program.cs) has its own pre-lock ArtifactReference branch
        // nothing exercised. This version actually races the two different endpoints.
        var (roomDirectory, executionId) = await CreatePausedFailedRoomDirectoryAsync();
        var bindingsFilePath = Path.Combine(roomDirectory, "bindings.json");

        // Set directly rather than relying on the /api/rooms/run request below to set it as a side
        // effect (Program.cs) -- run and decide are about to fire with no await between them, so that
        // side effect would itself race decide's own read of it.
        DaemonHost.App!.Services.GetRequiredService<BindingsPathHolder>().BindingsFilePath = bindingsFilePath;

        // /api/rooms/run against an already-Paused workflow dispatches nothing (Paused steps are
        // never "ready") -- it still exercises /api/rooms/run's lock wrapper concurrently with
        // decide's, which is the actual coverage gap; it just cannot itself add a completion.
        var run = _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/run",
            new RunRoomRequest(roomDirectory, null, bindingsFilePath),
            TestContext.Current.CancellationToken);
        var decide = _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/decide",
            new DecideRoomRequest(roomDirectory, WorkerStep.Value, executionId, DecisionType.RetryWithRevision),
            TestContext.Current.CancellationToken);

        var responses = await Task.WhenAll(run, decide);
        foreach (var response in responses)
        {
            Assert.True(response.IsSuccessStatusCode);
        }

        await WaitForCompletionsAsync(roomDirectory, expectedCompletions: 1);
        await Task.Delay(SlowCollisionStubAdapter.DispatchDelay * 2, TestContext.Current.CancellationToken);

        AssertNoCollision(roomDirectory);
        Assert.Equal(1, ReadCompletionsCount(roomDirectory));
    }

    [Fact]
    public async Task ConcurrentDecides_OnTheSameDirectory_OnlyOneDispatchesAndTheLoserIsRecorded()
    {
        var (roomDirectory, executionId) = await CreatePausedFailedRoomDirectoryAsync();

        // DecideCommand always loads a bindings file regardless of decision type (Aer.Cli's
        // DecideCommand.cs), read through the daemon's DI-registered BindingsPathHolder -- normally
        // populated as a side effect of /api/rooms/open or /api/rooms/run, neither of which this test
        // calls (see DaemonIntegrationTests.Reject_TriggersASecondWebSocketBroadcast... for the same
        // pattern), so it must be set directly.
        DaemonHost.App!.Services.GetRequiredService<BindingsPathHolder>().BindingsFilePath =
            Path.Combine(roomDirectory, "bindings.json");

        var decide1 = _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/decide",
            new DecideRoomRequest(roomDirectory, WorkerStep.Value, executionId, DecisionType.RetryWithRevision),
            TestContext.Current.CancellationToken);
        var decide2 = _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/decide",
            new DecideRoomRequest(roomDirectory, WorkerStep.Value, executionId, DecisionType.RetryWithRevision),
            TestContext.Current.CancellationToken);

        var responses = await Task.WhenAll(decide1, decide2);
        foreach (var response in responses)
        {
            Assert.True(response.IsSuccessStatusCode);
        }

        await WaitForCompletionsAsync(roomDirectory, expectedCompletions: 1);
        await Task.Delay(SlowCollisionStubAdapter.DispatchDelay * 2, TestContext.Current.CancellationToken);

        AssertNoCollision(roomDirectory);
        Assert.Equal(1, ReadCompletionsCount(roomDirectory));

        // #828: the loser -- the second decide to actually run, once serialised by the #590 lock --
        // finds the execution no longer Paused (ExternalDecisionValidator) and throws
        // InvalidExternalDecisionException. Both endpoints answer 200 before dispatch runs, so before
        // #828 this failure reached Console.Error and nowhere else. Confirms it is now durably
        // recorded rather than silently lost.
        var errorLogPath = Path.Combine(roomDirectory, ".aer", "turn-errors.log");
        var errorLog = await WaitForFileContentAsync(errorLogPath);
        Assert.Contains("/api/rooms/decide", errorLog);
        // RoomClient.DecideAsync's in-process fallback catches InvalidExternalDecisionException
        // itself and returns MutationOutcome(ex.Message) rather than throwing (see Program.cs's
        // #828 comment) -- what's recorded is ExternalDecisionValidator's message text, not the
        // exception type name.
        Assert.Contains("is not the currently paused latest attempt", errorLog);
    }

    [Fact]
    public async Task ARunThatFailsToResolveItsBindings_StillReleasesTheLockForTheNextRun()
    {
        // Exception-safety arm (work item 4): a dispatch that fails must release the per-directory
        // lock so a follow-up dispatch on the same directory still runs. UnknownWorkerAdapterException
        // is an AerFlowException, caught inside RoomClient.RunAsync's own fallback branch and turned
        // into a MutationOutcome rather than an escaping .NET exception -- but Program.cs's
        // turnLock.Release() sits in a `finally` around the ENTIRE `await session.RunAsync(...)` call,
        // not inside a catch keyed to a specific exception type, so it runs identically whether
        // RunAsync swallows the failure internally or lets one escape. That makes this HTTP-level test
        // representative of both cases -- see the commit body for why a second, lock-internal unit
        // test would be redundant rather than additive here.
        var (roomDirectory, goodBindingsFilePath) = await CreateReadyRoomDirectoryAsync();
        var badBindingsFilePath = await WriteUnresolvableBindingsAsync(roomDirectory);

        var badRunResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/run",
            new RunRoomRequest(roomDirectory, null, badBindingsFilePath),
            TestContext.Current.CancellationToken);
        Assert.True(badRunResponse.IsSuccessStatusCode);

        // Wait for the failed dispatch to actually finish (and release the lock) before firing the
        // next one -- #828's error log is the observable signal that the background body completed.
        var errorLogPath = Path.Combine(roomDirectory, ".aer", "turn-errors.log");
        await WaitForFileContentAsync(errorLogPath);

        var goodRunResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/run",
            new RunRoomRequest(roomDirectory, null, goodBindingsFilePath),
            TestContext.Current.CancellationToken);
        Assert.True(goodRunResponse.IsSuccessStatusCode);

        // If the lock were never released, this would hang until WaitForCompletionsAsync's own
        // internal 30s deadline and then fail on the assertion below -- a genuine timeout, not a
        // silent pass.
        await WaitForCompletionsAsync(roomDirectory, expectedCompletions: 1);
        Assert.Equal(1, ReadCompletionsCount(roomDirectory));
    }

    [Fact]
    public async Task ConcurrentRuns_OnDifferentDirectories_StillProceedConcurrently()
    {
        var rendezvous = CreateRendezvousDirectory();
        var (directoryA, bindingsA) = await CreateReadyRoomDirectoryAsync(rendezvous);
        var (directoryB, bindingsB) = await CreateReadyRoomDirectoryAsync(rendezvous);

        var runA = _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/run", new RunRoomRequest(directoryA, null, bindingsA), TestContext.Current.CancellationToken);
        var runB = _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/run", new RunRoomRequest(directoryB, null, bindingsB), TestContext.Current.CancellationToken);

        var responses = await Task.WhenAll(runA, runB);
        foreach (var response in responses)
        {
            Assert.True(response.IsSuccessStatusCode);
        }

        await WaitForCompletionsAsync(directoryA, expectedCompletions: 1);
        await WaitForCompletionsAsync(directoryB, expectedCompletions: 1);

        // Both dispatches saw the other's arrival, which only a pair in flight at once can do.
        Assert.Equal(2, CountRendezvousProofs(rendezvous));

        AssertNoCollision(directoryA);
        AssertNoCollision(directoryB);
    }

    /// <summary>
    /// The control for the fact above (#1211): the same two directories dispatched one after the
    /// other, which is what global serialisation would look like. Only the second run can find two
    /// arrivals -- the first waited out <see cref="SlowCollisionStubAdapter.RendezvousTimeout"/>
    /// alone and exited before the second announced itself -- so a serialised pair yields one proof
    /// and can never reach the two the fact above requires.
    /// </summary>
    [Fact]
    public async Task SequentialRuns_OnDifferentDirectories_DoNotClearTheRendezvous()
    {
        var rendezvous = CreateRendezvousDirectory();
        var (directoryA, bindingsA) = await CreateReadyRoomDirectoryAsync(rendezvous);
        var (directoryB, bindingsB) = await CreateReadyRoomDirectoryAsync(rendezvous);

        var runA = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/run", new RunRoomRequest(directoryA, null, bindingsA), TestContext.Current.CancellationToken);
        Assert.True(runA.IsSuccessStatusCode);
        await WaitForCompletionsAsync(directoryA, expectedCompletions: 1);

        var runB = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/run", new RunRoomRequest(directoryB, null, bindingsB), TestContext.Current.CancellationToken);
        Assert.True(runB.IsSuccessStatusCode);
        await WaitForCompletionsAsync(directoryB, expectedCompletions: 1);

        Assert.Equal(1, CountRendezvousProofs(rendezvous));
    }

    private static string CreateRendezvousDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aer_1211_rendezvous_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static int CountRendezvousProofs(string rendezvousDirectory) =>
        Directory.GetFiles(rendezvousDirectory, SlowCollisionStubAdapter.ConcurrencyProofFilePrefix + "*").Length;

    private static void AssertNoCollision(string roomDirectory)
    {
        var collisionFile = Path.Combine(roomDirectory, SlowCollisionStubAdapter.CollisionFileName);
        Assert.False(File.Exists(collisionFile),
            "Two dispatches against the same room directory overlapped -- the per-directory lock did not serialise them.");
    }

    private static int ReadCompletionsCount(string roomDirectory)
    {
        var completionsFile = Path.Combine(roomDirectory, SlowCollisionStubAdapter.CompletionsFileName);
        return File.Exists(completionsFile) ? LiveFileReader.ReadLines(completionsFile).Count : 0;
    }

    private static async Task<(string RoomDirectory, string BindingsFilePath)> CreateReadyRoomDirectoryAsync(
        string? rendezvousDirectory = null)
    {
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("dispatch-serialization-test"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(WorkerStep, "worker", [], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]));

        var roomDirectory = Path.Combine(Path.GetTempPath(), $"aer_590_dispatch_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

        var promptTemplate = rendezvousDirectory is null
            ? "irrelevant, no vendor is really invoked"
            : SlowCollisionStubAdapter.RendezvousSentinelPrefix + rendezvousDirectory;

        var bindingsFilePath = Path.Combine(roomDirectory, "bindings.json");
        await WriteSlowCollisionBindingsAsync(bindingsFilePath, roomDirectory, promptTemplate);

        return (roomDirectory, bindingsFilePath);
    }

    /// <summary>
    /// A single-step workflow whose one attempt has already failed (deterministically, via
    /// <see cref="SlowCollisionStubAdapter.ForceFailureSentinel"/>) and paused -- hand-written
    /// directly into <c>flow.jsonl</c>, the same technique
    /// <c>DaemonIntegrationTests.CreatePausedRoomDirectoryAsync</c> uses for its own Paused fixture,
    /// swapping <c>ExecutionSucceeded</c> for <c>ExecutionFailed</c> so the paused outcome is Failed --
    /// <c>ExternalDecisionValidator</c> refuses <c>RetryWithRevision</c> once the paused outcome is
    /// Succeeded, so a fixture built the other way could never legitimately re-dispatch.
    /// </summary>
    private static async Task<(string RoomDirectory, string ExecutionId)> CreatePausedFailedRoomDirectoryAsync()
    {
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("dispatch-serialization-paused-test"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(WorkerStep, "worker", [], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1), PausePoint: new PausePoint([]))]));

        var roomDirectory = Path.Combine(Path.GetTempPath(), $"aer_590_dispatch_paused_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

        var bindingsFilePath = Path.Combine(roomDirectory, "bindings.json");
        await WriteSlowCollisionBindingsAsync(bindingsFilePath, roomDirectory, promptTemplate: SlowCollisionStubAdapter.ForceFailureSentinel);

        var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
        var request = new ExecutionRequest(
            executionId,
            new WorkflowId("dispatch-serialization-paused-test"),
            WorkerStep,
            "worker",
            Inputs: [],
            Outputs: ["out"],
            Timeout: TimeSpan.FromSeconds(30),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        await using (var writer = new FlowEventLogWriter(Path.Combine(roomDirectory, "flow.jsonl")))
        {
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.ExecutionFailed(executionId, FailureClassification.Permanent, "forced failure for #590 test fixture"),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.WorkflowPaused(executionId, WorkerStep), TestContext.Current.CancellationToken);
        }

        return (roomDirectory, executionId.Value);
    }

    private static async Task WriteSlowCollisionBindingsAsync(string bindingsFilePath, string workingDirectory, string promptTemplate)
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["worker"] = new WorkerBindingConfigEntry(
                "slow-collision",
                new WorkerContract("worker", [], [new ProducedOutput("out")], []),
                promptTemplate,
                TimeSpan.FromSeconds(30),
                WorkingDirectory: workingDirectory),
        };

        await File.WriteAllTextAsync(bindingsFilePath, JsonSerializer.Serialize(config), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Names an adapter no registry entry can resolve, matching
    /// <c>DaemonIntegrationTests.WriteUnresolvableBindingsAsync</c>'s own convention (duplicated
    /// rather than shared -- each test class here owns its minimal fixture set) --
    /// <see cref="WorkerBindingResolver.Resolve"/> throws <see cref="UnknownWorkerAdapterException"/>
    /// synchronously, a fast, deterministic way to exercise the failure path with no live process.
    /// </summary>
    private static async Task<string> WriteUnresolvableBindingsAsync(string roomDirectory)
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["worker"] = new WorkerBindingConfigEntry(
                "not-a-registered-adapter", new WorkerContract("worker", [], [new ProducedOutput("out")], []),
                "irrelevant, never dispatched", TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(roomDirectory, "bad-bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config), TestContext.Current.CancellationToken);
        return path;
    }

    /// <summary>
    /// Polls until <paramref name="expectedCompletions"/> dispatches have actually run against
    /// <paramref name="roomDirectory"/> (or the timeout elapses -- the caller's own completions-count
    /// assertion is what turns a timeout into a failure, not this helper).
    /// </summary>
    private static async Task WaitForCompletionsAsync(string roomDirectory, int expectedCompletions)
    {
        var completionsFile = Path.Combine(roomDirectory, SlowCollisionStubAdapter.CompletionsFileName);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(completionsFile))
            {
                var lines = LiveFileReader.ReadLines(completionsFile);
                if (lines.Count >= expectedCompletions)
                {
                    return;
                }
            }

            await Task.Delay(100);
        }
    }

    /// <summary>
    /// #872: this used to poll for two conditions -- the file not existing yet, and the file being
    /// empty -- while reading with <c>File.ReadAllTextAsync</c>, i.e. <c>FileShare.Read</c>. While
    /// the daemon holds <c>turn-errors.log</c> open for its own append, that read throws and the
    /// exception escaped the polling loop entirely: the retry was present, it just did not cover the
    /// failure that actually happens. It now delegates to <see cref="LiveFileReader"/>, which owns
    /// both halves of the fix and the measurement behind them.
    /// </summary>
    private static Task<string> WaitForFileContentAsync(string filePath) =>
        LiveFileReader.WaitForContentAsync(filePath);
}
