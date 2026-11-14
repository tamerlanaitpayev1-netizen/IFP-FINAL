using ArenaQuiz.Client.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
});

builder.Services.AddScoped<LocalizationService>();
builder.Services.AddScoped<QuizApiClient>();
builder.Services.AddScoped<SoundService>();
builder.Services.AddScoped<GameStateContainer>();

await builder.Build().RunAsync();
