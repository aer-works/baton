namespace Baton.Cli.Tests.TestSupport;

/// <summary>
/// A fake <see cref="HttpMessageHandler"/> that records every request it receives and answers with a
/// fixed status — the seam <c>WatchNotifier</c>'s URL arm needs to be tested without an actual network
/// call. No fake handler existed anywhere in this test tree before #1488; this is deliberately minimal
/// (record + answer), not a general-purpose HTTP stub.
/// </summary>
public sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public System.Net.HttpStatusCode ResponseStatusCode { get; set; } = System.Net.HttpStatusCode.OK;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Requests.Add((request, body));
        return new HttpResponseMessage(ResponseStatusCode);
    }
}
