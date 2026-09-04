namespace Baton.Vendors;

/// <summary>
/// Creates the persistent, Baton-owned Codex state root used by the app-server broker. It contains
/// no operator config, instructions, skills, plugins, MCP declarations, or copied credentials.
/// Authentication in this root is established only by an operator running Codex's own login command
/// with <c>CODEX_HOME</c> pointed here; Baton never reads or transfers the credential.
/// </summary>
public static class CodexIsolatedHome
{
    public const string DirectoryName = "codex-home";

    public static string Prepare(string batonRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batonRoot);

        var target = Path.GetFullPath(Path.Combine(batonRoot, DirectoryName));
        Directory.CreateDirectory(target);
        return target;
    }
}
