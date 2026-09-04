using Baton.Artifacts;
using Baton.Status;

namespace Baton.Cli.Mcp;

/// <summary>
/// The <c>baton mcp</c> verb (#1458: folded from the standalone Baton.Mcp.Host executable) — composes
/// the requested <see cref="IMcpTool"/> set onto <see cref="McpServerHost"/> and runs the stdio MCP
/// protocol over <see cref="Console.In"/>/<see cref="Console.Out"/>. Same argument surface and stdio
/// protocol as the old Baton.Mcp.Host.exe: a vendor CLI or fleet-glass's pusher.py spawning the old
/// binary directly now spawns the packed `baton` tool with `mcp` as its first argument instead.
/// </summary>
public static class McpCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var captureFilePath = ParseArgValue(args, "--capture-file");
        // #833: no literal path arrives on the command line -- see Baton.Vendors.ClaudeWorkerAdapter's
        // EnsureMemoryProposalMcpConfig for why (canonical: the resolve-once-per-binding seam and the
        // env-inheritance mechanism this flag rests on). This flag only says whether to enable the tool.
        var enableMemoryProposalTool = args.Contains("--memory-proposal-tool");
        var enableFleetStatusTool = args.Contains("--fleet-status-tool");
        var enableRoomDetailTool = args.Contains("--room-detail-tool");

        List<IMcpTool> tools = [];
        if (captureFilePath is not null)
        {
            tools.Add(new YieldTool(captureFilePath));
        }

        if (enableFleetStatusTool)
        {
            tools.Add(new FleetStatusTool());
        }

        if (enableRoomDetailTool)
        {
            tools.Add(new RoomDetailTool());
        }

        if (enableMemoryProposalTool)
        {
            var outputDirectory = BatonEnvironmentSnapshot.Current.McpOutputDirectory;
            if (string.IsNullOrEmpty(outputDirectory))
            {
                Console.Error.WriteLine(
                    "--memory-proposal-tool requires BATON_OUTPUT_DIR in this process's environment (set per " +
                    "execution and inherited from the spawning vendor CLI); none was found.");
                return 1;
            }

            tools.Add(new MemoryProposalTool(Path.Combine(outputDirectory, MemoryProposalTool.CaptureDirectoryName)));

            // #595: promote-artifact rides the same opt-in as memory-edit-proposal rather than a
            // second flag -- both are worker-side escalation tools composed onto this same host, and
            // neither needs a flag of its own the other doesn't already require. BATON_OUTPUT_DIR is
            // always `{roomDir}/artifacts/execution_{id}` (ArtifactManager.AllocateOutputDirectory) --
            // structural, not a worker's claim, the same reasoning MemoryProposalEscalation's own
            // remarks give for trusting an execution_* directory name -- so the room directory and the
            // execution id are both derived from it rather than read from a second env var.
            var executionDirectory = Path.GetFullPath(outputDirectory);
            var artifactsRoot = Path.GetDirectoryName(executionDirectory);
            var roomDirectoryPath = artifactsRoot is null ? null : Path.GetDirectoryName(artifactsRoot);
            if (roomDirectoryPath is null)
            {
                Console.Error.WriteLine(
                    $"--memory-proposal-tool requires BATON_OUTPUT_DIR to resolve to a room's " +
                    $"artifacts/execution_<id> directory; got '{outputDirectory}'.");
                return 1;
            }

            var executionDirectoryName = Path.GetFileName(executionDirectory);
            var executionId = executionDirectoryName.StartsWith("execution_", StringComparison.Ordinal)
                ? executionDirectoryName["execution_".Length..]
                : null;
            var attribution = new ArtifactAttribution(ExecutionId: executionId, Role: null, Adapter: null, Model: null);

            tools.Add(new PromoteArtifactTool(roomDirectoryPath, attribution));
        }

        if (tools.Count == 0)
        {
            Console.Error.WriteLine("Usage: baton mcp [--capture-file <path>] [--memory-proposal-tool] [--fleet-status-tool] [--room-detail-tool]");
            return 1;
        }

        var host = new McpServerHost("baton-mcp-host", "1.0.0", tools);
        await host.RunAsync(Console.In, Console.Out).ConfigureAwait(false);
        return 0;
    }

    private static string? ParseArgValue(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
