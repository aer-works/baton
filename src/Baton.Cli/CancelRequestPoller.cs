using System.Collections.Concurrent;
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
/// <para>
/// #1563 (S0 of the quota design, #802): a target that has no live process to deliver to AND is not
/// genuinely settled — a step Failed with a scheduled <see cref="Domain.StepState.RetryNotBefore"/>,
/// the shape a vendor-quota park leaves behind — is marked via
/// <see cref="InFlightExecutionRegistry.MarkParkedCancelIntent"/> instead of being told it is too
/// late. That mark records nothing by itself; the pump's own idle-deferral wait validates and
/// appends the durable events once it wakes, exactly as <c>RequestCancellationAsync</c> does for a
/// live process.
/// </para>
/// </summary>
public static class CancelRequestPoller
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentDictionary<(string Path, string Target, DateTime LastWriteUtc), int> RetryCounters = new();

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
                try
                {
                    Console.Error.WriteLine($"cancel.request poll against '{roomDirectoryPath}' failed this tick: {ex.Message}");
                }
                catch
                {
                    // F6: swallow broken stderr pipe
                }
            }
        }
    }

    /// <summary>
    /// One poll cycle: absent file is the overwhelmingly common case and costs one stat. A present file
    /// is parsed, resolved (<see cref="CancelRequestFile.LatestTarget"/> against the room's own
    /// currently-projected <see cref="Domain.StepStatus.Running"/> step via
    /// <see cref="RunningExecutionResolver"/>, or taken as a literal <see cref="ExecutionId"/> otherwise),
    /// and delivered to <paramref name="inFlightExecutions"/>. A delivered request or a genuinely settled
    /// one is consumed; an undelivered still-running target is retried up to 5 ticks before being rejected
    /// (#1530); malformed content or an unresolvable <c>latest</c> is rejected immediately (fail closed,
    /// no guessing, reason in body) rather than retried forever or left to crash the pump.
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

        var lastWriteUtc = new FileInfo(requestPath).LastWriteTimeUtc;
        var content = await CancelRequestFile.TryReadAsync(requestPath, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            CancelRequestFile.Reject(requestPath, target: null, "malformed content (not valid JSON, or a blank/missing Target)");
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
                    content.Target,
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

        var retryKey = (requestPath, content.Target, lastWriteUtc);
        var delivered = await inFlightExecutions.RequestCancellationAsync(targetExecutionId, cancellationToken).ConfigureAwait(false);
        if (delivered)
        {
            RetryCounters.TryRemove(retryKey, out _);
            CancelRequestFile.Consume(requestPath);
            return;
        }

        // Delivered was false: re-check projection to differentiate settled from still-running/parked.
        var settleCheckReader = new FlowEventLogReader(logPath);
        var settleCheckEvents = await settleCheckReader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var settleCheckState = StateProjector.Project(settleCheckEvents, snapshot);
        var targetStep = settleCheckState.Steps.FirstOrDefault(s => s.LatestExecutionId == targetExecutionId);
        var stillRunning = targetStep?.Status == StepStatus.Running;

        // #1563 (S0 of the quota design, #802 "three independent locks" finding): a step-tied target sitting on a future
        // RetryNotBefore has no live process to register with — the worker already exited — but it
        // is not "already settled" either. The pump is parked in its idle-deferral wait, possibly
        // for as long as a vendor quota reset (MutationInterface's deferralCandidates). Mark the
        // intent on the SAME registry that wait watches instead of reporting the false "too late"
        // verdict below. Idempotent: safe to re-mark on every tick until the pump drains it.
        var isParked = targetStep is { Status: StepStatus.Failed, RetryNotBefore: not null };
        if (isParked)
        {
            inFlightExecutions.MarkParkedCancelIntent(targetExecutionId);
        }

        if (!stillRunning && !isParked)
        {
            RetryCounters.TryRemove(retryKey, out _);
            // The one-word difference that keeps this honest once the seam above can actually
            // settle a park: an execution the seam itself just cancelled did settle BECAUSE of this
            // request, not despite it — reporting "too late" for that case is the exact false claim
            // #802's "three independent locks" finding measured, just for the step-less case this method does not yet cover.
            var arrestedByThisRequest = targetStep?.Status == StepStatus.Cancelled;
            try
            {
                Console.Error.WriteLine(arrestedByThisRequest
                    ? $"cancel.request against '{roomDirectoryPath}' named execution '{targetExecutionId.Value}' — arrested by this request."
                    : $"cancel.request against '{roomDirectoryPath}' named execution '{targetExecutionId.Value}', which is "
                        + "not currently in flight — too late (it already settled).");
            }
            catch
            {
                // F6: swallow broken stderr pipe
            }

            CancelRequestFile.Consume(requestPath);
            return;
        }

        // #1563: a parked mark is a delivery GUARANTEE, not a hope — SettleParkedCancelIntentsAsync
        // (MutationInterface) will drain it and append the terminal events on the pump's very next
        // round. Folding this into the bounded-retry counter below would let a slow round (several
        // other steps mid-dispatch, event-log I/O contention) hit the 5-tick ceiling before the pump
        // gets there, rejecting a request that was already going to succeed with the false claim
        // "not reachable" and deleting the pending file out from under the settle that follows
        // moments later. A live pump that never drains its mark is the dead-pump case #1586 covers,
        // not this one (scope note atop this file) — so this path retries forever rather than
        // guessing a ceiling for a wait this poller has no way to bound.
        if (isParked)
        {
            RetryCounters.TryRemove(retryKey, out _);
            return;
        }

        // Target STILL projects Running: count a bounded retry.
        var retries = RetryCounters.AddOrUpdate(retryKey, 1, (_, current) => current + 1);
        if (retries >= 5)
        {
            RetryCounters.TryRemove(retryKey, out _);
            CancelRequestFile.Reject(
                requestPath,
                content.Target,
                "target still running but not reachable through the in-flight registry (likely non-process work, #1530) — use Ctrl+C on the pump or wait for it to settle");
        }
    }
}
