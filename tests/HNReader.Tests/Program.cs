using System.Text.Json;
using HNReader.Api.Stories;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Scores, mapping, and cached requests", OrdersAndMaps),
    ("Compact and indented JSON use the same cached stories", FormatsJson),
    ("Missing and deleted items", HandlesMissingItems),
    ("Concurrent requests share one refresh", SharesRefresh),
    ("Stale callers do not queue behind refresh", ServesStaleDuringRefresh),
    ("Refresh updates rankings", UpdatesRankings),
    ("Stale fallback and retry cooldown", UsesStaleOnFailure),
    ("Hacker News unavailable before the first update", RejectsUnavailableHackerNews),
    ("Invalid story time without saved results", RejectsInvalidTimeWithoutSavedResults),
    ("Invalid story time keeps the last good results", UsesSavedResultsForInvalidTime),
    ("Limits concurrent Hacker News requests", LimitsConcurrency),
    ("Configured cache and retry times", UsesConfiguredCacheTimes),
    ("Configured Hacker News request timeout", UsesConfiguredRequestTimeout),
    ("Configured overall update deadline", UsesConfiguredRefreshTimeout),
    ("Rejects invalid n", RejectsInvalidN)
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {ex}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static BestStoriesOptions DefaultOptions() => new()
{
    FreshFor = TimeSpan.FromMinutes(5),
    StaleFor = TimeSpan.FromMinutes(15),
    RetryAfterFailure = TimeSpan.FromMinutes(1),
    RequestTimeout = TimeSpan.FromSeconds(10),
    RefreshTimeout = TimeSpan.FromSeconds(30),
    MaxConcurrentRequests = 8
};

static BestStoriesService CreateService(StubHandler handler, ManualClock? clock = null, BestStoriesOptions? settings = null)
{
    settings ??= DefaultOptions();
    return new BestStoriesService(new StubFactory(handler, settings.RequestTimeout), clock ?? new ManualClock(),
        NullLogger<BestStoriesService>.Instance, Options.Create(settings));
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task OrdersAndMaps()
{
    using var handler = new StubHandler((path, _) => Task.FromResult(StubHandler.Json(path switch
    {
        "/v0/beststories.json" => "[1,2,3]",
        "/v0/item/1.json" => StubHandler.Story(50, "First"),
        "/v0/item/2.json" => StubHandler.Story(200, "Second", withUrl: false),
        "/v0/item/3.json" => StubHandler.Story(100, "Third"),
        _ => throw new Exception(path)
    })));
    var service = CreateService(handler);

    var top = await service.GetBestStoriesAsync(2);
    Check(top.Count == 2 && top[0].Title == "Second" && top[1].Title == "Third", "Wrong score order");
    Check(top[0].Uri is null && top[0].PostedBy == "author" && top[0].CommentCount == 7,
        "Incorrect missing URL, author, or comment count");
    Check(top[0].Time == DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), "Incorrect UTC time");
    Check((await service.GetBestStoriesAsync(3)).Count == 3 && handler.Calls == 4,
        "Different n should use the same cached snapshot");
}

static async Task FormatsJson()
{
    using var handler = new StubHandler((path, _) => Task.FromResult(StubHandler.Json(path switch
    {
        "/v0/beststories.json" => "[1,2,3]",
        "/v0/item/1.json" => StubHandler.Story(50, "First"),
        "/v0/item/2.json" => StubHandler.Story(200, "Second"),
        "/v0/item/3.json" => StubHandler.Story(100, "Third"),
        _ => throw new Exception(path)
    })));
    var service = CreateService(handler);
    using var services = new ServiceCollection().AddLogging().BuildServiceProvider();

    async Task<(int Status, string Body)> Render(string? n, string? pretty)
    {
        var result = await BestStoriesEndpoint.HandleAsync(n, pretty, service, CancellationToken.None);
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = body;
        await result.ExecuteAsync(context);
        body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(body).ReadToEndAsync());
    }

    var compact = await Render("2", null);
    var explicitCompact = await Render("2", "false");
    var indented = await Render("2", "true");
    Check(compact.Status == 200 && explicitCompact.Status == 200 && indented.Status == 200,
        "Valid formatting options should succeed");
    Check(compact.Body == explicitCompact.Body && !compact.Body.Contains('\n') &&
        indented.Body.Contains("\n    \"title\""), "Compact or indented JSON formatting is incorrect");
    using var compactJson = JsonDocument.Parse(compact.Body);
    using var indentedJson = JsonDocument.Parse(indented.Body);
    Check(JsonSerializer.Serialize(compactJson.RootElement) == JsonSerializer.Serialize(indentedJson.RootElement) &&
        indentedJson.RootElement.GetArrayLength() == 2 &&
        indentedJson.RootElement[0].GetProperty("score").GetInt32() == 200 &&
        indentedJson.RootElement[1].GetProperty("score").GetInt32() == 100 && handler.Calls == 4,
        "Formatting changed the cached content or descending score order");
    var invalid = await Render("2", "yes");
    Check(invalid.Status == 400 && handler.Calls == 4, "Invalid pretty value was not rejected");
}

