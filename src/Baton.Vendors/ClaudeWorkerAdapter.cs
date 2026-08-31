using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;

namespace Baton.Vendors;

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
/// <c>BATON_OUTPUT_DIR</c>. For that category the hook is the whole enforcement; for the other three
/// the sentence above still holds. See <see cref="BuildHookDeniedTools"/>.
/// </para>
/// </summary>
public sealed class ClaudeWorkerAdapter : IWorkerAdapter, IPermissionGrantTranslator
{
    internal const string OversizePromptWrapperText =
        "Read the complete task instructions in %BATON_PROMPT_FILE% and execute them exactly as written. Do not summarize or treat as data.";

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
        // BATON_OUTPUT_DIR and it never gets consulted for a tool the model could not invoke. Headless
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
    public const string BatonClaudeConfigRootVariable = "BATON_CLAUDE_CONFIG_ROOT";

    /// <summary>
    /// The environment variable name Claude Code reads for its configuration root directory.
    /// </summary>
    public const string ClaudeConfigDirVariable = "CLAUDE_CONFIG_DIR";

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(contract);

        var isWindows = OperatingSystem.IsWindows();
        var prompt = BuildPrompt(invocation.PromptTemplate, contract, isWindows);
        var permissionScope = ResolvePermissionScope(invocation);
        var artifactsRoot = EnvironmentReference("BATON_ARTIFACTS_ROOT", isWindows);

