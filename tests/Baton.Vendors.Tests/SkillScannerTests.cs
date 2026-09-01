using Baton.Vendors;

namespace Baton.Vendors.Tests;

public sealed class SkillScannerTests
{
    [Fact]
    public void ParseDescriptionFromFrontmatter_ExtractsUnquotedDescription()
    {
        var content = """
            ---
            name: test-skill
            description: A helpful skill for testing
            ---
            # Body
            """;

        var desc = SkillScanner.ParseDescriptionFromFrontmatter(content);

        Assert.Equal("A helpful skill for testing", desc);
    }

    [Fact]
    public void ParseDescriptionFromFrontmatter_ExtractsDoubleQuotedDescription()
    {
        var content = """
            ---
            description: "A double quoted description"
            ---
            """;

        var desc = SkillScanner.ParseDescriptionFromFrontmatter(content);

        Assert.Equal("A double quoted description", desc);
    }

    [Fact]
    public void ParseDescriptionFromFrontmatter_ExtractsSingleQuotedDescription()
    {
        var content = """
            ---
            description: 'A single quoted description'
            ---
            """;

        var desc = SkillScanner.ParseDescriptionFromFrontmatter(content);

        Assert.Equal("A single quoted description", desc);
    }

    [Fact]
    public void ParseDescriptionFromFrontmatter_IsCaseInsensitive()
    {
        var content = "Description: Case insensitive description";

        var desc = SkillScanner.ParseDescriptionFromFrontmatter(content);

        Assert.Equal("Case insensitive description", desc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("name: only-name\n---\n# No description")]
    [InlineData("description:   ")]
    public void ParseDescriptionFromFrontmatter_ReturnsNullWhenMissingOrEmpty(string? content)
    {
        var desc = SkillScanner.ParseDescriptionFromFrontmatter(content);

        Assert.Null(desc);
    }

    [Fact]
    public void ReadDescription_ReturnsParsedDescription_WhenFileExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"skill-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var skillFile = Path.Combine(tempDir, "SKILL.md");
            File.WriteAllText(skillFile, "description: Custom skill description");

            var desc = SkillScanner.ReadDescription(skillFile, "test-skill");

            Assert.Equal("Custom skill description", desc);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public void ReadDescription_ReturnsFallback_WhenFileDoesNotExist()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}", "SKILL.md");

        var desc = SkillScanner.ReadDescription(missingPath, "fallback-skill");

        Assert.Equal("Skill in fallback-skill", desc);
    }

    [Fact]
    public void ReadDescription_ReturnsFallback_WhenFileHasNoDescription()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"skill-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var skillFile = Path.Combine(tempDir, "SKILL.md");
            File.WriteAllText(skillFile, "# Just a title with no frontmatter");

            var desc = SkillScanner.ReadDescription(skillFile, "empty-skill");

            Assert.Equal("Skill in empty-skill", desc);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public void DiscoverSkills_ReturnsEmpty_WhenDirectoryDoesNotExist()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), $"missing-dir-{Guid.NewGuid():N}");

        var items = SkillScanner.DiscoverSkills(missingDir);

        Assert.Empty(items);
    }

    [Fact]
    public void DiscoverSkills_EnumeratesSkillsAndIgnoresNonSkillSubdirectories()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"skills-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Valid skill 1
            var skill1Dir = Path.Combine(tempDir, "alpha-skill");
            Directory.CreateDirectory(skill1Dir);
            File.WriteAllText(Path.Combine(skill1Dir, "SKILL.md"), "description: Alpha skill description");

            // Valid skill 2 (no description, fallback expected)
            var skill2Dir = Path.Combine(tempDir, "beta-skill");
            Directory.CreateDirectory(skill2Dir);
            File.WriteAllText(Path.Combine(skill2Dir, "SKILL.md"), "# Beta without frontmatter");

            // Non-skill subdirectory (no SKILL.md)
            var nonSkillDir = Path.Combine(tempDir, "not-a-skill");
            Directory.CreateDirectory(nonSkillDir);
            File.WriteAllText(Path.Combine(nonSkillDir, "README.md"), "Not a skill");

            var items = SkillScanner.DiscoverSkills(tempDir);

            Assert.Equal(2, items.Count);
            Assert.Contains(items, i => i.Name == "alpha-skill" && i.Kind == "skill" && i.Description == "Alpha skill description");
            Assert.Contains(items, i => i.Name == "beta-skill" && i.Kind == "skill" && i.Description == "Skill in beta-skill");
            Assert.DoesNotContain(items, i => i.Name == "not-a-skill");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }
}
