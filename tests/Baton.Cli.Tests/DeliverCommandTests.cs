using System.Text.Json;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Tests;

public sealed class DeliverCommandTests : IDisposable
{
    private readonly string _tempHome;
    private readonly IDisposable _scope;

    public DeliverCommandTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"baton-deliver-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHome);
        _scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = _tempHome });
    }

    public void Dispose()
    {
        _scope.Dispose();
        if (Directory.Exists(_tempHome))
        {
            DirectoryCleanup.DeleteRecursively(_tempHome);
        }
    }

    [Fact]
    public async Task Deliver_NonExistentFile_ThrowsCliArgumentException()
    {
        var nonExistent = Path.Combine(_tempHome, "does-not-exist.md");
        var options = new DeliverOptions(nonExistent, null, Path.Combine(BatonPaths.Rooms, "conductor"));
        using var sw = new StringWriter();

        var ex = await Assert.ThrowsAsync<CliArgumentException>(() =>
            DeliverCommand.ExecuteAsync(options, sw, TestContext.Current.CancellationToken));

        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public async Task Deliver_CreatesRoomStructure_CopiesFile_AndWritesManifest()
    {
        var sourceFile = Path.Combine(_tempHome, "source-notes.md");
        await File.WriteAllTextAsync(sourceFile, "# Morning Brief\n\nTask list here.", TestContext.Current.CancellationToken);

        var conductorRoom = Path.Combine(BatonPaths.Rooms, "conductor");
        var options = new DeliverOptions(sourceFile, null, conductorRoom);
        using var sw = new StringWriter();

        var result = await DeliverCommand.ExecuteAsync(options, sw, TestContext.Current.CancellationToken);

        Assert.Equal("Morning Brief", result.Title);
        Assert.Equal(Path.GetFullPath(sourceFile), result.SourcePath);
        Assert.True(File.Exists(result.DestinationPath));
        Assert.Equal(Path.Combine(conductorRoom, "artifacts", "conductor", "source-notes.md"), result.DestinationPath);

        // Verify bindings.json stub
        var bindingsFile = BatonPaths.RoomBindingsFile(conductorRoom);
        Assert.True(File.Exists(bindingsFile));
        var bindings = WorkerBindingConfigParser.Parse(await File.ReadAllTextAsync(bindingsFile, TestContext.Current.CancellationToken));
        Assert.True(bindings.ContainsKey("conductor"));

        // Verify registry
        var registryEntries = await RoomRegistryStore.ReadDistinctByRoomAsync(BatonPaths.RoomRegistryFile, TestContext.Current.CancellationToken);
        Assert.Contains(registryEntries, e => e.RoomPath.Equals(conductorRoom, StringComparison.OrdinalIgnoreCase));

        // Verify manifest
        var manifestFile = Path.Combine(conductorRoom, "artifacts", "conductor", "manifest.jsonl");
        Assert.True(File.Exists(manifestFile));
        var manifestLines = await File.ReadAllLinesAsync(manifestFile, TestContext.Current.CancellationToken);
        Assert.Single(manifestLines);
        var entry = JsonSerializer.Deserialize<ConductorManifestEntry>(manifestLines[0]);
        Assert.NotNull(entry);
        Assert.Equal("Morning Brief", entry!.Title);
        Assert.Equal(Path.GetFullPath(sourceFile), entry.SourcePath);
        Assert.Equal(result.Sha256, entry.Sha256);
    }

    [Fact]
    public async Task Deliver_WithExplicitTitle_UsesProvidedTitle()
    {
        var sourceFile = Path.Combine(_tempHome, "report.md");
        await File.WriteAllTextAsync(sourceFile, "# Heading in File\n\nContent", TestContext.Current.CancellationToken);

        var conductorRoom = Path.Combine(BatonPaths.Rooms, "conductor");
        var options = new DeliverOptions(sourceFile, "Override Title", conductorRoom);
        using var sw = new StringWriter();

        var result = await DeliverCommand.ExecuteAsync(options, sw, TestContext.Current.CancellationToken);

        Assert.Equal("Override Title", result.Title);
    }

    [Fact]
    public async Task Deliver_WithoutHeading_DefaultsTitleToBasename()
    {
        var sourceFile = Path.Combine(_tempHome, "data.txt");
        await File.WriteAllTextAsync(sourceFile, "plain text without headings", TestContext.Current.CancellationToken);

        var conductorRoom = Path.Combine(BatonPaths.Rooms, "conductor");
        var options = new DeliverOptions(sourceFile, null, conductorRoom);
        using var sw = new StringWriter();

        var result = await DeliverCommand.ExecuteAsync(options, sw, TestContext.Current.CancellationToken);

        Assert.Equal("data.txt", result.Title);
    }

    [Fact]
    public async Task Deliver_RedeliveryOfSameSource_ReplacesManifestLineAndOverwritesFile()
    {
        var sourceFile = Path.Combine(_tempHome, "evolving-plan.md");
        await File.WriteAllTextAsync(sourceFile, "# Version 1\nInitial content", TestContext.Current.CancellationToken);

        var conductorRoom = Path.Combine(BatonPaths.Rooms, "conductor");
        var options1 = new DeliverOptions(sourceFile, null, conductorRoom);
        using var sw1 = new StringWriter();
        var result1 = await DeliverCommand.ExecuteAsync(options1, sw1, TestContext.Current.CancellationToken);

        // Edit source file
        await File.WriteAllTextAsync(sourceFile, "# Version 2\nUpdated content", TestContext.Current.CancellationToken);

        var options2 = new DeliverOptions(sourceFile, null, conductorRoom);
        using var sw2 = new StringWriter();
        var result2 = await DeliverCommand.ExecuteAsync(options2, sw2, TestContext.Current.CancellationToken);

        Assert.Equal("Version 2", result2.Title);
        Assert.NotEqual(result1.Sha256, result2.Sha256);

        // Destination file has new content
        var destContent = await File.ReadAllTextAsync(result2.DestinationPath, TestContext.Current.CancellationToken);
        Assert.Contains("Version 2", destContent);

        // Manifest has exactly 1 line (replaced)
        var manifestFile = Path.Combine(conductorRoom, "artifacts", "conductor", "manifest.jsonl");
        var manifestLines = await File.ReadAllLinesAsync(manifestFile, TestContext.Current.CancellationToken);
        Assert.Single(manifestLines);
        var entry = JsonSerializer.Deserialize<ConductorManifestEntry>(manifestLines[0]);
        Assert.Equal("Version 2", entry!.Title);
        Assert.Equal(result2.Sha256, entry.Sha256);
    }

    [Fact]
    public async Task Deliver_DifferentSourceFiles_AppendsToManifest()
    {
        var file1 = Path.Combine(_tempHome, "file1.md");
        var file2 = Path.Combine(_tempHome, "file2.md");
        await File.WriteAllTextAsync(file1, "# File 1", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(file2, "# File 2", TestContext.Current.CancellationToken);

        var conductorRoom = Path.Combine(BatonPaths.Rooms, "conductor");
        using var sw = new StringWriter();

        await DeliverCommand.ExecuteAsync(new DeliverOptions(file1, null, conductorRoom), sw, TestContext.Current.CancellationToken);
        await DeliverCommand.ExecuteAsync(new DeliverOptions(file2, null, conductorRoom), sw, TestContext.Current.CancellationToken);

        var manifestFile = Path.Combine(conductorRoom, "artifacts", "conductor", "manifest.jsonl");
        var manifestLines = await File.ReadAllLinesAsync(manifestFile, TestContext.Current.CancellationToken);
        Assert.Equal(2, manifestLines.Length);
    }
}
