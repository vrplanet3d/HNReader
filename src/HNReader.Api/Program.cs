using HNReader.Api.Stories;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient("HackerNews", client =>
{
    client.BaseAddress = new Uri("https://hacker-news.firebaseio.com/v0/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<BestStoriesService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.MapGet("/api/stories/best", BestStoriesEndpoint.HandleAsync);

app.Run();
