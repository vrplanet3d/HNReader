namespace HNReader.Api.Stories;

public sealed record BestStory(
    string Title,
    string? Uri,
    string? PostedBy,
    DateTimeOffset Time,
    int Score,
    int CommentCount);