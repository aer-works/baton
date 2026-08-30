using System.Reflection;
using Aer.Adapters;
using Aer.Cli;
using Aer.Flow;
using Aer.Flow.Domain;
using Aer.Flow.Status;
using Aer.Flow.Store;

if (args.Length == 1 && args[0] == "--version")
{
    Console.WriteLine(VersionInfo.GetVersion(Assembly.GetExecutingAssembly()));
    return 0;
}

// #543: the PreToolUse hook target ClaudeWorkerAdapter writes into claude-settings.json, spawned
// directly by Claude Code (exec form -- no shell) on every tool call. Deliberately bypasses the
// workflow-execution pipeline below (WorkerAdapterRegistry, FlowStateReporter, the AerFlowException
// boundary): none of that applies, and this needs to stay a fast, dependency-free stdin round trip
// since PreToolUse blocks the model's turn until it returns. Not listed in the usage banner below --
// an operator never types this, Claude Code does.
if (args.Length >= 1 && args[0] == "hook-check")
{
    var deniedTools = Environment.GetEnvironmentVariable(HookCheckCommand.DeniedToolsEnvironmentVariable);
    // #649: the hook needs to know where this execution's outbox is to allow a withheld write into
    // it. AER_OUTPUT_DIR reaches this process the same way the denied list does -- a hook subprocess
    // inherits the worker's environment.
    var outputDir = Environment.GetEnvironmentVariable("AER_OUTPUT_DIR");
    // #679: where a granted write may land -- see HookCheckCommand.Execute's own parameter docs for
    // what its absence means.
    var workspaceDir = Environment.GetEnvironmentVariable(HookCheckCommand.WorkspaceEnvironmentVariable);
    return HookCheckCommand.Execute(Console.In, Console.Error, deniedTools, outputDir, workspaceDir);
}

// #554: the same idea for agy, and a separate command because the two vendors share none of the
// mechanics -- agy nests the tool name at `toolCall.name` and reads its verdict from a `decision`
// field on STDOUT, where claude uses a root-level `tool_name` and signals denial by exiting 2.
// Note `Console.Out`, not `Console.Error`: on this vendor stdout carries the verdict, and anything
// else written there would be unparseable output that agy reads as an allow.
if (args.Length >= 1 && args[0] == "agy-hook-check")
{
    var deniedTools = Environment.GetEnvironmentVariable(AgyHookCheckCommand.DeniedToolsEnvironmentVariable);
    var shellPatterns = Environment.GetEnvironmentVariable(AgyHookCheckCommand.ShellPatternsEnvironmentVariable);
    // #390: the DenyAlways channel — agy's sole enforcement for a standing "never" (no vendor flag can
    // express a command family here), so it is read and passed like the allow channel.
    var deniedShellPatterns = Environment.GetEnvironmentVariable(
        AgyHookCheckCommand.DeniedShellPatternsEnvironmentVariable);
    // #679: the outbox reaches this gate for the GRANTED-write bound only. #649's withheld-write
    // exemption remains claude-only and is not extended here.
    var agyOutputDir = Environment.GetEnvironmentVariable("AER_OUTPUT_DIR");
    var agyWorkspaceDir = Environment.GetEnvironmentVariable(HookCheckCommand.WorkspaceEnvironmentVariable);
    return AgyHookCheckCommand.Execute(
        Console.In, Console.Out, deniedTools, shellPatterns, agyOutputDir, agyWorkspaceDir, deniedShellPatterns);
}

var knownSubcommands = new[] { "run", "dispatch", "redispatch", "cancel", "decide", "supply", "resume", "status", "templates" };
if (args.Length == 0 || !knownSubcommands.Contains(args[0]))
{
    Console.Error.WriteLine(RunOptionsParser.Usage);
    Console.Error.WriteLine($"       {DispatchOptionsParser.Usage[7..]}");
    Console.Error.WriteLine($"       {RedispatchOptionsParser.Usage[7..]}");
    Console.Error.WriteLine(
        "       aer cancel <room-dir> --execution <execution-id> --bindings <bindings-file> [--workflow-id <id>]");
    Console.Error.WriteLine(
        "       aer decide <room-dir> --execution <execution-id> --type resume|reject|retry-with-revision|supersede " +
        "[--target-step <step-id>] [--supplementary <execution-id>] --bindings <bindings-file> [--workflow-id <id>]");
    Console.Error.WriteLine(
        "       aer supply <room-dir> --worker <role> --output <name> --file <source-path> " +
        "--bindings <bindings-file> [--workflow-id <id>]");
    Console.Error.WriteLine($"       {ResumeOptionsParser.Usage[7..]}");
    Console.Error.WriteLine($"       {StatusOptionsParser.Usage[7..]}");
    Console.Error.WriteLine("       aer templates [--json]");
    Console.Error.WriteLine("       aer --version");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  {RunOptionsParser.ResumeNote}");
    return 64;
}

