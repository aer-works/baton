using System.Text;
using System.Text.Json;
using Baton.Artifacts;
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
        // F1 (2026-09-02 review): the destination filename is unique per source_path, not just the
        // basename — it carries an 8-hex-char prefix hashed off the source path.
        Assert.Equal(
            Path.Combine(conductorRoom, "artifacts", "conductor"),
            Path.GetDirectoryName(result.DestinationPath));
        Assert.Matches("^[0-9a-f]{8}-source-notes\\.md$", Path.GetFileName(result.DestinationPath));

        // Verify bindings.json stub
        var bindingsFile = BatonPaths.RoomBindingsFile(conductorRoom);
        Assert.True(File.Exists(bindingsFile));
        var bindingsBytes = await File.ReadAllBytesAsync(bindingsFile, TestContext.Current.CancellationToken);
        Assert.Equal((byte)'{', bindingsBytes[0]);
        var bindings = WorkerBindingConfigParser.Parse(await File.ReadAllTextAsync(bindingsFile, TestContext.Current.CancellationToken));
        Assert.True(bindings.ContainsKey("conductor"));

        // Verify registry
        var registryEntries = await RoomRegistryStore.ReadDistinctByRoomAsync(BatonPaths.RoomRegistryFile, TestContext.Current.CancellationToken);
        Assert.Contains(registryEntries, e => e.RoomPath.Equals(conductorRoom, StringComparison.OrdinalIgnoreCase));

        // Verify manifest - written without BOM, first byte is '{' (0x7B)
        var manifestFile = Path.Combine(conductorRoom, "artifacts", "conductor", "manifest.jsonl");
        Assert.True(File.Exists(manifestFile));

        var manifestBytes = await File.ReadAllBytesAsync(manifestFile, TestContext.Current.CancellationToken);
        Assert.NotEmpty(manifestBytes);
        Assert.Equal((byte)'{', manifestBytes[0]); // 0x7B
        Assert.NotEqual((byte)0xEF, manifestBytes[0]); // Not UTF-8 BOM

        // Round-trip through File.ReadAllLines with the same UTF8 without BOM encoding
        var utf8NoBom = new UTF8Encoding(false);
        var manifestLines = await File.ReadAllLinesAsync(manifestFile, utf8NoBom, TestContext.Current.CancellationToken);
        Assert.Single(manifestLines);
        var entry = JsonSerializer.Deserialize<ConductorManifestEntry>(manifestLines[0]);
        Assert.NotNull(entry);
        Assert.Equal("Morning Brief", entry!.Title);
        Assert.Equal(Path.GetFullPath(sourceFile), entry.SourcePath);
        Assert.Equal(result.Sha256, entry.Sha256);
        Assert.Equal(Path.GetFileName(result.DestinationPath), entry.ArtifactFile);
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
    public async Task Deliver_RedeliveryOfSameSource_AppendsVersionRatherThanOverwriting()
    {
        // #496: DeliverCommand now routes its file write through RoomArtifacts.Write, so a
        // re-delivery must append a version -- the prior bytes stay readable at version 1 -- rather
        // than the raw File.Copy(overwrite: true) this replaced.
        var sourceFile = Path.Combine(_tempHome, "versioned-plan.md");
        await File.WriteAllTextAsync(sourceFile, "# Version 1\nInitial content", TestContext.Current.CancellationToken);

        var conductorRoom = Path.Combine(BatonPaths.Rooms, "conductor");
        var options1 = new DeliverOptions(sourceFile, null, conductorRoom);
        using var sw1 = new StringWriter();
        var result1 = await DeliverCommand.ExecuteAsync(options1, sw1, TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(sourceFile, "# Version 2\nUpdated content", TestContext.Current.CancellationToken);

        var options2 = new DeliverOptions(sourceFile, null, conductorRoom);
        using var sw2 = new StringWriter();
        var result2 = await DeliverCommand.ExecuteAsync(options2, sw2, TestContext.Current.CancellationToken);

        var artifactName = Path.Combine("conductor", Path.GetFileName(result2.DestinationPath));
        var versions = RoomArtifacts.Versions(conductorRoom, artifactName);

        Assert.Equal(2, versions.Count);
        Assert.Equal("conductor", versions[0].ProducedBy.Role);
        Assert.Null(versions[0].ProducedBy.ExecutionId);

        var v1Bytes = RoomArtifacts.Read(conductorRoom, artifactName, 1);
        Assert.NotNull(v1Bytes);
        Assert.Contains("Version 1", Encoding.UTF8.GetString(v1Bytes!));

        var v2Bytes = RoomArtifacts.Read(conductorRoom, artifactName, 2);
        Assert.NotNull(v2Bytes);
        Assert.Contains("Version 2", Encoding.UTF8.GetString(v2Bytes!));

        // The current path (what every pre-#496 reader keeps using) is still the latest content.
        Assert.Equal(result1.DestinationPath, result2.DestinationPath);
        Assert.Contains("Version 2", await File.ReadAllTextAsync(result2.DestinationPath, TestContext.Current.CancellationToken));
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

    [Fact]
    public async Task Deliver_SameBasenameDifferentSourceDirs_ProducesTwoFilesWithCorrectBytes()
    {
        // F1 (2026-09-02 review): two different sources sharing a basename must land on two distinct
        // on-disk files, each holding its own bytes -- not silently overwrite one another.
        var dirA = Path.Combine(_tempHome, "projA");
        var dirB = Path.Combine(_tempHome, "projB");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        var fileA = Path.Combine(dirA, "notes.md");
        var fileB = Path.Combine(dirB, "notes.md");
        await File.WriteAllTextAsync(fileA, "# Notes A\nProject A content", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(fileB, "# Notes B\nProject B content", TestContext.Current.CancellationToken);

        var conductorRoom = Path.Combine(BatonPaths.Rooms, "conductor");
        using var sw = new StringWriter();

        var resultA = await DeliverCommand.ExecuteAsync(new DeliverOptions(fileA, null, conductorRoom), sw, TestContext.Current.CancellationToken);
        var resultB = await DeliverCommand.ExecuteAsync(new DeliverOptions(fileB, null, conductorRoom), sw, TestContext.Current.CancellationToken);

        Assert.NotEqual(resultA.DestinationPath, resultB.DestinationPath);
        Assert.True(File.Exists(resultA.DestinationPath));
        Assert.True(File.Exists(resultB.DestinationPath));
        Assert.Equal("# Notes A\nProject A content", await File.ReadAllTextAsync(resultA.DestinationPath, TestContext.Current.CancellationToken));
        Assert.Equal("# Notes B\nProject B content", await File.ReadAllTextAsync(resultB.DestinationPath, TestContext.Current.CancellationToken));

        var manifestFile = Path.Combine(conductorRoom, "artifacts", "conductor", "manifest.jsonl");
        var manifestLines = await File.ReadAllLinesAsync(manifestFile, TestContext.Current.CancellationToken);
        Assert.Equal(2, manifestLines.Length);
        var entries = manifestLines.Select(l => JsonSerializer.Deserialize<ConductorManifestEntry>(l)!).ToList();
        var entryA = entries.Single(e => e.SourcePath == Path.GetFullPath(fileA));
        var entryB = entries.Single(e => e.SourcePath == Path.GetFullPath(fileB));
        Assert.NotEqual(entryA.ArtifactFile, entryB.ArtifactFile);
    }

    [Fact]
    public async Task Deliver_CrossLanguageFixture_MatchesCheckedInFixtureExactBytes()
    {
        var repoRoot = FindRepoRoot();
        var fixturePath = Path.Combine(repoRoot, "tests", "fixtures", "conductor-manifest.jsonl");
        Assert.True(File.Exists(fixturePath), $"Fixture file must exist at {fixturePath}");

        var expectedBytes = await File.ReadAllBytesAsync(fixturePath, TestContext.Current.CancellationToken);
        Assert.NotEmpty(expectedBytes);
        Assert.Equal((byte)'{', expectedBytes[0]); // 0x7B
        Assert.NotEqual((byte)0xEF, expectedBytes[0]); // Not UTF-8 BOM

        var entry = new ConductorManifestEntry(
            "Fixture Plan",
            "/fixtures/fixture-plan.md",
            "2026-09-02T12:00:00.0000000Z",
            "760c986cec5f6622f8320ce7db6ffc893c0406fcc4e18ebda30df7d599d1d78b",
            "c44a8b84-fixture-plan.md");

        var tempManifest = Path.Combine(_tempHome, "fixture-manifest.jsonl");
        var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
        var json = JsonSerializer.Serialize(entry, jsonOptions) + "\n";
        var utf8NoBom = new UTF8Encoding(false);
        await File.WriteAllTextAsync(tempManifest, json, utf8NoBom, TestContext.Current.CancellationToken);

        var actualBytes = await File.ReadAllBytesAsync(tempManifest, TestContext.Current.CancellationToken);
        Assert.Equal(expectedBytes, actualBytes);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Baton.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate repo root containing Baton.slnx");
    }
}
