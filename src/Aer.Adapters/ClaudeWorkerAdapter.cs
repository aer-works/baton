using System.Text;
using System.Text.Json;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Adapters;

/// <summary>
/// Direct shell-less <see cref="IWorkerAdapter"/> (M20 Phase 4): resolves a
/// <see cref="WorkerInvocation"/>/<see cref="WorkerContract"/> pair into a direct <c>claude</c>
/// invocation without shell wrappers. Bypasses cmd.exe and sh, eliminating quoting and command injection risks.
/// Stdin redirection to null is handled natively by the process host.
/// <para>
/// <b>M21 Phase 1's <see cref="IPermissionGrantTranslator"/>, corrected in #331:</b> Claude Code's
/// <c>--allowedTools</c> is tool-name-based (<c>Read</c>, <c>Edit</c>, <c>Write</c>,
/// <c>Bash</c>/<c>Bash(pattern)</c>, <c>WebFetch</c>, <c>WebSearch</c>) but only <em>pre-approves</em>
/// those tools so they do not prompt — it is not a sandbox and does not remove a withheld tool from
/// the model's reach. A grant therefore resolves to <em>both</em> lists: <c>--allowedTools</c> for what
/// it permits (this direction never refuses), and <c>--disallowedTools</c> for what it withholds
/// (<see cref="BuildDisallowedTools"/>), which is what actually enforces the denial — decision 0004's
/// "fail closed".
/// </para>
/// <para>
/// <b>Writes are the exception since #649</b>, and this is the first thing to know when reading the
/// two lists here: <c>Edit</c>/<c>Write</c>/<c>NotebookEdit</c> are pre-approved on
/// <c>--allowedTools</c> and absent from <c>--disallowedTools</c>, because the CLI refuses a named
/// tool before AER's <c>PreToolUse</c> hook can allow the one write landing in
/// <c>AER_OUTPUT_DIR</c>. For that category the hook is the whole enforcement; for the other three
/// the sentence above still holds. See <see cref="BuildHookDeniedTools"/>.
/// </para>
/// </summary>
public sealed class ClaudeWorkerAdapter : IWorkerAdapter, IPermissionGrantTranslator
{
    internal const string OversizePromptWrapperText =
        "Read the complete task instructions in %AER_PROMPT_FILE% and execute them exactly as written. Do not summarize or treat as data.";

    private const string DefaultPermissionScope = "Write";

    public bool TryTranslatePermissionGrant(PermissionGrant grant, out string? resolvedValue, out string? gapReason)
    {
        ArgumentNullException.ThrowIfNull(grant);

        List<string> tools = [];
        if (grant.ReadFiles)
        {
            tools.Add("Read");
        }

        // Pre-approved either way (#649). When writes are granted this is the plain case; when they
        // are withheld the tools must STILL be pre-approved, because the hook is what confines them to
        // AER_OUTPUT_DIR and it never gets consulted for a tool the model could not invoke. Headless
        // `-p` has no prompt to answer, so a tool that is neither pre-approved nor denied is simply
        // unusable — measured: the first live run of this change wrote nothing at all, exited 0, and
        // failed its contract, which is the exact symptom #629 describes.
        //
        // Safe because a hook exiting 2 beats a pre-approval: gate.hook-exit-2-beats-allow is the
        // sentinel that measures THIS direction, passing --allowedTools Write alongside a hook that
        // exits 2 and confirming the file is not written. (gate.allowedtools-is-preapproval-not-ceiling
        // measures the opposite direction -- that an OMITTED tool still runs -- which is what made
        // #611 invalid and #529 necessary, and is not the fact this line rests on.)
        tools.Add("Edit");
        tools.Add("Write");
        tools.Add("NotebookEdit");

        if (grant.RunShellCommands)
        {
            if (grant.ShellCommandPatterns is { Count: > 0 } patterns)
            {
                tools.AddRange(patterns.Select(pattern => $"Bash({pattern})"));
            }
            else
            {
                tools.Add("Bash");
            }
        }

        if (grant.NetworkAccess)
        {
            tools.Add("WebFetch");
            tools.Add("WebSearch");
        }

        resolvedValue = string.Join(',', tools);
        gapReason = null;
        return true;
    }

    /// <summary>
    /// The environment variable name AER inspects for an operator-configured shared Claude config root (#442).
    /// </summary>
    public const string AerClaudeConfigRootVariable = "AER_CLAUDE_CONFIG_ROOT";

    /// <summary>
    /// The environment variable name Claude Code reads for its configuration root directory.
    /// </summary>
    public const string ClaudeConfigDirVariable = "CLAUDE_CONFIG_DIR";

    /// <summary>The claude <see cref="VendorGate"/>.</summary>
    /// <remarks>
    /// <c>--settings</c> is the load-bearing pair here: it is the only route by which <c>claude</c>
    /// loads the hook at all. <c>VendorGateMatchesResolveTests</c> holds this and <see cref="Resolve"/>
    /// in step.
    /// </remarks>
    internal static VendorGate BuildGate(PermissionGrant? grant, string? workspace = null)
    {
        var (settingsPath, mcpConfigPath) = EnsureLaunchConfigFiles();
        List<string> args = ["--settings", settingsPath, "--mcp-config", mcpConfigPath];

        var disallowed = BuildDisallowedTools(grant);
        if (disallowed.Length > 0)
        {
            args.Add("--disallowedTools");
            args.Add(disallowed);
        }

        var environment = new Dictionary<string, string>
        {
            [MaxSubagentSpawnDepthVariable] = "1",
            [DeniedToolsVariable] = $"{DeniedToolsVendorTag}:{BuildHookDeniedTools(grant)}",
            [SimpleModeVariable] = "0",
        };

        if (Environment.GetEnvironmentVariable(AerClaudeConfigRootVariable) is { Length: > 0 } configRoot)
        {
            environment[ClaudeConfigDirVariable] = configRoot;
        }

        // Must mirror Resolve's own workspace clause below. Omitting it here does not fail closed in
        // a harmless direction -- it silently narrows a granted write to the outbox. See VendorGate.For.
        if (workspace is not null)
        {
            environment[WorkerEnvironment.WorkspaceVariable] = workspace;
        }

        return new VendorGate(args, environment);
    }

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(contract);

