using System.Runtime.InteropServices;

namespace Baton.Vendors;

/// <summary>
/// Resolves the native Codex binary without executing a PowerShell or cmd npm shim. On Windows an
/// npm install places those shims on PATH and keeps the real platform binary below
/// <c>node_modules/@openai</c>; a desktop install may instead place <c>codex.exe</c> directly on PATH.
/// Other platforms can execute the package's shebang launcher directly.
/// </summary>
public static class CodexExecutableResolver
{
    public static string Resolve() => Resolve(
        Environment.GetEnvironmentVariable("PATH"),
        RuntimeInformation.ProcessArchitecture,
        OperatingSystem.IsWindows());

    internal static string Resolve(string? searchPath, Architecture architecture, bool isWindows)
    {
        if (!isWindows || string.IsNullOrWhiteSpace(searchPath))
        {
            return "codex";
        }

        var (package, target) = architecture switch
        {
            Architecture.Arm64 => ("codex-win32-arm64", "aarch64-pc-windows-msvc"),
            _ => ("codex-win32-x64", "x86_64-pc-windows-msvc"),
        };

        foreach (var rawDirectory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            var direct = Path.Combine(directory, "codex.exe");
            if (File.Exists(direct))
            {
                return direct;
            }

            if (!File.Exists(Path.Combine(directory, "codex.cmd"))
                && !File.Exists(Path.Combine(directory, "codex.ps1")))
            {
                continue;
            }

            var packageTail = Path.Combine("vendor", target, "bin", "codex.exe");
            string[] packageCandidates =
            [
                Path.Combine(directory, "node_modules", "@openai", "codex", "node_modules", "@openai", package, packageTail),
                Path.Combine(directory, "node_modules", "@openai", package, packageTail),
            ];
            var packaged = packageCandidates.FirstOrDefault(File.Exists);
            if (packaged is not null)
            {
                return packaged;
            }
        }

        // Let the managed process boundary produce the ordinary, actionable program-not-found error when neither
        // supported native installation shape is present.
        return "codex";
    }
}
