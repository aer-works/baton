using System.Reflection;
using Aer.Adapters;
using Aer.Cli;
using Aer.Flow;
using Aer.Flow.Domain;

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
    // #445: the ask band, reaching this process the same way the denied list does. Set only by a
    // gate-enabled dispatch -- unset here is what keeps a one-shot run's hook output unchanged.
    var askTools = Environment.GetEnvironmentVariable(HookCheckCommand.AskToolsEnvironmentVariable);
    // Console.Out as well as Console.Error, and the two are not interchangeable: claude reads a
    // structured hook decision from stdout and a denial reason from stderr.
    return HookCheckCommand.Execute(
        Console.In, Console.Error, deniedTools, outputDir, workspaceDir, askTools, Console.Out);
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

var knownSubcommands = new[] { "run", "dispatch", "cancel", "decide", "supply", "status", "templates" };
if (args.Length == 0 || !knownSubcommands.Contains(args[0]))
{
    Console.Error.WriteLine(RunOptionsParser.Usage);
    Console.Error.WriteLine($"       {DispatchOptionsParser.Usage[7..]}");
    Console.Error.WriteLine(
        "       aer cancel <room-dir> --execution <execution-id> --bindings <bindings-file> [--workflow-id <id>]");
    Console.Error.WriteLine(
        "       aer decide <room-dir> --execution <execution-id> --type resume|reject|retry-with-revision|supersede " +
        "[--target-step <step-id>] [--supplementary <execution-id>] --bindings <bindings-file> [--workflow-id <id>]");
    Console.Error.WriteLine(
        "       aer supply <room-dir> --worker <role> --output <name> --file <source-path> " +
        "--bindings <bindings-file> [--workflow-id <id>]");
    Console.Error.WriteLine("       aer status <room-dir> [--follow]");
    Console.Error.WriteLine("       aer templates [--json]");
    Console.Error.WriteLine("       aer --version");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  {RunOptionsParser.ResumeNote}");
    return 64;
}

using var hostStopSource = new CancellationTokenSource();

// §9's host-initiated stop (M10 Phase 2), finally wired to something: Ctrl+C no longer kills the
// process outright — it cancels the ambient token the pump races against, which records
// CancellationRequested for every in-flight execution before signalling any of them (§7's
// intent-first ordering). Suppressing the default SIGINT behavior is what keeps the process alive
// long enough for that to happen.
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    hostStopSource.Cancel();
};

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
                result = await RunCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, cancellationToken: hostStopSource.Token)
                    .ConfigureAwait(false);
                break;
            }

        case "dispatch":
            {
                var options = DispatchOptionsParser.Parse(args[1..]);
                result = await DispatchCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, hostStopSource.Token)
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

    return result.State.Status == WorkflowStatus.Terminal && result.State.Steps.All(step => step.Status == StepStatus.Succeeded)
        ? 0
        : 1;
}
catch (AerFlowException ex)
{
    // The typed-exception boundary CLAUDE.md's error-handling rules require: every malformed
    // workflow/bindings/argument failure surfaces as one of these further up the call stack, so
    // this is the one place that turns it into a clean CLI failure instead of a raw stack trace.
    Console.Error.WriteLine(ex.Message);
    return 1;
}
