using System.Diagnostics;
using System.Text.Json;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Adapters.Tests;

/// <summary>
/// M20 Phase 4's deliverable: unit tests for the refactored, direct shell-less
/// <see cref="AgyWorkerAdapter"/> resolving.
/// </summary>
[Collection(LaunchConfigCollection.Name)]
public class AgyWorkerAdapterTests
{
    private static readonly WorkerContract ArchitectContract = new(
        "architect", ["goal"], [new ProducedOutput("plan.md")], []);

    private static string GetPrompt(CoreDispatchTarget target) => target.Args[1];

    [Fact]
    public void Resolves_to_direct_agy_execution_without_shell_wrapper()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Equal("agy", target.Program);
        Assert.Equal("-p", target.Args[0]);
        Assert.Equal("--mode", target.Args[2]);
        Assert.Equal("accept-edits", target.Args[3]);
        Assert.Equal("--add-dir", target.Args[4]);

        var artifactsRootVar = OperatingSystem.IsWindows() ? "%AER_ARTIFACTS_ROOT%" : "$AER_ARTIFACTS_ROOT";
        Assert.Equal(artifactsRootVar, target.Args[5]);
    }

    /// <summary>
    /// M23 Phase 3 (#272): WorkingDirectory carries no vendor-specific meaning — every adapter forwards
    /// it into CoreDispatchTarget unchanged. For <c>agy</c> that is necessary and <b>not sufficient</b>;
    /// see <see cref="The_rooms_directory_is_bound_with_add_dir_because_agy_ignores_the_process_cwd"/>.
    /// </summary>
    [Fact]
    public void A_configured_WorkingDirectory_is_forwarded_into_the_resolved_target()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", WorkingDirectory: "/home/user/my-project"), ArchitectContract);

        Assert.Equal("/home/user/my-project", target.WorkingDirectory);
    }

    /// <summary>
    /// #491: <c>agy -p</c> <b>ignores the process working directory</b>, so setting it on the dispatch
    /// target does not point the worker at the room's folder. Measured in #472 and recorded in
    /// <c>docs/vendor-capabilities.md</c>: launched from a directory listed in the CLI's own
    /// <c>trustedWorkspaces</c>, the emitted command still carried the CLI's install path as
    /// <c>Cwd</c>; from an untrusted directory it used the CLI's scratch dir and began a recursive
    /// search of the home folder looking for a file in the launch directory.
    /// </summary>
    /// <remarks>
    /// The failure this guards is silent rather than loud — a worker that cannot see the project does
    /// not error, it answers confidently about the wrong directory — and J11 (two subscriptions in one
    /// room) is a human-attested journey, so nothing automated would have caught it.
    /// </remarks>
    [Fact]
    public void Resolve_sets_OversizePromptWrapper_referencing_AER_PROMPT_FILE()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);
        Assert.NotNull(target.OversizePromptWrapper);
        Assert.Contains("%AER_PROMPT_FILE%", target.OversizePromptWrapper);
    }

    /// <summary>
    /// #1088: under StreamJson, agy streams with its OWN grammar — `--output-format stream-json`, and
    /// NEVER claude's `--verbose`. <see cref="AgyWorkerAdapter"/>'s Resolve comment owns why.
    /// </summary>
    [Fact]
    public void StreamJson_emits_agy_output_format_stream_json_and_never_claude_verbose()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", StreamJson: true), ArchitectContract);

        Assert.Contains(
            target.Args.Zip(target.Args.Skip(1)),
            pair => pair.First == "--output-format" && pair.Second == "stream-json");
        Assert.DoesNotContain("--verbose", target.Args);
    }

    [Fact]
    public void Without_StreamJson_no_output_format_flag_is_emitted()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain("--output-format", target.Args);
        Assert.DoesNotContain("stream-json", target.Args);
    }

    /// <summary>
    /// #1089: under StreamJson the target carries a terminal-success detector that recognises agy's
    /// SUCCESS `result` event and nothing else; in text mode there is no such event, so it is null and
    /// the classifier's timeout guard fails safe.
    /// </summary>
    [Fact]
    public void StreamJson_wires_a_terminal_success_detector_that_recognises_only_agys_success_result()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", StreamJson: true), ArchitectContract);

        Assert.NotNull(target.DetectsTerminalSuccess);
        Assert.True(target.DetectsTerminalSuccess!(
            """{"event":"result","result":{"status":"SUCCESS","response":"done","usage":{"total_tokens":5}}}"""));
        Assert.False(target.DetectsTerminalSuccess!("""{"event":"result","result":{"status":"ERROR"}}"""));
        Assert.False(target.DetectsTerminalSuccess!("""{"event":"step_update","step_update":{"state":"DONE","step_type":"tool"}}"""));
        Assert.False(target.DetectsTerminalSuccess!("not json"));
    }

    [Fact]
    public void Without_StreamJson_no_terminal_success_detector_is_wired()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Null(target.DetectsTerminalSuccess);
    }

    [Fact]
    public void The_rooms_directory_is_bound_with_add_dir_because_agy_ignores_the_process_cwd()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", WorkingDirectory: "/home/user/my-project"), ArchitectContract);

        var addDirValues = target.Args
            .Select((arg, i) => (arg, i))
            .Where(pair => pair.arg == "--add-dir")
            .Select(pair => target.Args[pair.i + 1])
            .ToList();

        Assert.Contains("/home/user/my-project", addDirValues);

        // Composes with the artifacts root rather than replacing it — --add-dir is repeatable on agy,
        // and the worker needs both its outputs and the project it is reasoning about.
        var artifactsRootVar = OperatingSystem.IsWindows() ? "%AER_ARTIFACTS_ROOT%" : "$AER_ARTIFACTS_ROOT";
        Assert.Contains(artifactsRootVar, addDirValues);
    }

    /// <summary>A directory-less room (#407's neutral-scratch case) must not emit an empty --add-dir.</summary>
    /// <remarks>
    /// <para>
    /// Rewritten twice by #554. It originally counted <c>--add-dir</c> occurrences as a proxy for "no
    /// empty value was emitted", which broke when the gate workspace added a second one. The first
    /// rewrite asserted only that no value was blank — and an independent reviewer showed that was
    /// weaker than the original in a way that mattered: changing the adapter to
    /// <c>invocation.WorkingDirectory ?? Directory.GetCurrentDirectory()</c> would still pass, while
    /// regressing #407 by binding the daemon's own cwd as the worker's workspace.
    /// </para>
    /// <para>
    /// So it now pins the <b>exact set</b>. A future third <c>--add-dir</c> on a directory-less room
    /// has to come through this test deliberately — which is the test doing its job, not failing for
    /// the wrong reason.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_directory_add_dir_is_emitted_when_the_room_has_no_working_directory()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        var addDirValues = target.Args
            .Select((arg, i) => (arg, i))
            .Where(pair => pair.arg == "--add-dir")
            .Select(pair => target.Args[pair.i + 1])
            .ToList();

        var artifactsRootVar = OperatingSystem.IsWindows() ? "%AER_ARTIFACTS_ROOT%" : "$AER_ARTIFACTS_ROOT";

        Assert.Equal(2, addDirValues.Count);
        Assert.Equal(artifactsRootVar, addDirValues[0]);
        Assert.EndsWith(AgyWorkerAdapter.AgyWorkspaceDirectoryName, addDirValues[1], StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_permission_scope_overrides_the_default()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "yolo"), ArchitectContract);

        Assert.Equal("yolo", target.Args[3]);
    }

    // #588: agy -p has its own 5-minute print-mode wait, decoupled from anything AER configures, so
    // a long task under a 20-minute AER timeout died at 5 minutes with exit 0 and no output.

    [Fact]
    public void Resolve_passes_print_timeout_derived_from_the_invocations_own_timeout()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Timeout: TimeSpan.FromMinutes(20)), ArchitectContract);

        // 20 minutes + the 60s margin, as whole seconds.
        Assert.Equal("1260s", ArgValue(target, "--print-timeout"));
    }

    /// <summary>
    /// The polarity control. Without it, an adapter that emitted a hardcoded <c>--print-timeout</c>
    /// regardless of the invocation would pass the test above — and would then be overriding the
    /// vendor default in cases where AER has no timeout to declare.
    /// </summary>
    [Fact]
    public void Resolve_omits_print_timeout_entirely_when_the_invocation_declares_no_timeout()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Timeout: null), ArchitectContract);

        Assert.DoesNotContain("--print-timeout", target.Args);
    }

    /// <summary>
    /// agy's limit must expire strictly after AER's, never at the same moment. Whichever fires first
    /// decides the failure mode, and they are not equally good: AER's yields
    /// <c>CoreExitReason.TimedOut</c> and a real diagnostic, agy's yields a clean exit 0 with no
    /// output — the silent failure this issue was filed for. Equality would make that a race.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(300)]
    [InlineData(1200)]
    public void The_print_timeout_always_expires_strictly_after_AERs_own_timeout(int aerTimeoutSeconds)
    {
        var aerTimeout = TimeSpan.FromSeconds(aerTimeoutSeconds);
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Timeout: aerTimeout), ArchitectContract);

        var emitted = ArgValue(target, "--print-timeout");
        Assert.NotNull(emitted);

        var emittedSeconds = int.Parse(emitted.TrimEnd('s'), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(
            emittedSeconds > aerTimeoutSeconds,
            $"print-timeout {emittedSeconds}s must exceed AER's own {aerTimeoutSeconds}s, or agy can give up first");
    }

    /// <summary>
    /// Guards the exact formatting trap this was measured into. <c>agy</c> parses Go durations:
    /// <c>1200s</c>, <c>20m0s</c> and <c>20m</c> are accepted, but <c>00:20:00</c> — which is
    /// precisely what <see cref="TimeSpan.ToString()"/> produces — is rejected with
    /// <c>time: unknown unit ":" in duration</c> and exit code 2. Interpolating the TimeSpan directly
    /// would have broken every gemini dispatch outright.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(1200)]
    [InlineData(7200)]
    public void The_print_timeout_is_a_Go_duration_never_a_dotnet_TimeSpan_rendering(int seconds)
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Timeout: TimeSpan.FromSeconds(seconds)), ArchitectContract);

        var emitted = ArgValue(target, "--print-timeout");
        Assert.NotNull(emitted);
        Assert.Matches(@"^\d+s$", emitted);
        Assert.DoesNotMatch(@"^\d{2}:\d{2}:\d{2}", emitted);
    }

    /// <summary>
    /// A fractional duration must round up, never down: rounding down would emit a backstop fractionally
    /// tighter than intended, which is the direction that reintroduces the race. Zero is floored to a
    /// value the flag will actually parse.
    /// </summary>
    [Theory]
    [InlineData(0.5, "61s")]
    [InlineData(90.4, "151s")]
    [InlineData(0, "60s")]
    public void The_print_timeout_rounds_up_and_never_emits_a_non_positive_duration(
        double seconds, string expected)
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Timeout: TimeSpan.FromSeconds(seconds)), ArchitectContract);

        Assert.Equal(expected, ArgValue(target, "--print-timeout"));
    }

    [Fact]
    public void A_negative_timeout_still_yields_a_duration_agys_parser_accepts()
    {
        // The first version of this comment claimed a negative timeout was "a config error AER's own
        // timeout would reject first". Nothing rejected it: WorkerBindingConfigParser validated
        // Adapter, Contract, PromptTemplate and WorkingDirectory and never Timeout, so the value went
        // straight through to AerTask.WithTimeout. A Timeout > TimeSpan.Zero check now exists there
        // (WorkerBindingConfigParser.Parse) and is what makes this unreachable in practice.
        //
        // The floor stays regardless, because it guards a different thing: an unparseable flag value
        // fails the whole dispatch at argument parsing with exit 2, which is a worse failure than the
        // one being fixed. This asserts the rendering stays parseable even for input the parser should
        // now never hand over.
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Timeout: TimeSpan.FromSeconds(-9999)), ArchitectContract);

        Assert.Matches(@"^\d+s$", ArgValue(target, "--print-timeout"));
    }

    /// <summary>
    /// Adding the margin to a near-maximum <see cref="TimeSpan"/> overflows, and
    /// <see cref="TimeSpan"/> addition throws on overflow rather than saturating. A binding config is
    /// operator-authored and any parseable TimeSpan is accepted, so this is reachable — and it would
    /// throw out of binding <i>resolution</i>, taking down every worker in the file rather than the
    /// one with the silly value.
    /// </summary>
    [Fact]
    public void An_enormous_timeout_does_not_overflow_while_adding_the_margin()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Timeout: TimeSpan.MaxValue), ArchitectContract);

        Assert.Matches(@"^\d+s$", ArgValue(target, "--print-timeout"));
    }

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

    /// <remarks>
    /// De-positioned by #554: this asserted <c>Args[6]</c>/<c>Args[7]</c>, which shifted when the
    /// gate workspace added a second <c>--add-dir</c> pair. The claim was always "the model is
    /// passed through", never "it sits at index 6" — <see cref="ArgValue"/> already existed for
    /// exactly this and is what the neighbouring effort test uses.
    /// </remarks>
    [Fact]
    public void A_model_is_passed_through_when_set()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Model: "gemini-3-pro"), ArchitectContract);

        Assert.Equal("gemini-3-pro", ArgValue(target, "--model"));
    }

    [Fact]
    public void No_model_flag_is_emitted_when_the_model_is_unset()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain("--model", target.Args);
    }

    [Fact]
    public void An_effort_is_passed_through_when_set()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Effort: "high"), ArchitectContract);

        Assert.Equal("high", ArgValue(target, "--effort"));
    }

    [Fact]
    public void No_effort_flag_is_emitted_when_the_effort_is_unset()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain("--effort", target.Args);
    }

    [Fact]
    public void The_prompt_names_every_declared_output_and_its_env_var_path()
    {
        var contract = new WorkerContract(
            "architect", [], [new ProducedOutput("plan.md"), new ProducedOutput("summary.md")], []);

        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        var prompt = GetPrompt(target);
        var outputVar = OperatingSystem.IsWindows() ? "%AER_OUTPUT_DIR%" : "$AER_OUTPUT_DIR";
        var separator = OperatingSystem.IsWindows() ? '\\' : '/';
        Assert.Contains($"plan.md: {outputVar}{separator}plan.md", prompt);
        Assert.Contains($"summary.md: {outputVar}{separator}summary.md", prompt);
    }

    [Fact]
    public void The_prompt_names_every_required_input_and_its_env_var_path()
    {
        var contract = new WorkerContract(
            "critic", ["plan", "guidelines"], [new ProducedOutput("review.md")], []);

        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Review the plan."), contract);

        var prompt = GetPrompt(target);
        var inputVar0 = OperatingSystem.IsWindows() ? "%AER_INPUT_0%" : "$AER_INPUT_0";
        var inputVar1 = OperatingSystem.IsWindows() ? "%AER_INPUT_1%" : "$AER_INPUT_1";
        Assert.Contains($"plan: {inputVar0}", prompt);
        Assert.Contains($"guidelines: {inputVar1}", prompt);
    }

    [Fact]
    public void A_contract_with_no_inputs_omits_the_inputs_section()
    {
        var contract = new WorkerContract("architect", [], [new ProducedOutput("plan.md")], []);

        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        Assert.DoesNotContain("Inputs, in the order listed", GetPrompt(target));
    }

    [Fact]
    public void Prompt_keeps_newlines_for_readability_on_all_platforms()
    {
        var contract = new WorkerContract("architect", ["goal"], [new ProducedOutput("plan.md")], []);
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        Assert.Contains('\n', GetPrompt(target));
    }

    [Fact]
    public void Shell_metacharacters_and_percent_signs_are_passed_raw_because_no_shell_evaluates_them()
    {
        var invocation = new WorkerInvocation("Quote this: \"$HOME\" and `whoami` and 100% path %PATH%.");

        var target = new AgyWorkerAdapter().Resolve(invocation, ArchitectContract);

        var prompt = GetPrompt(target);
        Assert.Contains("Quote this: \"$HOME\" and `whoami` and 100% path %PATH%.", prompt);
    }

    /// <summary>Issue #292: CoreDispatcher's durable prompt.txt capture reads this field, not target.Args -- it must carry the identical text the -p argument does.</summary>
    [Fact]
    public void PromptText_carries_the_same_resolved_prompt_as_the_p_argument()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Equal(GetPrompt(target), target.PromptText);
    }

    [Fact]
    public void Null_invocation_or_contract_throws()
    {
        var adapter = new AgyWorkerAdapter();

        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(null!, ArchitectContract));
        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(new WorkerInvocation("Draft a plan."), null!));
    }

    // M21 Phase 1: the structured PermissionGrant builder path. The tests above are untouched —
    // proving a hand-typed raw PermissionScope (including "yolo", a value outside the --mode
    // vocabulary the structured translator emits) still resolves identically.

    [Theory]
    [InlineData(false, false, "default")]
    [InlineData(true, false, "plan")]
    [InlineData(true, true, "accept-edits")]
    [InlineData(false, true, "accept-edits")]
    public void A_permission_grant_maps_read_write_combinations_to_the_matching_mode(
        bool readFiles, bool writeFiles, string expectedMode)
    {
        var grant = new PermissionGrant(ReadFiles: readFiles, WriteFiles: writeFiles);
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        Assert.Equal(expectedMode, target.Args[3]);
    }

    [Fact]
    public void A_permission_grant_takes_precedence_over_a_raw_permission_scope_when_both_are_set()
    {
        var grant = new PermissionGrant(WriteFiles: true);
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "yolo", PermissionGrant: grant), ArchitectContract);

        Assert.Equal("accept-edits", target.Args[3]);
    }

    [Fact]
    public void Requesting_shell_commands_is_refused_rather_than_approximated()
    {
        var grant = new PermissionGrant(RunShellCommands: true);

        var ex = Assert.Throws<PermissionGrantUnsupportedException>(() => new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract));

        Assert.Equal("agy", ex.AdapterName);
    }

    [Fact]
    public void Requesting_network_access_is_refused_rather_than_approximated()
    {
        var grant = new PermissionGrant(NetworkAccess: true);

        var ex = Assert.Throws<PermissionGrantUnsupportedException>(() => new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract));

        Assert.Equal("agy", ex.AdapterName);
    }

    [Fact]
    public void TryTranslatePermissionGrant_refuses_shell_commands_without_throwing()
    {
        var adapter = new AgyWorkerAdapter();

        var succeeded = adapter.TryTranslatePermissionGrant(
            new PermissionGrant(RunShellCommands: true), out var resolved, out var gapReason);

        Assert.False(succeeded);
        Assert.Null(resolved);
        Assert.NotNull(gapReason);
    }

    [Fact]
    public void Requesting_shell_and_network_access_together_translates_to_dangerously_skip_permissions()
    {
        var adapter = new AgyWorkerAdapter();
        var grant = new PermissionGrant(RunShellCommands: true, NetworkAccess: true);

        var succeeded = adapter.TryTranslatePermissionGrant(grant, out var resolved, out var gapReason);

        Assert.True(succeeded);
        Assert.Equal("--dangerously-skip-permissions", resolved);
        Assert.Null(gapReason);
    }

    [Fact]
    public void A_shell_grant_narrowed_by_patterns_is_refused_rather_than_widened_to_every_command()
    {
        // #659: agy shell grants scoped by PermissionGrant.ShellCommandPatterns translate and emit
        // AER_HOOK_SHELL_PATTERNS to be enforced by the PreToolUse hook (AgyHookCheckCommand).
        var adapter = new AgyWorkerAdapter();
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: true, RunShellCommands: true,
            ShellCommandPatterns: ["git *"], NetworkAccess: true);

        var succeeded = adapter.TryTranslatePermissionGrant(grant, out var resolved, out var gapReason);

        Assert.True(succeeded);
        Assert.Equal("--dangerously-skip-permissions", resolved);
        Assert.Null(gapReason);
    }

    [Fact]
    public void Resolving_a_pattern_scoped_shell_grant_emits_shell_patterns_environment_variable()
    {
        var adapter = new AgyWorkerAdapter();
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: true, RunShellCommands: true,
            ShellCommandPatterns: ["git *"], NetworkAccess: true);

        var target = adapter.Resolve(new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        Assert.Contains(target.Environment!, env => env.Name == AgyWorkerAdapter.ShellPatternsVariable && env.Value == "agy:git *");
    }

    [Fact]
    public void Resolving_a_grant_with_a_denyalways_pattern_emits_the_denied_shell_patterns_variable()
    {
        // #390: the agy hook is the only enforcement for a standing "never", so the adapter must emit
        // AER_HOOK_DENIED_SHELL_PATTERNS. Its absence is fail-closed at the hook, so a missing emission
        // would silently deny every run_command — this pins that the channel is actually sent.
        // The meaningful case: the shell is granted (agy expresses that only as the network-bundled
        // --dangerously-skip-permissions) and a standing "never" carves rm back out via the hook.
        var adapter = new AgyWorkerAdapter();
        var grant = new PermissionGrant(
            ReadFiles: true, RunShellCommands: true, NetworkAccess: true,
            DeniedShellCommandPatterns: ["rm *"]);

        var target = adapter.Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant),
            ArchitectContract);

        Assert.Contains(
            target.Environment!,
            env => env.Name == AgyWorkerAdapter.DeniedShellPatternsVariable && env.Value == "agy:rm *");
    }

    [Fact]
    public void A_grant_with_no_denies_still_emits_the_denied_shell_patterns_variable_present_but_empty()
    {
        // The channel is always emitted ("agy:" at minimum) so the hook can tell "no standing denies"
        // (Present+empty) from "the channel broke" (Absent) — the same distinction the allow channel draws.
        var adapter = new AgyWorkerAdapter();
        var grant = new PermissionGrant(ReadFiles: true, RunShellCommands: true, NetworkAccess: true);

        var target = adapter.Resolve(new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        Assert.Contains(
            target.Environment!,
            env => env.Name == AgyWorkerAdapter.DeniedShellPatternsVariable && env.Value == "agy:");
    }

    [Fact]
    public void An_empty_pattern_list_alongside_a_shell_grant_still_translates()
    {
        // The control, and the polarity mirror of the refusal above: the two differ only in whether
        // the pattern list has anything in it. Without this, the refusal passes just as well on an
        // adapter that rejects every shell grant — which would break the daemon's "auto" permission
        // mode, the one live shape that grants the shell at all.
        var adapter = new AgyWorkerAdapter();
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: true, RunShellCommands: true,
            ShellCommandPatterns: [], NetworkAccess: true);

        var succeeded = adapter.TryTranslatePermissionGrant(grant, out var resolved, out var gapReason);

        Assert.True(succeeded);
        Assert.Equal("--dangerously-skip-permissions", resolved);
        Assert.Null(gapReason);
    }

    [Fact]
    public void Patterns_without_a_shell_grant_are_not_refused()
    {
        // The second control. Patterns only mean anything alongside a shell grant, so a stray list on
        // a grant that withholds the shell is inert rather than a contradiction — refusing it would
        // reject a harmless binding, and the UI keeps the text box populated when the box is unticked.
        var adapter = new AgyWorkerAdapter();
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: true, RunShellCommands: false,
            ShellCommandPatterns: ["git:*"], NetworkAccess: false);

        var succeeded = adapter.TryTranslatePermissionGrant(grant, out var resolved, out var gapReason);

        Assert.True(succeeded);
        Assert.Equal("accept-edits", resolved);
        Assert.Null(gapReason);
    }

    [Fact]
    public void Resolving_with_shell_and_network_access_emits_dangerously_skip_permissions_as_standalone_argument()
    {
        var grant = new PermissionGrant(RunShellCommands: true, NetworkAccess: true);
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        Assert.Equal("agy", target.Program);
        Assert.Equal("-p", target.Args[0]);
        Assert.Equal("--dangerously-skip-permissions", target.Args[2]);
        Assert.DoesNotContain("--mode", target.Args);
        Assert.Equal("--add-dir", target.Args[3]);
    }

    // ---------------------------------------------------------------- #554: the PreToolUse gate
    //
    // Decision 0029 makes the hook mandatory on every spawned worker. The tests below assert the
    // three things that have to hold for it to actually gate anything: the workspace is handed to
    // --add-dir (agy loads hooks from nowhere else, #538), the denied-tool list reaches the hook
    // process (via the environment -- measured by the `agy.hook-env-inherited` sentinel), and the
    // mapping covers the tools that would otherwise leak the withheld category.

    private static string EnvValue(CoreDispatchTarget target, string name) =>
        target.Environment!.Single(pair => pair.Name == name).Value;

    [Fact]
    public void Every_invocation_carries_the_agy_workspace_on_add_dir_so_the_gate_is_loaded()
    {
        // Unconditional, like #543's claude side: not only when a flow declares a gate. A hook
        // installed only sometimes cannot be relied on by anything downstream.
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan."), ArchitectContract);

        var addDirValues = target.Args
            .Select((arg, i) => (arg, i))
            .Where(pair => pair.arg == "--add-dir")
            .Select(pair => target.Args[pair.i + 1])
            .ToList();

        Assert.Contains(addDirValues, dir =>
            dir.EndsWith(AgyWorkerAdapter.AgyWorkspaceDirectoryName, StringComparison.Ordinal));
    }

    [Fact]
    public void The_gate_workspace_holds_a_hooks_file_naming_the_agy_hook_check_command()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan."), ArchitectContract);

        var workspace = target.Args
            .Select((arg, i) => (arg, i))
            .Where(pair => pair.arg == "--add-dir")
            .Select(pair => target.Args[pair.i + 1])
            .Single(dir => dir.EndsWith(AgyWorkerAdapter.AgyWorkspaceDirectoryName, StringComparison.Ordinal));

        var hooksPath = Path.Combine(workspace, ".agents", "hooks.json");
        Assert.True(File.Exists(hooksPath), $"no hooks.json was written to '{hooksPath}'");

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(hooksPath));
        var handler = doc.RootElement
            .EnumerateObject().Single().Value      // hooks are keyed by an arbitrary NAME at the root
            .GetProperty("PreToolUse")[0];
        Assert.Equal("*", handler.GetProperty("matcher").GetString());

        var command = handler.GetProperty("hooks")[0].GetProperty("command").GetString()!;
        Assert.Contains("agy-hook-check", command, StringComparison.Ordinal);
        // Shell-parsed, with no exec form available on this vendor: a raw Windows path's \U and \t
        // would be read as escapes, so the path must be forward-slashed inside its quotes.
        Assert.DoesNotContain('\\', command);
    }

    [Fact]
    public void A_withheld_category_reaches_the_hook_through_the_environment()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: false,
                                        RunShellCommands: true, NetworkAccess: true);
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var denied = StripVendorTag(EnvValue(target, AgyWorkerAdapter.DeniedToolsVariable)).Split(',');

        Assert.Contains("write_to_file", denied);
        Assert.Contains("replace_file_content", denied);
        Assert.Contains("multi_replace_file_content", denied);
        // Polarity: the granted categories must NOT be withheld, or a gate that denies everything
        // would pass the assertions above while breaking every worker.
        Assert.DoesNotContain("view_file", denied);
        Assert.DoesNotContain("run_command", denied);
        Assert.DoesNotContain("search_web", denied);
    }

    [Fact]
    public void Withholding_reads_also_withholds_the_tools_that_return_file_contents()
    {
        // grep_search returns file CONTENT, and list_dir/find_by_name disclose structure -- mapping
        // ReadFiles to view_file alone leaves the withheld category reachable. Found by the
        // implementation advisor reading agy's tool list against the first draft of this mapping.
        var grant = new PermissionGrant(ReadFiles: false, WriteFiles: true,
                                        RunShellCommands: true, NetworkAccess: true);
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var denied = StripVendorTag(EnvValue(target, AgyWorkerAdapter.DeniedToolsVariable)).Split(',');

        Assert.Contains("view_file", denied);
        Assert.Contains("grep_search", denied);
        Assert.Contains("list_dir", denied);
        Assert.Contains("find_by_name", denied);
        Assert.DoesNotContain("write_to_file", denied);
    }

    [Fact]
    public void Withholding_the_shell_also_withholds_control_of_background_shell_processes()
    {
        // manage_task sends stdin to and kills background commands, so withholding run_command
        // alone leaves shell control reachable.
        //
        // Network is withheld here too, and not by choice: TryTranslatePermissionGrant refuses
        // shell-without-network and network-without-shell outright, because the only agy flag that
        // grants either grants both. So the two categories are expressible only together, and a
        // shell-withheld grant is always also a network-withheld one on this vendor.
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true,
                                        RunShellCommands: false, NetworkAccess: false);
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var denied = StripVendorTag(EnvValue(target, AgyWorkerAdapter.DeniedToolsVariable)).Split(',');

        Assert.Contains("run_command", denied);
        Assert.Contains("manage_task", denied);
        Assert.DoesNotContain("view_file", denied);
    }

    /// <summary>
    /// The fourth category, which had no arm at all until #596 — reads, writes and the shell each had
    /// one, and <c>search_web</c> appeared in this file exactly once, as a polarity assertion inside
    /// another test. Deleting the <c>NetworkAccess</c> branch from <c>BuildDeniedTools</c> failed
    /// nothing, which matters more than usual here: under <c>--dangerously-skip-permissions</c> the
    /// denied-tools list is the entire enforcement boundary, so an unguarded category is an unguarded
    /// capability.
    /// </summary>
    /// <remarks>
    /// Withheld alongside the shell rather than alone, because it cannot be isolated: a grant with
    /// network withheld and the shell granted is refused outright by
    /// <c>TryTranslatePermissionGrant</c> (agy has no flag expressing that pair). The polarity arm is
    /// what keeps the test honest under that constraint — a gate denying everything would fail it.
    /// </remarks>
    [Fact]
    public void Withholding_network_access_also_withholds_the_tools_that_reach_the_network()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true,
                                        RunShellCommands: false, NetworkAccess: false);
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var denied = StripVendorTag(EnvValue(target, AgyWorkerAdapter.DeniedToolsVariable)).Split(',');

        Assert.Contains("search_web", denied);
        Assert.Contains("read_url_content", denied);
        // A prefix entry, not a tool name: agy's corpus offers `browser_.*` as a matcher example
        // while enumerating none of the actual names, so the family is withheld by prefix.
        Assert.Contains("browser_*", denied);
        Assert.DoesNotContain("view_file", denied);
        Assert.DoesNotContain("write_to_file", denied);
    }

    /// <summary>
    /// Guards the <b>boolean</b> category population, and only that. Each of the four booleans is
    /// covered by a withholding test above, but nothing stopped a fifth <i>boolean</i> being added to
    /// <see cref="PermissionGrant"/> and silently contributing no denied tools — under
    /// <c>--dangerously-skip-permissions</c> that is a capability granted with no arm to catch it.
    /// This fails until the new one is covered, which is the point: a prompt to write the test, not a
    /// substitute for one.
    /// </summary>
    /// <remarks>
    /// <b>A non-boolean dimension already exists that this guard cannot see, by construction.</b>
    /// <see cref="PermissionGrant.ShellCommandPatterns"/> is the fifth constructor parameter and is
    /// filtered out below, so it contributes no denied tools and nothing here notices — nor would an
    /// enum or a host allowlist added later. That is not hypothetical drift: this adapter never reads
    /// the field at all, while <c>ClaudeWorkerAdapter</c> honours it, which is its own defect — #624. Widening the filter is not the fix, because a pattern list does not map onto
    /// "withheld → deny these names"; it needs a per-vendor answer.
    /// </remarks>
    [Fact]
    public void Every_permission_category_has_a_withholding_arm_in_this_suite()
    {
        var categories = typeof(PermissionGrant)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Where(p => p.ParameterType == typeof(bool))
            .Select(p => p.Name!)
            .ToHashSet();

        // Each name here is asserted by a test in this file: reads and writes by the two
        // skip-permissions arms, the shell by the background-process arm, the network by the arm
        // directly above.
        var covered = new HashSet<string>
        {
            nameof(PermissionGrant.ReadFiles),
            nameof(PermissionGrant.WriteFiles),
            nameof(PermissionGrant.RunShellCommands),
            nameof(PermissionGrant.NetworkAccess),
        };

        Assert.Equal(
            categories.OrderBy(n => n, StringComparer.Ordinal),
            covered.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void An_invocation_with_no_grant_sets_the_variable_to_empty_rather_than_omitting_it()
    {
        // Always present so the value is AER's own rather than an inherited one. This does NOT
        // make absent distinguishable from empty -- agy-hook-check collapses both to allow, see
        // #600 -- so this asserts only what it can: the variable is set, and set to empty.
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan."), ArchitectContract);

        // #600: the tag is what makes this an empty list AER actively sent, rather than an absence.
        Assert.Equal("agy:", EnvValue(target, AgyWorkerAdapter.DeniedToolsVariable));
    }

    [Fact]
    public void The_denied_tools_variable_matches_the_cli_side_contract()
    {
        // Aer.Adapters cannot reference Aer.Cli, so this name is a plain string contract asserted
        // on both sides. If they drift the hook reads an empty list and allows everything.
        Assert.Equal("AER_HOOK_DENIED_TOOLS", AgyWorkerAdapter.DeniedToolsVariable);
    }

    // Everything above asserts against the C# objects Resolve() builds and the JSON it writes --
    // all of which would pass equally against a hook command that looks correct on paper and fails
    // the instant agy spawns it. These take the command out of the written hooks.json, split it, and
    // launch the assembly directly with a real agy payload and the real environment variable.
    //
    // WHAT THEY DO NOT COVER, and #710 is what happens when that is forgotten. They spawn via
    // ProcessStartInfo.ArgumentList, so the arguments go to the child verbatim. agy does not: it
    // hands the whole string to `cmd /c` on Windows or `sh -c` on Unix, and the shell decides what
    // the arguments even are. These tests therefore prove the assembly and its arguments behave;
    // they are structurally incapable of catching a command string the shell cannot parse, which is
    // exactly the defect that left the gate dead for months while they passed.
    //
    // That half belongs to a vendor check that runs the shipped command through agy itself, named
    // here because a reader of this pair needs to know it is one of two halves, not the whole:
    // record-once-ok: #710 tools/vendor-verify/verify.py
    // `agy.hook-command-survives-a-metacharacter-in-its-path`.
    //
    // Why it matters more here than on the claude side, where the equivalent pair already exists
    // (ClaudeWorkerAdapterTests.RunResolvedHookCommand): agy's handler has no exec form -- only a
    // single shell-parsed `command` string -- and a hook that cannot start produces no stdout, which
    // `agy.hook-malformed-stdout-fails-open` measured as an ALLOW. So on this vendor a hook that
    // fails to launch is an ungated worker, silently, with no --disallowedTools backstop
    // (`agy.permissions-are-global-only`). The `File.Exists` guard in BuildHooksJson checks the
    // path and proves nothing about whether the assembled command can actually run.

    [Fact]
    public void The_written_hook_commands_assembly_denies_a_withheld_tool_when_launched_directly()
    {
        var (decision, reason) = RunWrittenHookCommand(
            new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false, NetworkAccess: false),
            """{"toolCall":{"name":"run_command","args":{"CommandLine":"ls"}},"stepIdx":1}""");

        Assert.Equal("deny", decision);
        Assert.Contains("run_command", reason);
    }

    [Fact]
    public void The_written_hook_commands_assembly_allows_a_granted_tool_when_launched_directly()
    {
        // Same grant, same payload shape, different tool -- so neither verdict can come from a
        // command that answers unconditionally.
        var (decision, _) = RunWrittenHookCommand(
            new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false, NetworkAccess: false),
            """{"toolCall":{"name":"view_file","args":{"AbsolutePath":"x"}},"stepIdx":1}""");

        Assert.Equal("allow", decision);
    }

    [Fact]
    public void The_hook_assembly_carries_its_runtimeconfig_so_dotnet_can_load_it()
    {
        // Added on the claude side by #543's own review pass, for the same reason: asserting the
        // .dll exists proves nothing about whether `dotnet <dll>` can start it. A missing
        // .runtimeconfig.json makes the hook fail to launch -- which on agy reads as an allow.
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, "Aer.Cli.dll");
        var runtimeConfigPath = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");

        Assert.True(File.Exists(runtimeConfigPath),
            $"'{runtimeConfigPath}' is missing, so `dotnet \"{assemblyPath}\"` cannot start the hook");
    }

    // HookAssemblyToken's branches, exercised with real directories rather than left to the one
    // shape this machine's install path happens to have -- on a spaceless install every production
    // call takes the bare-path early return, so without these the 8.3 branch and the refusal never
    // run under test at all. Why each shape is required is the method's own xmldoc; these only pin
    // that the code does what it says there.

    [Fact]
    public void The_hook_token_is_the_bare_forward_slash_path_when_it_is_clean_on_windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the bare-token rule is cmd's, not sh's");

        Assert.Equal("C:/plain/Aer.Cli.dll",
            AgyWorkerAdapter.HookAssemblyToken(@"C:\plain\Aer.Cli.dll"));
    }

    [Fact]
    public void The_hook_token_is_single_quoted_on_unix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sh strips single quotes; cmd does not");

        Assert.Equal("'/opt/aer flow/Aer.Cli.dll'",
            AgyWorkerAdapter.HookAssemblyToken("/opt/aer flow/Aer.Cli.dll"));
    }

    [Fact]
    public void A_spaced_windows_directory_yields_a_clean_token_for_the_same_file()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the 8.3 detour is a Windows mechanism");

        var (directory, assemblyPath) = TempHookAssemblyUnder("aer flow probe-");
        try
        {
            string token;
            try
            {
                token = AgyWorkerAdapter.HookAssemblyToken(assemblyPath);
            }
            catch (InvalidOperationException)
            {
                // The method's contract when the volume cannot produce a clean 8.3 form is a loud
                // refusal, and that is the right behaviour -- but on such a volume this test cannot
                // observe the clean-token half, and pretending it did would be the false green.
                Assert.Skip("this volume has 8.3 name generation disabled, so only the refusal branch is reachable");
                return;
            }

            AssertCmdCanTokenize(token);
            Assert.EndsWith("/Aer.Cli.dll", token, StringComparison.Ordinal);
            Assert.True(File.Exists(token),
                $"the token must still resolve to the same file, or the hook dies at spawn: {token}");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void An_ampersand_directory_is_never_emitted_as_a_token_cmd_would_split()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "`&` is an operator to cmd, not to sh");

        // `&` is legal in an 8.3 name, so unlike a space the short form may keep it -- Windows
        // preserves it when it falls inside the retained prefix and drops it with the truncated
        // tail otherwise. Both outcomes honour the contract; what the contract forbids is the
        // third one: returning a token that still carries the character, silently, for cmd to
        // split into a command that never starts and an allow nobody sees.
        var (directory, assemblyPath) = TempHookAssemblyUnder("a&b probe-");
        try
        {
            string token;
            try
            {
                token = AgyWorkerAdapter.HookAssemblyToken(assemblyPath);
            }
            catch (InvalidOperationException refusal)
            {
                Assert.Contains("decision 0029", refusal.Message);
                return;
            }

            AssertCmdCanTokenize(token);
            Assert.True(File.Exists(token),
                $"the token must still resolve to the same file, or the hook dies at spawn: {token}");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    private static (string Directory, string AssemblyPath) TempHookAssemblyUnder(string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var assemblyPath = Path.Combine(directory, "Aer.Cli.dll");
        File.WriteAllText(assemblyPath, "not a real assembly; only existence matters here");
        return (directory, assemblyPath);
    }

    private static void AssertCmdCanTokenize(string token) =>
        Assert.DoesNotContain(token, t => t is ' ' or '&' or '^' or ',' or ';' or '=');

    [Fact]
    public void A_spaced_path_with_no_directory_to_shorten_is_refused_loudly()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "on sh the quotes make the same input fine");

        // No directory component, so the 8.3 remedy has nothing to shorten -- the contract is a
        // loud refusal, never a command that is emitted and then silently reads as an allow.
        var refusal = Assert.Throws<InvalidOperationException>(
            () => AgyWorkerAdapter.HookAssemblyToken("Aer Cli.dll"));
        Assert.Contains("decision 0029", refusal.Message);
    }

    /// <summary>
    /// Spawns the <c>command</c> string out of the written <c>hooks.json</c> and returns the parsed
    /// verdict. Parsed, not substring-matched: agy parses this stream, and output that merely
    /// contains "deny" while being invalid JSON is an allow.
    /// </summary>
    private static (string Decision, string Reason) RunWrittenHookCommand(
        PermissionGrant grant, string stdin)
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var workspace = target.Args
            .Select((arg, i) => (arg, i))
            .Where(pair => pair.arg == "--add-dir")
            .Select(pair => target.Args[pair.i + 1])
            .Single(dir => dir.EndsWith(AgyWorkerAdapter.AgyWorkspaceDirectoryName, StringComparison.Ordinal));

        using var doc = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(workspace, ".agents", "hooks.json")));
        var command = doc.RootElement
            .EnumerateObject().Single().Value
            .GetProperty("PreToolUse")[0]
            .GetProperty("hooks")[0]
            .GetProperty("command").GetString()!;

        // The pattern pins the shape PER SHELL, deliberately strict rather than permissive, because
        // the token the assembly path may wear is a measured constraint of the shell agy uses on
        // each platform, not a style. On Windows the token must be bare: `cmd /c` resolves neither a
        // quoted path nor a bare one containing a space once an argument follows, so a command that
        // grew a quote here would be one that never starts -- and on this vendor a hook that never
        // starts is an ALLOW. A tolerant regex would let that through silently, which is what
        // happened twice: `"` until #706, then `'` until #710. On Unix the token must be
        // single-quoted, because `sh -c` strips those quotes and a space then needs no special
        // handling -- and the capture is the text INSIDE them, which is what `sh` hands `dotnet`.
        // Passing the quotes through to ArgumentList would feed `dotnet` a literal quoted filename
        // no shell ever produces, which is how this pair first went red on the Linux CI leg.
        //
        // This assertion pins the SHAPE. Whether agy's shell really resolves it is a vendor question
        // this test cannot reach -- see the note above the pair, and
        // `agy.hook-command-survives-a-metacharacter-in-its-path`, which runs it through agy.
        var pattern = OperatingSystem.IsWindows() ? @"^(\S+) (\S+) (\S+)$" : @"^(\S+) '([^']+)' (\S+)$";
        var match = System.Text.RegularExpressions.Regex.Match(command, pattern);
        Assert.True(match.Success,
            $"hook command does not have the shape this platform's shell was measured to resolve "
            + $"(bare on Windows, single-quoted on Unix) -- the wrong token here is a command agy's "
            + $"shell cannot start, which reads as an allow: {command}");

        var startInfo = new ProcessStartInfo(match.Groups[1].Value)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(match.Groups[2].Value);
        startInfo.ArgumentList.Add(match.Groups[3].Value);

        // Forward both channels the adapter emits and the hook reads (#600 denied tools, #659 shell
        // patterns). Since #659 the hook denies when the shell-pattern channel is Absent — the same
        // fail-closed posture as denied tools — so a launcher that forwarded only one var would deny
        // every call, including the granted one this test proves is allowed. Production sends the full
        // environment dict; this mirrors that for the two vars that gate the verdict.
        foreach (var name in new[] { AgyWorkerAdapter.DeniedToolsVariable, AgyWorkerAdapter.ShellPatternsVariable })
        {
            startInfo.Environment[name] = target.Environment!.First(e => e.Name == name).Value;
        }

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var exited = process.WaitForExit(TimeSpan.FromSeconds(60));
        Assert.True(exited, "agy-hook-check did not exit within 30s");

        using var verdict = System.Text.Json.JsonDocument.Parse(stdout);
        return (verdict.RootElement.GetProperty("decision").GetString()!,
                verdict.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "");
    }

    /// <summary>
    /// #600 tags the denied-tools value with its vendor (<c>agy:</c>) so an absent list, an empty
    /// one AER set, and another vendor's list are distinguishable. Every assertion below is about the
    /// tool names, so the tag is removed here rather than repeated in each one — and pinned once, in
    /// <see cref="The_denied_tools_value_is_tagged_with_this_adapters_vendor"/>.
    /// </summary>
    private static string StripVendorTag(string value)
    {
        Assert.StartsWith("agy:", value, StringComparison.Ordinal);
        return value["agy:".Length..];
    }

    [Fact]
    public void The_denied_tools_value_is_tagged_with_this_adapters_vendor()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("p", PermissionGrant: new PermissionGrant(ReadFiles: true, WriteFiles: false)),
            ArchitectContract);

        var value = target.Environment!.Single(v => v.Name == AgyWorkerAdapter.DeniedToolsVariable).Value;

        Assert.StartsWith("agy:", value, StringComparison.Ordinal);
    }

    [Fact]
    public void Classifies_verbatim_specimen_as_ExhaustedUntil_with_exact_reset_timestamp()
    {
        var specimen = "Worker exited with non-zero code 1. stderr: Error: Individual quota reached. Please upgrade your subscription to increase your limits. Resets in 28m40s.";
        var now = new DateTimeOffset(2026, 7, 30, 15, 0, 0, TimeSpan.Zero);
        var testTime = new TestTimeProvider(now);

        var adapter = new AgyWorkerAdapter();
        var classified = adapter.TryClassifyFailure(specimen, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Equal(now.AddMinutes(28).AddSeconds(40), retryNotBefore);
    }

    [Fact]
    public void Quota_refusal_on_the_stdout_tail_alone_classifies_ExhaustedUntil()
    {
        // #1128: the real refusal (measured live 2026-08-12, execution eca57a30) arrived in the
        // stream-json result envelope on STDOUT with empty stderr — the single-tail path never saw
        // it. Verbatim from that run's log.
        var stdoutTail = """{"event":"result","result":{"conversation_id":"eca57a30-db54-4be3-b760-53d708f8ae79","status":"ERROR","response":"","error":"Individual quota reached. Please upgrade your subscription to increase your limits. Resets in 1h39m10s."}}""";
        var now = new DateTimeOffset(2026, 8, 12, 22, 0, 0, TimeSpan.Zero);
        var testTime = new TestTimeProvider(now);

        var adapter = new AgyWorkerAdapter();
        var classified = adapter.TryClassifyFailure(stderrTail: null, stdoutTail, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Equal(now.AddHours(1).AddMinutes(39).AddSeconds(10), retryNotBefore);
    }

    [Fact]
    public void Worker_prose_about_permissions_on_stdout_does_not_veto_the_run()
    {
        // #1124 review finding E — why is the doc comment on AgyWorkerAdapter's two-tail
        // TryClassifyFailure override. This proves the negative half: a worker legitimately
        // discussing this repo's gate ("auto-denied" + "permission" are its daily vocabulary)
        // keeps its successful run un-vetoed.
        var stdoutTail = """{"event":"result","result":{"status":"OK","response":"When a tool is auto-denied, the permission gate writes an ask file and the worker blocks until a human answers."}}""";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new AgyWorkerAdapter();
        var classified = adapter.TryClassifyFailure(stderrTail: null, stdoutTail, testTime, out var classification, out _);

        Assert.False(classified);
        Assert.Null(classification);
    }

    [Fact]
    public void Non_quota_stderr_classifies_as_null()
    {
        var stderr = "Worker exited with non-zero code 1. stderr: Error: Failed to execute tool.";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new AgyWorkerAdapter();
        var classified = adapter.TryClassifyFailure(stderr, testTime, out var classification, out var retryNotBefore);

        Assert.False(classified);
        Assert.Null(classification);
        Assert.Null(retryNotBefore);
    }

    [Fact]
    public void Quota_like_stderr_without_parseable_duration_classifies_as_null()
    {
        var stderr = "Worker exited with non-zero code 1. stderr: Error: Individual quota reached. Resets in tomorrow.";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new AgyWorkerAdapter();
        var classified = adapter.TryClassifyFailure(stderr, testTime, out var classification, out var retryNotBefore);

        Assert.False(classified);
        Assert.Null(classification);
        Assert.Null(retryNotBefore);
    }

    [Fact]
    public void Quota_reset_with_overflowing_digits_classifies_as_null_not_a_thrown_exception()
    {
        // A duration too large for int must classify false, not throw -- the why (the pump's
        // deliberately catch-free classification path) lives on the TryParse block in
        // AgyWorkerAdapter.TryClassifyQuotaExhaustion.
        var stderr = "Error: Individual quota reached. Please upgrade your subscription to increase your limits. Resets in 99999999999999999999m.";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new AgyWorkerAdapter();
        var classified = adapter.TryClassifyFailure(stderr, testTime, out var classification, out var retryNotBefore);

        Assert.False(classified);
        Assert.Null(classification);
        Assert.Null(retryNotBefore);
    }

    [Theory]
    [InlineData("Resets in 2h.", 2, 0, 0)]
    [InlineData("Resets in 28m.", 0, 28, 0)]
    [InlineData("Resets in 40s.", 0, 0, 40)]
    [InlineData("Resets in 1h30s.", 1, 0, 30)]
    public void Each_optional_duration_group_parses_alone_and_in_partial_combination(
        string resetText, int hours, int minutes, int seconds)
    {
        // The three regex groups are independently optional; only the all-three specimen was
        // covered, so a group-reading edit could break one combination with every test green.
        var stderr = $"Error: Individual quota reached. Please upgrade your subscription to increase your limits. {resetText}";
        var now = new DateTimeOffset(2026, 7, 30, 15, 0, 0, TimeSpan.Zero);
        var testTime = new TestTimeProvider(now);

        var adapter = new AgyWorkerAdapter();
        var classified = adapter.TryClassifyFailure(stderr, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Equal(now.AddHours(hours).AddMinutes(minutes).AddSeconds(seconds), retryNotBefore);
    }

    [Fact]
    public void Classifies_verbatim_auto_denied_tool_stderr_as_ToolDenied_with_null_retryNotBefore()
    {
        var specimen = "jetski: no output produced — a tool required the \"command\" permission that headless mode cannot prompt for, so it was auto-denied. Add an allow-rule under permissions.allow in settings.json (e.g. command(<target>)).";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new AgyWorkerAdapter();
        var classified = adapter.TryClassifyFailure(specimen, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ToolDenied, classification);
        Assert.Null(retryNotBefore);
    }

    [Theory]
    [InlineData("a tool required the command permission that headless mode cannot prompt for")]
    [InlineData("a required tool was auto-denied without user input")]
    public void Single_marker_stderr_does_not_classify_as_auto_denied_tool(string stderr)
    {
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new AgyWorkerAdapter();
        var classified = adapter.TryClassifyFailure(stderr, testTime, out var classification, out var retryNotBefore);

        Assert.False(classified);
        Assert.Null(classification);
        Assert.Null(retryNotBefore);
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    /// <summary>
    /// The agy arm of the no-silent-behaviour-change requirement stated on
    /// <see cref="ClaudeWorkerAdapterTests.Not_opting_in_to_the_memory_proposal_tool_keeps_the_empty_mcp_config"/>:
    /// no extra `--add-dir` for a workspace nobody asked for.
    /// </summary>
    // record-once-ok: #801 tests/Aer.Adapters.Tests/ClaudeWorkerAdapterTests.cs
    [Fact]
    public void Not_opting_in_to_the_memory_proposal_tool_adds_no_extra_add_dir()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("p"), ArchitectContract);

        Assert.DoesNotContain(target.Args, a => a.Contains("memory-proposal", StringComparison.Ordinal));
    }

    /// <summary>
    /// #801: opting in grants an extra `--add-dir` pointing to a workspace with `.agents/mcp_config.json`
    /// (see <see cref="AgyWorkerAdapter.MemoryProposalWorkspaceDirectoryName"/>) -- agy's only
    /// lever; why is the adapter's own remarks' business, not restated here.
    /// </summary>
    // record-once-ok: #801 src/Aer.Adapters/AgyWorkerAdapter.cs
    [Fact]
    public void Opting_in_to_the_memory_proposal_tool_materializes_a_workspace_config_and_grants_it()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("p", EnableMemoryProposalTool: true), ArchitectContract);

        var expectedWorkspace = Path.Combine(
            AerPaths.WorkerLaunchConfig, AgyWorkerAdapter.MemoryProposalWorkspaceDirectoryName);
        var configPath = Path.Combine(expectedWorkspace, ".agents", "mcp_config.json");

        Assert.Contains(expectedWorkspace, target.Args);
        Assert.True(File.Exists(configPath), "the workspace's mcp_config.json must already exist");

        using var mcpDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
        var server = mcpDoc.RootElement.GetProperty("mcpServers").GetProperty("aer-memory-proposal");
        Assert.Equal("dotnet", server.GetProperty("command").GetString());
        var serverArgs = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Contains(serverArgs, a => a!.EndsWith("Aer.Mcp.Host.dll", StringComparison.Ordinal));
        Assert.Contains("--memory-proposal-tool", serverArgs);
        Assert.DoesNotContain(serverArgs, a => a!.Contains("memory-proposals", StringComparison.Ordinal));
    }

    [Fact]
    public void Non_shell_grant_injects_HOME_and_USERPROFILE_redirects()
    {
        var nonShellGrant = new PermissionGrant(ReadFiles: true, WriteFiles: false, RunShellCommands: false, NetworkAccess: false);
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan.", PermissionGrant: nonShellGrant), ArchitectContract);

        var home = target.Environment!.SingleOrDefault(e => e.Name == "HOME").Value;
        var userProfile = target.Environment!.SingleOrDefault(e => e.Name == "USERPROFILE").Value;

        Assert.NotNull(home);
        Assert.NotNull(userProfile);
        Assert.Equal(home, userProfile);
        Assert.Contains("gemini_home", home);
    }

    [Fact]
    public void Shell_granted_worker_does_not_inject_HOME_or_USERPROFILE_redirects()
    {
        var shellGrant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true);
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Build.", PermissionGrant: shellGrant), ArchitectContract);

        Assert.DoesNotContain(target.Environment!, e => e.Name == "HOME");
        Assert.DoesNotContain(target.Environment!, e => e.Name == "USERPROFILE");
    }

    [Fact]
    public void Batch_dispatch_points_home_redirect_under_execution_output_dir()
    {
        var nonShellGrant = new PermissionGrant(ReadFiles: true, WriteFiles: false, RunShellCommands: false, NetworkAccess: false);
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan.", PermissionGrant: nonShellGrant), ArchitectContract);

        var home = target.Environment!.Single(e => e.Name == "HOME").Value;
        var expectedRef = OperatingSystem.IsWindows() ? "%AER_OUTPUT_DIR%" : "$AER_OUTPUT_DIR";
        Assert.StartsWith(expectedRef, home);
    }

    /// <summary>
    /// No SessionId on purpose: the daemon mints a vendor session id only for claude, so a real agy
    /// session turn is classified entirely by the <c>.aer/room.json</c> kind marker (ReadRoomKind ==
    /// Interactive) on the bindings directory -- this pins that clause alone, not the claude-only
    /// shortcut in front of it.
    /// </summary>
    [Fact]
    public void Session_dispatch_points_home_redirect_under_session_root()
    {
        var tempSessionDir = Path.Combine(Path.GetTempPath(), "test-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempSessionDir, ".aer"));
        File.WriteAllText(Path.Combine(tempSessionDir, ".aer", "room.json"), "{}");

        try
        {
            var nonShellGrant = new PermissionGrant(ReadFiles: true, WriteFiles: false, RunShellCommands: false, NetworkAccess: false);
            var target = new AgyWorkerAdapter().Resolve(
                new WorkerInvocation("Chat turn.", PermissionGrant: nonShellGrant, BindingsFileDirectory: tempSessionDir), ArchitectContract);

            var home = target.Environment!.Single(e => e.Name == "HOME").Value;
            var expectedHome = Path.Combine(tempSessionDir, ".gemini_home");
            Assert.Equal(expectedHome, home);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempSessionDir);
        }
    }

    // #1084: the three arms below pin the seed AgyWorkerAdapter emits -- present for the write-granted
    // case, absent for the two that must not carry it. Why the seed is needed and what it permits is on
    // Resolve; the vendor claim that agy honours it is the live `aer dispatch advise --adapter agy` run.
    [Fact]
    public void Write_granted_accept_edits_role_seeds_write_allow_into_redirected_home()
    {
        var writeGrant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false, NetworkAccess: false);
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan.", PermissionGrant: writeGrant), ArchitectContract);

        var seed = Assert.Single(target.SeedFiles!);
        Assert.Contains("gemini_home", seed.PathTemplate);
        Assert.EndsWith(Path.Combine(".gemini", "antigravity-cli", "settings.json"), seed.PathTemplate);

        // Content parses as JSON (a Windows path substituted with backslashes would not) and carries the
        // least-privilege, per-output rule with a forward-slashed target and the unexpanded placeholder.
        using var doc = JsonDocument.Parse(seed.Content);
        var allow = doc.RootElement.GetProperty("permissions").GetProperty("allow");
        var rule = Assert.Single(allow.EnumerateArray()).GetString();
        var expectedRef = OperatingSystem.IsWindows() ? "%AER_OUTPUT_DIR%" : "$AER_OUTPUT_DIR";
        Assert.Equal($"write_file({expectedRef}/plan.md)", rule);
    }

    [Fact]
    public void Write_allow_seeds_one_rule_per_declared_output()
    {
        // A single-output contract cannot catch a `.Select` regression that drops all but the first
        // output; a two-output contract pins the rule SET, not just that some rule was emitted.
        var contract = new WorkerContract(
            "architect", ["goal"], [new ProducedOutput("plan.md"), new ProducedOutput("risks.md")], []);
        var writeGrant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false, NetworkAccess: false);
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan.", PermissionGrant: writeGrant), contract);

        var seed = Assert.Single(target.SeedFiles!);
        using var doc = JsonDocument.Parse(seed.Content);
        var rules = doc.RootElement.GetProperty("permissions").GetProperty("allow")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        var expectedRef = OperatingSystem.IsWindows() ? "%AER_OUTPUT_DIR%" : "$AER_OUTPUT_DIR";
        Assert.Equal([$"write_file({expectedRef}/plan.md)", $"write_file({expectedRef}/risks.md)"], rules);
    }

    [Fact]
    public void Shell_granted_worker_does_not_seed_write_allow()
    {
        // The skip-permissions path already auto-approves writes -- seeding an allow-rule would be
        // redundant, and this is how the front door was built. Polarity for the positive arm above.
        var shellGrant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true);
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Build.", PermissionGrant: shellGrant), ArchitectContract);

        Assert.True(target.SeedFiles is null or { Count: 0 });
    }

    [Fact]
    public void Raw_accept_edits_scope_without_a_grant_seeds_nothing()
    {
        // No grant -> no redirected home -> the seed is gated off, so this raw-scope path emits nothing.
        // The isolation reason the gate protects (never the operator's own home) is on Resolve.
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "accept-edits"), ArchitectContract);

        Assert.True(target.SeedFiles is null or { Count: 0 });
    }

    [Fact]
    public void Unknown_adapter_key_gemini_throws_plain_unknown_error_with_no_rename_hint()
    {
        // Hard cutover (#1035): "gemini" is not a recognized adapter identity — it falls through to
        // the generic unknown-adapter message, exactly like any other unregistered name. The message
        // must NOT carry the old "renamed to 'agy'" migration hint (polarity: proves the special-case
        // is gone, not merely that some message is thrown).
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["worker"] = new WorkerBindingConfigEntry(
                Adapter: "gemini",
                Contract: ArchitectContract,
                PromptTemplate: "Draft a plan.",
                Timeout: TimeSpan.FromMinutes(20))
        };

        var ex = Assert.Throws<UnknownWorkerAdapterException>(() =>
            WorkerBindingResolver.Resolve(config, WorkerAdapterRegistry.Default));

        Assert.Contains("gemini", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("renamed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkerTiers_json_loads_and_resolves_agy_adapter_while_preserving_gemini_model_strings()
    {
        var roles = BuiltInWorkflowTemplates.GetRoleTemplates();
        Assert.NotEmpty(roles);

        // Prove standard and cheap tiers map to agy adapter while keeping gemini-3.6-flash-* model names
        var tiersJsonPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Aer.Adapters", "WorkerTiers.json");
        Assert.True(File.Exists(tiersJsonPath), $"WorkerTiers.json must exist at {tiersJsonPath}");

        var json = File.ReadAllText(tiersJsonPath);
        Assert.Contains("\"adapter\": \"agy\"", json);
        Assert.DoesNotContain("\"adapter\": \"gemini\"", json);
        Assert.Contains("\"model\": \"gemini-3.6-flash-high\"", json);
        Assert.Contains("\"model\": \"gemini-3.6-flash-low\"", json);

        Assert.True(WorkerAdapterRegistry.Default.TryGetValue("agy", out var agyAdapter));
        Assert.IsType<AgyWorkerAdapter>(agyAdapter);
    }
}
