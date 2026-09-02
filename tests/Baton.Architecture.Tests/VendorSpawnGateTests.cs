namespace Baton.Architecture.Tests;

/// <summary>
/// #703's enforcement half. The invariant is one sentence — <b>AER must never spawn a vendor CLI
/// worker where its <c>PreToolUse</c> gate does not fire</b> (decision 0029) — and it was false on a
/// whole spawn path for months because nothing checked. Making it true once is worth little; this is
/// what makes a NEW ungated spawn fail the build instead of waiting for a review to notice.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it checks:</b> every site in <c>src/</c> that starts a process is on a reviewed list. It
/// deliberately does not try to decide whether a given site is gated — that is a property of the
/// arguments built at runtime, which no file scan can honestly assert. It asserts the weaker, real
/// thing: the set of places a process can be spawned does not grow silently.
/// </para>
/// <para>
/// <b>Its false negatives, named rather than left for someone to discover.</b> A check that reads as
/// enforcement while being trivially sidesteppable is worse than none.
/// </para>
/// <list type="number">
/// <item>Reflection or a delegate (<c>typeof(Process).GetMethod("Start")</c>) matches no text here.</item>
/// <item>An approved site spawning something that itself spawns a vendor CLI — a shell script, the
/// Go sidecar — is a grandchild this cannot see.</item>
/// <item>An approved site silently dropping its gate arguments — each adapter's own <c>Resolve</c>
/// tests cover the two shipped adapters; nothing covers a third that has not been written.</item>
/// </list>
/// <para>
/// Pure file reading over the repo, no project references, matching <see cref="ReferenceDirectionTests"/>.
/// </para>
/// </remarks>
public class VendorSpawnGateTests
{
    /// <summary>
    /// Every file permitted to start a process, with why it is not an ungated vendor spawn. Adding a
    /// line here is the deliberate act this test exists to force — and it is a review prompt, not a
    /// formality: if the new site spawns <c>claude</c> or <c>agy</c>, it needs the mandatory
    /// <c>PreToolUse</c> hook (decision 0029) wired the way each adapter's own <c>Resolve</c> wires it
    /// before it belongs on this list.
    /// </summary>
    private static readonly Dictionary<string, string> ApprovedSpawnSites = new()
    {
        ["src/Baton/Dispatch/CoreDispatcher.cs"] = "The gated dispatch path. Adapters build the gate into the target.",
        ["src/Baton/Core/Internal/BatonProcessRunner.cs"] = "The managed spawn primitive BatonTask.Run/RunAsync bottoms out into (#1474). Previously invisible to this scan -- the same spawn happened across the FFI boundary inside native/core's Rust Command::new -- now visible because the port is plain C#. Gating happens upstream: an adapter builds the PreToolUse gate into the CoreDispatchTarget before CoreDispatcher ever constructs a BatonTask, so this file spawns whatever CoreDispatcher hands it, already gated.",
        ["src/Baton.Vendors/AgyWorkerAdapter.cs"] = "Read-only agy registry queries (models/agent/plugin list) — no -p, no tool execution.",
        ["src/Baton.Cli/WorkspaceHead.cs"] = "Read-only 'git rev-parse HEAD' to capture a capture step's base ref — git, not a vendor CLI; no -p, no tool execution.",
        ["src/Baton/Workspaces/WorktreeProvisioner.cs"] = "'git worktree add/remove' plus 'git status' to provision and tear down a worker's workspace (#669) — git, not a vendor CLI; spawns no vendor process.",
        ["src/Baton/Mutation/VerifyRunner.cs"] = "#1623: the engine-run verify step. Spawns 'pixi run <task>' (e.g. gates-quiet) after a worker's own execution already exited 0 with a satisfied contract — never a vendor CLI, and never invoked from inside a worker's own turn.",
        ["src/Baton.Cli/WorkstreamJunctionLinker.cs"] = "'cmd.exe /c mklink /J' to create a --workstream navigation link (#1619) — a Windows shell built-in, not a vendor CLI; no -p, no tool execution, spawns no vendor process.",
    };

    private static readonly string[] SpawnMarkers = ["new ProcessStartInfo", "Process.Start", "new BatonTask"];

    [Fact]
    public void No_unreviewed_site_in_src_can_start_a_process()
    {
        var root = RepoRoot();
        var found = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => SpawnMarkers.Any(marker => File.ReadAllText(path).Contains(marker, StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        var unreviewed = found.Where(path => !ApprovedSpawnSites.ContainsKey(path)).ToList();
        Assert.True(
            unreviewed.Count == 0,
            "A new process-spawn site appeared in src/:\n  " + string.Join("\n  ", unreviewed)
            + "\n\nIf it can spawn a vendor CLI it needs the mandatory PreToolUse hook first — decision "
            + "0029 makes that hook mandatory on every worker AER spawns, and #703 is what happens when a "
            + "path skips it. Then add it to ApprovedSpawnSites with the reason it is safe.");

        // The other direction, so the list cannot rot into naming files that no longer spawn anything
        // and quietly stop meaning what it says.
        var stale = ApprovedSpawnSites.Keys.Where(path => !found.Contains(path)).ToList();
        Assert.True(
            stale.Count == 0,
            "ApprovedSpawnSites names files that no longer start a process:\n  " + string.Join("\n  ", stale));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pixi.toml")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }
}
