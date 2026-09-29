using System.Text.Json;

namespace HNReader.Api.Stories;

public static class BestStoriesEndpoint
{
    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions IndentedJson = new(CompactJson) { WriteIndented = true };

    public static async Task<IResult> HandleAsync(
        string? n, string? pretty, BestStoriesService stories, CancellationToken cancellationToken)
    {
        if (!int.TryParse(n, out var count) || count <= 0)
            return Results.BadRequest(new { error = "Supply a positive integer n, for example ?n=10." });

        var indented = false;
        if (pretty is not null && !bool.TryParse(pretty, out indented))
            return Results.BadRequest(new { error = "Supply pretty=true or pretty=false." });

        try
        {
            var best = await stories.GetBestStoriesAsync(count, cancellationToken);
            return Results.Json(best, indented ? IndentedJson : CompactJson);
        }
        catch (HackerNewsUnavailableException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Hacker News is temporarily unavailable. Please try again later.");
        }
    }
}