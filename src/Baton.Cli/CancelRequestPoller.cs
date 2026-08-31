using Baton.Domain;
using Baton.Mutation;
using Baton.Projection;
using Baton.Store;

namespace Baton.Cli;

/// <summary>
/// The pump-side reader for <see cref="CancelRequestFile"/> (#1495): polls a room directory for
/// <c>cancel.request</c> at a modest cadence — a cheap <see cref="File.Exists(string)"/> stat per tick,
/// never touching <c>flow.lock</c> — and routes a found request to
/// <see cref="InFlightExecutionRegistry.RequestCancellationAsync"/>, which itself appends the durable
/// <see cref="Baton.Domain.FlowEvent.CancellationRequested"/> record before signalling anything: this
/// poller adds no second recording path of its own. Started and stopped by <see cref="RunCommand"/>
/// alongside its own <see cref="MutationInterface.StartWorkflowAsync"/> call — the registry it is
/// given must be the same instance that call bound to the run's <c>IEventLogWriter</c>.
/// </summary>
public static class CancelRequestPoller
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs until <paramref name="cancellationToken"/> fires. Never throws for a malformed request, a
    /// transient filesystem error, or any other fault a single tick raises — every exception except the
    /// loop's own cancellation is caught, logged, and the loop continues — so a caller can
    /// fire-and-forget this alongside the pump call it accompanies: a poller fault must never replace
    /// whatever that pump call itself threw (or its successful result) by escaping this method.
    /// </summary>
    public static async Task RunAsync(
        string roomDirectoryPath,
        string logPath,
        WorkflowDefinitionSnapshot snapshot,
        InFlightExecutionRegistry inFlightExecutions,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await TickAsync(roomDirectoryPath, logPath, snapshot, inFlightExecutions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A tick's own fault (a torn log read racing the pump's own writer, an unreadable
                // request file) must never bring down the pump it is only ever a side channel to —
                // log it and keep polling; the request, if any, is simply retried next tick.
                Console.Error.WriteLine($"cancel.request poll against '{roomDirectoryPath}' failed this tick: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// One poll cycle: absent file is the overwhelmingly common case and costs one stat. A present file
    /// is parsed, resolved (<see cref="CancelRequestFile.LatestTarget"/> against the room's own
    /// currently-projected <see cref="Domain.StepStatus.Running"/> step via
    /// <see cref="RunningExecutionResolver"/>, or taken as a literal <see cref="ExecutionId"/> otherwise),
    /// delivered to <paramref name="inFlightExecutions"/>, and then renamed out of the way — malformed
    /// content or an unresolvable <c>latest</c> is rejected (fail closed, no guessing) rather than
    /// retried forever or left to crash the pump.
    /// </summary>
    internal static async Task TickAsync(
        string roomDirectoryPath,
        string logPath,
        WorkflowDefinitionSnapshot snapshot,
        InFlightExecutionRegistry inFlightExecutions,
        CancellationToken cancellationToken)
    {
        var requestPath = CancelRequestFile.GetPath(roomDirectoryPath);
        if (!File.Exists(requestPath))
        {
            return;
        }

        var content = await CancelRequestFile.TryReadAsync(requestPath, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            CancelRequestFile.Reject(requestPath, "malformed content (not valid JSON, or a blank/missing Target)");
            return;
        }

        ExecutionId targetExecutionId;
        if (string.Equals(content.Target, CancelRequestFile.LatestTarget, StringComparison.Ordinal))
        {
            var reader = new FlowEventLogReader(logPath);
            var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var state = StateProjector.Project(events, snapshot);
            var resolved = RunningExecutionResolver.Resolve(state);

            if (resolved.Single is not { } single)
            {
                CancelRequestFile.Reject(
                    requestPath,
                    resolved.RunningExecutionIds.Count == 0
                        ? "'latest' requested, but no execution is currently Running"
                        : $"'latest' requested, but {resolved.RunningExecutionIds.Count} executions are currently " +
                            $"Running ({string.Join(", ", resolved.RunningExecutionIds.Select(id => id.Value))}) — ambiguous");
                return;
            }

            targetExecutionId = single;
        }
        else
        {
            targetExecutionId = new ExecutionId(content.Target);
        }

        var delivered = await inFlightExecutions.RequestCancellationAsync(targetExecutionId, cancellationToken).ConfigureAwait(false);
        if (!delivered)
        {
            // Not registered: already settled by the time this tick ran, or never dispatched under
            // this id at all. A legitimate too-late no-op (same semantics MutationInterface's own
            // direct-mutation cancel path already has for a stale id) -- surfaced here rather than
            // silently equated with a real delivery, since nothing else observes the difference.
            Console.Error.WriteLine(
                $"cancel.request against '{roomDirectoryPath}' named execution '{targetExecutionId.Value}', which is "
                + "not currently in flight — too late (it already settled, or was never dispatched under this id).");
        }

        CancelRequestFile.Consume(requestPath);
    }
}
