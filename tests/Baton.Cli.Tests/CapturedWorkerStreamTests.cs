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
}
