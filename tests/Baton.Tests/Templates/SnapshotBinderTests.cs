using Baton.Tests.TestSupport;
using System.Text.Json;
using Baton.Domain;
using Baton.Store;
using Baton.Templates;

namespace Baton.Tests.Templates;

public class SnapshotBinderTests
{
    private static WorkflowDefinition SampleDefinition() => new(
        new WorkflowTemplateId("architect-critic-synth"),
        WorkflowTemplateVersion: 3,
        Steps:
        [
            new WorkflowStepDefinition(
                new StepId("architect"),
                "architect",
                Inputs: ["goal"],
                Outputs: ["plan"],
                DependsOn: [],
                RetryPolicy: new RetryPolicy(MaxAttempts: 3)),
        ]);

    [Fact]
    public void Bind_freezes_the_template_id_and_version_alongside_a_new_snapshot_id()
    {
        var definition = SampleDefinition();

        var snapshot = SnapshotBinder.Bind(definition);

        Assert.Equal(definition.WorkflowTemplateId, snapshot.WorkflowTemplateId);
        Assert.Equal(definition.WorkflowTemplateVersion, snapshot.WorkflowTemplateVersion);
        Assert.Equal(definition.Steps, snapshot.Steps);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.WorkflowDefinitionSnapshotId.Value));
    }

    [Fact]
    public void Bind_generates_a_distinct_SnapshotId_on_every_call()
    {
        var definition = SampleDefinition();

        var first = SnapshotBinder.Bind(definition);
        var second = SnapshotBinder.Bind(definition);

        Assert.NotEqual(first.WorkflowDefinitionSnapshotId, second.WorkflowDefinitionSnapshotId);
    }

    [Fact]
    public void Bind_rejects_an_invalid_definition_even_when_not_parsed_from_a_file()
    {
        var invalid = new WorkflowDefinition(
            new WorkflowTemplateId("bad"),
            1,
            Steps:
            [
                new WorkflowStepDefinition(new StepId("a"), "worker", [], [], [new StepId("ghost")], new RetryPolicy(1)),
            ]);

        Assert.Throws<WorkflowDefinitionValidationException>(() => SnapshotBinder.Bind(invalid));
    }

    [Fact]
    public async Task PersistAsync_writes_a_snapshot_that_round_trips_through_JSON()
    {
        var snapshot = SnapshotBinder.Bind(SampleDefinition());
        var path = Path.Combine(Path.GetTempPath(), $"snapshot-{Guid.NewGuid():N}.json");
        try
        {
            await SnapshotBinder.PersistAsync(snapshot, path, TestContext.Current.CancellationToken);

            var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            var reloaded = JsonSerializer.Deserialize<WorkflowDefinitionSnapshot>(json, SnapshotJson.Options);

            Assert.NotNull(reloaded);
            Assert.Equal(JsonSerializer.Serialize(snapshot, SnapshotJson.Options), JsonSerializer.Serialize(reloaded, SnapshotJson.Options));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    private static WorkflowDefinitionSnapshot LargeSnapshot(int stepCount) => new(
        new WorkflowDefinitionSnapshotId(Guid.NewGuid().ToString("n")),
        new WorkflowTemplateId("bulk-template"),
        WorkflowTemplateVersion: 1,
        Steps: Enumerable.Range(0, stepCount)
            .Select(i => new WorkflowStepDefinition(
                new StepId($"step-{i}"),
                "worker",
                Inputs: ["input-" + new string('x', 200)],
                Outputs: ["output-" + new string('y', 200)],
                DependsOn: [],
                RetryPolicy: new RetryPolicy(MaxAttempts: 3)))
            .ToArray());

    /// <summary>
    /// #818: a concurrent reader racing <see cref="SnapshotBinder.PersistAsync"/> must never observe
    /// <c>File.Exists == true</c> with truncated/unparseable JSON at the final path -- only the prior
    /// content (if any) or the complete new content. Real in-process race, not simulated: a large
    /// (multi-hundred-KB) payload widens the window enough that, against the pre-fix
    /// <c>File.WriteAllTextAsync(snapshotFilePath, ...)</c> implementation, a tight concurrent
    /// poll-and-parse loop reliably observed a truncated read within a handful of the 25 iterations
    /// below. Verified red against that implementation before adding the temp+rename fix; kept as a
    /// permanent regression test since, post-fix, the assertion holds unconditionally (an atomic
    /// rename has no intermediate state to observe), so it cannot become flaky going forward.
    /// </summary>
    [Fact]
    public async Task PersistAsync_never_exposes_partial_JSON_at_the_final_path_to_a_concurrent_reader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"snapshot-race-{Guid.NewGuid():N}.json");
        var sawTornRead = false;
        var readerFailure = string.Empty;

        try
        {
            for (var iteration = 0; iteration < 25 && !sawTornRead; iteration++)
            {
                var snapshot = LargeSnapshot(500);
                using var cts = new CancellationTokenSource();
                var readerTask = Task.Run(async () =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        byte[] bytes;
                        try
                        {
                            // #842: read the way LoadFromFileAsync now reads (delete-tolerant
                            // share) -- this reader's whole justification is simulating the
                            // product reader, and a default-share read is additionally the very
                            // handle shape that starved the writer's rename retry under
                            // full-suite load (the measured exhaustion on #842).
                            using var readStream = new FileStream(
                                path, FileMode.Open, FileAccess.Read,
                                FileShare.ReadWrite | FileShare.Delete);
                            using var buffer = new MemoryStream();
                            await readStream.CopyToAsync(buffer, TestContext.Current.CancellationToken);
                            bytes = buffer.ToArray();
                        }
                        catch (IOException)
                        {
                            continue; // final path mid-rename or not yet created -- not a torn read
                        }

                        if (bytes.Length == 0)
                        {
                            continue;
                        }

                        try
                        {
                            JsonSerializer.Deserialize<WorkflowDefinitionSnapshot>(bytes, SnapshotJson.Options);
                        }
                        catch (JsonException ex)
                        {
                            sawTornRead = true;
                            readerFailure = $"iteration {iteration}: {ex.Message}";
                            return;
                        }

                        // A brief gap between reads, rather than reopening the destination
                        // back-to-back: a real reader (one LoadFromFileAsync per CLI invocation)
                        // never holds it open continuously. #1267: the gap is load-bearing, not
                        // cosmetic -- this comment used to say #842's delete-tolerant share meant
                        // the rename no longer needed it, and that is measured false (0057's
                        // "Rests on"). Without the gap this reader starves the writer's retry.
                        try
                        {
                            await Task.Delay(3, cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }, TestContext.Current.CancellationToken);

                await SnapshotBinder.PersistAsync(snapshot, path, TestContext.Current.CancellationToken);
                cts.Cancel();
                await readerTask;
            }

            Assert.False(sawTornRead, $"concurrent reader observed unparseable JSON at the final path: {readerFailure}");
        }
        finally
        {
            // Best-effort scratch cleanup: FileCleanup.Delete retries the transient Windows lock
            // (Defender/indexer holding the just-written file) and swallows a persistent one, so a
            // leftover uniquely-named temp file can't mask the test's real result (#295 / #918).
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// #818: if the atomic rename step itself fails, the temp sibling must not linger forever -- best
    /// effort cleanup, same choice <c>Baton.Vendors.AtomicLaunchConfigWriter</c> makes -- and the real
    /// failure must propagate rather than being masked by the cleanup attempt.
    /// </summary>
    [Fact]
    public async Task PersistAsync_cleans_up_its_temp_file_and_rethrows_when_the_rename_fails()
    {
        var snapshot = SnapshotBinder.Bind(SampleDefinition());
        var directory = Path.Combine(Path.GetTempPath(), $"snapshot-fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        // A directory at the destination path makes File.Move's overwrite rename fail, simulating a
        // failed rename without needing to fabricate a genuine cross-process sharing violation.
        var path = Path.Combine(directory, "snapshot.json");
        Directory.CreateDirectory(path);

        try
        {
            // File.Move onto an existing directory surfaces as IOException on some platforms and
            // UnauthorizedAccessException on others -- either way it must not be swallowed. A tiny retry
            // budget forces the exhaustion (rethrow) path immediately rather than waiting out the
            // production wall-clock budget, which this permanent failure would otherwise burn in full.
            await Assert.ThrowsAnyAsync<Exception>(
                () => SnapshotBinder.PersistAsync(
                    snapshot, path, TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken));

            var leftoverTempFiles = Directory.GetFiles(directory, "*.tmp");
            Assert.Empty(leftoverTempFiles);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public async Task PersistAsync_creates_missing_parent_directories()
    {
        var snapshot = SnapshotBinder.Bind(SampleDefinition());
        var directory = Path.Combine(Path.GetTempPath(), $"snapshot-dir-{Guid.NewGuid():N}", "nested");
        var path = Path.Combine(directory, "snapshot.json");
        try
        {
            await SnapshotBinder.PersistAsync(snapshot, path, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(directory)))
            {
                DirectoryCleanup.DeleteRecursively(Path.GetDirectoryName(directory)!);
            }
        }
    }

    [Fact]
    public void PausePoint_deserialized_without_a_Kind_defaults_to_ReadyForReview()
    {
        // #334 backward-compat: a snapshot persisted before Kind existed has no "Kind" property on
        // its pause points. STJ materializes the missing constructor value as default(PausePointKind),
        // so ReadyForReview MUST be the zero value for every replayed pause to keep its original
        // approval-gate meaning — this test fails loudly if the enum members are ever reordered.
        var pausePoint = JsonSerializer.Deserialize<PausePoint>("""{"SupersedeTargets":[]}""", SnapshotJson.Options);

        Assert.NotNull(pausePoint);
        Assert.Equal(PausePointKind.ReadyForReview, pausePoint.Kind);
        Assert.Equal(0, (int)PausePointKind.ReadyForReview);
    }

    [Fact]
    public void Bind_preserves_a_NeedsInput_pause_kind_through_its_JSON_round_trip()
    {
        // Bind serializes then re-parses the definition (freezing it), so this proves the kind
        // survives the durable-snapshot JSON round trip — the route #334 carries the distinction by,
        // in place of an event-format change.
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("session-like"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(new StepId("chat"), "w", [], ["out"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(
                    new StepId("anchor"), "w2", ["out"], ["marker"], [new StepId("chat")], new RetryPolicy(1),
                    PausePoint: new PausePoint([new StepId("chat")], PausePointKind.NeedsInput)),
            ]);

        var snapshot = SnapshotBinder.Bind(definition);

        var anchor = snapshot.Steps.Single(step => step.StepId.Value == "anchor");
        Assert.Equal(PausePointKind.NeedsInput, anchor.PausePoint!.Kind);
    }

    [Fact]
    public async Task Editing_the_source_template_file_after_binding_has_no_effect_on_the_persisted_snapshot()
    {
        var templatePath = Path.Combine(Path.GetTempPath(), $"template-{Guid.NewGuid():N}.json");
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"snapshot-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(templatePath, JsonSerializer.Serialize(SampleDefinition(), SnapshotJson.Options), TestContext.Current.CancellationToken);
        try
        {
            var loaded = await WorkflowDefinitionParser.LoadFromFileAsync(templatePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(loaded);
            await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

            // Edit the template on disk after the snapshot was bound and persisted.
            var edited = SampleDefinition() with { WorkflowTemplateVersion = 4 };
            await File.WriteAllTextAsync(templatePath, JsonSerializer.Serialize(edited, SnapshotJson.Options), TestContext.Current.CancellationToken);

            var reloaded = await SnapshotBinder.LoadFromFileAsync(snapshotPath, TestContext.Current.CancellationToken);

            Assert.NotNull(reloaded);
            Assert.Equal(3, reloaded.WorkflowTemplateVersion);
        }
        finally
        {
            FileCleanup.Delete(templatePath);
            FileCleanup.Delete(snapshotPath);
        }
    }

    [Fact]
    public async Task PersistAsync_survives_a_transient_holder_that_outlasts_the_old_attempt_count_budget()
    {
        // The regression guard for the wall-clock retry (#398 class): a foreign handle (OS indexer/AV)
        // holds the just-written destination without FileShare.Delete for longer than the old
        // attempt-count budget (10 attempts, ~675ms of backoff) could survive. The wall-clock budget
        // (5s) keeps retrying until the holder releases, so the rename lands instead of throwing.
        var snapshot = SnapshotBinder.Bind(SampleDefinition());
        var directory = Path.Combine(Path.GetTempPath(), $"snapshot-hold-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "snapshot.json");
        // Seed a file so the rename is an OVERWRITE — the case a foreign reader blocks on Windows.
        await File.WriteAllTextAsync(path, "seed", TestContext.Current.CancellationToken);

        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            // A deliberately generous injected budget (30s), not the production default: the test proves
            // the wall-clock retry survives a hold that outlasts the old attempt-count budget, and the
            // wide margin keeps the test itself deterministic even if its own 800ms Task.Delay is starved
            // under CI load — the retry only needs to still be running when the holder releases.
            var persist = SnapshotBinder.PersistAsync(
                snapshot, path, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            // Hold well past the old ~675ms attempt-count budget, then release with room to spare.
            await Task.Delay(800, TestContext.Current.CancellationToken);
            holder.Dispose();

            await persist; // must NOT throw — the old attempt-count budget would have exhausted by now.

            var reloaded = await SnapshotBinder.LoadFromFileAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(snapshot.WorkflowTemplateVersion, reloaded.WorkflowTemplateVersion);
        }
        finally
        {
            holder.Dispose();
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public async Task LoadFromFileAsync_on_a_missing_snapshot_throws_SnapshotLoadException_not_a_raw_FileNotFound()
    {
        // A missing snapshot must surface as the typed SnapshotLoadException — the loader translates it
        // itself, since not every caller pre-checks existence (see LoadFromFileAsync for why).
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-snapshot-{Guid.NewGuid():N}.json");

        var ex = await Assert.ThrowsAsync<SnapshotLoadException>(
            () => SnapshotBinder.LoadFromFileAsync(missing, TestContext.Current.CancellationToken));
        Assert.Contains("does not exist", ex.Message);
    }
}