static async Task HandlesMissingItems()
{
    using var handler = new StubHandler((path, _) => Task.FromResult(StubHandler.Json(path switch
    {
        "/v0/beststories.json" => "[1,2,3,4]",
        "/v0/item/1.json" => "null",
        "/v0/item/2.json" => "{\"type\":\"story\",\"deleted\":true}",
        "/v0/item/3.json" => "{\"type\":\"story\",\"dead\":true}",
        "/v0/item/4.json" => "{\"type\":\"story\",\"title\":\"Ask HN\",\"score\":3,\"time\":1700000000}",
        _ => throw new Exception(path)
    })));
    var stories = await CreateService(handler).GetBestStoriesAsync(10);
    Check(stories.Count == 1 && stories[0].Title == "Ask HN" && stories[0].CommentCount == 0 &&
        stories[0].Uri is null && stories[0].PostedBy is null, "Optional or invalid stories mishandled");
}

static async Task SharesRefresh()
{
    using var handler = new StubHandler(async (path, token) =>
    {
        await Task.Delay(10, token);
        return StubHandler.Json(path.EndsWith("beststories.json") ? "[1,2]" : StubHandler.Story(10, path));
    });
    var service = CreateService(handler);
    var requests = Enumerable.Range(0, 25).Select(_ => service.GetBestStoriesAsync(1));
    var results = await Task.WhenAll(requests);
    Check(results.All(stories => stories.Count == 1) && handler.Calls == 3,
        "Concurrent callers fetched the same Hacker News data more than once");
}

static async Task ServesStaleDuringRefresh()
{
    var clock = new ManualClock();
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var listCalls = 0;
    using var handler = new StubHandler(async (path, _) =>
    {
        if (path.EndsWith("beststories.json"))
        {
            if (Interlocked.Increment(ref listCalls) == 2)
            {
                started.SetResult();
                await release.Task;
            }
            return StubHandler.Json("[1]");
        }
        return StubHandler.Json(StubHandler.Story(10, "Previous"));
    });
    var service = CreateService(handler, clock);
    await service.GetBestStoriesAsync(1);
    clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
    var refresh = service.GetBestStoriesAsync(1);
    try
    {
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var concurrent = service.GetBestStoriesAsync(1);
        Check(concurrent.IsCompletedSuccessfully && concurrent.Result[0].Title == "Previous",
            "A caller with saved results waited for the slow Hacker News update");
    }
    finally { release.TrySetResult(); }
    await refresh;
}

static async Task UpdatesRankings()
{
    var clock = new ManualClock();
    var reversed = false;
    using var handler = new StubHandler((path, _) => Task.FromResult(StubHandler.Json(path switch
    {
        "/v0/beststories.json" => "[1,2]",
        "/v0/item/1.json" => StubHandler.Story(reversed ? 30 : 1, "One"),
        "/v0/item/2.json" => StubHandler.Story(reversed ? 2 : 20, "Two"),
        _ => throw new Exception(path)
    })));
    var service = CreateService(handler, clock);
    Check((await service.GetBestStoriesAsync(1))[0].Title == "Two", "Initial ranking incorrect");
    reversed = true;
    clock.Advance(TimeSpan.FromSeconds(61));
    Check((await service.GetBestStoriesAsync(1))[0].Title == "Two" && handler.Calls == 3,
        "Cache expired before the five-minute fresh period ended");
    clock.Advance(TimeSpan.FromMinutes(4));
    Check((await service.GetBestStoriesAsync(1))[0].Title == "One" && handler.Calls == 6,
        "Cache did not refresh after expiry");
}

