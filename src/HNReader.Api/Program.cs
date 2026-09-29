using HNReader.Api.Stories;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<BestStoriesOptions>()
    .Bind(builder.Configuration.GetSection(BestStoriesOptions.SectionName))
    .Validate(o => o.FreshFor > TimeSpan.Zero && o.FreshFor <= TimeSpan.FromDays(365),
        "BestStories:FreshFor must be greater than 0 and no more than 365 days.")
    .Validate(o => o.StaleFor > TimeSpan.Zero && o.StaleFor <= TimeSpan.FromDays(365),
        "BestStories:StaleFor must be greater than 0 and no more than 365 days.")
    .Validate(o => o.RetryAfterFailure > TimeSpan.Zero && o.RetryAfterFailure <= TimeSpan.FromDays(1),
        "BestStories:RetryAfterFailure must be greater than 0 and no more than 1 day.")
    .Validate(o => o.RequestTimeout > TimeSpan.Zero && o.RequestTimeout <= TimeSpan.FromHours(1),
        "BestStories:RequestTimeout must be greater than 0 and no more than 1 hour.")
    .Validate(o => o.RefreshTimeout >= o.RequestTimeout && o.RefreshTimeout <= TimeSpan.FromHours(1),
        "BestStories:RefreshTimeout must be at least RequestTimeout and no more than 1 hour.")
    .Validate(o => o.MaxConcurrentRequests is >= 1 and <= 32,
        "BestStories:MaxConcurrentRequests must be between 1 and 32.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient("HackerNews", (services, client) =>
{
    client.BaseAddress = new Uri("https://hacker-news.firebaseio.com/v0/");
    client.Timeout = services.GetRequiredService<IOptions<BestStoriesOptions>>().Value.RequestTimeout;
});
builder.Services.AddSingleton<BestStoriesService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.MapGet("/api/stories/best", BestStoriesEndpoint.HandleAsync);

app.Run();