        var isWindows = OperatingSystem.IsWindows();
        var prompt = BuildPrompt(invocation.PromptTemplate, contract, isWindows);
        var permissionScope = ResolvePermissionScope(invocation);
        var artifactsRoot = EnvironmentReference("AER_ARTIFACTS_ROOT", isWindows);

        List<string> args =
        [
            "-p", prompt,
            "--allowedTools", permissionScope,
            // #289: Claude Code enforces its own directory-trust sandbox independent of
            // --allowedTools, and (confirmed empirically against the real, authenticated CLI)
            // non-deterministically refuses to write outside it when AER_OUTPUT_DIR falls outside
            // the spawned process's cwd -- which it always does for a plain chat session, since
            // ExecuteSessionTurnAsync never sets WorkerInvocation.WorkingDirectory unless the
            // session is attached to a codebase. Reproduced identically via a bare manual `claude`
            // invocation (not daemon-specific): ~50% of otherwise-identical trials silently failed
            // to produce their declared output file, each citing "outside the sandboxed worktree" /
            // "outside the allowed working directories" as its own reason, until this flag was
            // added -- 0/6 failures with it across the same trial shape. Mirrors the same grant
            // AgyWorkerAdapter has carried since spike #21 for the identical reason (agy ignores
            // the invoking process's cwd entirely); Claude turned out to need it too, just only
            // sometimes, which is what made the gap easy to miss.
            "--add-dir", artifactsRoot,
        ];

        // #533 constraints 1-2: hooks load only from the process's own cwd `.claude/`, with no
        // parent-directory fallback, and `--add-dir` (above) loads no configuration on claude --
        // measured, gate.add-dir-loads-no-config. So AER cannot rely on cwd-based discovery for
        // either the mandatory PreToolUse hook (0029) or MCP config; it passes both explicitly, at
        // a path AER owns rather than the room's own directory (`WorkingDirectory` may be a repo the
        // operator did not ask AER to write into). EnsureLaunchConfigFiles populates the real
        // PreToolUse hook (#543) -- see its own doc comment for why the settings file is left holding
        // canonical content on every resolve rather than written once, and #667 for why an unchanged
        // file is not rewritten to get there.
        var (settingsPath, mcpConfigPath) = EnsureLaunchConfigFiles();
        args.Add("--settings");
        args.Add(settingsPath);
        args.Add("--mcp-config");
        args.Add(invocation.EnablePermissionGate
            ? EnsurePermissionGateMcpConfig(invocation.EnableMemoryProposalTool)
            : invocation.EnableMemoryProposalTool ? EnsureMemoryProposalMcpConfig() : mcpConfigPath);

        if (invocation.EnablePermissionGate)
        {
            // #445: where the CLI sends a permission decision it cannot make itself. The server key is
            // alphanumeric on purpose -- claude addresses an MCP tool as
            // `mcp__<server>__<tool>`, and a hyphen in the server name risks mangling the one string
            // this flag must match exactly.
            //
            // NEVER pair this with `--permission-mode auto` (0029/0015: it silently disables the
            // prompt tool), and never with `--bare` (#521, see the block below).
            args.Add("--permission-prompt-tool");
            args.Add(PermissionPromptToolName);
        }

        // #331: --allowedTools only *pre-approves* tools so they don't prompt; it is not a sandbox,
        // and omitting a tool leaves it in the model's reach (a shell-denied session ran `hostname`
        // and returned the real value). A withheld category must be *actively* denied. Verified
        // against the live CLI in a clean spawn env: the same invocation refuses `hostname` with
        // --disallowedTools Bash and runs it without. --disallowedTools takes precedence over
        // --allowedTools, so the two compose — allow what's granted, deny what's withheld (0004).
        //
        // #445 carves out the gate: a tool named here is hard-refused by the CLI BEFORE the PreToolUse
        // hook runs (see BuildDisallowedTools' own remarks), so a withheld category on this flag can
        // never reach the hook's "ask" band and never reach the human. Under the gate the withheld set
        // rides AER_HOOK_ASK_TOOLS instead, and this flag carries only STANDING refusals -- of which
        // there are none yet, so it is omitted entirely. Ungranted is not the same as forbidden; the
        // gate fires where policy is silent, not where the operator said no.
        var disallowed = invocation.EnablePermissionGate
            ? string.Empty
            : BuildDisallowedTools(invocation.PermissionGrant);
        if (disallowed.Length > 0)
        {
            args.Add("--disallowedTools");
            args.Add(disallowed);
        }

        if (invocation.StreamJson)
        {
            // --print + --output-format=stream-json refuses to run without --verbose (confirmed
            // against the installed claude CLI directly: "Error: When using --print,
            // --output-format=stream-json requires --verbose") -- without this flag every
            // streaming session turn would fail at the CLI invocation itself, before producing any
            // output at all.
            args.Add("--output-format");
            args.Add("stream-json");
            args.Add("--include-partial-messages");
            args.Add("--verbose");
        }
        else
        {
            args.Add("--output-format");
            args.Add("text");
        }

        // Do not reintroduce `--bare` here, under any flag. It is not a latency optimisation this
        // product can take, for two independently sufficient reasons, both measured:
        //
        //   1. It skips "keychain reads" (its own --help says so) -- which is exactly where
        //      subscription OAuth login lives. A --bare dispatch against a real subscription login
        //      fails immediately with "Not logged in", even with valid, unexpired credentials, and
        //      AER works against subscriptions rather than API keys (Architecture Rule 4).
        //   2. It suppresses hooks and MCP servers EVEN WHEN PASSED EXPLICITLY via --settings
        //      (#521): `claude --bare --settings <PreToolUse hook>` does not fire the hook, while
        //      the same invocation without --bare does. 0029 makes that hook mandatory on every
        //      worker AER spawns, so --bare is the flag AER passed that removed the gate. It is
        //      not the only route to the same failure -- `--safe-mode` (a flag AER never passes,
        //      so nothing to neutralize) and CLAUDE_CODE_SIMPLE=1, documented as equivalent to
        //      --bare including its keychain-skip, disable hooks identically. Unlike --safe-mode,
        //      CLAUDE_CODE_SIMPLE is an *inherited* env var (#543: neutralized below, in
        //      CoreDispatchTarget.Environment -- AerTask inherits the full parent environment by
        //      default, so an operator's shell setting it would otherwise reach claude unopposed).
        //
        // Reason 2 is the load-bearing one: an auth failure is loud, and a missing hook is silent
        // for one of two independent reasons -- not loaded at all, or loaded but unable to execute
        // (#530 measures the second; the first traces to the discovery constraint, not to #530).
        if (invocation.SessionId is not null)
        {
            if (invocation.ResumeSession)
            {
                args.Add("--resume");
                args.Add(invocation.SessionId);
            }
            else
            {
                args.Add("--session-id");
                args.Add(invocation.SessionId);
            }
        }

