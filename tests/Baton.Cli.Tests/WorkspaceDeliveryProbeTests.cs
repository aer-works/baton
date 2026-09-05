using Baton.Cli;
using Baton.Cli.Daemon;

namespace Baton.Cli.Tests;

/// <summary>
/// #1901 C1 items 1 and 3: the settle-time probe that turns a room's bindings into the issue, PR and
/// diff shape its ledger rows carry. Every spawn goes through the injected
/// <see cref="WorkspaceDeliveryProbe.CommandRunner"/>, so nothing here runs <c>git</c>, installs
/// <c>gh</c>, or touches the network — which is also the point: the production path is one seam away
/// from these fixtures rather than a different code path.
/// </summary>
public sealed class WorkspaceDeliveryProbeTests
{
    private const string BindingsJson = """
        {
          "implement": {
            "Adapter": "claude",
            "PromptTemplate": "p",
            "Timeout": "00:30:00",
            "WorkingDirectory": "%WORKSPACE%",
            "Contract": { "DeclaredOutputs": [] }
          }
        }
        """;

    private const string TwoWorkersOneWorkspaceJson = """
        {
          "implement": {
            "Adapter": "claude",
            "PromptTemplate": "p",
            "Timeout": "00:30:00",
            "WorkingDirectory": "%WORKSPACE%",
            "Contract": { "DeclaredOutputs": [] }
          },
          "review": {
            "Adapter": "claude",
            "PromptTemplate": "p",
            "Timeout": "00:30:00",
            "WorkingDirectory": "%WORKSPACE%",
            "Contract": { "DeclaredOutputs": [] }
          }
        }
        """;

