namespace Aer.Adapters;

/// <summary>One vendor CLI's read-only presence: the adapter registry name, the binary it shells out to, and whether that binary is on PATH right now.</summary>
public sealed record VendorCliStatus(string AdapterName, string BinaryName, bool IsAvailable);

/// <summary>
/// The vendor-readiness probe (M19 Phase 4, issue #189; the non-expert audit's named finding): a
/// read-only PATH presence check for the binaries the vendor adapters shell out to — never
/// credential handling, never invocation (Adapter Isolation is untouched; this only answers
/// "would a dispatch find the CLI at all"). Lives beside the adapters because they are the layer
/// that owns each binary's name.
/// </summary>
public static class VendorCliPresence
{
    /// <summary>The vendor CLIs the default registry's adapters shell out to.</summary>
    private static readonly IReadOnlyList<(string AdapterName, string BinaryName)> KnownBinaries =
    [
        ("claude", "claude"),
        ("agy", "agy"),
    ];

    public static IReadOnlyList<VendorCliStatus> Probe(Func<string, bool>? isOnPath = null)
    {
        var finder = isOnPath ?? IsOnSystemPath;
        return [.. KnownBinaries.Select(entry => new VendorCliStatus(entry.AdapterName, entry.BinaryName, finder(entry.BinaryName)))];
    }

    private static bool IsOnSystemPath(string binaryName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory, binaryName + extension.ToLowerInvariant())))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH segment (stray quotes, invalid chars) is that segment's
                    // problem, not the probe's — skip it, keep looking.
                }
            }
        }

        return false;
    }
}
