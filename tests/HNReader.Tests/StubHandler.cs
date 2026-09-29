using System.Net;
using System.Text;

internal sealed class StubHandler(Func<string, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return respond(request.RequestUri!.AbsolutePath, cancellationToken);
    }

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    public static string Story(int score, string title, bool withUrl = true) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "story", title, score, by = "author", time = 1_700_000_000,
            descendants = 7, url = withUrl ? "https://example.com/article" : null
        });
}

internal sealed class StubFactory(HttpMessageHandler handler, TimeSpan requestTimeout) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
    {
        BaseAddress = new Uri("https://hacker-news.firebaseio.com/v0/"),
        Timeout = requestTimeout
    };
}

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-09-29T00:00:00+00:00");
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}