using System.Net;
using System.Net.Http.Json;
using Aer.Adapters;
using Aer.Daemon;
using Aer.Flow.Concurrency;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Aer.Ui.Tests.TestSupport;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// #672: the operator's HTTP decision surface for held work, and the specific polarity that
/// memory-proposal approval applies the write while rejection leaves memory/ untouched.
/// </summary>
[Collection("DaemonIntegrationTests")]
public class HeldWorkResolveEndpointTests : IAsyncLifetime
{
    private DaemonTestInstance? _daemon;
    private string _baseUrl = "";
    private readonly HttpClient _client = new();
    private string _roomDirectory = "";

    public async ValueTask InitializeAsync()
    {
        _daemon = await DaemonTestHost.StartAsync();
        _baseUrl = _daemon.BaseUrl;

        for (var i = 0; i < 30; i++)
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

        _roomDirectory = Path.Combine(Path.GetTempPath(), "aer_held_work_resolve_endpoint_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_roomDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        if (_daemon != null)
        {
            await _daemon.DisposeAsync();
        }

        _client.Dispose();

        if (Directory.Exists(_roomDirectory))
        {
            DirectoryCleanup.DeleteRecursively(_roomDirectory);
        }
    }

    private async Task<HeldWorkRef> DispatchMemoryProposalAsync(
        string operation = "add", string targetPath = "fact.md", string captureName = "proposal-1.json")
    {
        var captureDir = Path.Combine(_roomDirectory, "artifacts", "execution_1", "memory-proposals");
        Directory.CreateDirectory(captureDir);

        // The capture path IS the HeldWorkRef, so a caller dispatching more than one proposal must
        // vary this or the second dispatch collides with the first's still-open ref.
        var captureFile = Path.Combine(captureDir, captureName);
        var content = operation == "delete" ? "null" : "\"the fact\"";
        await File.WriteAllTextAsync(
            captureFile,
            $$"""{"Operation":"{{operation}}","TargetPath":"{{targetPath}}","Content":{{content}},"Rationale":"learned it"}""",
            TestContext.Current.CancellationToken);

        var @ref = new HeldWorkRef(Path.GetFullPath(captureFile));
        var roomLogPath = Path.Combine(_roomDirectory, "room.jsonl");
        var reader = new RoomEventLogReader(roomLogPath);
        await using var writer = new RoomEventLogWriter(roomLogPath);

        // Escalate it ourselves UNLESS the room's own sweep got there first. Most tests here run
        // against a dormant RoomWakeBridge and always take the dispatch branch; the one that arms
        // /api/rooms/watch races a live sweep that escalates exactly the same capture file, and
        // whoever wins is immaterial to what these tests assert. Without this the loser dies on
        // "already been dispatched" -- measured, not defensive: it is what the watching test hit
        // once the writer contention behind it was fixed.
        //
        // Checked in a loop rather than once, because the check and the dispatch are not atomic
        // against the sweep either.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var state = RoomProjector.Project(
                await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken));
            if (state.HeldWork.ContainsKey(@ref))
            {
                return @ref;
            }

