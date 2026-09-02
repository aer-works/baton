using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Store;
using Baton.Templates;
using Xunit;

namespace Baton.Cli.Tests;

public class CapturedWorkerStreamTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public void Reservation_DotPrefixProducedOutput_IsRejected()
    {
        // 1. ProducedOutput constructor
        var exConst = Assert.Throws<ArgumentException>(() => new ProducedOutput(".stdout.log"));
        Assert.Contains(".stdout.log", exConst.Message);
        // #1345: asserted against the shared clause rather than a copy of its words — the point of
        // ReservedOutputNames is that all four rejection sites say one thing, and a test spelling the
        // sentence out again is a fourth copy that can drift from the three it is guarding.
        Assert.Contains(ReservedOutputNames.RejectionClause, exConst.Message);

        // 2. WorkerBindingConfigParser
        var invalidJson = """
        {
          "worker": {
            "Adapter": "shell",
            "Contract": {
              "WorkerName": "worker",
              "RequiredInputs": [],
              "ProducedOutputs": [{ "Name": ".stderr.log" }],
              "OptionalMetadata": []
            },
            "PromptTemplate": "echo test",
            "Timeout": "00:01:00"
          }
        }
        """;
        var exConfig = Assert.Throws<WorkerBindingConfigException>(() => WorkerBindingConfigParser.Parse(invalidJson));
        Assert.Contains(".stderr.log", exConfig.Message);
        Assert.Contains(ReservedOutputNames.RejectionClause, exConfig.Message);

        // 3. WorkflowDefinitionValidator
        var invalidDef = new WorkflowDefinition(
            new WorkflowTemplateId("dot-output-test"),
            1,
            [new WorkflowStepDefinition(new StepId("step1"), "worker", [], [".stdout.log"], [], new RetryPolicy(1))]);

        var exDef = Assert.Throws<WorkflowDefinitionValidationException>(() => WorkflowDefinitionValidator.Validate(invalidDef));
        Assert.Contains(".stdout.log", exDef.Errors[0]);
        Assert.Contains(ReservedOutputNames.RejectionClause, exDef.Errors[0]);

        // Polarity: Normal name still validates
        var validOutput = new ProducedOutput("plan.md");
        Assert.Equal("plan.md", validOutput.Name);

        var validDef = new WorkflowDefinition(
            new WorkflowTemplateId("valid-output-test"),
            1,
            [new WorkflowStepDefinition(new StepId("step1"), "worker", [], ["plan.md"], [], new RetryPolicy(1))]);
        WorkflowDefinitionValidator.Validate(validDef);
    }

    [Fact]
    public void Immutability_AppendAfterTerminal_RefusedBySection16Mechanism()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"stream-immutability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var logger = new ExecutionStreamLogger(tempDir);
            logger.AppendStdout("chunk 1\n"u8.ToArray());
            Assert.False(logger.IsTerminal);

            logger.MarkTerminal();
            Assert.True(logger.IsTerminal);

            var exStdout = Assert.Throws<InvalidOperationException>(() => logger.AppendStdout("chunk 2\n"u8.ToArray()));
            Assert.Contains("terminal event", exStdout.Message);

            var exStderr = Assert.Throws<InvalidOperationException>(() => logger.AppendStderr("chunk 2\n"u8.ToArray()));
            Assert.Contains("terminal event", exStderr.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    /// <summary>
    /// #1525 Phase 2 regression test -- fails on main. Root cause (Phase 1): a claude role dispatch
    /// runs <c>--output-format text</c> (<c>RoleDispatch.cs:155</c> only streams <c>agy</c>), and
    /// claude's text mode writes nothing to stdout until the entire response is composed, so
    /// <c>AppendChunk</c> was never called -- and on main, <c>.stdout.log</c> is only created lazily,
    /// inside <c>AppendChunk</c>, on the first successful write. Measured live: 50/51 real dispatch
    /// rooms on this machine had the file, every completed one; the sole exception was this very
    /// task's own room, still running. See <see cref="ExecutionStreamLogger"/>'s constructor comment
    /// for why that gap matters operationally -- not restated here. This does not require a real
    /// vendor process to reproduce: the file's absence is a property of <see cref="ExecutionStreamLogger"/>
    /// alone, independent of what (if anything) ever calls <see cref="ExecutionStreamLogger.AppendStdout"/>.
    /// </summary>
    [Fact]
    public void Construction_CreatesBothStreamFilesEagerly_BeforeAnyChunkArrives()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"stream-eager-create-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var stdoutPath = Path.Combine(tempDir, ExecutionStreamLogger.StdoutLogFileName);
            var stderrPath = Path.Combine(tempDir, ExecutionStreamLogger.StderrLogFileName);
            Assert.False(File.Exists(stdoutPath));
            Assert.False(File.Exists(stderrPath));

            _ = new ExecutionStreamLogger(tempDir);

            // Present, and empty -- the honest state before a slow/buffered worker's first chunk
            // arrives, in place of main's "does not exist at all".
            Assert.True(File.Exists(stdoutPath));
            Assert.True(File.Exists(stderrPath));
            Assert.Equal(0, new FileInfo(stdoutPath).Length);
            Assert.Equal(0, new FileInfo(stderrPath).Length);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public void Rollover_CrossesCap_CreatesRolloverFileAndFreshFile()
    {
        // Seam used: ExecutionStreamLogger with a reduced cap of 100 bytes
        var tempDir = Path.Combine(Path.GetTempPath(), $"stream-rollover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            const long cap = 100;
            var logger = new ExecutionStreamLogger(tempDir, maxSizeBytes: cap);

            var stdoutPath = Path.Combine(tempDir, ExecutionStreamLogger.StdoutLogFileName);
            var stdoutRolloverPath = Path.Combine(tempDir, ExecutionStreamLogger.StdoutRolloverFileName);

            // Chunk 1: 60 bytes
            var chunk1 = new byte[60];
            Array.Fill(chunk1, (byte)'A');
            logger.AppendStdout(chunk1);

            Assert.True(File.Exists(stdoutPath));
            Assert.False(File.Exists(stdoutRolloverPath));
            Assert.Equal(60, new FileInfo(stdoutPath).Length);

            // Chunk 2: 60 bytes (60 + 60 = 120 > 100 cap -> rollover!)
            var chunk2 = new byte[60];
            Array.Fill(chunk2, (byte)'B');
            logger.AppendStdout(chunk2);

            Assert.True(File.Exists(stdoutPath));
            Assert.True(File.Exists(stdoutRolloverPath));
            Assert.Equal(60, new FileInfo(stdoutRolloverPath).Length);
            Assert.Equal(60, new FileInfo(stdoutPath).Length);
            Assert.Equal(chunk1, File.ReadAllBytes(stdoutRolloverPath));
            Assert.Equal(chunk2, File.ReadAllBytes(stdoutPath));

            // Chunk 3: 60 bytes -> rollover again!
            var chunk3 = new byte[60];
            Array.Fill(chunk3, (byte)'C');
            logger.AppendStdout(chunk3);

            Assert.Equal(60, new FileInfo(stdoutRolloverPath).Length);
            Assert.Equal(60, new FileInfo(stdoutPath).Length);
            Assert.Equal(chunk2, File.ReadAllBytes(stdoutRolloverPath));
            Assert.Equal(chunk3, File.ReadAllBytes(stdoutPath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public void Rollover_MarksTheStreamTruncatedOnlyOnceASegmentIsActuallyDiscarded()
    {
        // #1706 review M3. chunk1 survives a single rollover (it is still `.stdout.log.1`), so a reader
        // can replay the whole stream and there is nothing to mark; the SECOND rollover overwrites it.
        // `ExecutionStreamLogger.StdoutTruncationMarkerFileName`'s own doc has why the marker exists and
        // who reads it -- this pins WHEN it is written, which is the half a reader cannot verify.
        var tempDir = Path.Combine(Path.GetTempPath(), $"stream-truncation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var logger = new ExecutionStreamLogger(tempDir, maxSizeBytes: 100);
            var markerPath = Path.Combine(tempDir, ExecutionStreamLogger.StdoutTruncationMarkerFileName);

            static byte[] Chunk(char fill)
            {
                var chunk = new byte[60];
                Array.Fill(chunk, (byte)fill);
                return chunk;
            }

            logger.AppendStdout(Chunk('A'));
            Assert.False(File.Exists(markerPath));

            // First rollover: `.stdout.log.1` now holds chunk A. Nothing lost, so no marker -- the
            // polarity arm without which "always mark" would pass the assertion below.
            logger.AppendStdout(Chunk('B'));
            Assert.True(File.Exists(Path.Combine(tempDir, ExecutionStreamLogger.StdoutRolloverFileName)));
            Assert.False(File.Exists(markerPath));

            // Second rollover: chunk A is overwritten and gone.
            logger.AppendStdout(Chunk('C'));
            Assert.True(File.Exists(markerPath));
            Assert.Equal(0, new FileInfo(markerPath).Length);

            // stderr rolled zero times and must not be marked by stdout's loss -- the streams are
            // counted independently.
            Assert.False(File.Exists(Path.Combine(tempDir, ExecutionStreamLogger.StderrTruncationMarkerFileName)));

            // And the marker is one of this logger's own files, so nothing enumerating the output
            // directory presents it to an operator as a worker deliverable (#1345).
            Assert.True(ExecutionStreamLogger.IsStreamLogFileName(ExecutionStreamLogger.StdoutTruncationMarkerFileName));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public async Task RoundTrip_And_RenderEscaping_BothPolarities()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"stream-roundtrip-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);

            // Polarity 1: Real worker process emitting known bytes INCLUDING ANSI escape sequences + non-UTF-8 bytes
            var expectedRawBytes = new byte[]
            {
                0x1B, 0x5B, 0x33, 0x31, 0x6D, 0x52, 0x65, 0x64, 0x1B, 0x5B, 0x30, 0x6D, 0x0A, 0x80, 0x0A
            };

            var definition = new WorkflowDefinition(
                new WorkflowTemplateId("roundtrip-flow"),
                1,
                [new WorkflowStepDefinition(new StepId("worker"), "worker", [], ["out.txt"], [], new RetryPolicy(1))]);

            // printf, not echo: C# has no octal escapes, so a "\033" literal is NUL + "33" -- the
            // embedded NUL truncated the spawned command line and no stream file was ever written
            // (caught by this test's first Linux CI run). "\\033" hands printf a literal backslash
            // sequence, and printf -- unlike POSIX echo -- is required to interpret it as ESC.
            var cmdLine = OperatingSystem.IsWindows()
                ? "powershell -NoProfile -EncodedCommand VwByAGkAdABlAC0ATwB1AHQAcAB1AHQAIAAoAFsAYwBoAGEAcgBdADIANwAgACsAIAAnAFsAMwAxAG0AUgBlAGQAJwAgACsAIABbAGMAaABhAHIAXQAyADcAIAArACAAJwBbADAAbQAnACkA & echo Red > %BATON_OUTPUT_DIR%\\out.txt"
                : "printf '\\033[31mRed\\033[0m\\n' && echo Red > \"$BATON_OUTPUT_DIR/out.txt\"";

            // #945: this budget only bounds how long the test waits for a trivial echo/printf, never
            // a behaviour this test verifies (round-trip byte capture + render escaping) -- unlike a
            // wait this repo's v-and-v gate rightly protects, widening it trades no coverage away.
            // Measured before widening, not guessed: the real subprocess itself completes in ~200ms
            // unloaded and stays under ~4s even pinned to 2 cores against six competing CPU-bound
            // processes (matching windows-latest's vCPU count); a second hypothesis -- BatonTask.RunAsync's
            // Task.Run has no TaskCreationOptions.LongRunning, so ThreadPool starvation could queue a
            // late dispatch -- also failed to reproduce (a 40-way flood against MinThreads pinned to 2
            // still started a new dispatch in ~0ms; modern .NET grows the pool fast enough under a large
            // backlog). Neither measured mechanism explains the two observed 30-31s CI failures, which is
            // itself the finding: something specific to the live runner (CPU steal, I/O contention) that
            // is not reproducible on a dev host. 90s keeps a real hang bounded and unmistakable while
            // absorbing CI noise this repo can measure but not locally explain.
            var bindings = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["worker"] = new WorkerBindingConfigEntry(
                    "shell",
                    new WorkerContract("worker", [], [new ProducedOutput("out.txt")], []),
                    cmdLine,
                    TimeSpan.FromSeconds(90))
            };

            var workflowFile = Path.Combine(testRoot, "workflow.json");
            var bindingsFile = Path.Combine(testRoot, "bindings.json");
            await File.WriteAllTextAsync(workflowFile, JsonSerializer.Serialize(definition), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(bindingsFile, JsonSerializer.Serialize(bindings), TestContext.Current.CancellationToken);

            var runOptions = new RunOptions(workflowFile, bindingsFile, roomDirectory);
            var runResult = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            // Succeeded, not just Terminal: a failed run is also Terminal, and that weaker assert
            // let the broken command line above fall through to a misleading missing-file failure.
            Assert.Equal(WorkflowStatus.Terminal, runResult.State.Status);
            Assert.Equal(StepStatus.Succeeded, runResult.State.Steps[0].Status);

            var execId = runResult.State.Steps[0].LatestExecutionId!.Value.Value;
            var execDir = Path.Combine(roomDirectory, "artifacts", $"execution_{execId}");
            var stdoutFile = Path.Combine(execDir, ExecutionStreamLogger.StdoutLogFileName);

            Assert.True(File.Exists(stdoutFile), $"Expected stream log file at {stdoutFile}");

            var stdoutContent = File.ReadAllBytes(stdoutFile);
            Assert.NotEmpty(stdoutContent);

            // Render with StatusCommand --follow and verify neutralized output
            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory, Follow: true), output, TestContext.Current.CancellationToken);
            var statusText = output.ToString();

            Assert.Contains("Workflow status: Terminal", statusText);
            Assert.Contains("\\x1b[31mRed\\x1b[0m", statusText);

            // Polarity 2: Normal printable text from a real worker process
            var roomDirectory2 = Path.Combine(testRoot, "task2");
            var cmdLineNormal = OperatingSystem.IsWindows()
                ? "powershell -NoProfile -Command \"Write-Output 'Normal text'\" & echo Red > %BATON_OUTPUT_DIR%\\out.txt"
                : "echo 'Normal text' && echo Red > \"$BATON_OUTPUT_DIR/out.txt\"";

            var bindings2 = new Dictionary<string, WorkerBindingConfigEntry>
            {
                // #945: same widened, measured-not-guessed budget as polarity 1 above.
                ["worker"] = new WorkerBindingConfigEntry(
                    "shell",
                    new WorkerContract("worker", [], [new ProducedOutput("out.txt")], []),
                    cmdLineNormal,
                    TimeSpan.FromSeconds(90))
            };

            var bindingsFile2 = Path.Combine(testRoot, "bindings2.json");
            await File.WriteAllTextAsync(bindingsFile2, JsonSerializer.Serialize(bindings2), TestContext.Current.CancellationToken);

            var runOptions2 = new RunOptions(workflowFile, bindingsFile2, roomDirectory2);
            var runResult2 = await RunCommand.ExecuteAsync(runOptions2, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, runResult2.State.Status);
            Assert.Equal(StepStatus.Succeeded, runResult2.State.Steps[0].Status);

            var execId2 = runResult2.State.Steps[0].LatestExecutionId!.Value.Value;
            var execDir2 = Path.Combine(roomDirectory2, "artifacts", $"execution_{execId2}");
            var stdoutFile2 = Path.Combine(execDir2, ExecutionStreamLogger.StdoutLogFileName);

            Assert.True(File.Exists(stdoutFile2), $"Expected stream log file at {stdoutFile2}");

            var stdoutContent2 = File.ReadAllBytes(stdoutFile2);
            Assert.NotEmpty(stdoutContent2);

            var output2 = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory2, Follow: true), output2, TestContext.Current.CancellationToken);
            var statusText2 = output2.ToString();

            Assert.Contains("Normal text", statusText2);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void EscapeNonPrintable_MultiByteUtf8_DecodesCleanly_NoSpuriousEscapes()
    {
        // The lane review's high finding: a buffered multi-byte lead byte was escaped as \xNN AND
        // later decoded, so every non-ASCII character rendered twice. Three arms, one condition
        // apart: clean multi-byte in, escape-only for a real control byte, replacement for a
        // sequence truncated at end-of-input.
        var clean = StatusCommand.EscapeNonPrintable(System.Text.Encoding.UTF8.GetBytes("café ☕"));
        Assert.Equal("café ☕", clean);
        Assert.DoesNotContain("\\x", clean);

        var withEsc = StatusCommand.EscapeNonPrintable(new byte[] { (byte)'A', 0x1b, (byte)'B' });
        Assert.Equal("A\\x1bB", withEsc);

        var truncated = StatusCommand.EscapeNonPrintable(new byte[] { (byte)'A', 0xC3 });
        Assert.Equal("A�", truncated);
    }

    [Fact]
    public void TailStreams_AcrossRollover_DeliversContinuedContent()
    {
        // The lane review's medium finding: the reader-side rollover branch had zero coverage --
        // only the writer half was asserted. This drives StatusCommand.TailStreams across a real
        // rollover and requires the tail to keep delivering without dropping the post-rollover
        // content.
        var testRoot = Path.Combine(Path.GetTempPath(), $"tail-rollover-{Guid.NewGuid():N}");
        var execDir = Path.Combine(testRoot, "execution_tail");
        Directory.CreateDirectory(execDir);
        try
        {
            const long cap = 100;
            var logger = new ExecutionStreamLogger(execDir, maxSizeBytes: cap);
            var offsets = new Dictionary<string, long>(StringComparer.Ordinal);

            var chunkA = System.Text.Encoding.UTF8.GetBytes(new string('A', 60));
            logger.AppendStdout(chunkA);

            var firstRead = new StringWriter();
            StatusCommand.TailStreams(firstRead, testRoot, offsets);
            Assert.Contains(new string('A', 60), firstRead.ToString());

            // Crossing the cap rolls the file; the next tail must surface the new content.
            var chunkB = System.Text.Encoding.UTF8.GetBytes(new string('B', 60));
            logger.AppendStdout(chunkB);

            var secondRead = new StringWriter();
            StatusCommand.TailStreams(secondRead, testRoot, offsets);
            var secondText = secondRead.ToString();
            Assert.Contains(new string('B', 60), secondText);
            Assert.DoesNotContain(new string('A', 60), secondText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void FailureIsolation_WhenWriteFails_DoesNotThrowAndKeepsRetrying()
    {
        // #1525 F4: renamed from "...DisablesFurtherWrites" -- that used to be true (the latch was
        // permanent and cross-stream) and no longer is. A single obstructed write must not throw, must
        // not blind the OTHER stream, and must not stop a LATER write on the same stream from
        // succeeding once the obstruction is gone -- per-chunk open/close means a failed chunk leaves
        // no state for the next one to inherit.
        var tempDir = Path.Combine(Path.GetTempPath(), $"stream-failure-isolation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var stdoutObstruction = Path.Combine(tempDir, ExecutionStreamLogger.StdoutLogFileName);

            // Constructing the logger eager-creates both stream files (#1525) -- remove the stdout one
            // and replace it with a directory so the FIRST AppendStdout's FileStream open fails.
            var logger = new ExecutionStreamLogger(tempDir);
            FileCleanup.EnsureDeleted(stdoutObstruction);
            Directory.CreateDirectory(stdoutObstruction);

            // Obstructed stdout must not throw...
            logger.AppendStdout("first chunk\n"u8.ToArray());
            // ...must not blind stderr, which was never obstructed...
            logger.AppendStderr("stderr chunk\n"u8.ToArray());
            var stderrText = File.ReadAllText(Path.Combine(tempDir, ExecutionStreamLogger.StderrLogFileName));
            Assert.Contains("stderr chunk", stderrText);

            // ...and must not permanently disable stdout either: once the obstruction is cleared, the
            // next chunk succeeds normally.
            Directory.Delete(stdoutObstruction);
            logger.AppendStdout("recovered chunk\n"u8.ToArray());
            var stdoutText = File.ReadAllText(stdoutObstruction);
            Assert.Contains("recovered chunk", stdoutText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    /// <summary>
    /// #1525 F3: renamed from "FailureIsolation_InCoreDispatcher_WhenStreamLoggerFails_ExecutionStillSucceeds"
    /// -- nothing in this body sabotages the logger, so it never exercised failure isolation and would
    /// pass unchanged on a version of <c>main</c> with none of it. It is a happy-path end-to-end proof
    /// that the tee reaches the room through the real <c>RunCommand</c>/<c>CoreDispatcher</c> pump, not
    /// a unit-level fake. The actual fault-injected coverage for this class of bug lives in
    /// <see cref="FailureIsolation_WhenWriteFails_DoesNotThrowAndKeepsRetrying"/> (this file, unit
    /// level) and <c>Baton.Tests.Dispatch.CoreDispatcherTests.DispatchAsync_when_stream_logger_fails_execution_still_succeeds</c>
    /// (Core level, which sabotages a deterministically-known output directory before dispatch --
    /// something this CLI-level test cannot do because the execution ID, and therefore the output
    /// directory, is not known until the engine allocates it during the run).
    /// </summary>
    [Fact]
    public async Task EndToEnd_ViaRunCommand_StreamLoggerWritesRealStdoutIntoTheRoom()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"stream-dispatch-happy-path-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);

            var definition = new WorkflowDefinition(
                new WorkflowTemplateId("stream-failure-flow"),
                1,
                [new WorkflowStepDefinition(new StepId("worker"), "worker", [], ["out.txt"], [], new RetryPolicy(1))]);

            var cmdLine = OperatingSystem.IsWindows()
                ? "powershell -NoProfile -Command \"Write-Output 'Hello from failing-logger worker'\" & echo done > %BATON_OUTPUT_DIR%\\out.txt"
                : "echo 'Hello from failing-logger worker' && echo done > \"$BATON_OUTPUT_DIR/out.txt\"";

            var bindings = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["worker"] = new WorkerBindingConfigEntry(
                    "shell",
                    new WorkerContract("worker", [], [new ProducedOutput("out.txt")], []),
                    cmdLine,
                    TimeSpan.FromSeconds(90))
            };

            var workflowFile = Path.Combine(testRoot, "workflow.json");
            var bindingsFile = Path.Combine(testRoot, "bindings.json");
            await File.WriteAllTextAsync(workflowFile, JsonSerializer.Serialize(definition), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(bindingsFile, JsonSerializer.Serialize(bindings), TestContext.Current.CancellationToken);

            var runOptions = new RunOptions(workflowFile, bindingsFile, roomDirectory);
            var runResult = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, runResult.State.Status);
            Assert.Equal(StepStatus.Succeeded, runResult.State.Steps[0].Status);

            var execId = runResult.State.Steps[0].LatestExecutionId!.Value.Value;
            var execDir = Path.Combine(roomDirectory, "artifacts", $"execution_{execId}");
            var stdoutFile = Path.Combine(execDir, ExecutionStreamLogger.StdoutLogFileName);
            Assert.True(File.Exists(stdoutFile));
            var text = await File.ReadAllTextAsync(stdoutFile, TestContext.Current.CancellationToken);
            Assert.Contains("Hello from failing-logger worker", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task CancelledExecution_PersistsStdoutEmittedBeforeCancellation()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"stream-cancel-persist-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);

            var definition = new WorkflowDefinition(
                new WorkflowTemplateId("cancel-persist-flow"),
                1,
                [new WorkflowStepDefinition(new StepId("worker"), "worker", [], ["out.txt"], [], new RetryPolicy(1))]);

            // #1550: the Windows branch used to invoke `powershell -NoProfile -Command "Write-Output
            // '...'; Start-Sleep -Seconds 30"`. That never actually ran as a script. .NET's
            // ArgumentList escapes the embedded quotes as `\"`; cmd.exe has no concept of
            // backslash-escaped quotes, so its `/c` handling strips only the first/last quote of the
            // whole argument and leaves both `\"` sequences intact; and powershell.exe's own
            // CommandLineToArgvW-style argv parsing then treats each `\"` as a literal quote character
            // rather than a region delimiter, so `-Command` collected the remaining tokens as a bare
            // *string literal* (`"Write-Output '...'; Start-Sleep -Seconds 30"`), which PowerShell just
            // evaluates and prints verbatim. Measured directly (a throwaway diagnostic reproducing the
            // exact ArgumentList invocation): exits in ~150ms, stdout is the raw script source text,
            // Start-Sleep never runs, and `echo done > out.txt` fires almost immediately afterward. The
            // test's own assertion happened to still pass by accident -- the misparsed stdout literally
            // contains the substring "pre-cancel output line" as part of the echoed script source --
            // but nothing about cancellation-in-flight was ever exercised: the "cancelled" run had
            // already finished on its own well before cts.Cancel() fired.
            //
            // Replaced with a quote-free command line: no `"` characters anywhere, so .NET's single
            // outer quote pair (added because the string contains spaces) is the only pair cmd.exe's
            // `/c` first/last-quote-strip rule ever sees, and `ping` stands in for the long-running
            // step instead of Start-Sleep -- `timeout` was tried and rejected, since it fails with
            // "input redirection is not supported" because BatonProcessRunner.cs closes the child's
            // redirected stdin.
            // #1547: the wait below must outlast MarkerWaitSeconds (see below) by a wide margin.
            // Cancellation job-terminates the whole tree the instant cts.Cancel() fires, so a wait
            // this long costs nothing on the passing path -- it only guards against the marker-wait
            // ever taking so long that the child would have finished naturally and written out.txt on
            // its own, which is exactly what the #1550 discriminator above checks for.
            var cmdLine = OperatingSystem.IsWindows()
                ? "echo pre-cancel output line & ping -n 301 127.0.0.1 > nul & echo done > %BATON_OUTPUT_DIR%\\out.txt"
                : "echo 'pre-cancel output line' && sleep 300 && echo done > \"$BATON_OUTPUT_DIR/out.txt\"";

            var bindings = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["worker"] = new WorkerBindingConfigEntry(
                    "shell",
                    new WorkerContract("worker", [], [new ProducedOutput("out.txt")], []),
                    cmdLine,
                    TimeSpan.FromSeconds(400))
            };

            var workflowFile = Path.Combine(testRoot, "workflow.json");
            var bindingsFile = Path.Combine(testRoot, "bindings.json");
            await File.WriteAllTextAsync(workflowFile, JsonSerializer.Serialize(definition), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(bindingsFile, JsonSerializer.Serialize(bindings), TestContext.Current.CancellationToken);

            // #1525 F8: was cts.CancelAfter(1500ms) -- a cold `powershell -NoProfile` start on a
            // loaded box regularly exceeds 1.5s, which would kill the child before it ever wrote the
            // marker line, failing this test for a reason that has nothing to do with cancellation
            // persistence. Wait on the marker instead: poll the execution directory this run allocates
            // until its (now eagerly-created, #1525) .stdout.log actually contains the pre-cancel line,
            // then cancel. Bounded so a genuine regression still fails the test instead of hanging it.
            //
            // #1547: 20s wasn't enough -- main run 33458670493 failed this exact NotNull assert at ~21s
            // under CI load (windows-shard-other, no spawn-failure or stream-logger warning in the log,
            // just the bare timeout). Refuted as a product-side ordering gap by the failure location
            // alone: this NotNull fires strictly before cts.Cancel() below, so no cancel-path code has
            // run yet when it fails -- corroborated by BatonProcessRunner.cs's RunWithLiveCapture,
            // which drains every chunk through its live foreach and only raises Exited after that loop
            // returns, so CoreDispatcher.cs can never observe MarkTerminal before every AppendStdout for
            // a given process. With #1550's quote-free command line fixed, a 20-iteration local
            // measurement of this same marker-wait put every run at 246-356ms -- tight, sub-second,
            // nowhere near 20s -- so 60s is a headroom judgment over the observed >20s CI outlier and
            // that local baseline, not a second measurement of the CI tail itself, which isn't
            // reproducible on a dev host (see the #945 comment elsewhere in this file on the same
            // theme). A genuine regression in stdout persistence still fails this test well inside 60s;
            // only the CI-load tail needed the room.
            const int MarkerWaitSeconds = 60;
            using var cts = new CancellationTokenSource();
            var runOptions = new RunOptions(workflowFile, bindingsFile, roomDirectory);
            var runTask = RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: cts.Token);

            var artifactsDir = Path.Combine(roomDirectory, "artifacts");
            var markerDeadline = DateTime.UtcNow.AddSeconds(MarkerWaitSeconds);
            string? stdoutFileWithMarker = null;
            while (DateTime.UtcNow < markerDeadline && stdoutFileWithMarker is null)
            {
                if (Directory.Exists(artifactsDir))
                {
                    foreach (var execDir in Directory.GetDirectories(artifactsDir, "execution_*"))
                    {
                        var candidate = Path.Combine(execDir, ExecutionStreamLogger.StdoutLogFileName);
                        if (File.Exists(candidate))
                        {
                            string pollContent;
                            try
                            {
                                pollContent = File.ReadAllText(candidate);
                            }
                            catch (IOException)
                            {
                                continue; // writer holds the handle mid-flush; try again next poll
                            }

                            if (pollContent.Contains("pre-cancel output line"))
                            {
                                stdoutFileWithMarker = candidate;
                                break;
                            }
                        }
                    }
                }

                if (stdoutFileWithMarker is null)
                {
                    // Poll interval inside a real-condition loop bounded by markerDeadline above, not a
                    // fixed sleep standing in for synchronization -- the loop exits the instant the
                    // marker text appears, so 50ms only bounds how late the test notices.
                    // wait-ok: poll interval, not a synchronization sleep -- see markerDeadline above
                    await Task.Delay(50, TestContext.Current.CancellationToken);
                }
            }

            Assert.NotNull(stdoutFileWithMarker); // the marker never arrived -- nothing to cancel mid-stream
            cts.Cancel();

            try
            {
                await runTask;
            }
            catch (OperationCanceledException)
            {
                // Expected cancellation
            }

            Assert.True(Directory.Exists(artifactsDir));
            var execDirs = Directory.GetDirectories(artifactsDir, "execution_*");
            Assert.NotEmpty(execDirs);
            var stdoutFile = Path.Combine(execDirs[0], ExecutionStreamLogger.StdoutLogFileName);
            Assert.True(File.Exists(stdoutFile), $"Expected .stdout.log at {stdoutFile}");

            var content = await File.ReadAllTextAsync(stdoutFile, TestContext.Current.CancellationToken);
            Assert.Contains("pre-cancel output line", content);

            // #1550 discriminator: without this, the test above would pass identically whether or not
            // cancellation actually interrupted the worker -- exactly the failure mode the quoting bug
            // above produced silently. Job-termination on cancel kills the whole `cmd /c echo & ping &
            // echo` tree, so the trailing `echo done > out.txt` never runs; a worker that instead ran to
            // completion on its own would have written it.
            var outFile = Path.Combine(execDirs[0], "out.txt");
            Assert.False(File.Exists(outFile), $"out.txt exists at {outFile} -- the worker ran to completion instead of being cancelled");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1525 F6: the prior version of this test ("...SucceedsWithDeleteShare") opened the reader with
    /// <c>FileAccess.Read</c> and the writer per-chunk with <c>FileAccess.Write</c> -- neither side
    /// requests <c>FILE_SHARE_DELETE</c>'s counterpart (delete access), so it passed whether or not the
    /// <c>FileShare.Delete</c> flag was present on either open; it never exercised the flag at all.
    /// <para>
    /// What <c>FileShare.Delete</c> on the READER's open (<c>RoomDetailTool.cs</c>'s own share mode)
    /// actually buys: <see cref="ExecutionStreamLogger"/>'s 8 MiB rollover renames <c>.stdout.log</c>
    /// out from under a reader that may have it open for tailing (<c>RetryingFileMove.Move</c> inside
    /// <c>AppendChunk</c>). On Windows, a rename is a delete-class operation, and it only succeeds
    /// against a file another process has open if THAT process's open explicitly allowed delete
    /// sharing. This test exercises that directly, with the negative control the original lacked:
    /// the same move against a reader that did NOT request delete sharing must fail, proving the flag
    /// is what discriminates rather than some incidental race.
    /// </para>
    /// </summary>
    [Fact]
    public void RolloverMove_WhileReaderHoldsFileOpen_SucceedsOnlyWhenReaderSharesDelete()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"stream-concurrent-read-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var stdoutPath = Path.Combine(tempDir, ExecutionStreamLogger.StdoutLogFileName);
            File.WriteAllText(stdoutPath, "initial line\n");
            var movedPath = Path.Combine(tempDir, ExecutionStreamLogger.StdoutRolloverFileName);

            // Positive arm: reader opens with the same share mode RoomDetailTool.cs uses.
            using (var readerStream = new FileStream(stdoutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                RetryingFileMove.Move(stdoutPath, movedPath, overwrite: true);
                Assert.True(File.Exists(movedPath));
                Assert.False(File.Exists(stdoutPath));

                using var sr = new StreamReader(readerStream);
                Assert.Contains("initial line", sr.ReadToEnd());
            }

            // Negative control: without FileShare.Delete on the reader, the same move must fail --
            // this is what makes the positive arm above a real assertion about the flag, not a race.
            File.Move(movedPath, stdoutPath);
            using (var readerStream = new FileStream(stdoutPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.ThrowsAny<IOException>(() => File.Move(stdoutPath, movedPath));
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }
}
