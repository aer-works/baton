using Aer.Adapters.Tests.TestSupport;
using Aer.Flow.Domain;

namespace Aer.Adapters.Tests;

/// <summary>
/// The bindings write seam's round-trip bar (M16 Phase 4, issue #153): a saved file must
/// round-trip through the exact <see cref="WorkerBindingConfigParser.Parse"/> every other consumer
/// uses — provable at this layer precisely because the writer lives beside its parser (the phase's
/// placement decision of record).
/// </summary>
public class WorkerBindingConfigWriterTests
{
    private static Dictionary<string, WorkerBindingConfigEntry> TwoWorkerConfig() => new()
    {
        ["architect"] = new WorkerBindingConfigEntry(
            "claude",
            new WorkerContract(
                "architect",
                RequiredInputs: [],
                ProducedOutputs:
                [
                    // Exercises every JsonScalar variant through OutputCondition — the one spot the
                    // opaque produced-outputs round trip (Aer.Ui's WorkerBindingEntryViewModel) could
                    // silently lose fidelity if it were tested with a bare { "Name": ... } only.
                    new ProducedOutput("plan", new OutputCondition("/status", new JsonScalar.String("done"))),
                ],
                OptionalMetadata: ["priority"]),
            "Draft a plan and write it to your output file.",
            TimeSpan.FromMinutes(5),
            Model: "claude-opus-4",
            PermissionScope: "write-only",
            WorkingDirectory: "/home/user/my-project"),
        ["critic"] = new WorkerBindingConfigEntry(
            "gemini",
            new WorkerContract(
                "critic",
                RequiredInputs: ["plan"],
                ProducedOutputs:
                [
                    new ProducedOutput("review", new OutputCondition("/score", new JsonScalar.Number(1))),
                    new ProducedOutput("flag", new OutputCondition("/approved", new JsonScalar.Boolean(true))),
                    new ProducedOutput("note", new OutputCondition("/reason", JsonScalar.Null.Instance)),
                ],
                OptionalMetadata: []),
            "Review the plan.",
            TimeSpan.FromMinutes(1)),
    };

    /// <summary>
    /// A register wide enough that writing it is not instantaneous — the tear window the atomicity
    /// test hunts is proportional to how long the write takes, and <see cref="TwoWorkerConfig"/> is
    /// small enough to land inside one scheduling slice. Only that test needs this; every other one
    /// is about content, where two entries say as much as two hundred.
    /// </summary>
    private static Dictionary<string, WorkerBindingConfigEntry> WideConfig()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>();
        for (var i = 0; i < 400; i++)
        {
            config[$"worker-{i:D4}"] = new WorkerBindingConfigEntry(
                "claude",
                new WorkerContract(
                    $"worker-{i:D4}",
                    RequiredInputs: ["plan", "review"],
                    ProducedOutputs:
                    [
                        new ProducedOutput("plan", new OutputCondition("/status", new JsonScalar.String("done"))),
                    ],
                    OptionalMetadata: ["priority"]),
                // Long enough to make the payload hundreds of KB rather than a few, which is the point.
                new string('x', 500),
                TimeSpan.FromMinutes(5));
        }