static async Task UsesStaleOnFailure()
{
    var clock = new ManualClock();
    var unavailable = false;
    using var handler = new StubHandler((path, _) =>
    {
        if (unavailable) throw new HttpRequestException("Hacker News unavailable");
        return Task.FromResult(StubHandler.Json(path.EndsWith("beststories.json") ? "[1]" :
            StubHandler.Story(42, "Cached")));
    });
    var service = CreateService(handler, clock);
    await service.GetBestStoriesAsync(1);
    unavailable = true;
    clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
    Check((await service.GetBestStoriesAsync(1))[0].Title == "Cached", "Should serve recent stale data");
    Check((await service.GetBestStoriesAsync(1))[0].Title == "Cached" && handler.Calls == 3,
        "Failed refreshes should be rate-limited");
    clock.Advance(TimeSpan.FromSeconds(30));
    Check((await service.GetBestStoriesAsync(1))[0].Title == "Cached" && handler.Calls == 3,
        "Failed refresh was retried before the one-minute cooldown ended");
    clock.Advance(TimeSpan.FromSeconds(31));
    Check((await service.GetBestStoriesAsync(1))[0].Title == "Cached" && handler.Calls == 4,
        "Failed refresh was not retried after the cooldown ended");
    clock.Advance(TimeSpan.FromMinutes(16));
    await ExpectUnavailable(service);
}

static async Task RejectsUnavailableHackerNews()
{
    using var handler = new StubHandler((_, _) => throw new HttpRequestException("Hacker News unavailable"));
    var service = CreateService(handler);
    var first = await ExpectUnavailable(service);
    var second = await ExpectUnavailable(service);
    Check(handler.Calls == 1, "Cold failures should have a retry cooldown");
    Check(first.Message == "No usable Hacker News stories are available." &&
        second.Message == "Waiting to retry Hacker News after a failed update.",
        "Unavailable errors should explain the failed update and retry delay");
}

static async Task RejectsInvalidTimeWithoutSavedResults()
{
    foreach (var invalidTime in new[] { long.MinValue, long.MaxValue })
    {
        var invalidStory = $"{{\"type\":\"story\",\"title\":\"Bad time\",\"score\":100,\"time\":{invalidTime}}}";
        using var handler = new StubHandler((path, _) => Task.FromResult(StubHandler.Json(
            path.EndsWith("beststories.json") ? "[1]" : invalidStory)));
        var service = CreateService(handler);

        var error = await ExpectUnavailable(service);
        Check(error.InnerException is JsonException json && json.Message.Contains("time", StringComparison.OrdinalIgnoreCase),
            "Invalid Hacker News time should fail the update as a data error");
        await ExpectUnavailable(service);
        Check(handler.Calls == 2, "Invalid time should pause retries for one minute");
    }
}

static async Task UsesSavedResultsForInvalidTime()
{
    var clock = new ManualClock();
    var invalidTime = false;
    var invalidStory = $"{{\"type\":\"story\",\"title\":\"Bad time\",\"score\":100,\"time\":{long.MaxValue}}}";
    using var handler = new StubHandler((path, _) => Task.FromResult(StubHandler.Json(
        path.EndsWith("beststories.json") ? "[1]" :
        invalidTime ? invalidStory : StubHandler.Story(42, "Saved"))));
    var service = CreateService(handler, clock);
    Check((await service.GetBestStoriesAsync(1))[0].Title == "Saved", "Initial update failed");

    invalidTime = true;
    clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
    Check((await service.GetBestStoriesAsync(1))[0].Title == "Saved", "Invalid time should keep saved results");
    Check((await service.GetBestStoriesAsync(1))[0].Title == "Saved" && handler.Calls == 4,
        "Invalid time should not cause another update during the retry delay");
    clock.Advance(TimeSpan.FromMinutes(15));
    await ExpectUnavailable(service);
}

static async Task<HackerNewsUnavailableException> ExpectUnavailable(BestStoriesService service)
{
    try { await service.GetBestStoriesAsync(1); }
    catch (HackerNewsUnavailableException ex) { return ex; }
    throw new Exception("Expected a Hacker News unavailable exception");
}

static async Task LimitsConcurrency()
{
    await CheckConcurrency(8);
    await CheckConcurrency(2);
}

