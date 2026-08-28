using System.Reflection;
using System.Xml.Linq;
using Aer.Adapters;
using Xunit;

namespace Aer.Adapters.Tests;

/// <summary>
/// #1319: pins the mechanism that makes the raw, RMW-unsafe <c>SaveMetadataAsync</c> unreachable from
/// endpoint code -- <c>internal</c> visibility, with no <c>InternalsVisibleTo</c> grant from
/// Aer.Adapters into Aer.Daemon. Without a test, a future change could quietly widen either half (flip
/// the modifier back to <c>public</c>, or add the grant) and the guard would be gone with nothing
/// failing to say so -- exactly the "missed site stays silently racy forever" the issue's own
/// description warns about, one level up.
/// </summary>
public class MetadataWriteGuardTests
{
    [Fact]
    public void SaveMetadataAsync_is_not_publicly_reachable()
    {
        var method = typeof(InteractiveSessionMaterializer).GetMethod(
            "SaveMetadataAsync", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.False(
            method.IsPublic,
            "SaveMetadataAsync must stay internal -- it is the raw read-modify-write-unsafe primitive; "
            + "production code must go through InteractiveSessionMaterializer.UpdateMetadataAsync.");
        Assert.True(method.IsAssembly, "expected internal (assembly-visible), found a narrower modifier than intended.");
    }

    [Fact]
    public void UpdateMetadataAsync_is_the_public_guarded_path()
    {
        var method = typeof(InteractiveSessionMaterializer).GetMethod(
            "UpdateMetadataAsync", BindingFlags.Static | BindingFlags.Public);

        Assert.NotNull(method);
    }

    [Fact]
    public void Aer_Adapters_grants_no_InternalsVisibleTo_into_Aer_Daemon()
    {
        var csprojPath = Path.Combine(RepoRoot(), "src", "Aer.Adapters", "Aer.Adapters.csproj");
        var doc = XDocument.Load(csprojPath);

        var grants = doc.Descendants("InternalsVisibleTo")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .ToList();

        Assert.DoesNotContain("Aer.Daemon", grants);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AerFlow.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate the repo root (AerFlow.slnx) by walking up from " + AppContext.BaseDirectory);
    }
}
