namespace Baton.Vendors;

/// <summary>
/// Shared scanner for <c>SKILL.md</c> frontmatter across worker adapters (#1512). Extracts skill
/// names and descriptions from directory layouts (<c>&lt;skillsDir&gt;/&lt;name&gt;/SKILL.md</c>) without a
/// general YAML dependency, per issue #1151's canonical skill packages proposal. Handles missing or
/// unreadable files explicitly with fallback descriptions rather than bare catches.
/// </summary>
public static class SkillScanner
{
    public const string SkillFileName = "SKILL.md";

    /// <summary>
    /// Parses the description line from <c>SKILL.md</c> frontmatter content, trimming whitespace
    /// and surrounding quotes.
    /// </summary>
    public static string? ParseDescriptionFromFrontmatter(string? markdownContent)
    {
        if (string.IsNullOrWhiteSpace(markdownContent))
        {
            return null;
        }

        var lines = markdownContent.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                var desc = trimmed["description:".Length..].Trim().Trim('"', '\'');
                if (!string.IsNullOrEmpty(desc))
                {
                    return desc;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the description from a <c>SKILL.md</c> file, or returns a fallback description
    /// (<c>"Skill in {fallbackName}"</c>) if the file does not exist, has no description, or cannot be read.
    /// Explicitly catches I/O and permission exceptions rather than swallowing all exceptions bare.
    /// </summary>
    public static string ReadDescription(string skillFilePath, string fallbackName)
    {
        var defaultDesc = $"Skill in {fallbackName}";
        if (string.IsNullOrWhiteSpace(skillFilePath) || !File.Exists(skillFilePath))
        {
            return defaultDesc;
        }

        try
        {
            var text = File.ReadAllText(skillFilePath);
            return ParseDescriptionFromFrontmatter(text) ?? defaultDesc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return defaultDesc;
        }
    }

    /// <summary>
    /// Discovers skills in the given directory by enumerating subdirectories containing <c>SKILL.md</c>.
    /// Returns an empty list if the directory does not exist or cannot be enumerated.
    /// </summary>
    public static IReadOnlyList<WorkerCapabilityItem> DiscoverSkills(string? skillsDirectory)
    {
        if (string.IsNullOrWhiteSpace(skillsDirectory) || !Directory.Exists(skillsDirectory))
        {
            return Array.Empty<WorkerCapabilityItem>();
        }

        var items = new List<WorkerCapabilityItem>();
        try
        {
            foreach (var skillSubDir in Directory.GetDirectories(skillsDirectory))
            {
                var skillFile = Path.Combine(skillSubDir, SkillFileName);
                if (File.Exists(skillFile))
                {
                    var name = Path.GetFileName(skillSubDir);
                    var desc = ReadDescription(skillFile, name);
                    items.Add(new WorkerCapabilityItem(name, "skill", desc));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // #1512 M1: directory unreadable or deleted concurrently -- fail open (a stray permission
            // error must not block dispatch), but not silently: an empty result here is otherwise
            // indistinguishable from a directory that legitimately has no skills, on a feature whose
            // whole point is telling the operator what the worker will have.
            Console.Error.WriteLine($"Warning: could not read skills directory '{skillsDirectory}': {ex.Message}");
        }

        return items;
    }
}