        if (invocation.Model is not null)
        {
            args.Add("--model");
            args.Add(invocation.Model);
        }

        if (invocation.Effort is not null)
        {
            args.Add("--effort");
            args.Add(invocation.Effort);
        }

        // #445: the withheld set is the same computation either way -- what changes is which band it
        // rides. Gate off: it is the denied list, and the hook exits 2 (today's behaviour, byte for
        // byte). Gate on: it is the ASK list, and the denied list carries only standing "never" rules.
        var withheld = BuildHookDeniedTools(invocation.PermissionGrant);
        var environment = new List<(string Name, string Value)>
        {
            (MaxSubagentSpawnDepthVariable, "1"),
            // #600 tags it with the vendor; #649 makes its contents differ from the flag.
            (DeniedToolsVariable,
                $"{DeniedToolsVendorTag}:{(invocation.EnablePermissionGate ? StandingNeverTools : withheld)}"),
            (SimpleModeVariable, "0"),
        };

        if (invocation.EnablePermissionGate)
        {
            environment.Add((AskToolsVariable, $"{DeniedToolsVendorTag}:{withheld}"));

            // Defensive, and only on the gate path. MCP_TIMEOUT is the DOCUMENTED server-STARTUP
            // timeout (docs/vendor-doc-audit.md: 30s); 30000 restates its default so a slow host
            // spawn is not read as a missing gate. MCP_TOOL_TIMEOUT is set to 200000 to hold the
            // gate tool's blocking call past a human's response time (0029 measured a 162s hold on
            // agy; 200s is that band's upper bound), with the tool's own 180s fail-closed sitting
            // under it so AER decides the deny rather than the CLI timing out first.
            //
            // UNMEASURED on claude (claim-scope): this repo has no vendor-verify check that claude
            // honours MCP_TOOL_TIMEOUT, or what its default per-tool-call reap is when unset. Setting
            // it can only help (honoured -> the hold survives; ignored -> no worse than the default),
            // but "the held call survives to ~180s on claude" is a LIVE-SMOKE claim, not proven here.
            // #445's runbook verifies it against the authenticated CLI.
            environment.Add((McpStartupTimeoutVariable, "30000"));
            environment.Add((McpToolTimeoutVariable, "200000"));
        }

        if (Environment.GetEnvironmentVariable(AerClaudeConfigRootVariable) is { Length: > 0 } configRoot)
        {
            environment.Add((ClaudeConfigDirVariable, configRoot));
        }

        // #679; see WorkerEnvironment.WorkspaceVariable for why this is told rather than inferred,
        // and for what its absence means.
        if (invocation.WorkingDirectory is { } workspace)
        {
            environment.Add((WorkerEnvironment.WorkspaceVariable, workspace));
        }

