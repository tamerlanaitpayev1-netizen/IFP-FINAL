using System.Diagnostics;
using ArenaQuiz.Client.Services;
using ArenaQuiz.Components;
using ArenaQuiz.Data;
using ArenaQuiz.Hubs;
using ArenaQuiz.Security;
using ArenaQuiz.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddHealthChecks();

builder.Services.AddDbContext<ArenaDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=arenaquiz.db"));

builder.Services.AddSingleton<TokenService>();
builder.Services.AddAuthentication(ArenaBearerAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ArenaBearerAuthHandler>(ArenaBearerAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

builder.Services.AddScoped<QuizRepositoryService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddHttpClient<AiQuizGeneratorService>();
builder.Services.AddSingleton<IGameRoomBroadcaster, SignalRGameRoomBroadcaster>();
builder.Services.AddSingleton<GameRoomService>();
builder.Services.AddHostedService<RoomCleanupHostedService>();

// Client services registered on server with NavigationManager.BaseUri for seamless prerendering.
builder.Services.AddScoped(sp =>
{
    var nav = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
});
builder.Services.AddScoped<LocalizationService>();
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

app.UseWhen(
    ctx => !ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/gamehub"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseAntiforgery();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapControllers();
app.MapHub<GameHub>("/gamehub");
app.MapHealthChecks("/healthz");

app.MapGet("/api/openapi.json", () => Results.Ok(new
{
    openapi = "3.0.3",
    info = new { title = "ArenaQuiz Real-Time API", version = "2.0.0" },
    paths = new Dictionary<string, object>
    {
        ["/api/quizzes"] = new { get = "List quiz summaries", post = "[Host] Create quiz" },
        ["/api/quizzes/{id}"] = new { get = "Public quiz preview (omits answers)", put = "[Host] Update quiz", delete = "[Host] Delete quiz" },
        ["/api/quizzes/{id}/edit"] = new { get = "[Host] Full quiz payload with answers" },
        ["/api/rooms/open"] = new { get = "Active lobbies" },
        ["/api/sessions/analytics"] = new { get = "Session history and hardest question stats" },
        ["/api/sessions/{id}/export.csv"] = new { get = "RFC-4180 CSV export" },
        ["/api/auth/login"] = new { post = "PBKDF2 login returning signed HMAC-SHA256 bearer token" },
        ["/healthz"] = new { get = "Liveness health check" },
        ["/gamehub"] = new { signalr = "Real-time GameHub (WebSockets)" }
    }
}));

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
