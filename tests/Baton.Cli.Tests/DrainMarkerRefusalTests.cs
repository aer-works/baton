using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Status;

namespace Baton.Cli.Tests;

/// <summary>
/// #1645 half (1) of the drain ruling, end to end through the three verbs themselves (no parser, no
/// process): with <see cref="BatonPaths.DrainMarkerFile"/> present, <c>baton dispatch</c>,
/// <c>baton redispatch</c> and <c>baton resume</c> refuse before doing anything, and the room the verb
/// would have created does not exist afterwards. Each refusal is paired with its own absent-marker arm:
/// a refusal that fires either way would be indistinguishable from a verb that is simply broken.
/// <para>
/// <b>"creates no room" is a claim about the COMMAND, not about the CLI.</b> Through <c>Program.cs</c> a
/// refused <c>dispatch</c>/<c>redispatch</c> does end up with a room directory — created solely to hold
/// the <c>terminal.json</c> validation-refusal record every pre-run refusal leaves, which refresh.py's
/// drain predicate skips as terminal. Don't cite these test names as evidence that nothing appears on
/// disk; <c>spec/baton.md</c> C-10 states what actually does.
/// </para>
/// </summary>
/// <remarks>
/// The marker is <see cref="BatonPaths.Root"/>-relative, so every arm scopes the storage root to a
/// temp directory via <see cref="BatonEnvironmentSnapshot.BeginScope"/> rather than writing into this
/// machine's real <c>~/.baton</c> — writing a real marker here would park every live lane on the
/// machine for the duration of the test run.
/// <para>
/// Enrolled in <see cref="SerializedEnvironmentCollection"/> for the marker-absent arms only: those get
/// past the drain check and into the catalog read, and <c>DispatchCommandEndToEndTests</c> mutates the
/// catalog-path environment variables process-globally (one of its tests points them at a deliberately
/// broken file). Overlapping it would fail this class's arms with an unrelated catalog error that still
/// satisfies the drain-phrase assertion — a flake that reads as a message mismatch.
/// </para>
/// </remarks>
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class DrainMarkerRefusalTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["fake"] = new ContractOutputWorkerAdapter(satisfyOutputs: true) };

    // The phrase only the drain refusal carries. Not the bare word "drain": these tests' own temp
    // directories have it in their names, so a marker-absent arm asserting on that alone passed for the
    // wrong reason (and, at first, failed for it).
    private const string DrainRefusalPhrase = "tool-refresh drain is in progress";

    [Fact]
    public async Task Dispatch_refuses_while_a_drain_marker_is_present_and_creates_no_room()
    {
        var testRoot = NewTestRoot();
        try
        {
            var home = WriteMarker(testRoot, """{"since":"2026-09-02T05:00:00Z","pid":4242,"reason":"tool-refresh"}""");
            using var scope = BatonEnvironmentSnapshot.BeginScope(
                BatonEnvironmentSnapshot.Blank with { HomeOverride = home });

            var specPath = Path.Combine(testRoot, "spec.md");
            await File.WriteAllTextAsync(specPath, "Weigh the options for X.", TestContext.Current.CancellationToken);
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake");

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));

            AssertNamesTheMarkerAndTheAbort(ex, home);
            Assert.False(Directory.Exists(roomDirectory), "dispatch created a room despite refusing");
        }
        finally
        {
            Cleanup(testRoot);
        }
    }

    [Fact]
    public async Task Dispatch_with_no_marker_gets_past_the_drain_check()
    {
        var testRoot = NewTestRoot();
        try
        {
            var home = Path.Combine(testRoot, "home");
            Directory.CreateDirectory(home);
            using var scope = BatonEnvironmentSnapshot.BeginScope(
                BatonEnvironmentSnapshot.Blank with { HomeOverride = home });

            var specPath = Path.Combine(testRoot, "spec.md");
            await File.WriteAllTextAsync(specPath, "Weigh the options for X.", TestContext.Current.CancellationToken);
            var options = new DispatchOptions(
                "no-such-role-or-template", specPath, Path.Combine(testRoot, "task"), Adapter: "fake");

            // Polarity: the SAME call, marker absent, fails for its own reason (an unknown name) rather
            // than the drain refusal — so the arm above is measuring the marker, not a broken dispatch.
            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));

            Assert.DoesNotContain(DrainRefusalPhrase, ex.Message, StringComparison.Ordinal);
            Assert.Contains("no-such-role-or-template", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(testRoot);
        }
    }

    [Fact]
    public async Task Redispatch_refuses_while_a_drain_marker_is_present_and_creates_no_room()
    {
        var testRoot = NewTestRoot();
        try
        {
            var home = WriteMarker(testRoot, """{"since":"2026-09-02T05:00:00Z","pid":4242,"reason":"tool-refresh"}""");
            using var scope = BatonEnvironmentSnapshot.BeginScope(
                BatonEnvironmentSnapshot.Blank with { HomeOverride = home });

            var parentRoom = Path.Combine(testRoot, "parent");
            Directory.CreateDirectory(parentRoom);
            var childRoom = Path.Combine(testRoot, "child");
            var options = new RedispatchOptions(parentRoom, childRoom);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));

            AssertNamesTheMarkerAndTheAbort(ex, home);
            Assert.False(Directory.Exists(childRoom), "redispatch created a room despite refusing");
        }
        finally
        {
            Cleanup(testRoot);
        }
    }

    [Fact]
    public async Task Redispatch_with_no_marker_gets_past_the_drain_check()
    {
        var testRoot = NewTestRoot();
        try
        {
            var home = Path.Combine(testRoot, "home");
            Directory.CreateDirectory(home);
            using var scope = BatonEnvironmentSnapshot.BeginScope(
                BatonEnvironmentSnapshot.Blank with { HomeOverride = home });

            var options = new RedispatchOptions(Path.Combine(testRoot, "parent"), Path.Combine(testRoot, "child"));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => RedispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));

            Assert.DoesNotContain(DrainRefusalPhrase, ex.Message, StringComparison.Ordinal);
            Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(testRoot);
        }
    }

    [Fact]
    public async Task Resume_refuses_while_a_drain_marker_is_present_and_records_no_execution()
    {
        var testRoot = NewTestRoot();
        try
        {
            var home = WriteMarker(testRoot, """{"since":"2026-09-02T05:00:00Z","pid":4242,"reason":"tool-refresh"}""");
            using var scope = BatonEnvironmentSnapshot.BeginScope(
                BatonEnvironmentSnapshot.Blank with { HomeOverride = home });

            // "No room created" would be a vacuous assertion for this verb (see ResumeCommand). What
            // must not appear is a new execution — the artifacts directory the mutation writes into.
            var roomDirectory = Path.Combine(testRoot, "task");
            Directory.CreateDirectory(roomDirectory);
            var options = new ResumeOptions(
                roomDirectory, "observer", "carry on", null, Path.Combine(testRoot, "bindings.json"));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => ResumeCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));

            AssertNamesTheMarkerAndTheAbort(ex, home);
            Assert.Empty(Directory.GetFileSystemEntries(roomDirectory));
        }
        finally
        {
            Cleanup(testRoot);
        }
    }

    [Fact]
    public async Task Resume_with_no_marker_gets_past_the_drain_check()
    {
        var testRoot = NewTestRoot();
        try
        {
            var home = Path.Combine(testRoot, "home");
            Directory.CreateDirectory(home);
            using var scope = BatonEnvironmentSnapshot.BeginScope(
                BatonEnvironmentSnapshot.Blank with { HomeOverride = home });

            var roomDirectory = Path.Combine(testRoot, "task");
            Directory.CreateDirectory(roomDirectory);
            var options = new ResumeOptions(
                roomDirectory, "observer", "carry on", null, Path.Combine(testRoot, "bindings.json"));

            // Marker absent: the same call fails on its own missing-snapshot refusal, a different type
            // entirely — so the refusal above is the marker's, not resume's ordinary state check.
            var ex = await Record.ExceptionAsync(
                () => ResumeCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));

            Assert.NotNull(ex);
            Assert.IsNotType<CliArgumentException>(ex);
            Assert.DoesNotContain(DrainRefusalPhrase, ex!.Message, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(testRoot);
        }
    }

    [Fact]
    public async Task A_malformed_marker_still_refuses()
    {
        var testRoot = NewTestRoot();
        try
        {
            // Fail closed: a marker half-written when the writer was interrupted must not read as an
            // open gate just because its JSON does not parse.
            var home = WriteMarker(testRoot, """{"since":"2026-09-02T05:00:0""");
            using var scope = BatonEnvironmentSnapshot.BeginScope(
                BatonEnvironmentSnapshot.Blank with { HomeOverride = home });

            var specPath = Path.Combine(testRoot, "spec.md");
            await File.WriteAllTextAsync(specPath, "Weigh the options for X.", TestContext.Current.CancellationToken);
            var roomDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "fake");

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));

            AssertNamesTheMarkerAndTheAbort(ex, home);
            Assert.Contains("unreadable as JSON", ex.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(roomDirectory), "dispatch created a room despite refusing");
        }
        finally
        {
            Cleanup(testRoot);
        }
    }

    private static void AssertNamesTheMarkerAndTheAbort(CliArgumentException ex, string home)
    {
        // The two things a refusal has to carry for an agent or operator to act on it without guessing:
        // WHERE the marker is, and the one command that clears a stale one.
        Assert.Contains(DrainRefusalPhrase, ex.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(home, BatonPaths.DrainMarkerFileName), ex.Message, StringComparison.Ordinal);
        Assert.Contains(DrainMarker.AbortInvocation, ex.Message, StringComparison.Ordinal);
        Assert.Equal(DrainMarker.AbortInvocation, ex.TryInvocation);
    }

    private static string NewTestRoot() =>
        Path.Combine(Path.GetTempPath(), $"drain-marker-{Guid.NewGuid():N}");

    private static string WriteMarker(string testRoot, string content)
    {
        var home = Path.Combine(testRoot, "home");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, BatonPaths.DrainMarkerFileName), content);
        return home;
    }

    private static void Cleanup(string testRoot) => DirectoryCleanup.DeleteRecursively(testRoot);
}

