namespace Baton.Cli.Tests.TestSupport;

/// <summary>A fake <see cref="IWatchNotifier"/> that just counts/records calls — the seam
/// <c>WatchFireServiceTests</c> uses to assert exactly-once firing without spawning a real process or
/// making a real HTTP call (that mechanics is <c>WatchNotifierTests</c>'s own concern).</summary>
public sealed class RecordingWatchNotifier : IWatchNotifier
{
    public List<(string Target, WatchNotifyPayload Payload)> Calls { get; } = [];

    public Task NotifyAsync(string target, WatchNotifyPayload payload, CancellationToken cancellationToken)
    {
        Calls.Add((target, payload));
        return Task.CompletedTask;
    }
}
