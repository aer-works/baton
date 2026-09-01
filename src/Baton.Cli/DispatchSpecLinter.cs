using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// The permission category an instruction implies.
/// </summary>
public enum GrantCategory
{
    Shell,
    Network,
}

/// <summary>
/// A named heuristic matching shell- or network-implying instructions in task specs.
/// </summary>
/// <param name="Name">Identifier for the heuristic.</param>
/// <param name="RequiredCategory">The permission category required by instructions matching this heuristic.</param>
/// <param name="Matches">Predicate matching a line of text.</param>
/// <param name="Description">Human-readable description of what this heuristic matches.</param>
public sealed record SpecGrantHeuristic(
    string Name,
    GrantCategory RequiredCategory,
    Func<string, bool> Matches,
    string Description);

/// <summary>
/// A warning produced when a task spec contains instructions requiring a permission category not granted to the role.
/// </summary>
public sealed record SpecLintWarning(
    int LineNumber,
    string LineContent,
    GrantCategory MissingCategory,
    string HeuristicName,
    string RoleId)
{
    public string Format()
    {
        var categoryName = MissingCategory switch
        {
            GrantCategory.Shell => "shell",
            GrantCategory.Network => "network",
            _ => MissingCategory.ToString().ToLowerInvariant(),
        };

        return $"Warning: Spec line {LineNumber} ('{LineContent}') implies {categoryName} instructions ({HeuristicName}), but role '{RoleId}' has no-{categoryName} grant.";
    }
}

/// <summary>
/// Heuristically scans task specs at dispatch time for shell/network-implying instructions
/// (<c>gh </c>, <c>git </c>, <c>dotnet </c>, <c>pixi </c>, <c>curl</c>, "run the", "execute", URLs)
/// and compares them against the resolved role grant (issue #1500). WARN, never fail.
/// </summary>
public static class DispatchSpecLinter
{
    public static readonly IReadOnlyList<SpecGrantHeuristic> Heuristics =
    [
        new SpecGrantHeuristic("gh", GrantCategory.Shell, line => MatchesCommand(line, "gh"), "GitHub CLI invocation"),
        new SpecGrantHeuristic("gh", GrantCategory.Network, line => MatchesCommand(line, "gh"), "GitHub CLI network access"),
        new SpecGrantHeuristic("git", GrantCategory.Shell, line => MatchesCommand(line, "git"), "Git CLI invocation"),
        new SpecGrantHeuristic("dotnet", GrantCategory.Shell, line => MatchesCommand(line, "dotnet"), ".NET CLI invocation"),
        new SpecGrantHeuristic("pixi", GrantCategory.Shell, line => MatchesCommand(line, "pixi"), "Pixi task runner invocation"),
        new SpecGrantHeuristic("curl", GrantCategory.Shell, line => MatchesCommand(line, "curl"), "Curl tool invocation"),
        new SpecGrantHeuristic("curl", GrantCategory.Network, line => MatchesCommand(line, "curl"), "Curl network request"),
        new SpecGrantHeuristic("run the", GrantCategory.Shell, line => line.Contains("run the", StringComparison.OrdinalIgnoreCase), "'run the' execution phrase"),
        new SpecGrantHeuristic("execute", GrantCategory.Shell, line => line.Contains("execute", StringComparison.OrdinalIgnoreCase), "'execute' action phrase"),
        new SpecGrantHeuristic("url", GrantCategory.Network, line => line.Contains("http://", StringComparison.OrdinalIgnoreCase) || line.Contains("https://", StringComparison.OrdinalIgnoreCase), "HTTP/HTTPS URL"),
    ];

    /// <summary>
    /// Checks a command token with word boundaries, prompt prefixes, or backticks/quotes.
    /// </summary>
    public static bool MatchesCommand(string line, string command)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.TrimStart(' ', '\t', '`', '$', '>', '#');
        if (trimmed.StartsWith(command + " ", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(command, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (line.Contains($"`{command} ", StringComparison.OrdinalIgnoreCase)
            || line.Contains($"`{command}`", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (line.Contains($"\"{command} ", StringComparison.OrdinalIgnoreCase)
            || line.Contains($"'{command} ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (line.Contains($" {command} ", StringComparison.OrdinalIgnoreCase)
            || line.Contains($"({command} ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Scans <paramref name="spec"/> line-by-line and returns warnings for any line requiring capabilities withheld from <paramref name="grant"/>.
    /// </summary>
    public static IReadOnlyList<SpecLintWarning> Lint(string spec, PermissionGrant? grant, string roleId)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return [];
        }

        var hasShell = grant is { RunShellCommands: true };

        // A role like `review` declares NetworkAccess: false but scopes RunShellCommands to a
        // read-only, patterned allowlist that includes network-reaching commands (`gh issue view*`),
        // asserted via ShellCommandsAreReadOnly — the same exemption PermissionGrant.
        // CategoriesDefeatedByTheShell already encodes for exactly this shape. Checking NetworkAccess
        // alone would warn "no-network grant" on a line the role can actually execute (a cry-wolf
        // false positive on review, the catalog's own default role for src/ changes) — see #1500's
        // second-reader finding.
        var readOnlyPatternedShell = grant is
        {
            RunShellCommands: true,
            ShellCommandsAreReadOnly: true,
            ShellCommandPatterns.Count: > 0,
        };
        var hasNetwork = grant is { NetworkAccess: true } || readOnlyPatternedShell;

        var lines = spec.Split('\n');
        var warnings = new List<SpecLintWarning>();

        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var lineNumber = i + 1;
            var missingOnLine = new HashSet<GrantCategory>();

            foreach (var heuristic in Heuristics)
            {
                if (heuristic.Matches(rawLine))
                {
                    var isMissing = heuristic.RequiredCategory switch
                    {
                        GrantCategory.Shell => !hasShell,
                        GrantCategory.Network => !hasNetwork,
                        _ => false,
                    };

                    if (isMissing && missingOnLine.Add(heuristic.RequiredCategory))
                    {
                        warnings.Add(new SpecLintWarning(lineNumber, trimmed, heuristic.RequiredCategory, heuristic.Name, roleId));
                    }
                }
            }
        }

        return warnings;
    }
}