using var hostStopSource = new CancellationTokenSource();

// The host-initiated stop (M10 Phase 2), finally wired to something: Ctrl+C no longer kills the
// process outright — it cancels the ambient token the pump races against, which records
// CancellationRequested for every in-flight execution before signalling any of them, intent-first.
// Suppressing the default SIGINT behavior is what keeps the process alive long enough for that to
// happen.
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    hostStopSource.Cancel();
};

// #1356: the room directory for whichever mutating command is about to run, captured as soon as
// its options are parsed — declared here, outside the try, so the typed-exception catch below can
// still see it and record a pre-ledger failure sentinel for `run`/`dispatch` even though the
// AerFlowException that reaches it was thrown from deep inside RunCommand.ExecuteAsync, well past
// the switch's own scope (a variable declared inside `try` is not visible in its `catch`).
string? roomDirectoryPathForFailureSentinel = null;

try
{
    // Read-only, and never a mutation surface (#730) -- it produces no CommandResult (there is
    // nothing "resumed from" and nothing to pump to a fixed point) and always exits 0 when it
    // manages to print a status at all, so it is handled here rather than joining the
    // CommandResult/FlowStateReporter shape every mutating command below shares.
    if (args[0] == "status")
    {
        var statusOptions = StatusOptionsParser.Parse(args[1..]);
        await StatusCommand.ExecuteAsync(statusOptions, Console.Out, hostStopSource.Token).ConfigureAwait(false);
        return 0;
    }

    if (args[0] == "templates")
    {
        return await TemplatesCommand.ExecuteAsync(args[1..], Console.Out, hostStopSource.Token).ConfigureAwait(false);
    }

    CommandResult result;
    switch (args[0])
    {
        case "run":
            {
                var options = RunOptionsParser.Parse(args[1..]);
                roomDirectoryPathForFailureSentinel = options.RoomDirectoryPath;
                result = await RunCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, cancellationToken: hostStopSource.Token)
                    .ConfigureAwait(false);
                break;
            }

        case "dispatch":
            {
                var options = DispatchOptionsParser.Parse(args[1..]);
                roomDirectoryPathForFailureSentinel = options.RoomDirectoryPath;
                result = await DispatchCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, hostStopSource.Token)
                    .ConfigureAwait(false);
                break;
            }

        case "redispatch":
            {
                var options = RedispatchOptionsParser.Parse(args[1..]);
                roomDirectoryPathForFailureSentinel = options.RoomDirectoryPath;
                result = await RedispatchCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, hostStopSource.Token)
                    .ConfigureAwait(false);
                break;
            }

        case "cancel":
            {
                var options = CancelOptionsParser.Parse(args[1..]);
                result = await CancelCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, hostStopSource.Token)
                    .ConfigureAwait(false);
                break;
            }

        case "decide":
            {
                var options = DecideOptionsParser.Parse(args[1..]);
                result = await DecideCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, cancellationToken: hostStopSource.Token)
                    .ConfigureAwait(false);
                break;
            }

        case "resume":
            {
                var options = ResumeOptionsParser.Parse(args[1..]);
                result = await ResumeCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, hostStopSource.Token)
                    .ConfigureAwait(false);
                break;
            }

        default:
            {
                var options = SupplyOptionsParser.Parse(args[1..]);
                var supplyResult = await SupplyCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, hostStopSource.Token)
                    .ConfigureAwait(false);
                Console.WriteLine($"Supplementary execution: {supplyResult.ExecutionId}");
                result = supplyResult.Command;
                break;
            }
    }

    FlowStateReporter.Report(Console.Out, result);

    // #669: a provisioned worktree that was kept (uncommitted changes) or could not be removed is
    // surfaced, not swallowed — the run still succeeded, so this is an advisory on stderr.
    foreach (var teardown in result.WorktreeTeardowns)
    {
        Console.Error.WriteLine(
            $"worktree {teardown.Outcome} at {teardown.WorktreePath}"
            + (teardown.Detail is { } detail ? $" — {detail}" : string.Empty));
    }

    // #1356 point 4: written on reaching Terminal for every mutating command, not just `run` — a
    // workflow that pauses and is later carried to Terminal by a separate `aer decide` needs this
    // exactly as much as a straight-through `aer run`. Last, deliberately: every output an outcome
    // could reference is already on disk by the time the pump/decision call above returned.
    if (result.State.Status == WorkflowStatus.Terminal && result.RoomDirectoryPath is { } terminalRoomDirectoryPath)
    {
        // #1360: entries feeds the sentinel's per-execution usage. A fresh ledger read (CommandResult
        // carries only the already-projected FlowState, not the raw entries) -- one extra read at
        // terminal completion, not a hot path.
        var terminalLogPath = Path.Combine(terminalRoomDirectoryPath, "flow.jsonl");
        var terminalEntries = await new FlowEventLogReader(terminalLogPath)
            .ReadAllEntriesWithTimestampsAsync(CancellationToken.None).ConfigureAwait(false);
        var view = WorkflowStatusProjector.Project(result.State, result.Snapshot, terminalRoomDirectoryPath, terminalEntries);
        // CancellationToken.None: a Ctrl-C that already carried the workflow to Terminal must not
        // then lose the sentinel write for the terminal state it just reached.
        await TerminalSentinelWriter.WriteAsync(terminalRoomDirectoryPath, view, CancellationToken.None).ConfigureAwait(false);
    }

    // #1359: aer resume gets the same truthful exit-code table as run/dispatch — its own design
    // ruling names the completion contract explicitly, unlike cancel/decide/supply below, which
    // #1356 never asked to widen. #1441: aer redispatch drives the identical RunCommand pump a fresh
    // dispatch does, so it gets the same table for the same reason.
    if (args[0] is "run" or "dispatch" or "redispatch" or "resume")
    {
        return (int)RunExitCodeResolver.Resolve(result);
    }

    // Unchanged for cancel/decide/supply — #1356 scoped its exit-code table to run/dispatch only;
    // widening it to the rest was not asked for and is not done here.
    return result.State.Status == WorkflowStatus.Terminal && result.State.Steps.All(step => step.Status == StepStatus.Succeeded)
        ? 0
        : 1;
}
catch (AerFlowException ex) when (ex is Aer.Flow.Concurrency.WorkflowLockedException or Aer.Flow.Store.FlowJournalHeldException)
{
    // #1374 F1: this room is held by another Flow instance -- most often a live 'aer run' pump on
    // a perfectly healthy room, sometimes a background component's brief lock. Neither is a
    // provisioning/validation refusal, so this must NOT fall into the catch below: writing a
    // Failed sentinel here would tell a file-watcher a running room just died, and it would
    // contradict 'aer status --json' reading the very same room's ledger as Running at the same
    // moment. The room is left exactly as it was; the exit code alone says "retry later".
    WriteErrorWithTry(ex);
    return args[0] is "run" or "dispatch" or "redispatch" or "resume" ? (int)RunExitCode.RoomHeld : 1;
}
catch (AerFlowException ex)
{
    // The typed-exception boundary CLAUDE.md's error-handling rules require: every malformed
    // workflow/bindings/argument failure surfaces as one of these further up the call stack, so
    // this is the one place that turns it into a clean CLI failure instead of a raw stack trace.
    WriteErrorWithTry(ex);

    // #1356 points 2+3: for `run`/`dispatch` specifically, this is the provisioning/validation
    // failure class — distinct from a worker that actually ran and failed — and the room (which
    // Directory.CreateDirectory already created inside RunCommand/DispatchCommand by the time
    // anything here can throw) must be left queryable rather than eternally "Running/no ledger yet".
    //
    // #1374 F1: only when the room is genuinely pre-ledger (RoomLedgerProbe.HasLedger, not a bare
    // File.Exists -- see that type's own doc for why a zero-byte flow.jsonl must not count). A room
    // with a real ledger has been dispatched at least once before -- its ledger (or a still-live
    // pump) is the room's real terminal record, and this invocation's own failure must not overwrite
    // it with a fabricated Failed/no-outputs sentinel (see invoking-baton.md's exit-code section for
    // the scenario this guards). The exit code still reports the refusal; only the sentinel write is
    // conditional.
    if (args[0] is "run" or "dispatch" or "redispatch" && roomDirectoryPathForFailureSentinel is not null)
    {
        if (!RoomLedgerProbe.HasLedger(roomDirectoryPathForFailureSentinel))
        {
            await TerminalSentinelWriter.WriteValidationRefusedAsync(
                roomDirectoryPathForFailureSentinel, ex.Message, CancellationToken.None, ex.TryInvocation).ConfigureAwait(false);
        }

        return (int)RunExitCode.ValidationRefused;
    }

    // #1359: a resume always targets an already-dispatched room — it never has a pre-ledger state to
    // leave a sentinel for (that branch above is run/dispatch-only), but its own refusals (no
    // SessionId recorded, an ambiguous or unresolvable worker, a still-running target) are exactly
    // #1356's ValidationRefused shape: refused before anything new was dispatched.
    if (args[0] == "resume")
    {
        return (int)RunExitCode.ValidationRefused;
    }

    return 1;
}

// #1382 F8: the one place either AerFlowException catch above prints an error, so a Try line set on
// a future WorkflowLockedException/FlowJournalHeldException is never silently dropped again.
static void WriteErrorWithTry(AerFlowException ex)
{
    Console.Error.WriteLine(ex.Message);
    if (ex.TryInvocation is not null)
    {
        Console.Error.WriteLine($"Try: {ex.TryInvocation}");
    }
}