            try
            {
                await RoomMutationInterface.DispatchHeldWorkAsync(
                    _roomDirectory, @ref, MemoryProposalEscalation.MemoryProposalShape, MemoryProposalEscalation.NoBudget,
                    "operator", reader, writer, TestContext.Current.CancellationToken);
                return @ref;
            }
            catch (InvalidRoomMutationException) when (DateTime.UtcNow < deadline)
            {
                // The sweep won between the check and the dispatch; re-read and take the other branch.
            }
            catch (WorkflowLockedException) when (DateTime.UtcNow < deadline)
            {
                // #1104: with the bridge live (the #857 configuration), the raw fail-fast dispatch
                // can also lose the room guard to a sweep tick itself — reliably hit on the slower
                // macos runner. Same principle as above: whoever wins is immaterial; retry.
            }
        }
    }

    /// <summary>
    /// #857's harness half. Every other test in this class resolves against a daemon whose
    /// <c>RoomWakeBridge</c> is dormant — it stays asleep until something calls
    /// <c>/api/rooms/watch</c>, and no resolve test did. So the endpoint's behaviour with a live
    /// sweep taking the same room lock on a 500ms tick was unshipped-as-if-covered: the suite was
    /// green on a configuration production never runs in.
    /// <para>
    /// This arms the watch first, then resolves several times while the bridge is genuinely
    /// ticking. It deliberately does <b>not</b> assert that a collision occurred — a real 500ms
    /// tick cannot be made to collide on demand, and a test that needed it to would be the timed
    /// race this issue is about. The deterministic proof that a held lock is waited out rather than
    /// refused lives in <c>ConcurrencyGuardTests</c> and <c>MemoryProposalResolutionTests</c>. What
    /// this adds is the configuration: with the bridge live, resolve must never come back 409.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Resolving_while_the_room_wake_bridge_is_watching_never_returns_conflict()
    {
        var watchResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/watch",
            new { RoomDirectoryPath = _roomDirectory },
            TestContext.Current.CancellationToken);
        Assert.True(watchResponse.IsSuccessStatusCode, "Could not arm the room watch, so the bridge never ticked.");

        // Several resolves spread across more than one 500ms sweep interval, so the window the
        // sweep holds the lock in is actually spanned rather than stepped over once.
        for (var i = 0; i < 6; i++)
        {
            var @ref = await DispatchMemoryProposalAsync(
                targetPath: $"fact-{i}.md", captureName: $"proposal-sweep-{i}.json");

            var response = await _client.PostAsJsonAsync(
                $"{_baseUrl}/api/rooms/held-work/resolve",
                new ResolveHeldWorkRequest(_roomDirectory, @ref.Value, "approve"),
                TestContext.Current.CancellationToken);

            Assert.False(
                response.StatusCode == System.Net.HttpStatusCode.Conflict,
                $"Resolve {i} came back 409 with the wake bridge watching -- an operator's approve lost a "
                + "coin-flip to a routine sweep, which is exactly #857.");
            Assert.True(response.IsSuccessStatusCode);

            await Task.Delay(120, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Approving_a_memory_proposal_returns_ok_and_applies_the_write()
    {
        var @ref = await DispatchMemoryProposalAsync();

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/held-work/resolve",
            new ResolveHeldWorkRequest(_roomDirectory, @ref.Value, "approve"),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(
            "the fact",
            await File.ReadAllTextAsync(Path.Combine(_roomDirectory, "memory", "fact.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejecting_a_memory_proposal_returns_ok_and_leaves_memory_untouched()
    {
        var @ref = await DispatchMemoryProposalAsync();

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/held-work/resolve",
            new ResolveHeldWorkRequest(_roomDirectory, @ref.Value, "reject"),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        Assert.False(Directory.Exists(Path.Combine(_roomDirectory, "memory")));
    }

    /// <summary>
    /// Proves the existence guard the resolve endpoint takes for the reason its own comment
    /// records (Program.cs, <c>/api/rooms/held-work/resolve</c>): a bad-request response, and no
    /// stray directory left behind.
    /// </summary>
    [Fact]
    public async Task Resolving_against_a_nonexistent_room_directory_returns_bad_request_and_creates_nothing()
    {
        var nonexistentRoom = Path.Combine(_roomDirectory, "does-not-exist");

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/held-work/resolve",
            new ResolveHeldWorkRequest(nonexistentRoom, Path.Combine(nonexistentRoom, "nope.json"), "approve"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(Directory.Exists(nonexistentRoom));
    }

    [Fact]
    public async Task Resolving_an_unknown_ref_returns_bad_request()
    {
        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/held-work/resolve",
            new ResolveHeldWorkRequest(_roomDirectory, Path.Combine(_roomDirectory, "nope.json"), "approve"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Double_resolving_the_same_ref_returns_bad_request_on_the_second_call()
    {
        var @ref = await DispatchMemoryProposalAsync();

        var first = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/held-work/resolve",
            new ResolveHeldWorkRequest(_roomDirectory, @ref.Value, "approve"),
            TestContext.Current.CancellationToken);
        Assert.True(first.IsSuccessStatusCode);

        var second = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/held-work/resolve",
            new ResolveHeldWorkRequest(_roomDirectory, @ref.Value, "approve"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task An_invalid_outcome_value_returns_bad_request()
    {
        var @ref = await DispatchMemoryProposalAsync();

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/held-work/resolve",
            new ResolveHeldWorkRequest(_roomDirectory, @ref.Value, "maybe"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
