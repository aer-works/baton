using Aer.Mcp;
using Aer.Mcp.Host;

var captureFilePath = ParseArgValue(args, "--capture-file");
// #833: no literal path arrives on the command line -- see Aer.Adapters.ClaudeWorkerAdapter's
// EnsureMemoryProposalMcpConfig for why (canonical: the resolve-once-per-binding seam and the
// env-inheritance mechanism this flag rests on). This flag only says whether to enable the tool.
var enableMemoryProposalTool = args.Contains("--memory-proposal-tool");
var permissionGateShapeRaw = ParseArgValue(args, "--permission-gate-tool");

PermissionReturnShape? permissionReturnShape = permissionGateShapeRaw?.ToLowerInvariant() switch
{
    "claude" => PermissionReturnShape.ClaudeCallback,
    "agy" => PermissionReturnShape.AgyElected,
    null => null,
    _ => null,
};

if (permissionGateShapeRaw is not null && permissionReturnShape is null)
{
    Console.Error.WriteLine($"Invalid value '{permissionGateShapeRaw}' for --permission-gate-tool. Expected 'claude' or 'agy'.");
    return 1;
}

List<IMcpTool> tools = [];
if (captureFilePath is not null)
{
    tools.Add(new YieldTool(captureFilePath));
}

if (enableMemoryProposalTool || permissionReturnShape is not null)
{
    var outputDirectory = Environment.GetEnvironmentVariable("AER_OUTPUT_DIR");
    if (string.IsNullOrEmpty(outputDirectory))
    {
        var flagName = permissionReturnShape is not null ? "--permission-gate-tool" : "--memory-proposal-tool";
        Console.Error.WriteLine(
            $"{flagName} requires AER_OUTPUT_DIR in this process's environment (set per " +
            "execution and inherited from the spawning vendor CLI); none was found.");
        return 1;
    }

    if (enableMemoryProposalTool)
    {
        tools.Add(new MemoryProposalTool(Path.Combine(outputDirectory, MemoryProposalTool.CaptureDirectoryName)));
    }

    if (permissionReturnShape is { } shape)
    {
        tools.Add(new PermissionGateTool(outputDirectory, shape));
    }
}

if (tools.Count == 0)
{
    Console.Error.WriteLine("Usage: Aer.Mcp.Host [--capture-file <path>] [--memory-proposal-tool] [--permission-gate-tool <claude|agy>]");
    return 1;
}

var host = new McpServerHost("aer-mcp-host", "1.0.0", tools);
await host.RunAsync(Console.In, Console.Out).ConfigureAwait(false);
return 0;

static string? ParseArgValue(string[] args, string flag)
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
