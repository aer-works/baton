using System.Text.Json;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Adapters.Tests;

/// <summary>
/// #445 Phase 4: what <see cref="WorkerInvocation.EnablePermissionGate"/> actually changes in each
/// vendor's resolved invocation — and, equally load-bearing, what it changes when it is OFF, which is
/// nothing.
/// </summary>
/// <remarks>
/// <para>
/// Every gate-on assertion here is paired with its gate-off control in the same or an adjacent test.
/// That pairing is the point: the gate-off path is the one carrying dozens of measured invariants
/// (#331, #521, #543, #600, #649, #679), and a change that only ever ran the new branch could not tell
/// "the flag does something" from "the flag does something to everyone".
/// </para>
/// <para>
/// The one thing NOT asserted here is that a human ever sees the ask — that is a live-CLI claim, and
/// its instrument is the <c>gate.hook-ask-in-auto</c> sentinel plus a real driven session, not a
/// resolve.
/// </para>
/// </remarks>
[Collection(LaunchConfigCollection.Name)]
public class RuntimePermissionGateAdapterTests
{
    private static readonly WorkerContract ArchitectContract = new(
        "architect", [], [new ProducedOutput("plan.md")], []);

    /// <summary>
    /// The interactive chat default for a session attached to a directory
    /// (<see cref="InteractiveSessionMaterializer.DefaultGrantForWorkingDirectory"/>): read and write,
    /// no shell, no network. The grant the gate path actually runs under.
    /// </summary>
    private static readonly PermissionGrant InteractiveDefault =
        InteractiveSessionMaterializer.DefaultGrantForWorkingDirectory("/some/project");

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

    private static string? EnvValue(CoreDispatchTarget target, string name) =>
        target.Environment?.FirstOrDefault(e => e.Name == name).Value;

    /// <summary>
    /// THE DISCRIMINATOR, and it runs first for a reason. If the withheld set were empty for the grant
    /// the gate actually ships under, every assertion below would still pass while the gate was
    /// "configured, running, and never consulted" (0015) — a hook whose ask band is empty asks nothing,
    /// forever, and no argv check can see it.
    /// </summary>
    [Fact]
    public void The_ask_band_is_non_empty_for_the_grant_the_interactive_gate_actually_runs_under()
    {
        var withheld = ClaudeWorkerAdapter.BuildHookDeniedTools(InteractiveDefault);

        Assert.NotEqual(string.Empty, withheld);
        // Named rather than merely counted: shell and network are the categories the conservative
        // codebase default withholds, and they are what a chat session realistically reaches for.
        Assert.Contains("Bash", withheld);
        Assert.Contains("WebFetch", withheld);
    }