        return config;
    }

    [Fact]
    public async Task A_saved_config_round_trips_through_the_engines_own_parser()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bindings-writer-{Guid.NewGuid():N}.json");
        try
        {
            var config = TwoWorkerConfig();

            await WorkerBindingConfigWriter.SaveToFileAsync(config, path, TestContext.Current.CancellationToken);
            var parsed = await WorkerBindingConfigParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(config.Keys.OrderBy(k => k), parsed.Keys.OrderBy(k => k));
            foreach (var (workerName, entry) in config)
            {
                var parsedEntry = parsed[workerName];
                Assert.Equal(entry.Adapter, parsedEntry.Adapter);
                Assert.Equal(entry.PromptTemplate, parsedEntry.PromptTemplate);
                Assert.Equal(entry.Timeout, parsedEntry.Timeout);
                Assert.Equal(entry.Model, parsedEntry.Model);
                Assert.Equal(entry.PermissionScope, parsedEntry.PermissionScope);
                Assert.Equal(entry.WorkingDirectory, parsedEntry.WorkingDirectory);
                Assert.Equal(entry.Contract.WorkerName, parsedEntry.Contract.WorkerName);
                Assert.Equal(entry.Contract.RequiredInputs, parsedEntry.Contract.RequiredInputs);
                Assert.Equal(entry.Contract.OptionalMetadata, parsedEntry.Contract.OptionalMetadata);
                Assert.Equal(entry.Contract.ProducedOutputs.Count, parsedEntry.Contract.ProducedOutputs.Count);
                for (var i = 0; i < entry.Contract.ProducedOutputs.Count; i++)
                {
                    Assert.Equal(entry.Contract.ProducedOutputs[i].Name, parsedEntry.Contract.ProducedOutputs[i].Name);
                    Assert.Equal(entry.Contract.ProducedOutputs[i].Condition, parsedEntry.Contract.ProducedOutputs[i].Condition);
                }
            }
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task An_empty_config_is_valid_and_round_trips()
    {
        // The editor's New action mints exactly this shape (M16 Phase 4) — an empty config passes
        // the parser's checks (nothing to iterate), so a just-created bindings file is already a
        // parseable file.
        var path = Path.Combine(Path.GetTempPath(), $"bindings-writer-empty-{Guid.NewGuid():N}.json");
        try
        {
            await WorkerBindingConfigWriter.SaveToFileAsync(new Dictionary<string, WorkerBindingConfigEntry>(), path, TestContext.Current.CancellationToken);
            var parsed = await WorkerBindingConfigParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);

            Assert.Empty(parsed);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task An_entry_with_a_blank_adapter_is_rejected_at_write_time_and_nothing_is_written()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bindings-writer-invalid-{Guid.NewGuid():N}.json");
        var invalid = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                string.Empty,
                new WorkerContract("architect", [], [], []),
                "Draft a plan.",
                TimeSpan.FromMinutes(5)),
        };

        var exception = await Assert.ThrowsAsync<WorkerBindingConfigException>(
            () => WorkerBindingConfigWriter.SaveToFileAsync(invalid, path, TestContext.Current.CancellationToken));

        Assert.Contains("Adapter", exception.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task SaveToFileAsync_creates_missing_parent_directories()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bindings-writer-dirs-{Guid.NewGuid():N}", "nested");
        var path = Path.Combine(directory, "bindings.json");
        try
        {
            await WorkerBindingConfigWriter.SaveToFileAsync(TwoWorkerConfig(), path, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(Path.GetDirectoryName(directory)!);
        }
    }

    [Fact]
    public void Serialize_emits_indented_human_editable_json()
    {
        var json = WorkerBindingConfigWriter.Serialize(TwoWorkerConfig());

        Assert.Contains("\n", json);
        Assert.Equal(2, WorkerBindingConfigParser.Parse(json).Count);
    }

    /// <summary>
    /// #1266 / #1267: the replace loses to <b>any</b> open handle on the target, whatever share mode
    /// it was opened with. This is the measurement 0057's "Rests on" row cites, committed rather than
    /// left as an ad-hoc run — a decision record's evidence has to be re-runnable by whoever doubts it.
    /// </summary>
    /// <remarks>
    /// The `Delete`-sharing arm is the one that matters, and 0057's "Rests on" row holds why the
    /// intuition about it is wrong. Believing otherwise is what #1267 records being shipped as fact,
    /// and it made "open every reader delete-tolerant" look like a fix for rename contention.
    /// <para>
    /// The no-holder arm is the control, and it is not decoration: without it, a harness that could
    /// never move a file would report both share modes failing and read as a confirmation.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_replace_loses_to_an_open_handle_whatever_share_mode_it_used()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX renames over an open handle regardless of share mode; there is nothing to discriminate.");
        }

        var directory = Path.Combine(Path.GetTempPath(), $"bindings-share-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            Assert.False(TryReplaceWhileHeld(directory, FileShare.ReadWrite | FileShare.Delete));
            Assert.False(TryReplaceWhileHeld(directory, FileShare.Read));
            Assert.True(
                TryReplaceWhileHeld(directory, share: null),
                "the replace failed with no holder at all, so this measures the harness rather than sharing");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>Stages a file and replaces <paramref name="share"/>-held target; true when the move landed.</summary>
    private static bool TryReplaceWhileHeld(string directory, FileShare? share)
    {
        var target = Path.Combine(directory, $"bindings-{Guid.NewGuid():N}.json");
        File.WriteAllText(target, "{}");
        var staging = target + ".tmp";
        File.WriteAllText(staging, "{}");

        var holder = share is { } s ? new FileStream(target, FileMode.Open, FileAccess.Read, s) : null;
        try
        {
            File.Move(staging, target, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            holder?.Dispose();
        }
    }

    /// <summary>
    /// #1266: the wall-clock budget survives a holder that the attempt-count budget it replaced could
    /// not. The mirror of <c>SnapshotBinderTests</c>'s arm for the same switch — without it this
    /// writer's fix rests on a sibling's measurement rather than its own, which is the analogy the
    /// second reader declined to accept.
    /// </summary>
    [Fact]
    public async Task A_transient_holder_outlasting_the_old_attempt_count_budget_no_longer_fails_the_write()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Only on Windows does a default-share reader block the replace at all.");
        }

        var directory = Path.Combine(Path.GetTempPath(), $"bindings-hold-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "bindings.json");
        await WorkerBindingConfigWriter.SaveToFileAsync(TwoWorkerConfig(), path, TestContext.Current.CancellationToken);

        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            // 30s injected rather than the production default: the claim is that the retry is still
            // running when the holder releases, and the wide margin keeps the test deterministic even
            // if its own 400ms pause is starved under load. The old budget was ~200ms of backoff.
            var save = WorkerBindingConfigWriter.SaveToFileAsync(
                TwoWorkerConfig(), path, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            // wait-ok: holding past the retired budget, not waiting for a result.
            await Task.Delay(400, TestContext.Current.CancellationToken);
            holder.Dispose();

            await save; // must NOT throw — the attempt-count budget would have given up by now.

            Assert.Equal(2, WorkerBindingConfigParser.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Count);
        }
        finally
        {
            holder.Dispose();
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// #1266: a replace that can never succeed surfaces rather than retrying forever, and takes its
    /// staging file with it. The budget is injected tiny so the exhaustion path runs immediately
    /// instead of burning the production five seconds on a failure that will never clear.
    /// </summary>
    /// <remarks>
    /// A directory at the destination is a permanent failure that needs no second process to
    /// manufacture. It surfaces as <see cref="IOException"/> on some platforms and
    /// <see cref="UnauthorizedAccessException"/> on others — the same pair the retry filter catches,
    /// which is exactly why this arm exists: those two types must not become unfailable just because
    /// the writer retries them.
    /// </remarks>
    [Fact]
    public async Task A_replace_that_can_never_succeed_surfaces_and_leaves_no_staging_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bindings-exhaust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "bindings.json");
        Directory.CreateDirectory(path);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => WorkerBindingConfigWriter.SaveToFileAsync(
                    TwoWorkerConfig(), path, TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken));

            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// 0057 rule 1 (#1264): a reader racing a write sees one whole register or another, never a
    /// half. What this pins is the atomicity property; the crash case that makes it matter — a
    /// process killed mid-write, which no lock covers — is not reproducible in-process, so this
    /// stands in for it rather than covering it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Parses what it reads rather than measuring bytes: the claim is about what a *consumer* sees,
    /// and a truncated file is only a defect because it fails to parse. A length assertion would pass
    /// against a write that landed valid JSON in two visible steps.
    /// </para>
    /// <para>
    /// <b>It goes red against a truncate-write on both platforms, for two different reasons</b>, and
    /// the difference is the reason 0057's retry exists. On POSIX a reader does not block a writer, so
    /// the reader genuinely observes a truncated file and the parse fails — the defect, directly. On
    /// Windows the reader's handle makes the writer's own open fail with a sharing violation, so the
    /// truncate-write never gets far enough to tear anything and the test reddens on the write
    /// instead. Same verdict, different mechanism; claiming one red proof for both would be claiming
    /// more than was measured.
    /// </para>
    /// <para>
    /// <b>Scoped to what was actually re-measured:</b> the Windows red above was re-run against
    /// *this* construction (#1266). The POSIX red was measured against the previous one, before the
    /// reader gained its gap — and the gap is precisely what could cost that arm its observability,
    /// which is why the payload grew in the same change. Treat the POSIX arm as inherited rather than
    /// re-proven until someone runs it there.
    /// </para>
    /// <para>
    /// <b>The overlap is established, not hoped for (#1266).</b> The write loop does not start until
    /// the reader has completed one read-and-parse, because <c>Task.Run</c> having been called is not
    /// evidence the reader has been scheduled: on a loaded macOS runner all 60 writes finished inside
    /// 116ms before the reader got a slot, and the premise assertion below correctly failed the run
    /// rather than reporting a vacuous zero. The premise assertion stays anyway — a handshake proves
    /// the reader started, not that it kept going.
    /// </para>
    /// <para>
    /// <b>The reader pauses between reads, and the payload is large, and both are load-bearing.</b>
    /// A zero-gap reopen loop is not a consumer this product has — a read is one
    /// <c>LoadFromFileAsync</c> per operation — and on Windows it manufactures the one failure this
    /// test explicitly disclaims two paragraphs down: an open handle blocks the replace, so a reader
    /// that never lets go starves the writer's retry budget instead of measuring anything (#1266
    /// again, this time on Windows under full-gates load). The gap costs observability of the tear it
    /// hunts, which is what the large payload buys back: <see cref="TwoWorkerConfig"/> is written in
    /// so few bytes that a torn read is nearly unobservable through a 3ms gap. Shrink the payload and
    /// this test stops being able to fail. <c>SnapshotBinderTests</c> learned the same thing and
    /// widened its own window the same way.
    /// </para>
    /// <para>
    /// <b>Its green is not the evidence; the recorded red is.</b> Racy observation is inherent to the
    /// claim — lockstep the two and every read sees a completed write, so a truncate-writer would pass
    /// and the test would discriminate nothing; race them freely and the scheduler is in play both
    /// ways. So a passing run here means "no regression observed", never "atomicity proven", and the
    /// proof is the red run above, taken against this construction rather than the one it replaced.
    /// Treat a red as real: do not wave off a CI failure here as "just timing" without first checking
    /// whether it is this scenario in reverse.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_reader_racing_a_write_never_catches_a_half_written_register()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bindings-atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "bindings.json");

        try
        {
            await WorkerBindingConfigWriter.SaveToFileAsync(WideConfig(), path, TestContext.Current.CancellationToken);

            var torn = 0;
            var reads = 0;
            var readerIsLive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stop = new CancellationTokenSource();

            var reader = Task.Run(
                async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    string? json = null;
                    try
                    {
                        json = File.ReadAllText(path);
                    }
                    catch (IOException)
                    {
                        // The window this test exists to measure is a reader seeing WRONG CONTENT.
                        // Not being able to open the file at all during a replace is the sharing
                        // behaviour the daemon's read guard exists for (0057's Consequences) — a
                        // different fact, and not this one's to fail on.
                        //
                        // Deliberately NOT a `continue`: these failures cluster around the writer's
                        // replace attempts, so skipping the pause here would busy-loop the reader at
                        // exactly the moment the writer needs a gap to land in — reinstating the
                        // starvation this construction exists to remove, on the one path where it
                        // does the most damage.
                    }

                    if (json != null)
                    {
                        reads++;
                        try
                        {
                            WorkerBindingConfigParser.Parse(json);
                        }
                        catch (WorkerBindingConfigException)
                        {
                            Interlocked.Increment(ref torn);
                        }

                        readerIsLive.TrySetResult();
                    }

                    try
                    {
                        // wait-ok: shapes the reader, times out nothing — see the remarks.
                        await Task.Delay(3, stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            },
                TestContext.Current.CancellationToken);

            await readerIsLive.Task;

            for (var i = 0; i < 60; i++)
            {
                await WorkerBindingConfigWriter.SaveToFileAsync(WideConfig(), path, TestContext.Current.CancellationToken);
            }

            await stop.CancelAsync();
            await reader;

            // The premise, asserted rather than assumed: a reader that never got a look at the file
            // would report zero torn reads whatever the writer did.
            Assert.True(reads > 0, "the reader never observed the file, so it discriminates nothing");
            Assert.Equal(0, torn);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }
}