/// <summary>
/// The control for the isolation the six existing command end-to-end suites now rely on: they open
/// their <see cref="BatonEnvironmentSnapshot.BeginScope"/> in a CONSTRUCTOR, not inside each
/// <c>[Fact]</c>, and a scope that did not reach the test body would silently point them back at this
/// machine's real <c>~/.baton</c> — passing for the wrong reason until the day a real drain marker
/// existed. This class scopes a marker-bearing home from its constructor and asserts the refusal fires,
/// so the mechanism itself is pinned rather than assumed.
/// </summary>
public sealed class DrainMarkerCtorScopeControlTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), $"drain-marker-ctor-{Guid.NewGuid():N}");
    private readonly IDisposable _scope;

    public DrainMarkerCtorScopeControlTests()
    {
        var home = Path.Combine(_testRoot, "home");
        Directory.CreateDirectory(home);
        File.WriteAllText(
            Path.Combine(home, BatonPaths.DrainMarkerFileName),
            """{"since":"2026-09-02T05:00:00Z","pid":4242,"reason":"tool-refresh"}""");
        _scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
    }

    public void Dispose()
    {
        _scope.Dispose();
        DirectoryCleanup.DeleteRecursively(_testRoot);
    }

    [Fact]
    public async Task A_scope_opened_in_the_constructor_is_visible_to_the_test_body()
    {
        var options = new RedispatchOptions(Path.Combine(_testRoot, "parent"), Path.Combine(_testRoot, "child"));

        var ex = await Assert.ThrowsAsync<CliArgumentException>(
            () => RedispatchCommand.ExecuteAsync(
                options,
                new Dictionary<string, IWorkerAdapter>(),
                TestContext.Current.CancellationToken));

        Assert.Contains(BatonPaths.DrainMarkerFileName, ex.Message, StringComparison.Ordinal);
        Assert.Contains(_testRoot, ex.Message, StringComparison.Ordinal);
    }
}