    [Fact]
    public async Task A_branch_named_after_an_issue_records_the_issue_the_pr_and_the_diff_shape()
    {
        var (room, workspace) = NewRoomWithWorkspace();
        try
        {
            var delivery = await WorkspaceDeliveryProbe.ReadForRoomAsync(
                room,
                Canned(
                    branch: "1901-lane",
                    prListJson: """[{"number":1907}]""",
                    numstat: "10\t2\tsrc/Baton/Accounting/CostLedgerEntry.cs\n40\t1\ttests/Baton.Tests/Accounting/CostLedgerStoreTests.cs\n-\t-\tdocs/screenshot.png\n"),
                TestContext.Current.CancellationToken);

            var row = Assert.Single(delivery).Value;
            Assert.Equal("1901", row.Issue);
            Assert.Equal("1907", row.PullRequest);
            Assert.Equal(3, row.FilesChanged);

            // The binary row contributes a FILE and no line counts -- 10+40, not 10+40+0-with-a-throw.
            Assert.Equal(50, row.Additions);
            Assert.Equal(3, row.Deletions);
            Assert.Equal(1, row.TestFilesChanged);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// The control arm for the test above: the same room and the same runner shape, with every spawn
    /// failing the way a workspace with no <c>origin/main</c> ref and a missing <c>gh</c> actually
    /// fail. If this returned facts, the arm above would be measuring the fixture rather than the
    /// probe. <b>Named for the failed COMMAND, not for an unpushed branch</b> (#1913 review finding
    /// 3): an unpushed branch still diffs against <c>origin/main</c> and still records a shape, which
    /// is what <c>A_successful_diff_records_the_shape_even_with_no_pr_open_for_the_branch</c> pins.
    /// </summary>
    [Fact]
    public async Task A_failed_diff_and_a_gh_that_cannot_answer_leave_every_fact_absent()
    {
        var (room, workspace) = NewRoomWithWorkspace();
        try
        {
            var delivery = await WorkspaceDeliveryProbe.ReadForRoomAsync(
                room,
                (program, _, args, _) => Task.FromResult(
                    program == "git" && args.Contains("rev-parse")
                        ? new GhCliResult(Started: true, 0, "lane-with-no-issue\n", string.Empty)
                        // `git diff origin/main...HEAD` exits non-zero where there is no origin/main
                        // ref to diff against -- a clone that never fetched it, a fork whose trunk is
                        // named otherwise; `gh` missing from PATH never starts at all.
                        : program == "git"
                            ? new GhCliResult(Started: true, 128, string.Empty, "fatal: bad revision")
                            : new GhCliResult(Started: false, -1, string.Empty, "gh was not found on PATH.")),
                TestContext.Current.CancellationToken);

            var row = Assert.Single(delivery).Value;
            Assert.Null(row.Issue);
            Assert.Null(row.PullRequest);
            Assert.Null(row.FilesChanged);
            Assert.Null(row.Additions);
            Assert.Null(row.Deletions);
            Assert.Null(row.TestFilesChanged);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// A detached HEAD answers <c>rev-parse --abbrev-ref</c> with the literal <c>HEAD</c>, which names
    /// no branch — so there is no issue to derive, nothing to ask <c>gh</c> about, and no base to diff
    /// against. Absent rather than a diff measured against whatever HEAD happened to be.
    /// </summary>
    [Fact]
    public async Task A_detached_head_yields_nothing_rather_than_a_diff_against_an_unnamed_ref()
    {
        var (room, workspace) = NewRoomWithWorkspace();
        try
        {
            var delivery = await WorkspaceDeliveryProbe.ReadForRoomAsync(
                room,
                Canned(branch: "HEAD", prListJson: """[{"number":1907}]""", numstat: "9\t9\tsrc/x.cs\n"),
                TestContext.Current.CancellationToken);

            var row = Assert.Single(delivery).Value;
            Assert.Null(row.Issue);
            Assert.Null(row.PullRequest);
            Assert.Null(row.FilesChanged);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// The #669 worktree-teardown case (<c>Program.cs</c> runs that teardown BEFORE the cost-ledger
    /// append, so a delivered lane can reach here with nothing left on disk): skipped entirely rather
    /// than probed against a path that no longer exists. The spawn counter is what discriminates —
    /// without it, a probe that ran git anyway and swallowed the failure would look identical.
    /// </summary>
    [Fact]
    public async Task A_workspace_that_no_longer_exists_is_skipped_entirely()
    {
        var (room, workspace) = NewRoomWithWorkspace();
        DirectoryCleanup.DeleteRecursively(workspace);
        try
        {
            var spawned = 0;
            var delivery = await WorkspaceDeliveryProbe.ReadForRoomAsync(
                room,
                (_, _, _, _) =>
                {
                    spawned++;
                    return Task.FromResult(new GhCliResult(true, 0, "1901-lane\n", string.Empty));
                },
                TestContext.Current.CancellationToken);

            Assert.Empty(delivery);
            Assert.Equal(0, spawned);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    /// <summary>A room with no bindings file at all resolves to nothing, never an exception — the fail-open floor.</summary>
    [Fact]
    public async Task A_room_with_no_bindings_file_resolves_to_nothing()
    {
        var room = Path.Combine(Path.GetTempPath(), $"delivery-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(room);
        try
        {
            Assert.Empty(await WorkspaceDeliveryProbe.ReadForRoomAsync(
                room,
                (_, _, _, _) => throw new InvalidOperationException("must not spawn"),
                TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    /// <summary>
    /// #1913 review finding 2: a spawn that never answers is ABANDONED at its bound, and costs only
    /// the facts it would have produced. The one that hangs here is the diff, so the arm also
    /// discriminates: a bound that killed the whole probe would lose the issue and PR too, and a bound
    /// that did not fire would never return at all — which is why the test carries an xUnit timeout
    /// rather than asserting on elapsed time. An assertion after a hang is unreachable.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task A_spawn_that_never_answers_is_abandoned_at_its_bound_and_costs_only_its_own_facts()
    {
        var (room, workspace) = NewRoomWithWorkspace();
        try
        {
            var delivery = await WorkspaceDeliveryProbe.ReadForRoomAsync(
                room,
                (program, _, args, token) => program == "git" && args.Contains("--numstat")
                    // Ignores its token on purpose: the seam has to survive a runner that does not
                    // honour cancellation, which is what a wedged child process amounts to.
                    ? new TaskCompletionSource<GhCliResult>().Task
                    : Task.FromResult(program == "gh"
                        ? new GhCliResult(Started: true, 0, """[{"number":1913}]""", string.Empty)
                        : new GhCliResult(Started: true, 0, "1901-lane\n", string.Empty)),
                TestContext.Current.CancellationToken,
                spawnTimeout: TimeSpan.FromMilliseconds(250));

            var row = Assert.Single(delivery).Value;
            Assert.Equal("1901", row.Issue);
            Assert.Equal("1913", row.PullRequest);
            Assert.Null(row.FilesChanged);
            Assert.Null(row.Additions);
            Assert.Null(row.Deletions);
            Assert.Null(row.TestFilesChanged);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// #1913 review finding 2, the cancellation half: the settle site now hands this probe the host's
    /// own token, so a Ctrl-C reaches it. Cancelled, it still resolves to an answer — the absence
    /// every other failure here produces — rather than throwing into a settle whose ledger row has yet
    /// to be written.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task A_host_cancellation_mid_probe_costs_the_facts_and_never_throws()
    {
        var (room, workspace) = NewRoomWithWorkspace();

        // Linked to the test's own token so this arm still terminates on the [Fact] timeout if the
        // bound regresses -- the runner below never completes on its own.
        using var host = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            // Cancelled from INSIDE the first spawn, not before the call: the bindings read has already
            // succeeded, so what this arm exercises is a Ctrl-C reaching a spawn in flight -- the case
            // that could not happen at all while the settle site passed CancellationToken.None.
            var delivery = await WorkspaceDeliveryProbe.ReadForRoomAsync(
                room,
                (_, _, _, _) =>
                {
                    host.Cancel();
                    return new TaskCompletionSource<GhCliResult>().Task;
                },
                host.Token);

            var row = Assert.Single(delivery).Value;
            Assert.Null(row.Issue);
            Assert.Null(row.PullRequest);
            Assert.Null(row.FilesChanged);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// #1913 review finding 9(a): "one probe per distinct workspace, not per worker" had no test —
    /// every fixture was a one-worker room, so the cache was never exercised. Two workers sharing a
    /// directory spawn the same three commands once, and both rows carry the same answer.
    /// </summary>
    [Fact]
    public async Task Two_workers_sharing_a_workspace_are_probed_once_between_them()
    {
        var (room, workspace) = NewRoomWithWorkspace(TwoWorkersOneWorkspaceJson);
        try
        {
            var spawned = 0;
            var canned = Canned(branch: "1901-lane", prListJson: """[{"number":1913}]""", numstat: "1\t0\tsrc/x.cs\n");
            var delivery = await WorkspaceDeliveryProbe.ReadForRoomAsync(
                room,
                (program, directory, args, token) =>
                {
                    spawned++;
                    return canned(program, directory, args, token);
                },
                TestContext.Current.CancellationToken);

            Assert.Equal(2, delivery.Count);
            Assert.Equal(delivery["implement"], delivery["review"]);
            Assert.Equal("1901", delivery["implement"].Issue);

            // Three commands (rev-parse, diff, pr list) for one workspace -- not six for two workers.
            Assert.Equal(3, spawned);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// #1913 review finding 9(b): the argv itself, which nothing pinned — the canned runners
    /// discriminate on <c>rev-parse</c> and the program name alone, so a base ref that moved off
    /// <c>origin/main</c>, a <c>--numstat</c> that became <c>--shortstat</c>, or <c>gh</c> arguments
    /// that drifted would all still pass them. Three commands, in order, verbatim.
    /// </summary>
    [Fact]
    public async Task Every_spawn_asks_exactly_the_command_this_probe_documents()
    {
        var (room, workspace) = NewRoomWithWorkspace();
        try
        {
            var commands = new List<string>();
            await WorkspaceDeliveryProbe.ReadForRoomAsync(
                room,
                (program, directory, args, _) =>
                {
                    Assert.Equal(workspace, directory);
                    commands.Add($"{program} {string.Join(' ', args)}");
                    return Task.FromResult(program == "gh"
                        ? new GhCliResult(Started: true, 0, "[]", string.Empty)
                        : new GhCliResult(Started: true, 0, args.Contains("rev-parse") ? "1901-lane\n" : string.Empty, string.Empty));
                },
                TestContext.Current.CancellationToken);

            Assert.Equal(
                [
                    "git rev-parse --abbrev-ref HEAD",

                    // --no-renames and core.quotePath=false are load-bearing, not tidiness: with
                    // either default in force a changed path is not a path (`src/{a => tests/b}`, or a
                    // C-quoted non-ASCII one) and the tests/ prefix count silently misses it.
                    "git -c core.quotePath=false diff --numstat --no-renames origin/main...HEAD",
                    "gh pr list --head 1901-lane --json number",
                ],
                commands);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// #1913 review finding 3: a successful diff records a shape whether or not anything was ever
    /// pushed (<c>ReadDiffShapeAsync</c>'s own doc says what it measures), and the absence arm above
    /// is produced by a FAILED diff instead. An injected runner cannot model push state, so what this
    /// fixture asserts is the half it can: <c>gh</c> answers "no PR for this branch" while the diff
    /// succeeds, and the row records the work with no delivery beside it.
    /// </summary>
    [Fact]
    public async Task A_successful_diff_records_the_shape_even_with_no_pr_open_for_the_branch()
    {
        var (room, workspace) = NewRoomWithWorkspace();
        try
        {
            var delivery = await WorkspaceDeliveryProbe.ReadForRoomAsync(
                room,
                Canned(branch: "1901-lane", prListJson: "[]", numstat: "3\t1\tsrc/x.cs\n2\t0\ttests/y.cs\n"),
                TestContext.Current.CancellationToken);

            var row = Assert.Single(delivery).Value;
            Assert.Null(row.PullRequest);
            Assert.Equal(2, row.FilesChanged);
            Assert.Equal(5, row.Additions);
            Assert.Equal(1, row.Deletions);
            Assert.Equal(1, row.TestFilesChanged);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// #1913 review finding 9(c): a path git C-quoted anyway — the residue
    /// <see cref="WorkspaceDeliveryProbe"/>'s own comment names, which <c>core.quotePath=false</c>
    /// does not cover — still counts towards <c>testFilesChanged</c>. The control is the second row: a
    /// quoted path OUTSIDE <c>tests/</c> must not start counting just because the quote was stripped.
    /// </summary>
    [Fact]
    public async Task A_c_quoted_path_under_tests_still_counts_as_a_test_file()
    {
        var (room, workspace) = NewRoomWithWorkspace();
        try
        {
            var delivery = await WorkspaceDeliveryProbe.ReadForRoomAsync(
                room,
                Canned(
                    branch: "1901-lane",
                    prListJson: "[]",
                    numstat: "1\t0\t\"tests/Baton.Tests/caf\\303\\251.cs\"\n1\t0\t\"src/caf\\303\\251.cs\"\n"),
                TestContext.Current.CancellationToken);

            var row = Assert.Single(delivery).Value;
            Assert.Equal(2, row.FilesChanged);
            Assert.Equal(1, row.TestFilesChanged);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    [Theory]
    [InlineData("1901-lane", "1901")]
    [InlineData("1901-populate-issue-pr", "1901")]
    [InlineData("main", null)]
    [InlineData("1901", null)]
    [InlineData("feature-1901", null)]
    [InlineData("-1901", null)]
    public void An_issue_is_read_only_from_a_leading_number_followed_by_a_separator(string branch, string? expected) =>
        Assert.Equal(expected, WorkspaceDeliveryProbe.TryReadIssueNumber(branch));

    private static WorkspaceDeliveryProbe.CommandRunner Canned(string branch, string prListJson, string numstat) =>
        (program, _, args, _) => Task.FromResult(
            program == "gh"
                ? new GhCliResult(Started: true, 0, prListJson, string.Empty)
                : args.Contains("rev-parse")
                    ? new GhCliResult(Started: true, 0, branch + "\n", string.Empty)
                    : new GhCliResult(Started: true, 0, numstat, string.Empty));

    /// <summary>A room directory holding a bindings file that points at a real (empty) workspace directory.</summary>
    private static (string Room, string Workspace) NewRoomWithWorkspace(string? bindings = null)
    {
        var room = Path.Combine(Path.GetTempPath(), $"delivery-probe-{Guid.NewGuid():N}");
        var workspace = Path.Combine(Path.GetTempPath(), $"delivery-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(room);
        Directory.CreateDirectory(workspace);
        File.WriteAllText(
            Path.Combine(room, "bindings.json"),
            (bindings ?? BindingsJson).Replace(
                "%WORKSPACE%", workspace.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal));
        return (room, workspace);
    }
}