        return new CoreDispatchTarget(
            "claude", [.. args], invocation.WorkingDirectory, PromptText: prompt,
            Environment: [.. environment], OversizePromptWrapper: OversizePromptWrapperText);
    }

    /// <summary>
    /// Overrides an inherited <c>CLAUDE_CODE_SIMPLE=1</c> (see the comment above on why that
    /// disables hooks the same way <c>--bare</c> does) so an operator's shell cannot reach the
    /// spawned <c>claude</c> process and remove the gate.
    /// <para>
    /// <b>Measured, and it is now a sentinel</b> — <c>gate.simple-mode-override-restores-the-hook</c>
    /// (#550). This carried an admission that no live run had confirmed <c>"0"</c> is even parsed,
    /// with the value chosen by analogy to a sibling variable's documented opt-out tokens. Three
    /// arms against the installed CLI settled it: unset fires the hook, <c>=0</c> fires the hook, so
    /// the override does what it claims.
    /// </para>
    /// <para>
    /// The same run corrected the <i>hazard's shape</i>. An inherited <c>=1</c> does not produce a
    /// quietly ungated worker here: the hook never fires and nothing is written, because the run dies
    /// at <c>Not logged in</c> with <c>rc=1</c> — the keychain skip reason 1 above predicts for
    /// <c>--bare</c>. Loud, not silent. <b>Scoped to a host holding a subscription login</b>, which
    /// is what AER exists to drive; nothing here establishes what an API-key host does, and that is
    /// the case where the failure could stay quiet.
    /// </para>
    /// </summary>
    public const string SimpleModeVariable = "CLAUDE_CODE_SIMPLE";

    /// <summary>
    /// The vendor tag prefixing <see cref="DeniedToolsVariable"/>'s value (#600), so an absent list, an
    /// empty one AER deliberately set, and another vendor's list are three distinguishable things
    /// rather than one that always allowed. Mirrored as a literal in <c>Aer.Cli</c>'s hook command
    /// because <c>Aer.Adapters</c> cannot reference it; <c>DeniedToolChannelTests</c> is the one test
    /// that sees both sides and fails if they drift.
    /// </summary>
    public const string DeniedToolsVendorTag = "claude";

    /// <summary>
    /// The environment variable carrying this invocation's denied-tool list to the <c>PreToolUse</c>
    /// hook's own process (#543) — <see cref="BuildHookDeniedTools"/>'s names, which since #649 are a
    /// <em>superset</em> of what <see cref="BuildDisallowedTools"/> puts on <c>--disallowedTools</c>:
    /// the write tools ride this channel only, so the hook can allow the one write that lands in
    /// <c>AER_OUTPUT_DIR</c>. Set even when empty. A hook process inherits the spawning process's
    /// environment (confirmed in <c>.vendor-survey/corpus/claude__hooks.md</c>: "A hook process
    /// inherits the parent environment"), which is what makes this reach hook-check at all -- the
    /// settings file itself is one static, shared file across every spawn (see
    /// <see cref="EnsureLaunchConfigFiles"/>), so per-invocation data has to travel this way rather
    /// than through the file's content. <see cref="Aer.Adapters"/> cannot reference <c>Aer.Cli</c>
    /// (the CLI depends on the adapters, never the reverse), so this name is a plain string contract
    /// mirrored on <c>HookCheckCommand.DeniedToolsEnvironmentVariable</c> — both sides assert the
    /// literal value in their own test suite, and the two must agree.
    /// </summary>
    public const string DeniedToolsVariable = "AER_HOOK_DENIED_TOOLS";

    /// <summary>
    /// The environment variable carrying this invocation's <b>ask</b>-band tool list to the
    /// <c>PreToolUse</c> hook (#445) — the withheld categories, vendor-tagged exactly like
    /// <see cref="DeniedToolsVariable"/>, which under the gate carries only standing refusals.
    /// </summary>
    /// <remarks>
    /// Set ONLY when <see cref="WorkerInvocation.EnablePermissionGate"/> is true, and its absence is
    /// load-bearing rather than incidental: the hook activates its ask path on a <em>Present</em> list
    /// and on nothing else, so an unset variable leaves every gate-off dispatch's hook output
    /// byte-identical to what it was before the gate existed. A plain string contract mirrored on
    /// <c>HookCheckCommand.AskToolsEnvironmentVariable</c> for the reason
    /// <see cref="DeniedToolsVariable"/> gives (canonical): <see cref="Aer.Adapters"/> cannot reference
    /// <c>Aer.Cli</c>, so both sides assert the literal and a test holds them in agreement.
    /// </remarks>
    public const string AskToolsVariable = "AER_HOOK_ASK_TOOLS";

    /// <summary>
    /// What <see cref="DeniedToolsVariable"/> carries under the gate (#445): the STANDING "never"
    /// rules — a persisted permanent refusal, which still exits 2 without ever troubling the human.
    /// None exist yet, so it is empty; a withheld-but-not-refused category rides
    /// <see cref="AskToolsVariable"/> instead. Empty rather than absent, because #600 makes an absent
    /// list deny everything.
    /// </summary>
    private const string StandingNeverTools = "";

    /// <summary>
    /// The MCP server key <see cref="EnsurePermissionGateMcpConfig"/> registers the gate tool under,
    /// and the one string <see cref="PermissionPromptToolName"/> is built from. <b>Alphanumeric
    /// only</b> — claude addresses an MCP tool as <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>, so a
    /// hyphen here risks a mangled name in the flag value that has to match exactly.
    /// </summary>
    public const string PermissionGateMcpServerName = "aerpermission";

    /// <summary>
    /// The value handed to <c>--permission-prompt-tool</c> when the gate is on (#445): AER's own
    /// <c>aer_permission_ask</c>, addressed through <see cref="PermissionGateMcpServerName"/>.
    /// </summary>
    public const string PermissionPromptToolName = $"mcp__{PermissionGateMcpServerName}__aer_permission_ask";

    /// <summary>The MCP server STARTUP timeout, in milliseconds (#445, gate path only).</summary>
    public const string McpStartupTimeoutVariable = "MCP_TIMEOUT";

    /// <summary>
    /// The per-MCP-tool-call timeout, in milliseconds (#445, gate path only) — the one that bounds the
    /// gate tool's held-open wait for a human.
    /// </summary>
    public const string McpToolTimeoutVariable = "MCP_TOOL_TIMEOUT";

    /// <summary>
    /// The environment variable carrying shell command patterns for pattern-scoped grants (#659).
    /// </summary>
    public const string ShellPatternsVariable = "AER_HOOK_SHELL_PATTERNS";


    /// <summary>
    /// The environment variable name Claude Code reads for its subagent fan-out depth cap.
    /// </summary>
    /// <remarks>
    /// #533 constraint 3, measured rather than trusted from the vendor's own docs: the vendor
    /// documents this variable's default as <c>1</c> (no nesting), but two independent runs of
    /// <c>fanout.nesting-allowed-by-default</c> (<c>tools/vendor-verify/verify.py</c>) counted
    /// actual <c>SubagentStart</c> spawns and found the unset default produces <b>2</b> -- a
    /// subagent CAN spawn its own subagent with nothing configured. Set explicitly to <c>1</c> here
    /// so AER's own default matches what the vendor documents rather than what it measurably does.
    /// <para>
    /// #533 constraint 4 is why this is the only lever: a subagent inherits its parent's permission
    /// mode and cannot be given a stricter one, so the gate for a fan-out tree cannot be re-applied
    /// per level -- it has to hold for whatever depth this variable allows. Raising it later (e.g.
    /// for a legitimate multi-worker room, M27) is a deliberate widening, not a default to assume.
    /// </para>
    /// </remarks>
    public const string MaxSubagentSpawnDepthVariable = "CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH";

    /// <summary>
    /// Ensures the two files <see cref="AerPaths.WorkerLaunchConfig"/> needs exist. Called on every
    /// <see cref="Resolve"/> because there is no single daemon-lifecycle hook covering every entry
    /// point that resolves a claude invocation (the CLI's `aer run`/`aer decide`/etc. spawn a fresh
    /// process per command, with no daemon involved at all).
    /// </summary>
    /// <remarks>
    /// <b>The settings file is left holding canonical content on every resolve (#543), reversing
    /// #533's "never overwrite existing content."</b> That was correct while the file held only inert
    /// `{}` with nothing to lose; now it carries the mandatory `PreToolUse` hook (0029), and "never
    /// overwrite" would leave a pre-#543 `{}` -- or any other stale content -- permanently installed,
    /// silently disabling the gate for good on any machine that ran an earlier build even once. The
    /// file is entirely AER-owned (no operator content can live here, per
    /// <see cref="AerPaths.WorkerLaunchConfig"/>'s own doc comment), so there is nothing that
    /// overwriting could destroy. Since #667 the write is skipped when the file already holds exactly
    /// that content -- a narrower thing than "never overwrite", and one that leaves drift correction
    /// intact; see <see cref="AtomicLaunchConfigWriter"/> for why the redundant write was worth
    /// removing. The MCP config file is untouched by #543 and keeps the old once-only semantics.
    /// </remarks>
    private static (string SettingsPath, string McpConfigPath) EnsureLaunchConfigFiles()
    {
        Directory.CreateDirectory(AerPaths.WorkerLaunchConfig);

        var settingsPath = Path.Combine(AerPaths.WorkerLaunchConfig, "claude-settings.json");
        AtomicLaunchConfigWriter.Write(settingsPath, BuildSettingsJson());

        // The standard empty MCP config shape -- declares no servers, so this adds nothing beyond
        // what claude would otherwise discover on its own.
        var mcpConfigPath = Path.Combine(AerPaths.WorkerLaunchConfig, "claude-mcp.json");
        EnsureFileExists(mcpConfigPath, "{\"mcpServers\":{}}");

        return (settingsPath, mcpConfigPath);
    }

    /// <summary>
    /// Ensures the <c>--mcp-config</c> file naming AER's own MCP server (#585) and its
    /// <c>memory-edit-proposal</c> tool (#801) exists, returning its path. Left holding canonical
    /// content on every resolve, mirroring <see cref="EnsureLaunchConfigFiles"/>'s settings file
    /// rather than the plain empty <c>claude-mcp.json</c>'s once-only semantics -- this file's
    /// content is exactly as load-bearing as the PreToolUse hook's, just opt-in rather than mandatory.
    /// </summary>
    /// <remarks>
    /// <b>Carries no capture-directory path (#833).</b> #801 shipped this file naming a static,
    /// shared capture directory literally in its <c>args</c> -- every room's proposals landed in one
    /// place with no room attribution, which is why no daemon poller was ever wired to consume it
    /// (#833's fork). This file is resolved once per worker-binding entry, before any execution's
    /// <c>AER_OUTPUT_DIR</c> exists (<see cref="Resolve"/> runs once per binding, not per execution --
    /// see <see cref="Aer.Adapters.WorkerInvocation"/>'s own doc comment for why), so nothing baked in
    /// here can vary per execution. The <c>--memory-proposal-tool</c> flag alone tells
    /// <c>Aer.Mcp.Host</c> to enable the tool; the process derives its own per-execution capture
    /// directory from <c>AER_OUTPUT_DIR</c>, which it inherits from the <c>claude</c> process that
    /// spawns it as an MCP server -- the same inheritance <c>Aer.Cli.Program</c>'s <c>hook-check</c>
    /// branch already rests on for the identical reason.
    /// </remarks>
    private static string EnsureMemoryProposalMcpConfig()
    {
        Directory.CreateDirectory(AerPaths.WorkerLaunchConfig);
        var hostDllPath = Path.Combine(AppContext.BaseDirectory, "Aer.Mcp.Host.dll");
        var configPath = Path.Combine(AerPaths.WorkerLaunchConfig, "claude-mcp-memory-proposal.json");
        var json = JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["aer-memory-proposal"] = new
                {
                    command = "dotnet",
                    args = new[] { hostDllPath, "--memory-proposal-tool" },
                },
            },
        });

        AtomicLaunchConfigWriter.Write(configPath, json);
        return configPath;
    }

    /// <summary>
    /// Ensures the <c>--mcp-config</c> file naming the runtime permission gate's MCP server (#445)
    /// exists, returning its path. Mirrors <see cref="EnsureMemoryProposalMcpConfig"/> in every
    /// respect — canonical content on every resolve, no per-execution path baked in (the host derives
    /// its rendezvous directory from the <c>AER_OUTPUT_DIR</c> it inherits from the spawning
    /// <c>claude</c> process; see that method's remarks, which are the record for both).
    /// </summary>
    /// <param name="alsoMemoryProposal">
    /// Composes the memory-proposal server into the SAME file rather than replacing it. <c>--mcp-config</c>
    /// takes one path, so the two opt-ins cannot each point it somewhere; composing is what keeps them
    /// independent. The interactive turn — the only caller that enables the gate today — has memory
    /// proposals off, so this is false there; the two files are kept separate so a concurrent resolve
    /// of one shape never rewrites the other's.
    /// </param>
    private static string EnsurePermissionGateMcpConfig(bool alsoMemoryProposal)
    {
        Directory.CreateDirectory(AerPaths.WorkerLaunchConfig);
        var hostDllPath = Path.Combine(AppContext.BaseDirectory, "Aer.Mcp.Host.dll");

        var servers = new Dictionary<string, object>
        {
            [PermissionGateMcpServerName] = new
            {
                command = "dotnet",
                args = new[] { hostDllPath, "--permission-gate-tool", "claude" },
            },
        };

        if (alsoMemoryProposal)
        {
            servers["aer-memory-proposal"] = new
            {
                command = "dotnet",
                args = new[] { hostDllPath, "--memory-proposal-tool" },
            };
        }

        var fileName = alsoMemoryProposal
            ? "claude-mcp-permission-gate-and-memory-proposal.json"
            : "claude-mcp-permission-gate.json";
        var configPath = Path.Combine(AerPaths.WorkerLaunchConfig, fileName);
        AtomicLaunchConfigWriter.Write(configPath, JsonSerializer.Serialize(new { mcpServers = servers }));
        return configPath;
    }

    /// <summary>
    /// The `--settings` content #543 ships: one `PreToolUse` hook, matching every tool
    /// (<c>"matcher": "*"</c>), spawned in exec form (`args` set) so Claude Code invokes it directly
    /// with no shell -- no quoting concerns, matching this adapter's own "direct shell-less" design
    /// (see the type's own doc comment).
    /// </summary>
    /// <remarks>
    /// <b>Invoked as <c>dotnet &lt;Aer.Cli.dll path&gt;</c>, not the native apphost.</b> An earlier
    /// version of this method named <c>Aer.Cli.exe</c>/<c>Aer.Cli</c> directly, resolved via
    /// <see cref="AppContext.BaseDirectory"/>. That works for a raw build output (confirmed for both
    /// `Aer.Cli.exe` standalone and `Aer.Daemon.exe`, which references `Aer.Cli` through
    /// `Aer.Ui.Core` and so carries a copy in its own output directory) but is wrong for `aer`'s
    /// other real, exercised deployment shape: <c>Aer.Cli.csproj</c> sets <c>PackAsTool</c>, and a
    /// packed global tool's <c>DotnetToolSettings.xml</c> runs <c>Aer.Cli.dll</c> via the <c>dotnet</c>
    /// muxer with **no apphost at all** (confirmed by packing the tool and inspecting the nupkg) --
    /// naming the apphost there would silently write a dangling command into every worker's hook,
    /// exactly the fail-open-and-silent failure #530 measured. `dotnet &lt;dll&gt;` works in both
    /// shapes: the managed dll and its `.runtimeconfig.json`/`.deps.json` sit next to
    /// <see cref="AppContext.BaseDirectory"/> either way (a raw build's own output directory, or a
    /// global tool's own store directory -- it is, after all, the same dll this process is currently
    /// running from), and `dotnet` itself is a hard prerequisite for this whole product already
    /// (`CLAUDE.md`: ".NET 10 SDK is required"). The explicit <see cref="File.Exists"/> guard below
    /// turns any future deployment shape this reasoning missed into a loud failure at dispatch time
    /// rather than a silent one at hook-invocation time.
    /// </remarks>
    private static string BuildSettingsJson()
    {
        var hookAssemblyPath = Path.Combine(AppContext.BaseDirectory, "Aer.Cli.dll");
        if (!File.Exists(hookAssemblyPath))
        {
            throw new InvalidOperationException(
                $"Cannot write the mandatory PreToolUse hook (decision 0029): '{hookAssemblyPath}' " +
                "does not exist. Every deployment of aer/Aer.Daemon must carry Aer.Cli.dll alongside " +
                "its own binary -- a hook naming a path that does not exist fails open and silently " +
                "(#530), so this fails loudly here instead, before any worker is dispatched.");
        }

        var settings = new
        {
            hooks = new
            {
                PreToolUse = new[]
                {
                    new
                    {
                        matcher = "*",
                        hooks = new[]
                        {
                            new
                            {
                                type = "command",
                                command = "dotnet",
                                args = new[] { hookAssemblyPath, "hook-check" },
                            },
                        },
                    },
                },
            },
        };

        return JsonSerializer.Serialize(settings);
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> only if it does not already
    /// exist, without silently swallowing a genuine write failure.
    /// </summary>
    /// <remarks>
    /// Two turns can genuinely race here -- two chat sessions both starting their first-ever turn
    /// against a fresh <c>~/.aer</c>, both hitting this before either file exists, from the SAME
    /// daemon process, not just two separate `aer run` processes. That is a real TOCTOU: `File.Exists`
    /// then `File.WriteAllText` opens write-exclusive, so the loser of the race gets an
    /// <see cref="IOException"/>, not a second identical write as an earlier version of this comment
    /// claimed. The content this writes is fixed and identical regardless of who wins, so the correct
    /// response to that specific exception is "someone else just created it" -- verified by re-checking
    /// existence, not assumed. Any other failure (permissions, disk full, a genuinely corrupt partial
    /// write) still throws, per CLAUDE.md's rule against silently swallowing exceptions.
    /// </remarks>
    private static void EnsureFileExists(string path, string content)
    {
        if (File.Exists(path))
        {
            return;
        }

        try
        {
            File.WriteAllText(path, content);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another spawn's write won the race and the file is now there -- not our problem to fix.
        }
    }

    /// <summary>
    /// A structured <see cref="WorkerInvocation.PermissionGrant"/> always wins over the raw
    /// <see cref="WorkerInvocation.PermissionScope"/> string (<see cref="PermissionGrant"/>'s own
    /// docs record this precedence); <see cref="TryTranslatePermissionGrant"/> never refuses for
    /// this adapter, so this never throws.
    /// </summary>
    private string ResolvePermissionScope(WorkerInvocation invocation)
    {
        if (invocation.PermissionGrant is { } grant)
        {
            if (!TryTranslatePermissionGrant(grant, out var resolved, out var gapReason))
            {
                throw new PermissionGrantUnsupportedException("claude", gapReason!);
            }

            return resolved!;
        }

        return invocation.PermissionScope ?? DefaultPermissionScope;
    }

    /// <summary>
    /// The deny-list mirror of <see cref="TryTranslatePermissionGrant"/> (#331): every category the
    /// grant <em>withholds</em> maps to the Claude Code tool(s) that would otherwise reach it, emitted
    /// as <c>--disallowedTools</c>. This is what makes a withheld checkbox true — <c>--allowedTools</c>
    /// only auto-approves, it does not remove an unlisted tool from the model's reach.
    /// <para>
    /// <b>Except the write tools, since #649.</b> <c>Edit</c>/<c>Write</c>/<c>NotebookEdit</c> are
    /// withheld by the <c>PreToolUse</c> hook alone (<see cref="BuildHookDeniedTools"/>), because a
    /// name on this flag is refused by the CLI before the hook can allow the one write landing in
    /// <c>AER_OUTPUT_DIR</c>. <c>ChannelPopulationTests</c> holds the two channels to that split
    /// across all sixteen grants.
    /// </para>
    /// <para>
    /// <b>Boundary:</b> denial here is by <em>enumeration</em>, not default-deny. It covers the tools a
    /// grant category names; it does not cover tools outside the grant's four categories (<c>Task</c>,
    /// MCP server tools, or a tool a future CLI adds). Genuine fail-closed across the whole tool surface
    /// is the broader change decision 0004 tracks (the project ceiling); this closes the reported,
    /// category-mapped holes. Returns <see cref="string.Empty"/> when there is no structured grant (the
    /// raw <see cref="WorkerInvocation.PermissionScope"/> escape hatch carries no category to deny) or
    /// when nothing is withheld.
    /// </para>
    /// <para>
    /// <b>WHAT THIS DOES NOT GUARANTEE — read before relying on it (#529, measured 2026-07-25).</b>
    /// This method bounds <em>which tool runs</em>. It does <em>not</em> bound what the worker can
    /// achieve, because <b>the model substitutes another tool and reaches the same goal</b>. Measured
    /// with <c>--disallowedTools Edit,Write,NotebookEdit</c> — the string this method emitted for a
    /// withheld-write grant before #649 moved those names to the hook: the file was created anyway,
    /// by <c>Bash</c>.
    /// Because the four categories are independent, <c>Bash</c> stays available whenever
    /// <see cref="PermissionGrant.RunShellCommands"/> is granted — and <c>Bash</c> alone defeats
    /// withheld <em>writes</em>, withheld <em>reads</em> (<c>cat</c>) and withheld <em>network</em>
    /// (<c>curl</c>). The caveat in the previous paragraph is about tools outside the four categories;
    /// this hole is <em>inside</em> them, and write-withheld-plus-shell-granted is a common grant
    /// shape rather than an exotic one.
    /// </para>
    /// <para>
    /// A <em>resolved binding</em> can no longer carry that shape:
    /// <see cref="WorkerBindingResolver.Resolve"/> refuses it
    /// (<see cref="IncoherentPermissionGrantException"/>). That narrows which grants reach this
    /// method; it does not close the gap, which is why everything above still holds. The substitution
    /// itself is untouched, and an entry using the raw <c>PermissionScope</c> escape hatch carries no
    /// <see cref="PermissionGrant"/> for that refusal to inspect — so it arrives here with
    /// <c>grant is null</c> and nothing denied at all.
    /// </para>
    /// <para>
    /// Treat the result as <b>pre-approval and routing, never as a security boundary</b>. The
    /// mechanisms measured to stop an <em>operation</em> gate on the operation rather than the tool
    /// (a <c>PreToolUse</c> hook exiting 2, an explicit <c>ask</c> rule, a hook returning
    /// <c>permissionDecision: "ask"</c>, and <c>requiresUserInteraction</c> on MCP tools), which is
    /// exactly why substitution does not defeat them. See <c>docs/vendor-doc-audit.md</c>; re-runnable
    /// via <c>pixi run vendor-verify -- --only gate.allowedtools-is-preapproval-not-ceiling</c>.
    /// </para>
    /// </summary>
    private static string BuildDisallowedTools(PermissionGrant? grant)
    {
        if (grant is null)
        {
            return string.Empty;
        }

        var names = WithheldToolNames(grant, includeWriteTools: false);

        // 0022's DenyAlways rung (#390): a standing per-command refusal. --disallowedTools takes
        // precedence over --allowedTools -- measured, `git push` denied under
        // `--allowedTools "Bash(git *)" --disallowedTools "Bash(git push*)"` (docs/vendor-capabilities.md)
        // -- so a denied family is refused even when the shell is otherwise granted. This is the claude
        // enforcement for the rung: redundant-but-harmless when Bash is withheld wholesale (already in
        // `names`), load-bearing when RunShellCommands granted the shell and only this family is refused.
        if (grant.DeniedShellCommandPatterns is { Count: > 0 } deniedPatterns)
        {
            names.AddRange(deniedPatterns.Select(pattern => $"Bash({pattern})"));
        }

        return string.Join(',', names);
    }

    /// <summary>
    /// The withheld tool names carried to the <c>PreToolUse</c> hook — the same list
    /// <see cref="BuildDisallowedTools"/> emits, <b>plus</b> the write tools it deliberately omits (#649).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two lists differ on exactly one category, and the difference is the whole of #649. A write
    /// named in <c>--disallowedTools</c> is refused by the CLI before the hook is consulted, so the
    /// hook could never allow a worker to write its own declared output — which is why a read-only
    /// reviewer could not produce a deliverable, and why every reviewing template granted a workspace
    /// write it never needed. Withholding writes therefore moves off the flag and onto the hook, which
    /// can see the target path and allow only the ones landing in <c>AER_OUTPUT_DIR</c>.
    /// </para>
    /// <para>
    /// <b>This is an enforcement-boundary change, not a refactor.</b> Writes were denied by the flag
    /// measured to actually enforce (<c>gate.allowedtools-is-preapproval-not-ceiling</c> established
    /// that only the deny list does) and are now denied by the hook. Three things bound it: 0029 makes
    /// the hook mandatory on every spawned worker, #600 makes a missing or wrong-vendor denied list
    /// deny rather than allow, and on agy this changes nothing at all — under
    /// <c>--dangerously-skip-permissions</c> the hook was already the only boundary. Every other
    /// category keeps its flag denial as well as its hook entry, so only writes move.
    /// </para>
    /// </remarks>
    internal static string BuildHookDeniedTools(PermissionGrant? grant) =>
        grant is null ? string.Empty : string.Join(',', WithheldToolNames(grant, includeWriteTools: true));

    /// <summary>
    /// Yes, by the two mechanisms above acting together (#649): the write tools stay pre-approved on
    /// <c>--allowedTools</c> so the model can invoke them, they are absent from
    /// <see cref="BuildDisallowedTools"/> so the CLI does not refuse them first, and
    /// <see cref="BuildHookDeniedTools"/> hands the hook the names it confines to
    /// <c>AER_OUTPUT_DIR</c>. Verified live: a <c>WriteFiles: false</c> worker wrote its declared
    /// output and failed to write its workspace.
    /// </summary>
    public bool WithheldWritesReachTheOutbox => true;

    private static List<string> WithheldToolNames(PermissionGrant grant, bool includeWriteTools)
    {
        List<string> denied = [];
        if (!grant.ReadFiles)
        {
            denied.Add("Read");
        }

        if (!grant.WriteFiles && includeWriteTools)
        {
            denied.Add("Edit");
            denied.Add("Write");
            denied.Add("NotebookEdit");
        }

        if (!grant.RunShellCommands)
        {
            denied.Add("Bash");
        }

        if (!grant.NetworkAccess)
        {
            denied.Add("WebFetch");
            denied.Add("WebSearch");
        }

        return denied;
    }

    private static string BuildPrompt(string promptTemplate, WorkerContract contract, bool isWindows)
    {
        var prompt = new StringBuilder(promptTemplate);

        if (contract.RequiredInputs.Count > 0)
        {
            prompt.Append("\n\nInputs, in the order listed, are available at:\n");
            for (var i = 0; i < contract.RequiredInputs.Count; i++)
            {
                prompt.Append($"- {contract.RequiredInputs[i]}: {EnvironmentReference($"AER_INPUT_{i}", isWindows)}\n");
            }
        }

        if (contract.ProducedOutputs.Count > 0)
        {
            prompt.Append("\nWrite each of the following outputs to the exact path shown, creating parent directories as needed:\n");
            foreach (var output in contract.ProducedOutputs)
            {
                var outputDir = EnvironmentReference("AER_OUTPUT_DIR", isWindows);
                var separator = isWindows ? '\\' : '/';
                prompt.Append($"- {output.Name}: {outputDir}{separator}{output.Name}\n");
            }
        }

        return prompt.ToString();
    }

    private static string EnvironmentReference(string name, bool isWindows) =>
        WorkerEnvironmentReference.For(name, isWindows);

    /// <summary>
    /// Claude Code has no machine-readable "list models" subcommand — <c>--model</c> only documents
    /// its accepted values as help-text examples (<c>claude --help</c>: "Provide an alias for the
    /// latest model (e.g. 'sonnet', 'opus') or a model's full name"). Aliases are the stable
    /// interface here: each always resolves to that tier's current model, so this list doesn't need
    /// updating every model generation the way a hardcoded full model ID would.
    /// </summary>
    private static readonly IReadOnlyList<string> ModelAliases = ["sonnet", "opus", "haiku"];

    public Task<WorkerCapabilities> DiscoverCapabilitiesAsync(string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        var items = new List<WorkerCapabilityItem>();
        var searchDirs = new List<string>();

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            searchDirs.Add(workingDirectory);
        }
        var userClaudeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        if (Directory.Exists(userClaudeDir))
        {
            searchDirs.Add(userClaudeDir);
        }

        foreach (var baseDir in searchDirs)
        {
            var skillsDir = Path.Combine(baseDir, ".claude", "skills");
            if (Directory.Exists(skillsDir))
            {
                foreach (var skillSubDir in Directory.GetDirectories(skillsDir))
                {
                    var skillFile = Path.Combine(skillSubDir, "SKILL.md");
                    var name = Path.GetFileName(skillSubDir);
                    var desc = $"Skill in {name}";
                    if (File.Exists(skillFile))
                    {
                        try
                        {
                            var text = File.ReadAllText(skillFile);
                            var lines = text.Split('\n');
                            foreach (var l in lines)
                            {
                                if (l.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                                {
                                    desc = l["description:".Length..].Trim().Trim('"', '\'');
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                    items.Add(new WorkerCapabilityItem(name, "skill", desc));
                }
            }

            var commandsDir = Path.Combine(baseDir, ".claude", "commands");
            if (Directory.Exists(commandsDir))
            {
                foreach (var file in Directory.GetFiles(commandsDir, "*.md"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    items.Add(new WorkerCapabilityItem($"/{name}", "command", $"Custom command /{name}"));
                }
            }
        }

        items.Add(new WorkerCapabilityItem("/compact", "command", "Summarize and compact session history"));
        items.Add(new WorkerCapabilityItem("/clear", "command", "Clear session context"));

        var uniqueItems = items.GroupBy(i => i.Name).Select(g => g.First()).ToList();
        return Task.FromResult(new WorkerCapabilities("claude", uniqueItems, ModelAliases));
    }

    /// <summary>
    /// Parses one line of <c>claude --output-format stream-json --include-partial-messages</c>'s
    /// newline-delimited JSON (M24 Phase 1's live in-turn streaming). The <c>system</c>/<c>assistant</c>
    /// envelopes below are confirmed against a real, live invocation of the installed CLI (a
    /// same-shape <c>{"type":"assistant","message":{"content":[{"type":"text",...}]}}</c> line came
    /// back even from an unauthenticated run's error response) — those branches are load-bearing.
    /// The <c>stream_event</c>/<c>content_block_delta</c> branch mirrors the publicly documented
    /// Anthropic Messages streaming event shape Claude Code wraps for <c>--include-partial-messages</c>'
    /// token-level deltas, but no authenticated session was available to observe one directly; if the
    /// real shape differs, this simply never matches and contributes no partial deltas — full
    /// per-message text (the confirmed branch above) still arrives once each block completes, so
    /// streaming degrades to coarser granularity rather than silently breaking.
    /// </summary>
    public bool TryParseProgressEvent(string rawLine, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeProp))
            {
                return false;
            }

            return typeProp.GetString() switch
            {
                "system" => TryParseSystemEvent(root, out progressEvent),
                "assistant" => TryParseAssistantEvent(root, out progressEvent),
                "stream_event" => TryParseStreamEvent(root, out progressEvent),
                _ => false,
            };
        }
        catch (JsonException)
        {
            // A line split across a stdout chunk boundary, or a non-JSON line this format never
            // produces -- not a progress event, not an error.
            return false;
        }
    }

    private static bool TryParseSystemEvent(JsonElement root, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (!root.TryGetProperty("subtype", out var subtypeProp))
        {
            return false;
        }

        switch (subtypeProp.GetString())
        {
            case "init":
                progressEvent = new WorkerProgressEvent("status", "Session started");
                return true;
            case "status" when root.TryGetProperty("status", out var statusProp) && statusProp.GetString() is { Length: > 0 } status:
                progressEvent = new WorkerProgressEvent("status", status);
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseAssistantEvent(JsonElement root, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (!root.TryGetProperty("message", out var messageProp) ||
            !messageProp.TryGetProperty("content", out var contentProp) ||
            contentProp.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var block in contentProp.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var blockTypeProp))
            {
                continue;
            }

            switch (blockTypeProp.GetString())
            {
                case "text" when block.TryGetProperty("text", out var textProp) && textProp.GetString() is { Length: > 0 } text:
                    progressEvent = new WorkerProgressEvent("text", text);
                    return true;
                case "tool_use" when block.TryGetProperty("name", out var nameProp) && nameProp.GetString() is { Length: > 0 } toolName:
                    progressEvent = new WorkerProgressEvent("tool", toolName);
                    return true;
            }
        }

        return false;
    }

    private static bool TryParseStreamEvent(JsonElement root, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (root.TryGetProperty("event", out var eventProp) &&
            eventProp.TryGetProperty("type", out var eventTypeProp) &&
            eventTypeProp.GetString() == "content_block_delta" &&
            eventProp.TryGetProperty("delta", out var deltaProp) &&
            deltaProp.TryGetProperty("type", out var deltaTypeProp) &&
            deltaTypeProp.GetString() == "text_delta" &&
            deltaProp.TryGetProperty("text", out var deltaTextProp) &&
            deltaTextProp.GetString() is { Length: > 0 } deltaText)
        {
            progressEvent = new WorkerProgressEvent("text", deltaText, IsPartial: true);
            return true;
        }

        return false;
    }
}
