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
            var outputDirectory = Environment.GetEnvironmentVariable("BATON_OUTPUT_DIR");
            if (string.IsNullOrEmpty(outputDirectory))
            {
                Console.Error.WriteLine(
                    "--memory-proposal-tool requires BATON_OUTPUT_DIR in this process's environment (set per " +
                    "execution and inherited from the spawning vendor CLI); none was found.");
                return 1;
            }

            tools.Add(new MemoryProposalTool(Path.Combine(outputDirectory, MemoryProposalTool.CaptureDirectoryName)));
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
