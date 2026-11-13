using System.Diagnostics;
using ArenaQuiz.Client.Services;
using ArenaQuiz.Components;
using ArenaQuiz.Data;
using ArenaQuiz.Hubs;
using ArenaQuiz.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddControllers();
builder.Services.AddSignalR();

builder.Services.AddDbContext<ArenaDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=arenaquiz.db"));

builder.Services.AddScoped<QuizRepositoryService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddSingleton<IGameRoomBroadcaster, SignalRGameRoomBroadcaster>();
builder.Services.AddSingleton<GameRoomService>();

// Client services registered on server with NavigationManager.BaseUri for seamless prerendering.
builder.Services.AddScoped(sp =>
{
    var nav = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
});
builder.Services.AddScoped<QuizApiClient>();
builder.Services.AddScoped<SoundService>();
builder.Services.AddScoped<GameStateContainer>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ArenaDbContext>();
    await DbSeeder.SeedAsync(db);
}

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapControllers();
app.MapHub<GameHub>("/gamehub");

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(ArenaQuiz.Client._Imports).Assembly);

app.Lifetime.ApplicationStarted.Register(() =>
{
    string url = app.Urls.FirstOrDefault() ?? "http://localhost:5152";
    Console.WriteLine();
    Console.WriteLine("============================================================");
    Console.WriteLine("  ArenaQuiz запущен и работает!");
    Console.WriteLine($"  Откройте в браузере: {url}");
    Console.WriteLine("  Для остановки сервера нажмите Ctrl+C");
    Console.WriteLine("============================================================");
    Console.WriteLine();

    if (app.Environment.IsDevelopment() &&
        Environment.GetEnvironmentVariable("ARENAQUIZ_NO_BROWSER") != "1")
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Ignore if headless/restricted environment cannot launch a browser.
        }
    }
});

app.Run();

public partial class Program;
