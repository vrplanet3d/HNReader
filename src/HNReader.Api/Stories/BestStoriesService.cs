using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HNReader.Api.Stories;

public sealed class BestStoriesService(
    IHttpClientFactory httpClientFactory,
    TimeProvider clock,
    ILogger<BestStoriesService> logger,
    IOptions<BestStoriesOptions> options)
{
    private readonly BestStoriesOptions _settings = options.Value;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Snapshot? _snapshot;
    private DateTimeOffset _nextAttempt;

    public async Task<IReadOnlyList<BestStory>> GetBestStoriesAsync(int n, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);

        var snapshot = Volatile.Read(ref _snapshot);
        var now = clock.GetUtcNow();
        if (snapshot?.FreshUntil > now ||
            (snapshot?.StaleUntil > now && _refreshLock.CurrentCount == 0))
            return snapshot.Stories.Take(n).ToArray();

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = clock.GetUtcNow();
            snapshot = Volatile.Read(ref _snapshot);
            if (snapshot?.FreshUntil > now)
                return snapshot.Stories.Take(n).ToArray();

            if (now < _nextAttempt)
            {
                if (snapshot?.StaleUntil > now)
                    return snapshot.Stories.Take(n).ToArray();
                throw new HackerNewsUnavailableException("Waiting to retry Hacker News after a failed update.");
            }

            try
            {
                var stories = await LoadStoriesAsync();
                now = clock.GetUtcNow();
                snapshot = new Snapshot(stories, now + _settings.FreshFor, now + _settings.FreshFor + _settings.StaleFor);
                Volatile.Write(ref _snapshot, snapshot);
                return stories.Take(n).ToArray();
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
            {
                now = clock.GetUtcNow();
                _nextAttempt = now + _settings.RetryAfterFailure;
                logger.LogWarning(ex, "Unable to refresh Hacker News best stories");
                if (snapshot?.StaleUntil > now)
                    return snapshot.Stories.Take(n).ToArray();
                throw new HackerNewsUnavailableException("No usable Hacker News stories are available.", ex);
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<BestStory[]> LoadStoriesAsync()
    {
        using var client = httpClientFactory.CreateClient("HackerNews");
        using var timeout = new CancellationTokenSource(_settings.RefreshTimeout);

        var ids = await client.GetFromJsonAsync<int[]>("beststories.json", timeout.Token)
            ?? throw new JsonException("Hacker News returned a null best stories list.");

        var stories = new ConcurrentBag<(int Id, BestStory Story)>();
        await Parallel.ForEachAsync(ids.Distinct(),
            new ParallelOptions { MaxDegreeOfParallelism = _settings.MaxConcurrentRequests, CancellationToken = timeout.Token },
            async (id, cancellationToken) =>
            {
                var item = await client.GetFromJsonAsync<HackerNewsItem>($"item/{id}.json", cancellationToken);
                if (item is null || item.Deleted || item.Dead || item.Type != "story" ||
                    item.Title is null || item.Score is null || item.Time is null)
                    return;

                DateTimeOffset postedAt;
                try
                {
                    postedAt = DateTimeOffset.FromUnixTimeSeconds(item.Time.Value);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    throw new JsonException($"Hacker News story {id} has a time outside the supported date range.", ex);
                }

                stories.Add((id, new BestStory(item.Title, item.Url, item.By,
                    postedAt, item.Score.Value, item.Descendants ?? 0)));
            });

        return stories.OrderByDescending(entry => entry.Story.Score)
            .ThenBy(entry => entry.Id)
            .Select(entry => entry.Story)
            .ToArray();
    }

    private sealed record Snapshot(BestStory[] Stories, DateTimeOffset FreshUntil, DateTimeOffset StaleUntil);

    private sealed record HackerNewsItem(
        string? Type,
        string? Title,
        string? Url,
        string? By,
        long? Time,
        int? Score,
        int? Descendants,
        bool Deleted,
        bool Dead);
}

public sealed class HackerNewsUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);