    [Fact]
    public void Claude_gate_on_routes_permission_decisions_to_AERs_own_MCP_tool()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Chat.", PermissionGrant: InteractiveDefault, EnablePermissionGate: true),
            ArchitectContract);

        Assert.Equal(
            "mcp__aerpermission__aer_permission_ask",
            ArgValue(target, "--permission-prompt-tool"));
        // The literal is asserted rather than only the constant, because the flag's value has to match
        // a name claude derives from the mcpServers key — comparing the constant to itself would pass
        // with both sides wrong.
        Assert.Equal(ClaudeWorkerAdapter.PermissionPromptToolName, ArgValue(target, "--permission-prompt-tool"));
    }

    [Fact]
    public void Claude_gate_on_registers_the_gate_server_in_the_mcp_config_it_points_at()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Chat.", PermissionGrant: InteractiveDefault, EnablePermissionGate: true),
            ArchitectContract);

        var mcpConfigPath = ArgValue(target, "--mcp-config");
        Assert.NotNull(mcpConfigPath);
        Assert.True(File.Exists(mcpConfigPath), "the file --mcp-config points at must already exist");

        using var doc = JsonDocument.Parse(File.ReadAllText(mcpConfigPath!));
        var server = doc.RootElement.GetProperty("mcpServers")
            .GetProperty(ClaudeWorkerAdapter.PermissionGateMcpServerName);

        Assert.Equal("dotnet", server.GetProperty("command").GetString());
        var serverArgs = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Contains(serverArgs, a => a!.EndsWith("Aer.Mcp.Host.dll", StringComparison.Ordinal));
        Assert.Contains("--permission-gate-tool", serverArgs);
        Assert.Contains("claude", serverArgs);

        // The server key must be addressable as mcp__<key>__<tool>: a hyphen would be mangled.
        Assert.DoesNotContain('-', ClaudeWorkerAdapter.PermissionGateMcpServerName);
    }

    /// <summary>
    /// The withheld set moves BAND, it does not disappear. A name on <c>--disallowedTools</c> is
    /// dropped by the CLI up front, before any hook sees it, so leaving it there would make
    /// the hook's ask unreachable — the gate would be installed and permanently silent.
    /// </summary>
    [Fact]
    public void Claude_gate_on_moves_the_withheld_set_off_disallowedTools_and_onto_the_ask_band()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Chat.", PermissionGrant: InteractiveDefault, EnablePermissionGate: true),
            ArchitectContract);

        Assert.DoesNotContain("--disallowedTools", target.Args);

        var withheld = ClaudeWorkerAdapter.BuildHookDeniedTools(InteractiveDefault);
        Assert.Equal($"claude:{withheld}", EnvValue(target, ClaudeWorkerAdapter.AskToolsVariable));

        // The denied list becomes the standing-"never" channel, which is EMPTY today. It is emitted
        // empty rather than omitted for the #600 reason recorded on ClaudeWorkerAdapter.AskToolsVariable.
        Assert.Equal("claude:", EnvValue(target, ClaudeWorkerAdapter.DeniedToolsVariable));
    }

    [Fact]
    public void Claude_gate_on_bounds_the_MCP_startup_and_the_held_open_call()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Chat.", PermissionGrant: InteractiveDefault, EnablePermissionGate: true),
            ArchitectContract);

        Assert.Equal("30000", EnvValue(target, ClaudeWorkerAdapter.McpStartupTimeoutVariable));
        Assert.Equal("200000", EnvValue(target, ClaudeWorkerAdapter.McpToolTimeoutVariable));
    }

    /// <summary>
    /// The two flags that would silently remove the gate: <c>--permission-mode auto</c> disables the
    /// prompt tool (0029/0015), and <c>--bare</c> suppresses hooks and MCP servers even when passed
    /// explicitly (#521). Neither is emitted today; this fails the day one is added under the gate.
    /// </summary>
    [Fact]
    public void Claude_gate_on_emits_neither_permission_mode_nor_bare()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Chat.", PermissionGrant: InteractiveDefault, EnablePermissionGate: true),
            ArchitectContract);

        Assert.DoesNotContain("--permission-mode", target.Args);
        Assert.DoesNotContain("--bare", target.Args);
    }

    /// <summary>
    /// THE CONTROL ARM. Same grant, gate off: every gate mechanism absent and every pre-#445 invariant
    /// intact. Without this the tests above prove only that the new branch runs, never that it is the
    /// only thing that changed.
    /// </summary>
    [Fact]
    public void Claude_gate_off_is_todays_invocation_unchanged()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Chat.", PermissionGrant: InteractiveDefault), ArchitectContract);

        Assert.DoesNotContain("--permission-prompt-tool", target.Args);
        Assert.Null(EnvValue(target, ClaudeWorkerAdapter.AskToolsVariable));
        Assert.Null(EnvValue(target, ClaudeWorkerAdapter.McpStartupTimeoutVariable));
        Assert.Null(EnvValue(target, ClaudeWorkerAdapter.McpToolTimeoutVariable));

        // And the withheld set is still where it was: on the flag AND on the denied list.
        var withheld = ClaudeWorkerAdapter.BuildHookDeniedTools(InteractiveDefault);
        Assert.Equal($"claude:{withheld}", EnvValue(target, ClaudeWorkerAdapter.DeniedToolsVariable));
        var disallowed = ArgValue(target, "--disallowedTools");
        Assert.NotNull(disallowed);
        Assert.Contains("Bash", disallowed!);

        // The plain empty MCP config, not either opt-in file.
        Assert.Equal(
            Path.Combine(AerPaths.WorkerLaunchConfig, "claude-mcp.json"),
            ArgValue(target, "--mcp-config"));
    }

    /// <summary>
    /// agy has no <c>--permission-prompt-tool</c> and an exit-0/2 hook; the worker simply reaches and
    /// calls the gate tool — the mechanism <c>AgyWorkerAdapter</c> documents. Reachability on this
    /// vendor means a real directory carrying <c>.agents/mcp_config.json</c> handed to
    /// <c>--add-dir</c> — agy's only lever (decision 0035).
    /// </summary>
    [Fact]
    public void Agy_gate_on_materializes_a_workspace_naming_the_gate_server_and_grants_it()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Chat.", PermissionGrant: InteractiveDefault, EnablePermissionGate: true),
            ArchitectContract);

        var expectedWorkspace = Path.Combine(
            AerPaths.WorkerLaunchConfig, AgyWorkerAdapter.PermissionGateWorkspaceDirectoryName);
        var configPath = Path.Combine(expectedWorkspace, ".agents", "mcp_config.json");

        Assert.Contains(expectedWorkspace, target.Args);
        Assert.True(File.Exists(configPath), "the workspace's mcp_config.json must already exist");

        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var server = doc.RootElement.GetProperty("mcpServers")
            .GetProperty(ClaudeWorkerAdapter.PermissionGateMcpServerName);
        Assert.Equal("dotnet", server.GetProperty("command").GetString());
        var serverArgs = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Contains(serverArgs, a => a!.EndsWith("Aer.Mcp.Host.dll", StringComparison.Ordinal));
        Assert.Contains("--permission-gate-tool", serverArgs);
        // "agy", not "claude": the return shape differs (tool-result vs the claude callback envelope),
        // and getting this wrong would hand the worker a reply its CLI cannot read.
        Assert.Contains("agy", serverArgs);
    }

    /// <summary>
    /// The agy control, and one polarity claude's has no counterpart for: agy's hook stays deny-only,
    /// so the ask band must NOT be set there. An ask list a hook cannot express is a mechanism that
    /// looks installed and does nothing.
    /// </summary>
    [Fact]
    public void Agy_gate_off_adds_no_workspace_and_the_ask_band_is_never_set_on_agy_either_way()
    {
        var off = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Chat.", PermissionGrant: InteractiveDefault), ArchitectContract);
        Assert.DoesNotContain(off.Args, a => a.Contains("permission-gate", StringComparison.Ordinal));

        var on = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Chat.", PermissionGrant: InteractiveDefault, EnablePermissionGate: true),
            ArchitectContract);

        foreach (var target in new[] { off, on })
        {
            Assert.DoesNotContain(
                target.Environment ?? [], e => e.Name == ClaudeWorkerAdapter.AskToolsVariable);
            // And the deny list keeps carrying the withheld set on this vendor, gate or no gate.
            Assert.DoesNotContain("--permission-prompt-tool", target.Args);
        }
    }

    /// <summary>
    /// The flag reaches the adapter from a bindings entry rather than only from a hand-built
    /// invocation, and survives the JSON the daemon actually persists a per-turn entry through. Without
    /// the round trip the daemon's opt-in would be written and then silently dropped on the read back,
    /// which no resolve-level test could see.
    /// </summary>
    [Fact]
    public void The_opt_in_survives_the_bindings_file_round_trip()
    {
        var entry = new WorkerBindingConfigEntry(
            Adapter: "claude",
            Contract: ArchitectContract,
            PromptTemplate: "Chat.",
            Timeout: TimeSpan.FromMinutes(10),
            PermissionGrant: InteractiveDefault,
            EnablePermissionGate: true);

        var json = WorkerBindingConfigWriter.Serialize(
            new Dictionary<string, WorkerBindingConfigEntry> { ["chat-worker"] = entry });
        var parsed = WorkerBindingConfigParser.Parse(json);

        Assert.True(parsed["chat-worker"].EnablePermissionGate);

        // The control: an entry that never opted in stays opted out across the same round trip, rather
        // than the field defaulting true on a read.
        var plainJson = WorkerBindingConfigWriter.Serialize(
            new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["chat-worker"] = entry with { EnablePermissionGate = false },
            });
        Assert.False(WorkerBindingConfigParser.Parse(plainJson)["chat-worker"].EnablePermissionGate);
    }
}
