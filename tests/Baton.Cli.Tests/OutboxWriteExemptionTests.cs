namespace Baton.Cli.Tests;

/// <summary>
/// #649: a worker whose grant withholds writes must still be able to write its declared output. The
/// outbox is AER's own directory, outside the workspace — withholding "modify the workspace" was
/// never meant to withhold "write your report", and that conflation is why every reviewing template
/// grants a workspace write it does not need.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public class OutboxWriteExemptionTests
{
    private static readonly string Outbox =
        Path.Combine(Path.GetTempPath(), "baton-task", "artifacts", "execution_1");

    /// <summary>
    /// #679's workspace. Deliberately NOT a parent of <see cref="Outbox"/>: the two arms of the bound
    /// have to be separable, or a test cannot tell which one allowed a write.
    /// </summary>
    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "baton-workspace");

    private static int Decide(string toolName, string? targetPath, string? outbox = null)
    {
        var payload = Payload(toolName, targetPath is null ? null : new { file_path = targetPath });

        using var stderr = new StringWriter();
        return HookCheckCommand.Execute(new StringReader(payload), stderr, "claude:Edit,Write,NotebookEdit", outbox ?? Outbox);
    }

    [Fact]
    public void A_withheld_write_into_the_outbox_is_allowed()
    {
        // The deliverable. Without this a read-only reviewer cannot produce the artifact it was
        // dispatched to produce, which is what #629 now refuses at bind time.
        Assert.Equal(HookCheckCommand.AllowedExitCode, Decide("Write", Path.Combine(Outbox, "review.md")));
    }

    [Fact]
    public void A_withheld_write_into_the_workspace_is_still_denied()
    {
        // The polarity control, and the one that matters: without it everything above passes on a hook
        // that stopped enforcing writes altogether, which is the whole grant becoming decorative.
        Assert.Equal(
            HookCheckCommand.DeniedExitCode,
            Decide("Write", Path.Combine(Path.GetTempPath(), "repo", "src", "Program.cs")));
    }

    [Fact]
    public void A_traversal_out_of_the_outbox_is_denied()
    {
        Assert.Equal(
            HookCheckCommand.DeniedExitCode,
            Decide("Write", Path.Combine(Outbox, "..", "..", "..", "repo", "src", "Program.cs")));
    }

    [Fact]
    public void A_notebook_edit_targets_its_own_property_name()
    {
        // NotebookEdit carries notebook_path, not file_path. Reading only file_path would deny a
        // legitimate outbox write for a reason that has nothing to do with the grant.
        var payload = Payload("NotebookEdit", new { notebook_path = Path.Combine(Outbox, "n.ipynb") });
        using var stderr = new StringWriter();

        Assert.Equal(
            HookCheckCommand.AllowedExitCode,
            HookCheckCommand.Execute(new StringReader(payload), stderr, "claude:Edit,Write,NotebookEdit", Outbox));
    }

    [Fact]
    public void A_withheld_tool_with_no_path_argument_is_still_denied()
    {
        // Bash is withheld by name and has no target for the exemption to apply to. A hook that
        // allowed on a missing path would turn every withheld non-write tool into an allow.
        //
        // Scope, because the name suggests more than it proves: this guards
        // IsInside(null, ...) == false and nothing else. It passes with or without the
        // tool-name gate, since Bash carries no file_path either way — the gate itself is guarded by
        // The_exemption_covers_writes_only_not_every_tool_carrying_a_file_path.
        using var stderr = new StringWriter();
        var exitCode = HookCheckCommand.Execute(
            new StringReader(Payload("Bash", new { command = "rm -rf /" })),
            stderr, "claude:Bash", Outbox);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
    }

    [Fact]
    public void With_no_outbox_known_the_exemption_does_not_apply()
    {
        // Fails closed. A hook that cannot tell where the outbox is denies exactly as it did before
        // this exemption existed.
        using var stderr = new StringWriter();
        var exitCode = HookCheckCommand.Execute(
            new StringReader(Payload("Write", new { file_path = Path.Combine(Outbox, "review.md") })),
            stderr, "claude:Edit,Write,NotebookEdit", outboxDirectory: null);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
    }

    [Fact]
    public void The_exemption_covers_writes_only_not_every_tool_carrying_a_file_path()
    {
        // Read carries a file_path too. Keying the exemption off the field rather than the tool name
        // silently exempted reads inside the outbox from a withheld ReadFiles — a category #649 never
        // claimed. The withheld list here grants writes and withholds reads, which is the shape that
        // separates the two.
        using var stderr = new StringWriter();
        var exitCode = HookCheckCommand.Execute(
            new StringReader(Payload("Read", new { file_path = Path.Combine(Outbox, "review.md") })),
            stderr, "claude:Read", Outbox);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
    }

    [Fact]
    public void A_relative_outbox_is_refused_rather_than_resolved_against_the_workers_cwd()
    {
        // Measured on a live run: a relative --room-dir emitted BATON_OUTPUT_DIR as
        // `task2\artifacts\execution_<id>`. This process inherits the vendor CLI's cwd, which is the
        // workspace, so resolving it here certified a directory *inside the workspace* as the outbox
        // and allowed the write. The worker's report landed there, AER looked at the real path, found
        // nothing, and failed the contract after paying for the run in full.
        const string relative = @"task2\artifacts\execution_1";

        Assert.False(OutboxPath.IsInside(Path.Combine(relative, "review.md"), relative));

        // And the operator is told which of the two things went wrong. The generic withheld-tool
        // message sends them to their permission grant for a fault that is in their --room-dir.
        // record-once-ok: #443 src/Baton.Cli/HookCheckCommand.cs
        using var stderr = new StringWriter();
        var exitCode = HookCheckCommand.Execute(
            new StringReader(Payload("Write", new { file_path = Path.Combine(relative, "review.md") })),
            stderr, "claude:Edit,Write,NotebookEdit", relative);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Contains("not an absolute path", stderr.ToString(), StringComparison.Ordinal);

        // Control: the same shape rooted, which is what AER actually emits, still resolves.
        var rooted = Path.Combine(Path.GetTempPath(), "baton-task", "artifacts", "execution_1");
        Assert.True(OutboxPath.IsInside(Path.Combine(rooted, "review.md"), rooted));
    }

    [Fact]
    public void A_dangling_link_inside_the_outbox_cannot_launder_a_workspace_write()
    {
        // Directory.Exists and File.Exists both stat THROUGH a link, so a link whose target does not
        // exist yet answers false to both. Resolution keyed on those checks therefore appends the
        // link component unresolved and reports the path as contained. The worker's prompt already
        // tells it to create parent directories as needed, so the write creates the target through
        // the link — a workspace write laundered through the outbox.
        var root = Directory.CreateTempSubdirectory("baton-outbox-dangling-").FullName;
        try
        {
            var outbox = Directory.CreateDirectory(Path.Combine(root, "artifacts", "execution_1")).FullName;
            var neverCreated = Path.Combine(root, "repo", "src");

            var link = Path.Combine(outbox, "escape");
            try
            {
                Directory.CreateSymbolicLink(link, neverCreated);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            // The premise, and it is platform-split — measured, not assumed. On POSIX, Exists calls
            // stat, which follows the link, so a dangling one reports false and a resolver keyed on
            // Exists treats it as "not a link". Windows reports the reparse point itself as existing,
            // so the hole never opens there. The assertion is scoped to the platforms where it is the
            // premise; CI's Linux and macOS legs are what actually exercise this case.
            if (!OperatingSystem.IsWindows())
            {
                Assert.False(Directory.Exists(link));
            }

            Assert.False(OutboxPath.IsInside(Path.Combine(link, "Program.cs"), outbox));
            Assert.True(OutboxPath.IsInside(Path.Combine(outbox, "review.md"), outbox));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public void A_link_partway_along_the_path_cannot_launder_a_workspace_write()
    {
        // The shape OutboxPath's own remarks name as the dangerous one — a link mid-path whose final
        // component is an ordinary file — and which the last-position test cannot exercise.
        var root = Directory.CreateTempSubdirectory("baton-outbox-midlink-").FullName;
        try
        {
            var outbox = Directory.CreateDirectory(Path.Combine(root, "artifacts", "execution_1")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root, "repo", "src", "deep")).FullName;
            File.WriteAllText(Path.Combine(workspace, "Program.cs"), "// real file");

            var link = Path.Combine(outbox, "hop");
            try
            {
                Directory.CreateSymbolicLink(link, Path.Combine(root, "repo", "src"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            Assert.False(OutboxPath.IsInside(Path.Combine(link, "deep", "Program.cs"), outbox));
            Assert.True(OutboxPath.IsInside(Path.Combine(outbox, "review.md"), outbox));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public void A_link_planted_inside_the_outbox_cannot_launder_a_workspace_write()
    {
        // Path.GetFullPath normalises `..` textually and never follows a link, so a prefix comparison
        // on its output reports a path *through* a link as inside the outbox while the write lands
        // wherever the link points. Demonstrated on a real directory link rather than argued.
        var root = Directory.CreateTempSubdirectory("baton-outbox-link-").FullName;
        try
        {
            var outbox = Directory.CreateDirectory(Path.Combine(root, "artifacts", "execution_1")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root, "repo", "src")).FullName;

            var link = Path.Combine(outbox, "escape");
            try
            {
                Directory.CreateSymbolicLink(link, workspace);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Windows needs Developer Mode or elevation to create one. The Linux and macOS CI
                // legs carry this assertion; skipping here beats asserting nothing anywhere.
                return;
            }

            var throughTheLink = Path.Combine(link, "Program.cs");

            Assert.False(OutboxPath.IsInside(throughTheLink, outbox));
            // The control: the same outbox, a target that really is inside it. Without this, a
            // resolver that answered false for everything would pass the assertion above.
            Assert.True(OutboxPath.IsInside(Path.Combine(outbox, "review.md"), outbox));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public void A_link_whose_target_path_contains_another_link_cannot_launder_a_write()
    {
        // The measured bypass from #679's review, and the one shape the two tests above cannot reach:
        // both plant a single link, which one resolution pass handles. Here the FIRST link's target
        // is `<ws>/pub/x.txt` — a path that is itself routed through a second link. Resolving `hop`
        // substitutes that target wholesale, and a single-pass walk never revisits `pub`, so the
        // answer came back `<ws>/pub/x.txt`, prefix-matched the workspace, and reported contained
        // while the bytes landed outside it.
        //
        // The direct form (`<ws>/pub/x.txt`, no `hop`) denied correctly the whole time, which is what
        // identifies the second link as the launderer rather than the first. It is asserted below as
        // the discriminating control: a resolver that answers false for everything passes the first
        // assertion, and one that never regressed passes both.
        var root = Directory.CreateTempSubdirectory("baton-outbox-chain-").FullName;
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "ws")).FullName;
            var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            File.WriteAllText(Path.Combine(outside, "x.txt"), "landed outside");

            var secondLink = Path.Combine(workspace, "pub");
            var firstLink = Path.Combine(workspace, "hop");
            try
            {
                Directory.CreateSymbolicLink(secondLink, outside);
                File.CreateSymbolicLink(firstLink, Path.Combine(secondLink, "x.txt"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Windows needs Developer Mode or elevation. Skipped LOUDLY rather than returning
                // silently like the two tests above: this is the only arm covering a measured escape,
                // and a silent return makes an uncovered run look identical to a covered one.
                Assert.Skip("this host cannot create symbolic links; the Linux and macOS legs assert it");
                return;
            }

            Assert.False(OutboxPath.IsInside(firstLink, workspace));
            Assert.False(OutboxPath.IsInside(Path.Combine(secondLink, "x.txt"), workspace));
            Assert.True(OutboxPath.IsInside(Path.Combine(workspace, "src", "real.cs"), workspace));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public void A_link_cycle_denies_rather_than_resolving_to_where_it_gave_up()
    {
        // The hop limit existed to stop this spinning, and both it and the pass limit above then
        // returned the path they had reached — justified in a comment claiming such a path "cannot
        // match a root prefix by construction". For a cycle that is false in the worst direction: two
        // links inside the workspace pointing at each other land back on the starting path after an
        // even number of hops, so the resolver returned a path inside the root and ALLOWED, and the
        // outer loop could not tell that from a path needing no resolution at all.
        //
        // Denying is the only defensible answer, and it costs nothing real: the OS refuses to open a
        // cycle anyway, so no legitimate write is being turned away here.
        var root = Directory.CreateTempSubdirectory("baton-outbox-cycle-").FullName;
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "ws")).FullName;
            var a = Path.Combine(workspace, "a");
            var b = Path.Combine(workspace, "b");
            try
            {
                Directory.CreateSymbolicLink(a, b);
                Directory.CreateSymbolicLink(b, a);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Assert.Skip("this host cannot create symbolic links; the Linux and macOS legs assert it");
                return;
            }

            Assert.False(OutboxPath.IsInside(Path.Combine(a, "x.txt"), workspace));
            // Both controls matter: the same workspace still admits a real path, and a cycle in the
            // ROOT is unanswerable too — otherwise only the candidate side would be covered.
            Assert.True(OutboxPath.IsInside(Path.Combine(workspace, "src", "real.cs"), workspace));
            Assert.False(OutboxPath.IsInside(Path.Combine(workspace, "src", "real.cs"), a));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// #679 inverted: a grant decides whether a worker may write, never where. This test asserted the
    /// opposite until the bound existed — it was the characterisation of the defect, and the fix is
    /// what turned it red.
    /// </summary>
    [Fact]
    public void A_granted_write_outside_the_workspace_and_the_outbox_is_denied()
    {
        using var stderr = new StringWriter();
        var somewhereElse = Path.Combine(Path.GetTempPath(), "not-the-workspace", "anything.txt");

        Assert.Equal(
            HookCheckCommand.DeniedExitCode,
            HookCheckCommand.Execute(
                new StringReader(Payload("Write", new { file_path = somewhereElse })),
                stderr, "claude:Bash", Outbox, Workspace));

        // Two controls, because the assertion above has two ways to pass for the wrong reason. The
        // first: the same granted tool writing INSIDE the workspace, which must still be allowed —
        // without it a gate that denied every write would satisfy the assertion. The second: the same
        // path with Write withheld, which keeps this a statement about where rather than whether.
        using var inside = new StringWriter();
        Assert.Equal(
            HookCheckCommand.AllowedExitCode,
            HookCheckCommand.Execute(
                new StringReader(Payload("Write", new { file_path = Path.Combine(Workspace, "src", "x.cs") })),
                inside, "claude:Bash", Outbox, Workspace));
        Assert.Equal(HookCheckCommand.DeniedExitCode, Decide("Write", somewhereElse));
    }

    /// <summary>
    /// A granted write still reaches the outbox. The outbox is not inside the workspace, so a bound
    /// written as "workspace only" would pass every assertion above and break the deliverable every
    /// dispatch exists to produce.
    /// </summary>
    [Fact]
    public void A_granted_write_into_the_outbox_is_allowed_even_though_it_is_outside_the_workspace()
    {
        using var stderr = new StringWriter();

        Assert.Equal(
            HookCheckCommand.AllowedExitCode,
            HookCheckCommand.Execute(
                new StringReader(Payload("Write", new { file_path = Path.Combine(Outbox, "review.md") })),
                stderr, "claude:Bash", Outbox, Workspace));

        // The control: the outbox really is outside the workspace, so the allow above came from the
        // outbox arm of the bound and not from the workspace arm answering for it.
        Assert.False(OutboxPath.IsInside(Path.Combine(Outbox, "review.md"), Workspace));
    }

    /// <summary>
    /// With no workspace declared, a granted write is narrowed to the outbox rather than left
    /// unbounded — the decision recorded on #679 for the directory-less shape.
    /// </summary>
    [Fact]
    public void A_granted_write_with_no_workspace_declared_is_bounded_to_the_outbox()
    {
        using var stderr = new StringWriter();

        Assert.Equal(
            HookCheckCommand.DeniedExitCode,
            HookCheckCommand.Execute(
                new StringReader(Payload("Write", new { file_path = Path.Combine(Workspace, "src", "x.cs") })),
                stderr, "claude:Bash", Outbox, workspaceDirectory: null));

        // The control: the same null workspace, writing into the outbox, still allowed. Without it a
        // null workspace denying everything would satisfy the assertion above.
        using var outbox = new StringWriter();
        Assert.Equal(
            HookCheckCommand.AllowedExitCode,
            HookCheckCommand.Execute(
                new StringReader(Payload("Write", new { file_path = Path.Combine(Outbox, "review.md") })),
                outbox, "claude:Bash", Outbox, workspaceDirectory: null));
    }

    /// <summary>
    /// A write-family tool whose target this gate cannot read is denied rather than allowed. This is
    /// the condition `agy.hook-payload-carries-write-path` is recorded as non-sentinel on: a payload
    /// that stopped naming the target would break loudly here instead of going silently unbounded.
    /// </summary>
    [Fact]
    public void A_granted_write_whose_target_cannot_be_read_from_the_payload_is_denied()
    {
        using var stderr = new StringWriter();

        Assert.Equal(
            HookCheckCommand.DeniedExitCode,
            HookCheckCommand.Execute(
                new StringReader(Payload("Write", new { unexpected_key = "somewhere" })),
                stderr, "claude:Bash", Outbox, Workspace));

        // The control: an identical payload for a tool that is NOT write-family is still allowed, so
        // the denial above is about an unreadable write target and not about the odd key.
        using var nonWrite = new StringWriter();
        Assert.Equal(
            HookCheckCommand.AllowedExitCode,
            HookCheckCommand.Execute(
                new StringReader(Payload("Read", new { unexpected_key = "somewhere" })),
                nonWrite, "claude:Bash", Outbox, Workspace));
    }

    /// <summary>
    /// Builds a hook payload through the serializer rather than as a raw string literal, so a JSON
    /// brace never has to be escaped against C#'s own interpolation syntax — which is a way to write
    /// a test that passes for the wrong reason.
    /// </summary>
    private static string Payload(string toolName, object? toolInput) =>
        System.Text.Json.JsonSerializer.Serialize(
            toolInput is null ? new { tool_name = toolName } : (object)new { tool_name = toolName, tool_input = toolInput });
}
