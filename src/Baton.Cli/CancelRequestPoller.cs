using System.Collections.Concurrent;
using Baton.Domain;
using Baton.Mutation;
using Baton.Projection;
using Baton.Store;

namespace Baton.Cli;

/// <summary>Polls the room-side request file and records each request's durable room outcome.</summary>
public static class CancelRequestPoller
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    private static readonly ConcurrentDictionary<(string Path, string RequestId), int> RetryCounters = new();
    private static readonly ConcurrentDictionary<(string Path, string RequestId), byte> ParkedNoticePrinted = new();

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
                await TickAsync(roomDirectoryPath, logPath, snapshot, inFlightExecutions, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                try
                {
                    Console.Error.WriteLine($"cancel.request poll against '{roomDirectoryPath}' failed this tick: {ex.Message}");
                }
                catch
                {
                }
            }
        }
    }

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
            var malformed = CancelRequestFile.DescribeMalformed(lastWriteUtc);
            await ArrestLedger.RecordRequestedAsync(
                    roomDirectoryPath,
                    malformed.RequestId,
                    malformed.Target,
                    malformed.RequestedBy,
                    malformed.RequestedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            await CancelRequestFile.RejectAsync(
                    roomDirectoryPath,
                    requestPath,
                    malformed,
                    executionId: null,
                    "malformed content (not valid JSON, or a blank/missing Target)",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var request = CancelRequestFile.Describe(content, lastWriteUtc);
        await ArrestLedger.RecordRequestedAsync(
                roomDirectoryPath,
                request.RequestId,
                request.Target,
                request.RequestedBy,
                request.RequestedAt,
                cancellationToken)
            .ConfigureAwait(false);

        ExecutionId targetExecutionId;
        if (string.Equals(content.Target, CancelRequestFile.LatestTarget, StringComparison.Ordinal))
        {
            var state = StateProjector.Project(
                await new FlowEventLogReader(logPath).ReadAllAsync(cancellationToken).ConfigureAwait(false),
                snapshot);
            var resolved = RunningExecutionResolver.Resolve(state);
            if (resolved.Single is not { } single)
            {
                var reason = resolved.RunningExecutionIds.Count == 0
                    ? "'latest' requested, but no execution is currently Running or quota-parked"
                    : $"'latest' requested, but {resolved.RunningExecutionIds.Count} executions are currently Running or quota-parked ({string.Join(", ", resolved.RunningExecutionIds.Select(id => id.Value))}) — ambiguous";
                await CancelRequestFile.RejectAsync(
                        roomDirectoryPath,
                        requestPath,
                        request,
                        executionId: null,
                        reason,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            targetExecutionId = single;
        }
        else
        {
            targetExecutionId = new ExecutionId(content.Target);
        }

        var retryKey = (requestPath, request.RequestId);
        if (await inFlightExecutions.RequestCancellationAsync(targetExecutionId, cancellationToken).ConfigureAwait(false))
        {
            ClearRetry(retryKey);
            await CancelRequestFile.ConsumeDeliveredAsync(
                    roomDirectoryPath,
                    requestPath,
                    request,
                    targetExecutionId,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var settleEvents = await new FlowEventLogReader(logPath).ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var settleState = StateProjector.Project(settleEvents, snapshot);
        var targetStep = settleState.Steps.FirstOrDefault(step => step.LatestExecutionId == targetExecutionId);
        var stillArrestable = ArrestableExecutions.Find(settleState, snapshot, targetExecutionId) is not null;
        var isParked = targetStep is { Status: StepStatus.Failed, RetryNotBefore: not null };

        if (stillArrestable)
        {
            inFlightExecutions.MarkArrestIntent(
                targetExecutionId,
                isParked ? "quota-parked (no live process to signal)" : "no live process registered for this target");
        }

        if (!stillArrestable)
        {
            ClearRetry(retryKey);
            var deliveredByThisRequest = settleEvents
                .OfType<FlowEvent.ExecutionCancelled>()
                .Any(cancelled => cancelled.ExecutionId == targetExecutionId);
            try
            {
                Console.Error.WriteLine(deliveredByThisRequest
                    ? $"cancel.request against '{roomDirectoryPath}' named execution '{targetExecutionId.Value}' — arrested by this request."
                    : $"cancel.request against '{roomDirectoryPath}' named execution '{targetExecutionId.Value}' — too late (it already settled).");
            }
            catch
            {
            }

            if (deliveredByThisRequest)
            {
                await CancelRequestFile.ConsumeDeliveredAsync(
                        roomDirectoryPath,
                        requestPath,
                        request,
                        targetExecutionId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await CancelRequestFile.ConsumeExpiredAsync(
                        roomDirectoryPath,
                        requestPath,
                        request,
                        targetExecutionId,
                        "target had already settled before this cancel.request could be delivered",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (isParked)
        {
            RetryCounters.TryRemove(retryKey, out _);
            if (ParkedNoticePrinted.TryAdd(retryKey, 0))
            {
                try
                {
                    Console.Error.WriteLine($"cancel.request against '{roomDirectoryPath}' named execution '{targetExecutionId.Value}' is quota-parked; awaiting pump settlement.");
                }
                catch
                {
                }
            }

            return;
        }

        if (RetryCounters.AddOrUpdate(retryKey, 1, (_, current) => current + 1) >= 5)
        {
            ClearRetry(retryKey);
            const string reason = "arrest requested (#1556) but not yet confirmed settled after 5 polls — the pump may still deliver it";
            await CancelRequestFile.RejectAsync(
                    roomDirectoryPath,
                    requestPath,
                    request,
                    targetExecutionId,
                    reason,
                    cancellationToken)
                .ConfigureAwait(false);
            await inFlightExecutions.RecordCancellationRejectedAsync(targetExecutionId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ClearRetry((string Path, string RequestId) retryKey)
    {
        RetryCounters.TryRemove(retryKey, out _);
        ParkedNoticePrinted.TryRemove(retryKey, out _);
    }
}