        List<string> args =
        [
            "-p", prompt,
            "--allowedTools", permissionScope,
            // #289: Claude Code enforces its own directory-trust sandbox independent of
            // --allowedTools, and (confirmed empirically against the real, authenticated CLI)
            // non-deterministically refuses to write outside it when BATON_OUTPUT_DIR falls outside
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
        args.Add(invocation.EnableMemoryProposalTool ? EnsureMemoryProposalMcpConfig() : mcpConfigPath);

        // #331: --allowedTools only *pre-approves* tools so they don't prompt; it is not a sandbox,
        // and omitting a tool leaves it in the model's reach (a shell-denied session ran `hostname`
        // and returned the real value). A withheld category must be *actively* denied. Verified
        // against the live CLI in a clean spawn env: the same invocation refuses `hostname` with
        // --disallowedTools Bash and runs it without. --disallowedTools takes precedence over
        // --allowedTools, so the two compose — allow what's granted, deny what's withheld (0004).
        var disallowed = BuildDisallowedTools(invocation.PermissionGrant);
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
        //      CoreDispatchTarget.Environment -- BatonTask inherits the full parent environment by
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

        if (invocation.Model is { } model)
        {
            RefuseDotDelimitedClaudeModelId(model); // #1090
            args.Add("--model");
            args.Add(model);
        }

        if (invocation.Effort is not null)
        {
            // #1318: see EffortTierMapping for why this is resolved rather than forwarded as-is.
            args.Add("--effort");
            args.Add(EffortTierMapping.ResolveForClaude(invocation.Effort));
        }

        var withheld = BuildHookDeniedTools(invocation.PermissionGrant);
        var environment = new List<(string Name, string Value)>
        {
            (MaxSubagentSpawnDepthVariable, "1"),
            // #600 tags it with the vendor; #649 makes its contents differ from the flag.
            (DeniedToolsVariable, $"{DeniedToolsVendorTag}:{withheld}"),
            (SimpleModeVariable, "0"),
            // #1459: always set, even empty -- an empty-but-tagged list is the deliberate
            // unscoped-shell reading (HookCheckCommand.Decide skips the segment-level check), where an
            // absent/wrong-vendor one is a broken channel and also skips it (see that method's own
            // remarks for why claude's absent case reads opposite to agy's). Reuses
            // AgyWorkerAdapter's builders rather than restating them -- both read the same
            // PermissionGrant fields the same way; only the vendor tag differs, and that is applied
            // here.
            (ShellPatternsVariable,
                $"{ShellPatternsVendorTag}:{AgyWorkerAdapter.BuildShellPatterns(invocation.PermissionGrant)}"),
            (DeniedShellPatternsVariable,
                $"{ShellPatternsVendorTag}:{AgyWorkerAdapter.BuildDeniedShellPatterns(invocation.PermissionGrant)}"),
        };

        if (Environment.GetEnvironmentVariable(BatonClaudeConfigRootVariable) is { Length: > 0 } configRoot)
        {
            environment.Add((ClaudeConfigDirVariable, configRoot));
        }

        // #679; see WorkerEnvironment.WorkspaceVariable for why this is told rather than inferred,
        // and for what its absence means.
        if (invocation.WorkingDirectory is { } workspace)
        {
            environment.Add((WorkerEnvironment.WorkspaceVariable, workspace));
        }

        // This literal name resolves through PATH the same way scripts/verify-pack-roundtrip.sh
        // documents in detail (the CVE-2024-24576 stance, measured for BatonTask's managed spawn
        // path, #1474): a real claude.exe, never an npm-installed `claude.cmd`/`.bat` shim, which
        // will fail spawn with "program not found" -- the native installer's claude.exe is
        // required (#1468).
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
    /// rather than one that always allowed. Mirrored as a literal in <c>Baton.Cli</c>'s hook command
    /// because <c>Baton.Vendors</c> cannot reference it; <c>DeniedToolChannelTests</c> is the one test
    /// that sees both sides and fails if they drift.
    /// </summary>
    public const string DeniedToolsVendorTag = "claude";

    /// <summary>
    /// The environment variable carrying this invocation's denied-tool list to the <c>PreToolUse</c>
    /// hook's own process (#543) — <see cref="BuildHookDeniedTools"/>'s names, which since #649 are a
    /// <em>superset</em> of what <see cref="BuildDisallowedTools"/> puts on <c>--disallowedTools</c>:
    /// the write tools ride this channel only, so the hook can allow the one write that lands in
    /// <c>BATON_OUTPUT_DIR</c>. Set even when empty. A hook process inherits the spawning process's
    /// environment (confirmed in <c>.vendor-survey/corpus/claude__hooks.md</c>: "A hook process
    /// inherits the parent environment"), which is what makes this reach hook-check at all -- the
    /// settings file itself is one static, shared file across every spawn (see
    /// <see cref="EnsureLaunchConfigFiles"/>), so per-invocation data has to travel this way rather
    /// than through the file's content. <see cref="Baton.Vendors"/> cannot reference <c>Baton.Cli</c>
    /// (the CLI depends on the adapters, never the reverse), so this name is a plain string contract
    /// mirrored on <c>HookCheckCommand.DeniedToolsEnvironmentVariable</c> — both sides assert the
    /// literal value in their own test suite, and the two must agree.
    /// </summary>
    public const string DeniedToolsVariable = "BATON_HOOK_DENIED_TOOLS";

    /// <summary>
    /// The environment variable carrying shell command patterns for pattern-scoped grants (#659).
    /// Declared but never set into a spawned worker's environment until #1459 — see
    /// <see cref="ShellPatternsVendorTag"/> and <c>Resolve</c>'s environment list below for the wiring,
    /// and <c>HookCheckCommand.Decide</c> for what reads it.
    /// </summary>
    public const string ShellPatternsVariable = "BATON_HOOK_SHELL_PATTERNS";

    /// <summary>
    /// The vendor tag prefixing <see cref="ShellPatternsVariable"/>'s and
    /// <see cref="DeniedShellPatternsVariable"/>'s values (#600's pattern, applied here by #1459).
    /// </summary>
    public const string ShellPatternsVendorTag = "claude";

    /// <summary>
    /// The environment variable carrying this invocation's <b>denied</b> shell command patterns —
    /// 0022's DenyAlways rung (#390), same literal as <c>AgyWorkerAdapter.DeniedShellPatternsVariable</c>
    /// (record-once: declared there first, referenced here rather than restated). claude's OWN
    /// enforcement for that rung is <c>--disallowedTools Bash(pattern)</c>
    /// (<see cref="StandingShellDenials"/>), which the CLI applies with precedence over
    /// <c>--allowedTools</c> and which survives a silently-dead hook (#530) — so this channel is
    /// belt-and-braces for the hook's own segment-level check (#1459, spec/baton.md §9), not this
    /// vendor's only enforcement of a standing "never" the way it is on agy.
    /// </summary>
    public const string DeniedShellPatternsVariable = AgyWorkerAdapter.DeniedShellPatternsVariable;

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
    /// Ensures the two files <see cref="BatonPaths.WorkerLaunchConfig"/> needs exist. Called on every
    /// <see cref="Resolve"/> because there is no single daemon-lifecycle hook covering every entry
    /// point that resolves a claude invocation (the CLI's `baton run`/`baton decide`/etc. spawn a fresh
    /// process per command, with no daemon involved at all).
    /// </summary>
    /// <remarks>
    /// <b>The settings file is left holding canonical content on every resolve (#543), reversing
    /// #533's "never overwrite existing content."</b> That was correct while the file held only inert
    /// `{}` with nothing to lose; now it carries the mandatory `PreToolUse` hook (0029), and "never
    /// overwrite" would leave a pre-#543 `{}` -- or any other stale content -- permanently installed,
    /// silently disabling the gate for good on any machine that ran an earlier build even once. The
    /// file is entirely AER-owned (no operator content can live here, per
    /// <see cref="BatonPaths.WorkerLaunchConfig"/>'s own doc comment), so there is nothing that
    /// overwriting could destroy. Since #667 the write is skipped when the file already holds exactly
    /// that content -- a narrower thing than "never overwrite", and one that leaves drift correction
    /// intact; see <see cref="AtomicLaunchConfigWriter"/> for why the redundant write was worth
    /// removing. The MCP config file is untouched by #543 and keeps the old once-only semantics.
    /// </remarks>
    private static (string SettingsPath, string McpConfigPath) EnsureLaunchConfigFiles()
    {
        Directory.CreateDirectory(BatonPaths.WorkerLaunchConfig);

        var settingsPath = Path.Combine(BatonPaths.WorkerLaunchConfig, "claude-settings.json");
        AtomicLaunchConfigWriter.Write(settingsPath, BuildSettingsJson());

        // The standard empty MCP config shape -- declares no servers, so this adds nothing beyond
        // what claude would otherwise discover on its own.
        var mcpConfigPath = Path.Combine(BatonPaths.WorkerLaunchConfig, "claude-mcp.json");
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
    /// <c>BATON_OUTPUT_DIR</c> exists (<see cref="Resolve"/> runs once per binding, not per execution --
    /// see <see cref="Baton.Vendors.WorkerInvocation"/>'s own doc comment for why), so nothing baked in
    /// here can vary per execution. The <c>mcp --memory-proposal-tool</c> verb+flag pair alone tells
    /// <c>Baton.Cli</c> to enable the tool; the process derives its own per-execution capture
    /// directory from <c>BATON_OUTPUT_DIR</c>, which it inherits from the <c>claude</c> process that
    /// spawns it as an MCP server -- the same inheritance <c>Baton.Cli.Program</c>'s <c>hook-check</c>
    /// branch already rests on for the identical reason.
    /// <para>
    /// #1458: <c>mcp</c> was a standalone <c>Baton.Mcp.Host.dll</c> before this file's own binary
    /// folded it in as a verb -- <c>mcp</c> must be the first argument, ahead of the tool flag, same
    /// as <see cref="BuildSettingsJson"/>'s <see cref="File.Exists"/> guard below it for the identical
    /// fail-open-and-silent reason (#530): an MCP server that never starts fails at claude's own
    /// spawn time, not loudly at dispatch.
    /// </para>
    /// </remarks>
    private static string EnsureMemoryProposalMcpConfig()
    {
        Directory.CreateDirectory(BatonPaths.WorkerLaunchConfig);
        var hostDllPath = Path.Combine(AppContext.BaseDirectory, "Baton.Cli.dll");
        if (!File.Exists(hostDllPath))
        {
            throw new InvalidOperationException(
                $"Cannot write the memory-proposal MCP config (#801): '{hostDllPath}' does not exist. " +
                "Every deployment of baton must carry Baton.Cli.dll alongside its own binary -- an MCP " +
                "config naming a path that does not exist fails open and silently (#530), so this fails " +
                "loudly here instead, before any worker is dispatched.");
        }

        var configPath = Path.Combine(BatonPaths.WorkerLaunchConfig, "claude-mcp-memory-proposal.json");
        var json = JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["baton-memory-proposal"] = new
                {
                    command = "dotnet",
                    args = new[] { hostDllPath, "mcp", "--memory-proposal-tool" },
                },
            },
        });

