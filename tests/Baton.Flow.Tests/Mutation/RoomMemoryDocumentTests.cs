using Baton.Flow.Domain;
using Baton.Flow.Mutation;
using Baton.Flow.Store;

namespace Baton.Flow.Tests.Mutation;

public class RoomMemoryDocumentTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _roomLogPath;
    private readonly string _memoryRoot;

    public RoomMemoryDocumentTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "baton_room_memory_doc_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _roomLogPath = Path.Combine(_tempDirectory, "room.jsonl");
        _memoryRoot = Path.Combine(_tempDirectory, "memory");
    }

    private async Task<HeldWorkRef> DispatchMemoryProposalAsync(
        IRoomEventLogReader reader, IRoomEventLogWriter writer, string operation = "add", string targetPath = "fact.md", string content = "the fact")
    {
        var captureDir = Path.Combine(_tempDirectory, "artifacts", "execution_1", "memory-proposals");
        Directory.CreateDirectory(captureDir);
        var captureFile = Path.Combine(captureDir, "proposal-1.json");
        var contentJson = operation == "delete" ? "null" : $"\"{content}\"";
        await File.WriteAllTextAsync(
            captureFile,
            $$"""{"Operation":"{{operation}}","TargetPath":"{{targetPath}}","Content":{{contentJson}},"Rationale":"learned it"}""",
            TestContext.Current.CancellationToken);

        var @ref = new HeldWorkRef(Path.GetFullPath(captureFile));
        await RoomMutationInterface.DispatchHeldWorkAsync(
            _tempDirectory, @ref, MemoryProposalEscalation.MemoryProposalShape, MemoryProposalEscalation.NoBudget,
            "operator", reader, writer, TestContext.Current.CancellationToken);

        return @ref;
    }

    [Fact]
    public async Task Proposal_approved_updates_document_bumps_version_and_includes_attribution()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);
        var @ref = await DispatchMemoryProposalAsync(reader, writer, operation: "add", targetPath: "rules/rule1.md", content: "first rule");

        var docBefore = await RoomMemoryDocument.LoadAsync(_tempDirectory, TestContext.Current.CancellationToken);
        Assert.Equal(0, docBefore.Version);
        Assert.Empty(docBefore.FactFiles);
        Assert.Empty(docBefore.History);

        await MemoryProposalResolution.ResolveAsync(
            _tempDirectory, @ref, approve: true, reader, writer, TestContext.Current.CancellationToken);

        var docAfter = await RoomMemoryDocument.LoadAsync(_tempDirectory, TestContext.Current.CancellationToken);
        Assert.Equal(1, docAfter.Version);
        Assert.Single(docAfter.FactFiles);
        Assert.Equal("first rule", docAfter.FactFiles["rules/rule1.md"]);
        Assert.Single(docAfter.History);

        var versionRecord = docAfter.History[0];
        Assert.Equal(1, versionRecord.Version);
        Assert.Equal("add", versionRecord.Operation);
        Assert.Equal("rules/rule1.md", versionRecord.TargetPath);
        Assert.Equal("first rule", versionRecord.Content);
        Assert.Equal("learned it", versionRecord.Rationale);
        Assert.Equal("operator", versionRecord.Approver);
        // Pinned exactly (#672 review): the helper's capture dir is artifacts/execution_1/..., so
        // anything else here -- including the "unknown" fallback -- is a proposer-extraction defect.
        Assert.Equal("execution_1", versionRecord.Proposer);
    }

    [Fact]
    public async Task Proposal_rejected_leaves_document_untouched()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);
        var @ref = await DispatchMemoryProposalAsync(reader, writer, operation: "add", targetPath: "fact.md", content: "rejected fact");

        await MemoryProposalResolution.ResolveAsync(
            _tempDirectory, @ref, approve: false, reader, writer, TestContext.Current.CancellationToken);

        var doc = await RoomMemoryDocument.LoadAsync(_tempDirectory, TestContext.Current.CancellationToken);
        Assert.Equal(0, doc.Version);
        Assert.Empty(doc.FactFiles);
        Assert.Empty(doc.History);
    }

    [Fact]
    public async Task No_path_writes_document_except_through_resolution()
    {
        // Arm 1: a stray capture file nothing ever dispatched does not leak into the document.
        var captureDir = Path.Combine(_tempDirectory, "artifacts", "execution_2", "memory-proposals");
        Directory.CreateDirectory(captureDir);
        var captureFile = Path.Combine(captureDir, "proposal-2.json");
        await File.WriteAllTextAsync(
            captureFile,
            """{"Operation":"add","TargetPath":"secret.md","Content":"secret","Rationale":"unapproved"}""",
            TestContext.Current.CancellationToken);

        var doc = await RoomMemoryDocument.LoadAsync(_tempDirectory, TestContext.Current.CancellationToken);
        Assert.Equal(0, doc.Version);
        Assert.False(doc.FactFiles.ContainsKey("secret.md"));
        Assert.Empty(doc.History);

        // Arm 2 (#672 review: this is what the test's name actually promises): DISPATCHING a
        // proposal -- the real pre-resolution step, not just a stray file -- must not write either.
        // Only resolution applies; a regression where dispatch itself applied would fail here.
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);
        await DispatchMemoryProposalAsync(reader, writer, operation: "add", targetPath: "secret2.md", content: "still unapproved");

        var docAfterDispatch = await RoomMemoryDocument.LoadAsync(_tempDirectory, TestContext.Current.CancellationToken);
        Assert.Equal(0, docAfterDispatch.Version);
        Assert.False(docAfterDispatch.FactFiles.ContainsKey("secret2.md"));
        Assert.Empty(docAfterDispatch.History);
    }

    [Fact]
    public async Task A_crash_between_fact_write_and_version_append_is_visible_as_fact_history_divergence()
    {
        // The inner crash window MemoryProposalApplier.ApplyAsync documents: fact file landed,
        // version record not. Reconstructed here by applying normally and then removing the
        // version log -- byte-identical to dying after the fact write. The claim under test is
        // OBSERVABILITY, not detection: LoadAsync must faithfully report both halves (the fact
        // file present, the history empty) so a caller CAN see the divergence -- and must not
        // guess, because 0044's operator hand-edits produce the same shape legitimately.
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);
        var @ref = await DispatchMemoryProposalAsync(reader, writer, operation: "add", targetPath: "fact.md", content: "landed fact");
        await MemoryProposalResolution.ResolveAsync(
            _tempDirectory, @ref, approve: true, reader, writer, TestContext.Current.CancellationToken);

        Baton.Tests.Shared.FileCleanup.EnsureDeleted(Path.Combine(_memoryRoot, RoomMemoryDocument.VersionsFileName));

        var doc = await RoomMemoryDocument.LoadAsync(_tempDirectory, TestContext.Current.CancellationToken);
        Assert.Equal("landed fact", doc.FactFiles["fact.md"]);
        Assert.Empty(doc.History);
        Assert.Equal(0, doc.Version);
    }

    [Fact]
    public async Task Archiving_and_reopening_room_dir_preserves_document()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        HeldWorkRef @ref;
        await using (var writer = new RoomEventLogWriter(_roomLogPath))
        {
            @ref = await DispatchMemoryProposalAsync(reader, writer, operation: "add", targetPath: "fact.md", content: "persistent fact");

            await MemoryProposalResolution.ResolveAsync(
                _tempDirectory, @ref, approve: true, reader, writer, TestContext.Current.CancellationToken);
        }

        var docOriginal = await RoomMemoryDocument.LoadAsync(_tempDirectory, TestContext.Current.CancellationToken);

        // Move room directory (simulating archive & reopen in a new location). Through the shared
        // retry core: a mid-test move races the same scanner hold as the deletes around it (#1014).
        var archiveDirectory = Path.Combine(Path.GetTempPath(), "baton_room_archive_" + Guid.NewGuid().ToString("N"));
        Baton.Tests.Shared.CleanupRetry.Run(() => Directory.Move(_tempDirectory, archiveDirectory), swallowOnFinal: false);

        try
        {
            var docArchived = await RoomMemoryDocument.LoadAsync(archiveDirectory, TestContext.Current.CancellationToken);
            Assert.Equal(docOriginal.Version, docArchived.Version);
            Assert.Equal(docOriginal.FactFiles, docArchived.FactFiles);
            Assert.Equal(docOriginal.History.Count, docArchived.History.Count);
            Assert.Equal(docOriginal.History[0].TargetPath, docArchived.History[0].TargetPath);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(archiveDirectory);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            DirectoryCleanup.DeleteRecursively(_tempDirectory);
        }
    }
}
