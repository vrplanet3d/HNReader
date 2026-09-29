namespace HNReader.Api.Stories;

public sealed class BestStoriesOptions
{
    public const string SectionName = "BestStories";

    public TimeSpan FreshFor { get; set; }
    public TimeSpan StaleFor { get; set; }
    public TimeSpan RetryAfterFailure { get; set; }
    public TimeSpan RequestTimeout { get; set; }
    public TimeSpan RefreshTimeout { get; set; }
    public int MaxConcurrentRequests { get; set; }
    public int CircuitBreakerFailureThreshold { get; set; }
    public TimeSpan CircuitBreakerOpenFor { get; set; }
}