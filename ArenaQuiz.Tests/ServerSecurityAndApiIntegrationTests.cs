using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ArenaQuiz.Shared.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;

namespace ArenaQuiz.Tests;

public sealed class ArenaQuizWebFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"arenaquiz-int-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("ARENAQUIZ_NO_BROWSER", "1");
        builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={_dbPath}");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch
        {
            // Best-effort cleanup of temp SQLite file.
        }
    }
}

public sealed class ServerSecurityAndApiIntegrationTests(ArenaQuizWebFactory factory)
    : IClassFixture<ArenaQuizWebFactory>
{
    private static readonly Guid SeededQuiz1Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task HealthzAndOpenApi_ReturnOkWithExpectedMetadata()
    {
        using var client = factory.CreateClient();

        var health = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var openApi = await client.GetAsync("/api/openapi.json");
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        string body = await openApi.Content.ReadAsStringAsync();
        Assert.Contains("\"openapi\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/api/quizzes", body);
    }

    [Fact]
    public async Task PublicQuizPreview_DoesNotLeakCorrectOptionIndexOrExplanation()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/quizzes/{SeededQuiz1Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("correctOptionIndex", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("explanation", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QuizMutationAndEditEndpoints_EnforceRealRbacAndAuth()
    {
        using var client = factory.CreateClient();
        var dummyQuiz = new CreateQuizRequest(
            "RBAC Test Quiz",
            "Security",
            "Testing RBAC enforcement",
            "Nobody",
            [new("Q1", null, ["A", "B", "C", "D"], 0, 20, 1000, "Because A")]);

        // 1. Unauthenticated requests blocked with 401
        var unauthPost = await client.PostAsJsonAsync("/api/quizzes", dummyQuiz);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthPost.StatusCode);

        var unauthEdit = await client.GetAsync($"/api/quizzes/{SeededQuiz1Id}/edit");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthEdit.StatusCode);

        // 2. Passwordless quick-switch is strictly blocked for Host accounts
        var hostQuickSwitch = await client.PostAsJsonAsync("/api/auth/quick-switch", new QuickSwitchRoleRequest("tamerlan"));
        Assert.Equal(HttpStatusCode.BadRequest, hostQuickSwitch.StatusCode);

        // 3. Student Player bearer token gets 403 Forbidden on Host routes
        var studentSwitch = await client.PostAsJsonAsync("/api/auth/quick-switch", new QuickSwitchRoleRequest("student"));
        Assert.Equal(HttpStatusCode.OK, studentSwitch.StatusCode);
        var studentSession = await studentSwitch.Content.ReadFromJsonAsync<UserSessionDto>();
        Assert.NotNull(studentSession);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", studentSession.Token);
        var playerCreateAttempt = await client.PostAsJsonAsync("/api/quizzes", dummyQuiz);
        Assert.Equal(HttpStatusCode.Forbidden, playerCreateAttempt.StatusCode);

        // 4. Real Host login succeeds and permits /edit, Create, Update, Delete
        client.DefaultRequestHeaders.Authorization = null;
        var hostLogin = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("tamerlan", "host123"));
        Assert.Equal(HttpStatusCode.OK, hostLogin.StatusCode);
        var hostSession = await hostLogin.Content.ReadFromJsonAsync<UserSessionDto>();
        Assert.NotNull(hostSession);
        Assert.Equal(UserRole.Host, hostSession.Role);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", hostSession.Token);
        var hostEdit = await client.GetAsync($"/api/quizzes/{SeededQuiz1Id}/edit");
        Assert.Equal(HttpStatusCode.OK, hostEdit.StatusCode);
        var detail = await hostEdit.Content.ReadFromJsonAsync<QuizDetailDto>();
        Assert.NotNull(detail);
        Assert.True(detail.Questions.Count > 0);

        var hostCreate = await client.PostAsJsonAsync("/api/quizzes", dummyQuiz);
        Assert.Equal(HttpStatusCode.Created, hostCreate.StatusCode);
        var created = await hostCreate.Content.ReadFromJsonAsync<QuizSummaryDto>();
        Assert.NotNull(created);

        var hostUpdate = await client.PutAsJsonAsync($"/api/quizzes/{created.Id}", dummyQuiz with { Title = "RBAC Updated Quiz" });
        Assert.Equal(HttpStatusCode.OK, hostUpdate.StatusCode);
        var updated = await hostUpdate.Content.ReadFromJsonAsync<QuizSummaryDto>();
        Assert.Equal("RBAC Updated Quiz", updated?.Title);

        var hostDelete = await client.DeleteAsync($"/api/quizzes/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, hostDelete.StatusCode);
    }

    [Fact]
    public async Task SignalRHub_RealConnectionsEnforceHostControlAntiSpoofingAndReclaim()
    {
        // Trigger server host creation so BaseAddress is active
        using var _ = factory.CreateClient();

        await using var hostConn = BuildHubConnection();
        await using var player1Conn = BuildHubConnection();
        await using var player2Conn = BuildHubConnection();
        await using var hostRecoveryConn = BuildHubConnection();

        await hostConn.StartAsync();
        await player1Conn.StartAsync();
        await player2Conn.StartAsync();
        await hostRecoveryConn.StartAsync();

        var createRes = await hostConn.InvokeAsync<Result<RoomSnapshotDto>>(
            "CreateRoom",
            new CreateRoomRequest(SeededQuiz1Id, "Тамерлан (Host)", GameMode.ClassicSpeed, false));

        Assert.True(createRes.IsSuccess);
        string roomCode = createRes.Value!.RoomCode;
        string hostControlToken = createRes.Value.HostControlToken!;

        var p1Snap = await player1Conn.InvokeAsync<Result<RoomSnapshotDto>>(
            "JoinRoom",
            new JoinRoomRequest(roomCode, "PlayerOne", TeamSide.None, null));
        var p2Snap = await player2Conn.InvokeAsync<Result<RoomSnapshotDto>>(
            "JoinRoom",
            new JoinRoomRequest(roomCode, "PlayerTwo", TeamSide.None, null));

        Assert.True(p1Snap.IsSuccess);
        Assert.True(p2Snap.IsSuccess);

        string p1Id = p1Snap.Value!.Players.First(p => p.Nickname == "PlayerOne").PlayerId;
        string p2Id = p2Snap.Value!.Players.First(p => p.Nickname == "PlayerTwo").PlayerId;

        // Non-host player cannot start the game or remove players
        var playerStartAttempt = await player1Conn.InvokeAsync<Result<RoomSnapshotDto>>("StartGame", roomCode);
        Assert.False(playerStartAttempt.IsSuccess);

        var playerKickAttempt = await player1Conn.InvokeAsync<Result<bool>>("RemovePlayer", roomCode, p2Id);
        Assert.False(playerKickAttempt.IsSuccess);

        // Host starts the match
        var hostStart = await hostConn.InvokeAsync<Result<RoomSnapshotDto>>("StartGame", roomCode);
        Assert.True(hostStart.IsSuccess);

        // Player 1 cannot spoof Player 2's PlayerId when submitting an answer
        var spoofSubmit = await player1Conn.InvokeAsync<Result<AnswerAckDto>>(
            "SubmitAnswer",
            new SubmitAnswerRequest(roomCode, p2Id, 0, 0));
        Assert.False(spoofSubmit.IsSuccess);

        // Player 1 submits cleanly with their own connection/PlayerId
        var validSubmit = await player1Conn.InvokeAsync<Result<AnswerAckDto>>(
            "SubmitAnswer",
            new SubmitAnswerRequest(roomCode, p1Id, 0, 0));
        Assert.True(validSubmit.IsSuccess);

        // Invalid recovery token rejected; valid recovery token transfers host control
        var invalidReclaim = await hostRecoveryConn.InvokeAsync<Result<RoomSnapshotDto>>("ReclaimHost", roomCode, "bad-token");
        Assert.False(invalidReclaim.IsSuccess);

        var validReclaim = await hostRecoveryConn.InvokeAsync<Result<RoomSnapshotDto>>("ReclaimHost", roomCode, hostControlToken);
        Assert.True(validReclaim.IsSuccess);

        var pauseAfterReclaim = await hostRecoveryConn.InvokeAsync<Result<bool>>("TogglePauseQuestion", roomCode);
        Assert.True(pauseAfterReclaim.IsSuccess);
        Assert.True(pauseAfterReclaim.Value);

        // Old host connection is no longer authorized
        var oldHostAdvance = await hostConn.InvokeAsync<Result<RoomSnapshotDto>>("NextQuestion", roomCode);
        Assert.False(oldHostAdvance.IsSuccess);
    }

    private HubConnection BuildHubConnection() =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "gamehub"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .Build();
}