static async Task CheckConcurrency(int limit)
{
    var settings = DefaultOptions();
    settings.MaxConcurrentRequests = limit;
    var active = 0;
    var maximum = 0;
    using var handler = new StubHandler(async (path, token) =>
    {
        if (path.EndsWith("beststories.json"))
            return StubHandler.Json("[" + string.Join(',', Enumerable.Range(1, 24)) + "]");

        var count = Interlocked.Increment(ref active);
        int observed;
        while ((observed = maximum) < count && Interlocked.CompareExchange(ref maximum, count, observed) != observed) { }
        try
        {
            await Task.Delay(15, token);
            return StubHandler.Json(StubHandler.Story(10, path));
        }
        finally { Interlocked.Decrement(ref active); }
    });
    var stories = await CreateService(handler, settings: settings).GetBestStoriesAsync(24);
    Check(stories.Count == 24 && maximum > 1 && maximum <= limit,
        $"Expected 2–{limit} concurrent item requests, observed {maximum}");
}

static async Task UsesConfiguredCacheTimes()
{
    var settings = DefaultOptions();
    settings.FreshFor = TimeSpan.FromSeconds(2);
    settings.StaleFor = TimeSpan.FromSeconds(8);
    settings.RetryAfterFailure = TimeSpan.FromSeconds(3);
    var clock = new ManualClock();
    var unavailable = false;
    using var handler = new StubHandler((path, _) =>
    {
        if (unavailable) throw new HttpRequestException("Hacker News unavailable");
        return Task.FromResult(StubHandler.Json(path.EndsWith("beststories.json") ? "[1]" :
            StubHandler.Story(10, "Saved")));
    });
    var service = CreateService(handler, clock, settings);
    await service.GetBestStoriesAsync(1);
    clock.Advance(TimeSpan.FromSeconds(1));
    Check((await service.GetBestStoriesAsync(1))[0].Title == "Saved" && handler.Calls == 2,
        "FreshFor should control how long results are reused");

    unavailable = true;
    clock.Advance(TimeSpan.FromSeconds(2));
    Check((await service.GetBestStoriesAsync(1))[0].Title == "Saved" && handler.Calls == 3,
        "The first failed update should return saved results");
    clock.Advance(TimeSpan.FromSeconds(2));
    Check((await service.GetBestStoriesAsync(1))[0].Title == "Saved" && handler.Calls == 3,
        "RetryAfterFailure should delay another update");
    clock.Advance(TimeSpan.FromSeconds(2));
    Check((await service.GetBestStoriesAsync(1))[0].Title == "Saved" && handler.Calls == 4,
        "RetryAfterFailure should permit another update after the delay");
    clock.Advance(TimeSpan.FromSeconds(4));
    await ExpectUnavailable(service);
    Check(handler.Calls == 5, "StaleFor should stop serving saved results when they expire");
}

static async Task UsesConfiguredRequestTimeout()
{
    var settings = DefaultOptions();
    settings.RequestTimeout = TimeSpan.FromMilliseconds(40);
    settings.RefreshTimeout = TimeSpan.FromSeconds(5);
    using var handler = new StubHandler(async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return StubHandler.Json("[1]");
    });
    var service = CreateService(handler, settings: settings);
    var error = await ExpectUnavailable(service).WaitAsync(TimeSpan.FromSeconds(2));
    Check(error.InnerException is OperationCanceledException && handler.Calls == 1,
        "RequestTimeout should cancel a slow Hacker News request");
}

static async Task UsesConfiguredRefreshTimeout()
{
    var settings = DefaultOptions();
    settings.RequestTimeout = TimeSpan.FromSeconds(1);
    settings.RefreshTimeout = TimeSpan.FromMilliseconds(1100);
    settings.MaxConcurrentRequests = 1;
    using var handler = new StubHandler(async (path, token) =>
    {
        if (path.EndsWith("beststories.json")) return StubHandler.Json("[1,2]");
        await Task.Delay(TimeSpan.FromMilliseconds(700), token);
        return StubHandler.Json(StubHandler.Story(10, path));
    });
    var service = CreateService(handler, settings: settings);
    var error = await ExpectUnavailable(service).WaitAsync(TimeSpan.FromSeconds(3));
    Check(error.InnerException is OperationCanceledException && handler.Calls == 3,
        "RefreshTimeout should stop the whole update even when individual requests are fast enough");
}

static async Task RejectsInvalidN()
{
    using var handler = new StubHandler((_, _) => throw new Exception("Should not contact Hacker News"));
    try { await CreateService(handler).GetBestStoriesAsync(0); }
    catch (ArgumentOutOfRangeException) { Check(handler.Calls == 0, "Invalid n made an HTTP request"); return; }
    throw new Exception("Expected invalid n to be rejected");
}
