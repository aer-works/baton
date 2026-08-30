using System.Diagnostics;
using Baton.Flow.Dispatch;
using Baton.Flow.Domain;
using Baton.Flow.Outcomes;
using Baton.Flow.Status;

namespace Baton.Vendors.Tests;


/// <summary>
/// M20 Phase 4's deliverable: unit tests for the refactored, direct shell-less
/// <see cref="ClaudeWorkerAdapter"/> resolving.
/// </summary>
[Collection(LaunchConfigCollection.Name)]
public class ClaudeWorkerAdapterTests
{
    private static readonly WorkerContract ArchitectContract = new(
        "architect", ["goal"], [new ProducedOutput("plan.md")], []);

    private static string GetPrompt(CoreDispatchTarget target) => target.Args[1];

    /// <summary>The value token immediately after <paramref name="flag"/> in the flat argv, or null.</summary>
    private static string? ArgValue(CoreDispatchTarget target, string flag)
    {
        for (var i = 0; i < target.Args.Count - 1; i++)
        {
            if (target.Args[i] == flag)
            {
                return target.Args[i + 1];
            }
        }

        return null;
    }

    [Fact]
    public void Resolves_to_direct_claude_execution_without_shell_wrapper()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Equal("claude", target.Program);
        Assert.Equal("-p", target.Args[0]);
        Assert.Equal("--allowedTools", target.Args[2]);
        Assert.Equal("Write", target.Args[3]);
        Assert.Equal("--add-dir", target.Args[4]);
        // #533 inserted --settings/--mcp-config after --add-dir's value; positional indices past
        // that point are no longer stable, so this uses the order-independent helper like every
        // newer test in this file already does.
        Assert.Equal("text", ArgValue(target, "--output-format"));
    }

    [Fact]
    public void Resolve_sets_OversizePromptWrapper_referencing_BATON_PROMPT_FILE()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);
        Assert.NotNull(target.OversizePromptWrapper);
        Assert.Contains("%BATON_PROMPT_FILE%", target.OversizePromptWrapper);
    }

    /// <summary>
    /// #289: Claude Code's own directory-trust sandbox (separate from --allowedTools) was found,
    /// via a live run against the real authenticated CLI, to non-deterministically refuse to write
    /// BATON_OUTPUT_DIR when it falls outside the spawned process's cwd -- which it always does for a
    /// plain chat session with no WorkingDirectory. --add-dir BATON_ARTIFACTS_ROOT (the same grant
    /// AgyWorkerAdapter already carries for agy, per ArtifactManager.BuildEnvironment's own doc
    /// comment) eliminated the failure across every trial once added.
    /// </summary>
    [Fact]
    public void The_artifacts_root_is_granted_via_add_dir_so_output_writes_outside_cwd_are_trusted()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Equal("--add-dir", target.Args[4]);
        var artifactsRootVar = OperatingSystem.IsWindows() ? "%BATON_ARTIFACTS_ROOT%" : "$BATON_ARTIFACTS_ROOT";
        Assert.Equal(artifactsRootVar, target.Args[5]);
    }

    /// <summary>M23 Phase 3 (#272): WorkingDirectory carries no vendor-specific meaning — every adapter forwards it into CoreDispatchTarget unchanged.</summary>
    [Fact]
    public void A_configured_WorkingDirectory_is_forwarded_into_the_resolved_target()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", WorkingDirectory: "/home/user/my-project"), ArchitectContract);

        Assert.Equal("/home/user/my-project", target.WorkingDirectory);
    }

    [Fact]
    public void A_null_WorkingDirectory_leaves_the_resolved_target_with_no_explicit_cwd()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Null(target.WorkingDirectory);
    }

    [Fact]
    public void An_explicit_permission_scope_overrides_the_default()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Bash(git:*)"), ArchitectContract);

        Assert.Equal("Write,Bash(git:*)", target.Args[3]);
    }

    [Fact]
    public void A_model_is_passed_through_when_set()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Model: "claude-opus-4-5"), ArchitectContract);

        Assert.Equal("claude-opus-4-5", ArgValue(target, "--model"));
    }

    [Fact]
    public void No_model_flag_is_emitted_when_the_model_is_unset()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain("--model", target.Args);
    }

    [Fact]
    public void An_effort_is_passed_through_when_set()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Effort: "high"), ArchitectContract);

        Assert.Equal("high", ArgValue(target, "--effort"));
    }

    [Fact]
    public void No_effort_flag_is_emitted_when_the_effort_is_unset()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain("--effort", target.Args);
    }

    [Fact]
    public void The_prompt_names_every_declared_output_and_its_env_var_path()
    {
        var contract = new WorkerContract(
            "architect", [], [new ProducedOutput("plan.md"), new ProducedOutput("summary.md")], []);

        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        var prompt = GetPrompt(target);
        var outputVar = OperatingSystem.IsWindows() ? "%BATON_OUTPUT_DIR%" : "$BATON_OUTPUT_DIR";
        var separator = OperatingSystem.IsWindows() ? '\\' : '/';
        Assert.Contains($"plan.md: {outputVar}{separator}plan.md", prompt);
        Assert.Contains($"summary.md: {outputVar}{separator}summary.md", prompt);
    }

    [Fact]
    public void The_prompt_names_every_required_input_and_its_env_var_path()
    {
        var contract = new WorkerContract(
            "critic", ["plan", "guidelines"], [new ProducedOutput("review.md")], []);

        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Review the plan."), contract);

        var prompt = GetPrompt(target);
        var inputVar0 = OperatingSystem.IsWindows() ? "%BATON_INPUT_0%" : "$BATON_INPUT_0";
        var inputVar1 = OperatingSystem.IsWindows() ? "%BATON_INPUT_1%" : "$BATON_INPUT_1";
        Assert.Contains($"plan: {inputVar0}", prompt);
        Assert.Contains($"guidelines: {inputVar1}", prompt);
    }

    [Fact]
    public void A_contract_with_no_inputs_omits_the_inputs_section()
    {
        var contract = new WorkerContract("architect", [], [new ProducedOutput("plan.md")], []);

        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        Assert.DoesNotContain("Inputs, in the order listed", GetPrompt(target));
    }

    [Fact]
    public void Prompt_keeps_newlines_for_readability_on_all_platforms()
    {
        var contract = new WorkerContract("architect", ["goal"], [new ProducedOutput("plan.md")], []);
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        Assert.Contains('\n', GetPrompt(target));
    }

    [Fact]
    public void Shell_metacharacters_and_percent_signs_are_passed_raw_because_no_shell_evaluates_them()
    {
        var invocation = new WorkerInvocation("Quote this: \"$HOME\" and `whoami` and 100% path %PATH%.");

        var target = new ClaudeWorkerAdapter().Resolve(invocation, ArchitectContract);

        var prompt = GetPrompt(target);
        Assert.Contains("Quote this: \"$HOME\" and `whoami` and 100% path %PATH%.", prompt);
    }

    /// <summary>Issue #292: CoreDispatcher's durable prompt.txt capture reads this field, not target.Args -- it must carry the identical text the -p argument does.</summary>
    [Fact]
    public void PromptText_carries_the_same_resolved_prompt_as_the_p_argument()
    {
        var contract = new WorkerContract("architect", ["goal"], [new ProducedOutput("plan.md")], []);
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        Assert.Equal(GetPrompt(target), target.PromptText);
    }

    [Fact]
    public void Null_invocation_or_contract_throws()
    {
        var adapter = new ClaudeWorkerAdapter();

        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(null!, ArchitectContract));
        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(new WorkerInvocation("Draft a plan."), null!));
    }

    // M21 Phase 1: the structured PermissionGrant builder path. The tests above are untouched —
    // proving a hand-typed raw PermissionScope still resolves identically is exactly "don't touch
    // the existing cases."

    [Fact]
    public void A_permission_grant_composes_every_category_into_allowedTools_in_a_fixed_order()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        Assert.Equal("Read,Edit,Write,NotebookEdit,Bash,WebFetch,WebSearch", target.Args[3]);
    }

    [Fact]
    public void A_permission_grant_scopes_shell_commands_to_its_patterns_when_given()
    {
        var grant = new PermissionGrant(RunShellCommands: true, ShellCommandPatterns: ["git:*", "npm:*"]);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        // The write tools precede the shell entries because #649 pre-approves them unconditionally —
        // pre-approval is not a ceiling, and the hook is what confines them to BATON_OUTPUT_DIR. The
        // pattern scoping this test is about is unaffected by that.
        Assert.Equal("Edit,Write,NotebookEdit,Bash(git:*),Bash(npm:*)", target.Args[3]);
    }

    [Fact]
    public void A_permission_grant_takes_precedence_over_a_raw_permission_scope_when_both_are_set()
    {
        var grant = new PermissionGrant(ReadFiles: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Bash(git:*)", PermissionGrant: grant),
            ArchitectContract);

        // What this test is about is that the raw scope's Bash(git:*) is gone — the grant won. The
        // write tools present are the grant's own #649 pre-approval, not the raw scope leaking in.
        Assert.Equal("Read,Edit,Write,NotebookEdit", target.Args[3]);
        Assert.DoesNotContain("Bash", target.Args[3], StringComparison.Ordinal);
    }

    [Fact]
    public void TryTranslatePermissionGrant_never_refuses_for_claude()
    {
        var adapter = new ClaudeWorkerAdapter();

        var succeeded = adapter.TryTranslatePermissionGrant(
            new PermissionGrant(RunShellCommands: true, NetworkAccess: true), out var resolved, out var gapReason);

        Assert.True(succeeded);
        // Write tools ride the allow list unconditionally since #649; what this test is about is
        // that translation never returns false for claude, and that the shell/network arms resolve.
        Assert.Equal("Edit,Write,NotebookEdit,Bash,WebFetch,WebSearch", resolved);
        Assert.Null(gapReason);
    }

    // #331: --allowedTools only *pre-approves*; a withheld category must be *actively* denied via
    // --disallowedTools or a subscription worker still reaches the tool (a shell-denied session ran
    // `hostname`). These assert the enforcing flag is emitted onto the argv — the default-CI guard for
    // this class of bug, which shape-only translation tests could not catch. That the CLI *honours*
    // the flag is a live-vendor smoke gate (docs/runbooks/live-claude-smoke.md), not a unit test.

    [Fact]
    public void A_withheld_shell_grant_actively_denies_Bash_not_merely_omits_it_from_the_allow_list()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        Assert.DoesNotContain("Bash", ArgValue(target, "--allowedTools")!); // omitted from the allow-list...
        Assert.Contains("Bash", ArgValue(target, "--disallowedTools")!);    // ...and actively denied.
    }

    [Fact]
    public void The_disallowed_list_is_the_exact_complement_of_the_withheld_categories()
    {
        // Read granted; write, shell and network all withheld. Every withheld category maps to its
        // denied tool(s) EXCEPT writes, which #649 moved to the hook: named here, the CLI would refuse
        // the write before the hook could allow the one landing in BATON_OUTPUT_DIR. The hook's own list
        // still carries them — see Withheld_writes_leave_the_flag_and_move_to_the_hooks_list.
        var grant = new PermissionGrant(ReadFiles: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        // Writes are pre-approved so the hook can be consulted at all, and absent from the deny flag
        // so the CLI does not refuse them first. Both halves are #649; neither is enforcement.
        Assert.Equal("Read,Edit,Write,NotebookEdit", ArgValue(target, "--allowedTools"));
        Assert.Equal("Bash,WebFetch,WebSearch", ArgValue(target, "--disallowedTools"));
    }

    [Fact]
    public void A_fully_permissive_grant_withholds_nothing_and_emits_no_disallowed_list()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        Assert.DoesNotContain("--disallowedTools", target.Args);
    }

    [Fact]
    public void A_read_only_scoped_shell_grant_allows_only_its_patterns_and_denies_the_named_mutating_ones()
    {
        // #1456: the review role's actual grant shape -- read-only git/gh patterns allowed, mutating
        // families explicitly denied on top, no bare "Bash" anywhere on either flag. This is what
        // makes the ceiling real per docs/vendor-capabilities.md's measured negative control (a Bash
        // pattern not on the allow list is refused, not merely unprompted).
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: false, RunShellCommands: true,
            ShellCommandPatterns: ["git diff*", "gh pr view*"], NetworkAccess: false,
            DeniedShellCommandPatterns: ["git commit*", "git push*"], ShellCommandsAreReadOnly: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var allowed = ArgValue(target, "--allowedTools")!;
        Assert.Contains("Bash(git diff*)", allowed);
        Assert.Contains("Bash(gh pr view*)", allowed);
        Assert.DoesNotContain("Bash,", allowed, StringComparison.Ordinal);
        Assert.DoesNotContain("Bash(git commit*)", allowed);

        var denied = ArgValue(target, "--disallowedTools")!;
        Assert.Contains("Bash(git commit*)", denied);
        Assert.Contains("Bash(git push*)", denied);
        Assert.DoesNotContain("Bash(git diff*)", denied);
        // Bare "Bash" (the category-level denial #331 emits when the shell is fully withheld) must
        // not appear -- this grant GRANTS the shell, just scoped, so the bare-tool denial branch
        // (WithheldToolNames) must not fire.
        Assert.DoesNotMatch(@"(^|,)Bash(,|$)", denied);
    }

    [Fact]
    public void A_raw_permission_scope_with_no_structured_grant_emits_no_disallowed_list()
    {
        // The Advanced escape hatch carries no categories to deny — a hand-typed scope is taken as-is.
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Read,Edit"), ArchitectContract);

        Assert.DoesNotContain("--disallowedTools", target.Args);
    }

    /// <summary>
    /// #533 constraints 1-2: hooks and MCP config load only from cwd's own `.claude/`, with no
    /// parent-directory fallback, and `--add-dir` loads neither on claude -- so both are passed
    /// explicitly, at files AER owns rather than the room's own directory.
    /// </summary>
    [Fact]
    public void Settings_and_mcp_config_are_passed_at_BATON_owned_paths_that_exist_and_are_valid_json()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        var settingsPath = ArgValue(target, "--settings");
        var mcpConfigPath = ArgValue(target, "--mcp-config");

        Assert.NotNull(settingsPath);
        Assert.NotNull(mcpConfigPath);
        Assert.StartsWith(BatonPaths.WorkerLaunchConfig, settingsPath);
        Assert.StartsWith(BatonPaths.WorkerLaunchConfig, mcpConfigPath);
        Assert.True(File.Exists(settingsPath), "the file --settings points at must already exist");
        Assert.True(File.Exists(mcpConfigPath), "the file --mcp-config points at must already exist");

        // Both must be valid, parseable JSON, or the CLI invocation this constructs fails outright.
        using var settingsDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
        using var mcpDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(mcpConfigPath));
        Assert.Equal(System.Text.Json.JsonValueKind.Object, settingsDoc.RootElement.ValueKind);
        Assert.True(mcpDoc.RootElement.TryGetProperty("mcpServers", out _));
    }

    /// <summary>
    /// #543 reverses #533's "never overwrite" for this one file: the settings file is entirely
    /// AER-owned (nothing an operator could have put there survives), and it now carries the
    /// mandatory `PreToolUse` hook, so leaving stale content in place would permanently disable the
    /// gate on any machine that ran a pre-#543 build even once.
    /// </summary>
    /// <remarks>
    /// <b>Has to be asserted through <c>Resolve</c>, not only on the writer.</b> With this test moved
    /// down to <c>AtomicLaunchConfigWriterTests</c>, swapping <c>EnsureLaunchConfigFiles</c> back to
    /// <c>EnsureFileExists</c> -- the pre-#543 regression itself -- left the suite green. The
    /// writer-level test proves the writer corrects drift; this one proves the adapter routes through
    /// it. Different claims, not a restatement.
    /// </remarks>
    [Fact]
    public void A_settings_file_with_stale_content_is_overwritten_with_the_canonical_hook_on_the_next_resolve()
    {
        new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);
        var settingsPath = Path.Combine(BatonPaths.WorkerLaunchConfig, "claude-settings.json");
        Assert.True(File.Exists(settingsPath));

        const string stale = """{"hooks":{"PreToolUse":[{"stale":"pre-543-content"}]}}""";
        File.WriteAllText(settingsPath, stale);

        new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft another plan."), ArchitectContract);

        var rewritten = File.ReadAllText(settingsPath);
        Assert.NotEqual(stale, rewritten);
        Assert.DoesNotContain("stale", rewritten);
    }

    /// <summary>
    /// The actual hook payload #543 ships: one `PreToolUse` matcher group covering every tool,
    /// invoked as `dotnet &lt;Baton.Cli.dll path&gt; hook-check` in exec form (`args` present, so
    /// Claude Code spawns it with no shell) -- see `BuildSettingsJson`'s doc comment for why this
    /// names the managed dll via `dotnet` rather than a native apphost (the packed global tool has
    /// no apphost at all).
    /// </summary>
    [Fact]
    public void The_settings_file_carries_a_PreToolUse_hook_that_matches_every_tool_and_points_at_hook_check()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);
        var settingsPath = ArgValue(target, "--settings")!;

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
        var preToolUse = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse");
        Assert.Equal(1, preToolUse.GetArrayLength());

        var matcherGroup = preToolUse[0];
        Assert.Equal("*", matcherGroup.GetProperty("matcher").GetString());

        var handler = matcherGroup.GetProperty("hooks")[0];
        Assert.Equal("command", handler.GetProperty("type").GetString());
        Assert.Equal("dotnet", handler.GetProperty("command").GetString());

        var args = handler.GetProperty("args").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(2, args.Count);
        Assert.EndsWith("Baton.Cli.dll", args[0]);
        Assert.True(File.Exists(args[0]), "the hook's first arg must point at a real, existing Baton.Cli.dll");
        Assert.Equal("hook-check", args[1]);

        // `dotnet <dll>` needs the dll's own .runtimeconfig.json alongside it to run at all -- a
        // review pass on #543 pointed out that checking only the .dll's existence proves nothing
        // about whether `dotnet` can actually load it.
        var runtimeConfigPath = Path.ChangeExtension(args[0], null) + ".runtimeconfig.json";
        Assert.True(
            File.Exists(runtimeConfigPath),
            $"dotnet needs '{runtimeConfigPath}' alongside Baton.Cli.dll to run it at all");
    }

    /// <summary>
    /// #543: the settings file is one static, shared file across every spawn, so per-invocation
    /// data (what this specific worker was denied) has to reach hook-check another way -- the
    /// process environment, which a hook subprocess inherits from claude, which inherits it from
    /// AER's own spawn (confirmed in `.vendor-survey/corpus/claude__hooks.md`: "A hook process
    /// inherits the parent environment"). This is the same string `--disallowedTools` receives, not
    /// a separately-derived value, so the two mechanisms can never disagree about what was withheld.
    /// </summary>
    [Fact]
    public void The_denied_tools_environment_variable_is_the_flag_plus_the_write_tools()
    {
        var grant = new PermissionGrant(ReadFiles: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        // #649: the two channels deliberately differ, on writes and only on writes. The flag is what
        // the CLI enforces directly; the hook list is what it enforces with the target path in hand.
        Assert.NotNull(target.Environment);
        var hookList = target.Environment!.Single(v => v.Name == ClaudeWorkerAdapter.DeniedToolsVariable).Value;

        // #600's vendor tag and #649's differing contents, on the same value.
        Assert.Equal("Bash,WebFetch,WebSearch", ArgValue(target, "--disallowedTools"));
        Assert.Equal("claude:Edit,Write,NotebookEdit,Bash,WebFetch,WebSearch", hookList);
    }

    [Fact]
    public void The_denied_tools_environment_variable_is_set_even_when_nothing_is_withheld()
    {
        // hook-check must see an explicit "" rather than a missing variable it could confuse with
        // "not spawned by AER at all" -- Contains below also proves the variable is present at all.
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.NotNull(target.Environment);
        // #600: tagged, so "AER set this and nothing is withheld" is distinguishable from "the variable
        // never arrived". The empty list after the tag is the part that still means "nothing withheld".
        Assert.Contains((ClaudeWorkerAdapter.DeniedToolsVariable, "claude:"), target.Environment);
    }

    /// <summary>
    /// #543, from review: an inherited `CLAUDE_CODE_SIMPLE=1` disables hooks the same way `--bare`
    /// does (see the doc comment above `SimpleModeVariable`'s declaration), and `BatonTask` inherits
    /// the full parent environment by default -- so this override has to actually be on the argv
    /// this method returns, not merely exist as an idea in a comment.
    /// </summary>
    [Fact]
    public void An_inherited_CLAUDE_CODE_SIMPLE_is_overridden_in_the_process_environment()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.NotNull(target.Environment);
        Assert.Contains((ClaudeWorkerAdapter.SimpleModeVariable, "0"), target.Environment);
    }

    /// <summary>
    /// #533 constraint 3, measured (not vendor-documented) default: `verify.py`'s
    /// `fanout.nesting-allowed-by-default` found a subagent CAN spawn its own subagent with nothing
    /// configured, so AER sets the cap explicitly rather than trusting the vendor's stated default.
    /// </summary>
    [Fact]
    public void The_subagent_spawn_depth_is_capped_to_one_via_the_process_environment()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.NotNull(target.Environment);
        Assert.Contains(
            (ClaudeWorkerAdapter.MaxSubagentSpawnDepthVariable, "1"),
            target.Environment);
    }

    // The tests above assert against the C# objects Resolve() builds -- they would pass equally
    // against a hook command that looks right on paper but fails the moment Claude Code actually
    // spawns it. These two spawn the exact command+args the settings file names, as a real child
    // process fed real stdin and the real environment variable, exactly as Claude Code's exec-form
    // hook dispatch does -- proving the wiring, not just the shape. `Baton.Vendors.Tests` has no
    // project reference to `Baton.Cli` (layering: the CLI depends on the adapters, never the
    // reverse), so this runs the built executable directly rather than calling HookCheckCommand
    // in-process; it needs `Baton.Cli` built into a sibling output directory, true for any normal
    // `pixi run test` / `pixi run build` run.

    [Fact]
    public void The_resolved_hook_command_actually_denies_a_withheld_tool_when_spawned_for_real()
    {
        var grant = new PermissionGrant(ReadFiles: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var (exitCode, stderr) = RunResolvedHookCommand(target, """{"tool_name": "Bash"}""");

        Assert.Equal(2, exitCode);
        Assert.Contains("Bash", stderr);
    }

    [Fact]
    public void The_resolved_hook_command_actually_allows_a_granted_tool_when_spawned_for_real()
    {
        var grant = new PermissionGrant(ReadFiles: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var (exitCode, stderr) = RunResolvedHookCommand(target, """{"tool_name": "Read"}""");

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
    }

    private static (int ExitCode, string Stderr) RunResolvedHookCommand(CoreDispatchTarget target, string stdin)
    {
        var settingsPath = ArgValue(target, "--settings")!;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
        var handler = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0].GetProperty("hooks")[0];
        var command = handler.GetProperty("command").GetString()!;
        var args = handler.GetProperty("args").EnumerateArray().Select(e => e.GetString()).ToList();

        var startInfo = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg!);
        }

        var deniedToolsVar = target.Environment!.First(e => e.Name == ClaudeWorkerAdapter.DeniedToolsVariable);
        startInfo.Environment[deniedToolsVar.Name] = deniedToolsVar.Value;

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEnd();
        var exited = process.WaitForExit(TimeSpan.FromSeconds(60));
        Assert.True(exited, "hook-check did not exit within 30s");

        return (process.ExitCode, stderr);
    }

    [Fact]
    public void Withheld_writes_leave_the_flag_and_move_to_the_hooks_list()
    {
        // #649's boundary change, asserted on both channels at once because the whole point is that
        // they now differ. A write named in --disallowedTools is refused by the CLI before the hook is
        // consulted, so leaving it there makes the outbox exemption unreachable and a read-only
        // reviewer unable to produce the artifact it was dispatched for. The hook keeps the names,
        // because it is what still denies a workspace write.
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: false);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var flag = ArgValue(target, "--disallowedTools") ?? string.Empty;
        var hookList = target.Environment!.Single(v => v.Name == ClaudeWorkerAdapter.DeniedToolsVariable).Value;

        Assert.DoesNotContain("Write", flag, StringComparison.Ordinal);
        Assert.DoesNotContain("Edit", flag, StringComparison.Ordinal);
        Assert.Contains("Write", hookList, StringComparison.Ordinal);
        Assert.Contains("Edit", hookList, StringComparison.Ordinal);
        Assert.Contains("NotebookEdit", hookList, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_other_withheld_category_still_appears_on_both_channels()
    {
        // The control on the change above. Only writes move; a change that dropped every category from
        // the flag would pass the first assertion and quietly remove the enforcement the flag provides
        // for the categories where the hook has no path to inspect.
        var grant = new PermissionGrant(
            ReadFiles: false, WriteFiles: true, RunShellCommands: false, NetworkAccess: false);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var flag = ArgValue(target, "--disallowedTools")!;
        var hookList = target.Environment!.Single(v => v.Name == ClaudeWorkerAdapter.DeniedToolsVariable).Value;

        foreach (var tool in new[] { "Read", "Bash", "WebFetch", "WebSearch" })
        {
            Assert.Contains(tool, flag, StringComparison.Ordinal);
            Assert.Contains(tool, hookList, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// #801: a dispatch that does not opt in must see today's exact `--mcp-config` -- the shared,
    /// deliberately empty `claude-mcp.json` -- with no silent behaviour change from this issue's work.
    /// </summary>
    [Fact]
    public void Not_opting_in_to_the_memory_proposal_tool_keeps_the_empty_mcp_config()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        var mcpConfigPath = ArgValue(target, "--mcp-config");

        Assert.Equal(Path.Combine(BatonPaths.WorkerLaunchConfig, "claude-mcp.json"), mcpConfigPath);
        using var mcpDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(mcpConfigPath!));
        Assert.False(mcpDoc.RootElement.GetProperty("mcpServers").EnumerateObject().Any());
    }

    /// <summary>
    /// #801/#833: opting in points `--mcp-config` at a real config naming AER's own MCP server and
    /// the `memory-edit-proposal` tool, invoked via `Baton.Mcp.Host.dll --memory-proposal-tool` -- the
    /// same `dotnet <dll>` shape #543 requires for the PreToolUse hook, for the identical
    /// packed-global-tool deployment reason. No capture-directory path rides the args (#833) -- see
    /// `ClaudeWorkerAdapter.EnsureMemoryProposalMcpConfig`'s own remarks (canonical) for why.
    /// </summary>
    [Fact]
    public void Opting_in_to_the_memory_proposal_tool_points_mcp_config_at_a_real_server_naming_the_tool_host()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", EnableMemoryProposalTool: true), ArchitectContract);

        var mcpConfigPath = ArgValue(target, "--mcp-config");

        Assert.NotNull(mcpConfigPath);
        Assert.NotEqual(Path.Combine(BatonPaths.WorkerLaunchConfig, "claude-mcp.json"), mcpConfigPath);
        Assert.True(File.Exists(mcpConfigPath), "the file --mcp-config points at must already exist");

        using var mcpDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(mcpConfigPath!));
        var server = mcpDoc.RootElement.GetProperty("mcpServers").GetProperty("baton-memory-proposal");
        Assert.Equal("dotnet", server.GetProperty("command").GetString());
        var serverArgs = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Contains(serverArgs, a => a!.EndsWith("Baton.Mcp.Host.dll", StringComparison.Ordinal));
        Assert.Contains("--memory-proposal-tool", serverArgs);
        Assert.DoesNotContain(serverArgs, a => a!.Contains("memory-proposals", StringComparison.Ordinal));
    }

    [Fact]
    public void Claude_config_root_unset_injects_no_CLAUDE_CONFIG_DIR()
    {
        var original = Environment.GetEnvironmentVariable(ClaudeWorkerAdapter.BatonClaudeConfigRootVariable);
        try
        {
            Environment.SetEnvironmentVariable(ClaudeWorkerAdapter.BatonClaudeConfigRootVariable, null);
            var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

            Assert.DoesNotContain(target.Environment!, e => e.Name == ClaudeWorkerAdapter.ClaudeConfigDirVariable);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClaudeWorkerAdapter.BatonClaudeConfigRootVariable, original);
        }
    }

    [Fact]
    public void Claude_config_root_set_injects_CLAUDE_CONFIG_DIR_for_batch_and_gate()
    {
        var original = Environment.GetEnvironmentVariable(ClaudeWorkerAdapter.BatonClaudeConfigRootVariable);
        var testPath = OperatingSystem.IsWindows() ? @"C:\baton\claude-root" : "/baton/claude-root";
        try
        {
            Environment.SetEnvironmentVariable(ClaudeWorkerAdapter.BatonClaudeConfigRootVariable, testPath);

            var target = new ClaudeWorkerAdapter().Resolve(
                new WorkerInvocation("Draft a plan.", SessionId: "session-123", ResumeSession: true), ArchitectContract);

            Assert.Contains(target.Environment!, e => e.Name == ClaudeWorkerAdapter.ClaudeConfigDirVariable && e.Value == testPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClaudeWorkerAdapter.BatonClaudeConfigRootVariable, original);
        }
    }

    [Fact]
    public void TruncatedEnvelopeInTail_FailsClosed_NoClassificationNoThrow()
    {
        // #1115 review: the tail buffers cut front-first mid-line, so the classifier can be
        // handed half a JSON envelope — even one whose retained half still contains the literal
        // "credits_required". Unparseable input must fail closed: no classification, no throw.
        var frontCut = """error","errorCode":"credits_required","result":"Subscription quota exhausted."}""";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(frontCut, testTime, out var classification, out _);

        Assert.False(classified);
        Assert.Null(classification);
    }

    [Fact]
    public void CreditsRequired_ClassifiesExhaustedUntil()
    {
        var envelope = """{"type":"result","is_error":true,"errorCode":"credits_required","result":"Subscription quota exhausted."}""";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(envelope, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Null(retryNotBefore);
    }

    [Theory]
    [InlineData("""{"type":"result","is_error":true,"errorCode":"other_error","result":"Failed"}""")]
    [InlineData("""{"type":"result","is_error":true,"result":"Failed without errorCode"}""")]
    public void OrdinaryError_StaysUnclassified(string envelope)
    {
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(envelope, testTime, out var classification, out var retryNotBefore);

        Assert.False(classified);
        Assert.Null(classification);
        Assert.Null(retryNotBefore);
    }

    [Fact]
    public void CreditsRequiredProseInMessageText_DoesNotTrigger()
    {
        var envelope = """{"type":"assistant","message":{"content":[{"type":"text","text":"The system reported credits_required in prose text"}]}}""";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(envelope, testTime, out var classification, out var retryNotBefore);

        Assert.False(classified);
        Assert.Null(classification);
        Assert.Null(retryNotBefore);
    }

    [Fact]
    public void CreditsRequired_OnStdoutTail_ClassifiesExhaustedUntil()
    {
        var envelope = """{"type":"result","is_error":true,"errorCode":"credits_required","result":"Subscription quota exhausted."}""";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        IFailureClassifier adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(stderrTail: null, stdoutTail: envelope, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Null(retryNotBefore);
    }



    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