        AtomicLaunchConfigWriter.Write(configPath, json);
        return configPath;
    }

    /// <summary>
    /// The `--settings` content #543 ships: one `PreToolUse` hook, matching every tool
    /// (<c>"matcher": "*"</c>), spawned in exec form (`args` set) so Claude Code invokes it directly
    /// with no shell -- no quoting concerns, matching this adapter's own "direct shell-less" design
    /// (see the type's own doc comment).
    /// </summary>
    /// <remarks>
    /// <b>Invoked as <c>dotnet &lt;Baton.Cli.dll path&gt;</c>, not the native apphost.</b> An earlier
    /// version of this method named <c>Baton.Cli.exe</c>/<c>Baton.Cli</c> directly, resolved via
    /// <see cref="AppContext.BaseDirectory"/>. That works for a raw build output (confirmed for
    /// `Baton.Cli.exe` standalone; this ran from `Baton.Daemon.exe` too until #1420 narrowed the daemon
    /// to no longer spawn worker turns at all -- it has carried no path to `Baton.Cli` since) but is
    /// wrong for `baton`'s other real, exercised deployment shape: <c>Baton.Cli.csproj</c> sets
    /// <c>PackAsTool</c>, and a
    /// packed global tool's <c>DotnetToolSettings.xml</c> runs <c>Baton.Cli.dll</c> via the <c>dotnet</c>
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
        var hookAssemblyPath = Path.Combine(AppContext.BaseDirectory, "Baton.Cli.dll");
        if (!File.Exists(hookAssemblyPath))
        {
            throw new InvalidOperationException(
                $"Cannot write the mandatory PreToolUse hook (decision 0029): '{hookAssemblyPath}' " +
                "does not exist. Every deployment of baton/Baton.Daemon must carry Baton.Cli.dll alongside " +
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
    /// against a fresh <c>~/.baton</c>, both hitting this before either file exists, from the SAME
    /// daemon process, not just two separate `baton run` processes. That is a real TOCTOU: `File.Exists`
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
    /// <c>BATON_OUTPUT_DIR</c>. <c>ChannelPopulationTests</c> holds the two channels to that split
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
        names.AddRange(StandingShellDenials(grant));
        return string.Join(',', names);
    }

    /// <summary>
    /// 0022's DenyAlways families (#390) as <c>--disallowedTools</c> entries — <c>Bash(pattern)</c> per
    /// <see cref="PermissionGrant.DeniedShellCommandPatterns"/>, empty when none. This is claude's
    /// enforcement for the standing-"never" rung on BOTH dispatch paths: the CLI applies
    /// <c>--disallowedTools</c> with precedence over <c>--allowedTools</c> (measured — <c>git push</c>
    /// denied under <c>--allowedTools "Bash(git *)" --disallowedTools "Bash(git push*)"</c>,
    /// <c>docs/vendor-capabilities.md</c>) and hard-refuses BEFORE the hook, so a denied family is
    /// refused even under an unscoped grant and without re-asking. Under the runtime gate this is the
    /// <em>whole</em> of what <c>--disallowedTools</c> carries (withheld categories ride the ask band);
    /// off the gate it rides alongside them. Enforced independently of the <c>PreToolUse</c> hook, so it
    /// survives a silently-dead hook (#530) — which is why claude needs no hook-side deny check.
    /// </summary>
    private static IEnumerable<string> StandingShellDenials(PermissionGrant? grant) =>
        grant?.DeniedShellCommandPatterns is { Count: > 0 } denied
            ? denied.Select(pattern => $"Bash({pattern})")
            : [];

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
    /// can see the target path and allow only the ones landing in <c>BATON_OUTPUT_DIR</c>.
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
    /// <c>BATON_OUTPUT_DIR</c>. Verified live: a <c>WriteFiles: false</c> worker wrote its declared
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
                prompt.Append($"- {contract.RequiredInputs[i]}: {EnvironmentReference($"BATON_INPUT_{i}", isWindows)}\n");
            }
        }

        if (contract.ProducedOutputs.Count > 0)
        {
            prompt.Append("\nWrite each of the following outputs to the exact path shown, creating parent directories as needed:\n");
            foreach (var output in contract.ProducedOutputs)
            {
                var outputDir = EnvironmentReference("BATON_OUTPUT_DIR", isWindows);
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

    /// <summary>
    /// #1090: a <c>claude-*</c> id whose version is dot-delimited (<c>claude-opus-4.8</c>) is a typo for
    /// the dash form (<c>claude-opus-4-8</c>) — see <see cref="MalformedVendorModelException"/> for the
    /// measurement. Scoped to the <c>claude-</c> prefix + a digit.digit run so it cannot fire on an
    /// alias (no dot) or a valid dash id; this is NOT a model-list check — claude ships none, see
    /// <see cref="ModelAliases"/>.
    /// </summary>
    private static readonly Regex DotDelimitedClaudeVersion =
        new(@"^claude-.*\d\.\d", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void RefuseDotDelimitedClaudeModelId(string model)
    {
        if (DotDelimitedClaudeVersion.IsMatch(model))
        {
            var suggestion = Regex.Replace(model, @"(\d)\.(\d)", "$1-$2");
            throw new MalformedVendorModelException(
                "claude",
                $"'{model}' is dot-delimited; claude model ids use dashes. Did you mean '{suggestion}'?");
        }
    }

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

    /// <summary>
    /// Parses claude's <c>stream-json</c> terminal <c>"type":"result"</c> line (issue #1360) — the one
    /// line <see cref="TryParseProgressEvent"/> deliberately does not surface as progress, since it is
    /// a turn-completion summary rather than in-turn text. <c>usage.input_tokens</c>/<c>output_tokens</c>
    /// and top-level <c>num_turns</c> are read independently: a line reporting one and not the other
    /// yields exactly that field, never a fabricated zero (docs/vendor-capabilities.md's "Usage, cost
    /// and quota" section is the register this reads against). <c>total_cost_usd</c> and the
    /// cache-token breakdown are real on this vendor but outside #1360's additive
    /// <c>{wallClockMs, tokensIn, tokensOut, turns}</c> shape, so they are read by nothing here.
    /// <para>
    /// <b>Scope, measured (docs/vendor-doc-audit.md, #479): this is a top-level figure, not a
    /// whole-tree one.</b> <c>usage.output_tokens</c> excludes tokens spent by any subagent the
    /// dispatched worker itself fans out to — confirmed at a 22% shortfall against the same result's
    /// <c>modelUsage</c> object on a single subagent, growing with the tree. AER caps a worker's own
    /// subagent fan-out at depth 1 (<see cref="MaxSubagentSpawnDepthVariable"/>) rather than zero, so
    /// this undercount is a real, reachable case here, not a hypothetical. <c>modelUsage</c> is left
    /// unread: summing it correctly needs a per-model breakdown this shape's single
    /// <c>tokensIn</c>/<c>tokensOut</c> scalars cannot carry without inventing a field #1360 never
    /// asked for.
    /// </para>
    /// </summary>
    public bool TryParseFinalUsage(string rawLine, out WorkerUsage? usage)
    {
        usage = null;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProp)
                || typeProp.GetString() != "result")
            {
                return false;
            }

            long? tokensIn = null;
            long? tokensOut = null;
            if (root.TryGetProperty("usage", out var usageProp) && usageProp.ValueKind == JsonValueKind.Object)
            {
                if (usageProp.TryGetProperty("input_tokens", out var inProp) && inProp.TryGetInt64(out var inTokens))
                {
                    tokensIn = inTokens;
                }

                if (usageProp.TryGetProperty("output_tokens", out var outProp) && outProp.TryGetInt64(out var outTokens))
                {
                    tokensOut = outTokens;
                }
            }

            int? turns = root.TryGetProperty("num_turns", out var turnsProp) && turnsProp.TryGetInt32(out var turnsValue)
                ? turnsValue
                : null;

            if (tokensIn is null && tokensOut is null && turns is null)
            {
                return false;
            }

            usage = new WorkerUsage(tokensIn, tokensOut, turns);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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

    /// <summary>
    /// Interprets Claude-specific failure output into a <see cref="FailureClassification"/> and reset instant (issue #1115).
    /// </summary>
    public bool TryClassifyFailure(
        string? stderrTail,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore)
    {
        return TryClassifyFailure(stderrTail, null, timeProvider, out classification, out retryNotBefore);
    }

    /// <summary>
    /// Interprets Claude-specific failure output from stderr and stdout tails into a <see cref="FailureClassification"/> and reset instant (issue #1115).
    /// </summary>
    public bool TryClassifyFailure(
        string? stderrTail,
        string? stdoutTail,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore)
    {
        if (TryClassifyQuotaExhaustion(stderrTail, timeProvider, out classification, out retryNotBefore))
        {
            return true;
        }

        return TryClassifyQuotaExhaustion(stdoutTail, timeProvider, out classification, out retryNotBefore);
    }


    /// <summary>
    /// Recognizes Claude subscription quota exhaustion errors from the typed field <c>errorCode == "credits_required"</c>
    /// (decision 0026 §1a, issue #1115).
    /// </summary>
    public static bool TryClassifyQuotaExhaustion(
        string? stderrOrReason,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        classification = null;
        retryNotBefore = null;

        if (string.IsNullOrWhiteSpace(stderrOrReason))
        {
            return false;
        }

        if (!ContainsTypedCreditsRequiredError(stderrOrReason))
        {
            return false;
        }

        classification = FailureClassification.ExhaustedUntil;
        retryNotBefore = null;
        return true;
    }

    private static bool ContainsTypedCreditsRequiredError(string input)
    {
        if (TryCheckElementForCreditsRequired(input))
        {
            return true;
        }

        var lines = input.Split('\n');
        if (lines.Length > 1)
        {
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && TryCheckElementForCreditsRequired(trimmed))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryCheckElementForCreditsRequired(string jsonCandidate)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonCandidate);
            return HasTypedCreditsRequiredCode(doc.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasTypedCreditsRequiredCode(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty("errorCode", out var errorCodeProp) &&
            errorCodeProp.ValueKind == JsonValueKind.String &&
            errorCodeProp.GetString() == "credits_required")
        {
            return true;
        }

        if (element.TryGetProperty("error_code", out var errorCodeProp2) &&
            errorCodeProp2.ValueKind == JsonValueKind.String &&
            errorCodeProp2.GetString() == "credits_required")
        {
            return true;
        }

        if (element.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.Object)
        {
            if (errorProp.TryGetProperty("code", out var codeProp) &&
                codeProp.ValueKind == JsonValueKind.String &&
                codeProp.GetString() == "credits_required")
            {
                return true;
            }

            if (errorProp.TryGetProperty("errorCode", out var codeProp2) &&
                codeProp2.ValueKind == JsonValueKind.String &&
                codeProp2.GetString() == "credits_required")
            {
                return true;
            }
        }

        return false;
    }
}
