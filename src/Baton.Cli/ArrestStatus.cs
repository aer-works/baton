using Baton.Projection;
using Baton.Status;
using Baton.Store;

namespace Baton.Cli;

internal static class ArrestStatus
{
    public static async Task<IReadOnlyList<ArrestRecord>> ReadAsync(
        string roomDirectoryPath,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(roomDirectoryPath, BatonPaths.RoomLogFileName);
        var events = await new RoomEventLogReader(path).ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        return RoomProjector.Project(events).Arrests;
    }

    public static void WriteText(TextWriter output, IReadOnlyList<ArrestRecord> arrests)
    {
        if (arrests.Count == 0)
        {
            return;
        }

        output.WriteLine("Arrests:");
        foreach (var arrest in arrests)
        {
            var execution = arrest.ExecutionId is { } executionId ? executionId.Value : "none";
            var terminalAt = arrest.DeliveredAt ?? arrest.RejectedAt ?? arrest.ExpiredAt;
            var terminal = terminalAt is { } instant ? $" terminalAt={instant:O}" : string.Empty;
            var reason = string.IsNullOrWhiteSpace(arrest.Reason) ? string.Empty : $" reason={arrest.Reason}";
            output.WriteLine(
                $"  {arrest.State}: request={arrest.RequestId} target={arrest.Target} execution={execution} " +
                $"askedBy={arrest.RequestedBy} requestedAt={arrest.RequestedAt:O}{terminal}{reason}");
        }
    }
}