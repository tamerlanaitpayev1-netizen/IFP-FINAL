using ArenaQuiz.Data;
using ArenaQuiz.Security;
using ArenaQuiz.Services;
using ArenaQuiz.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArenaQuiz.Tests;

public sealed class GameRoomServiceTests : IAsyncLifetime
{
    private SqliteConnection _connection = default!;
    private ServiceProvider _provider = default!;
    private FakeBroadcaster _broadcaster = default!;
    private GameRoomService _roomService = default!;
    private Guid _quizId;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<ArenaDbContext>(opt => opt.UseSqlite(_connection));
        services.AddScoped<QuizRepositoryService>();
        services.AddSingleton(new TokenService(new ConfigurationBuilder().Build()));
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<GameRoomService>>(NullLogger<GameRoomService>.Instance);

        _broadcaster = new FakeBroadcaster();
        services.AddSingleton<IGameRoomBroadcaster>(_broadcaster);
        services.AddSingleton<GameRoomService>();

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArenaDbContext>();
        await db.Database.EnsureCreatedAsync();

        var repo = scope.ServiceProvider.GetRequiredService<QuizRepositoryService>();
        var created = await repo.CreateQuizAsync(new CreateQuizRequest(
            "Test Quiz",
            "Testing",
            "Automated test suite quiz",
            "TestRunner",
            [
                new("Question 1?", null, ["Correct A", "Wrong B", "Wrong C", "Wrong D"], 0, 20, 1000, "A is right."),
                new("Question 2?", null, ["Correct A", "Wrong B", "Wrong C", "Wrong D"], 0, 20, 1000, "A is still right.")
            ]));

        _quizId = created.Value!.Id;
        _roomService = _provider.GetRequiredService<GameRoomService>();
    }

    public async Task DisposeAsync()
    {
        _roomService.Dispose();
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task CreateJoinStartSubmitAndFinish_FullRoomLifecycle_PersistsSessionToSqlite()
    {
        const string hostConn = "conn-host-1";
        const string p1Conn = "conn-p1";
        const string p2Conn = "conn-p2";

        var createRes = await _roomService.CreateRoomAsync(new CreateRoomRequest(_quizId, "Host", GameMode.ClassicSpeed, false), hostConn);
        Assert.True(createRes.IsSuccess);
        string code = createRes.Value!.RoomCode;
        Assert.False(string.IsNullOrWhiteSpace(createRes.Value.HostControlToken));

        var p1Join = await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "Alice", TeamSide.None, null), p1Conn);
        var p2Join = await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "Bob", TeamSide.None, null), p2Conn);
        Assert.True(p1Join.IsSuccess);
        Assert.True(p2Join.IsSuccess);

        var startRes = await _roomService.StartGameAsync(code, hostConn);
        Assert.True(startRes.IsSuccess);
        Assert.Equal(RoomPhase.QuestionActive, startRes.Value!.Phase);

        var ack1 = await _roomService.SubmitAnswerAsync(new SubmitAnswerRequest(code, p1Join.Value.Player.PlayerId, 0, 0), p1Conn);
        Assert.True(ack1.IsSuccess);
        Assert.Single(_broadcaster.AnswerProgressEvents);
        Assert.Equal(1, _broadcaster.AnswerProgressEvents[0].Progress.AnsweredCount);

        // Second (last) player submission triggers immediate reveal
        var ack2 = await _roomService.SubmitAnswerAsync(new SubmitAnswerRequest(code, p2Join.Value.Player.PlayerId, 0, 1), p2Conn);
        Assert.True(ack2.IsSuccess);
        Assert.Single(_broadcaster.RevealedEvents);

        // Advance to Question 2 and finish match
        var q2Res = await _roomService.NextQuestionAsync(code, hostConn);
        Assert.True(q2Res.IsSuccess);
        Assert.Equal(1, q2Res.Value!.CurrentQuestionIndex);

        await _roomService.SubmitAnswerAsync(new SubmitAnswerRequest(code, p1Join.Value.Player.PlayerId, 1, 0), p1Conn);
        await _roomService.SubmitAnswerAsync(new SubmitAnswerRequest(code, p2Join.Value.Player.PlayerId, 1, 0), p2Conn);

        var endRes = await _roomService.NextQuestionAsync(code, hostConn);
        Assert.True(endRes.IsSuccess);
        Assert.Equal(RoomPhase.PodiumFinished, endRes.Value!.Phase);
        Assert.Single(_broadcaster.EndedSessions);
        Assert.Equal("Alice", _broadcaster.EndedSessions[0].WinnerNickname);

        using var scope = _provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<QuizRepositoryService>();
        var analytics = await repo.GetAnalyticsOverviewAsync();
        Assert.Equal(1, analytics.TotalSessionsPlayed);
        Assert.NotNull(analytics.QuestionStats);
        Assert.Equal(2, analytics.QuestionStats.Count);
    }

    [Fact]
    public async Task JoinRoom_ReconnectTokenRestoresDisconnectedPlayerScore()
    {
        const string hostConn = "conn-host-reconn";
        const string p1Conn = "conn-p1-initial";
        const string p1NewConn = "conn-p1-new";

        var createRes = await _roomService.CreateRoomAsync(new CreateRoomRequest(_quizId, "Host", GameMode.ClassicSpeed, false), hostConn);
        string code = createRes.Value!.RoomCode;

        var joinRes = await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "Amina", TeamSide.None, null), p1Conn);
        string token = joinRes.Value.Snapshot.ReconnectToken!;
        string playerId = joinRes.Value.Player.PlayerId;

        await _roomService.StartGameAsync(code, hostConn);
        await _roomService.SubmitAnswerAsync(new SubmitAnswerRequest(code, playerId, 0, 0), p1Conn);
        await _roomService.HandleDisconnectAsync(p1Conn);

        var rejoinRes = await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "Amina", TeamSide.None, token), p1NewConn);
        Assert.True(rejoinRes.IsSuccess);
        Assert.True(rejoinRes.Value.IsReconnect);
        Assert.Equal(playerId, rejoinRes.Value.Player.PlayerId);
        Assert.True(rejoinRes.Value.Player.IsConnected);
        Assert.True(rejoinRes.Value.Player.TotalScore > 0);
    }

    [Fact]
    public async Task JoinRoom_BlocksNicknameHijackWithoutReconnectToken()
    {
        const string hostConn = "conn-host-hijack";
        const string legConn = "conn-legit";
        const string attackerConn = "conn-attacker";

        var createRes = await _roomService.CreateRoomAsync(new CreateRoomRequest(_quizId, "Host", GameMode.ClassicSpeed, false), hostConn);
        string code = createRes.Value!.RoomCode;

        await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "Dias", TeamSide.None, null), legConn);
        await _roomService.HandleDisconnectAsync(legConn);

        var hijackAttempt = await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "Dias", TeamSide.None, null), attackerConn);
        Assert.False(hijackAttempt.IsSuccess);
        Assert.Contains("already registered", hijackAttempt.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReclaimHost_ValidTokenTransfersControlAndInvalidTokenFails()
    {
        const string hostConn = "conn-host-old";
        const string newHostConn = "conn-host-refreshed";

        var createRes = await _roomService.CreateRoomAsync(new CreateRoomRequest(_quizId, "Host", GameMode.ClassicSpeed, false), hostConn);
        string code = createRes.Value!.RoomCode;
        string hostToken = createRes.Value.HostControlToken!;

        var badReclaim = await _roomService.ReclaimHostAsync(code, "wrong-token-value", newHostConn);
        Assert.False(badReclaim.IsSuccess);

        var goodReclaim = await _roomService.ReclaimHostAsync(code, hostToken, newHostConn);
        Assert.True(goodReclaim.IsSuccess);

        await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "Player1", TeamSide.None, null), "conn-p1");

        var oldHostTryStart = await _roomService.StartGameAsync(code, hostConn);
        Assert.False(oldHostTryStart.IsSuccess);

        var newHostTryStart = await _roomService.StartGameAsync(code, newHostConn);
        Assert.True(newHostTryStart.IsSuccess);
    }

    [Fact]
    public async Task HostActions_NonHostConnectionRejectedAcrossLifecycle()
    {
        const string hostConn = "conn-host-sec";
        const string playerConn = "conn-player-sec";

        var createRes = await _roomService.CreateRoomAsync(new CreateRoomRequest(_quizId, "Host", GameMode.ClassicSpeed, false), hostConn);
        string code = createRes.Value!.RoomCode;
        var joinRes = await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "P1", TeamSide.None, null), playerConn);

        Assert.False((await _roomService.StartGameAsync(code, playerConn)).IsSuccess);
        Assert.False((await _roomService.RemovePlayerAsync(code, joinRes.Value.Player.PlayerId, playerConn)).IsSuccess);

        Assert.True((await _roomService.StartGameAsync(code, hostConn)).IsSuccess);

        Assert.False((await _roomService.TogglePauseQuestionAsync(code, playerConn)).IsSuccess);
        Assert.False((await _roomService.RevealCurrentQuestionAsync(code, playerConn)).IsSuccess);
        Assert.False((await _roomService.NextQuestionAsync(code, playerConn)).IsSuccess);
    }

    [Fact]
    public async Task SubmitAnswer_SpoofedPlayerIdFromDifferentConnection_IsRejected()
    {
        const string hostConn = "conn-host-spoof";
        const string p1Conn = "conn-p1-spoof";
        const string p2Conn = "conn-p2-spoof";

        var createRes = await _roomService.CreateRoomAsync(new CreateRoomRequest(_quizId, "Host", GameMode.ClassicSpeed, false), hostConn);
        string code = createRes.Value!.RoomCode;

        var p1 = await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "P1", TeamSide.None, null), p1Conn);
        var p2 = await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "P2", TeamSide.None, null), p2Conn);

        await _roomService.StartGameAsync(code, hostConn);

        var spoofAttempt = await _roomService.SubmitAnswerAsync(
            new SubmitAnswerRequest(code, p2.Value.Player.PlayerId, 0, 0),
            p1Conn);

        Assert.False(spoofAttempt.IsSuccess);
        Assert.Contains("another player", spoofAttempt.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True((await _roomService.SubmitAnswerAsync(new SubmitAnswerRequest(code, p1.Value.Player.PlayerId, 0, 0), p1Conn)).IsSuccess);
    }

    [Fact]
    public async Task TogglePauseQuestion_PausesBlocksAnswersAndResumesCleanly()
    {
        const string hostConn = "conn-host-pause";
        const string p1Conn = "conn-p1-pause";

        var createRes = await _roomService.CreateRoomAsync(new CreateRoomRequest(_quizId, "Host", GameMode.ClassicSpeed, false), hostConn);
        string code = createRes.Value!.RoomCode;
        var p1 = await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "Runner", TeamSide.None, null), p1Conn);
        await _roomService.StartGameAsync(code, hostConn);

        var pauseRes = await _roomService.TogglePauseQuestionAsync(code, hostConn);
        Assert.True(pauseRes.IsSuccess);
        Assert.True(pauseRes.Value);

        var blockedWhilePaused = await _roomService.SubmitAnswerAsync(new SubmitAnswerRequest(code, p1.Value.Player.PlayerId, 0, 0), p1Conn);
        Assert.False(blockedWhilePaused.IsSuccess);
        Assert.Contains("paused", blockedWhilePaused.Error, StringComparison.OrdinalIgnoreCase);

        var resumeRes = await _roomService.TogglePauseQuestionAsync(code, hostConn);
        Assert.True(resumeRes.IsSuccess);
        Assert.False(resumeRes.Value);

        var acceptedAfterResume = await _roomService.SubmitAnswerAsync(new SubmitAnswerRequest(code, p1.Value.Player.PlayerId, 0, 0), p1Conn);
        Assert.True(acceptedAfterResume.IsSuccess);
        Assert.Equal(2, _broadcaster.PauseEvents.Count);
    }

    [Fact]
    public async Task KnockoutMode_WrongAnswerEliminatesPlayer()
    {
        const string hostConn = "conn-host-ko";
        const string p1Conn = "conn-ko-survivor";
        const string p2Conn = "conn-ko-victim";

        var createRes = await _roomService.CreateRoomAsync(new CreateRoomRequest(_quizId, "Host", GameMode.KnockoutElimination, false), hostConn);
        string code = createRes.Value!.RoomCode;

        var survivor = await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "Survivor", TeamSide.None, null), p1Conn);
        var knocked = await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "KnockedOut", TeamSide.None, null), p2Conn);

        await _roomService.StartGameAsync(code, hostConn);
        await _roomService.SubmitAnswerAsync(new SubmitAnswerRequest(code, survivor.Value.Player.PlayerId, 0, 0), p1Conn);
        await _roomService.SubmitAnswerAsync(new SubmitAnswerRequest(code, knocked.Value.Player.PlayerId, 0, 2), p2Conn);

        var reveal = _broadcaster.RevealedEvents.Single();
        Assert.Contains("KnockedOut", reveal.NewlyEliminatedNicknames);
        Assert.True(reveal.Leaderboard.First(p => p.Nickname == "KnockedOut").IsEliminated);
        Assert.False(reveal.Leaderboard.First(p => p.Nickname == "Survivor").IsEliminated);
    }

    [Fact]
    public async Task KnockoutMode_DisconnectedPlayerWhoDidNotAnswerIsNotEliminated()
    {
        const string hostConn = "conn-host-kodrop";
        const string p1Conn = "conn-kodrop-1";
        const string p2Conn = "conn-kodrop-2";

        var createRes = await _roomService.CreateRoomAsync(new CreateRoomRequest(_quizId, "Host", GameMode.KnockoutElimination, false), hostConn);
        string code = createRes.Value!.RoomCode;

        await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "DroppedPlayer", TeamSide.None, null), p1Conn);
        await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "SilentConnected", TeamSide.None, null), p2Conn);

        await _roomService.StartGameAsync(code, hostConn);
        await _roomService.HandleDisconnectAsync(p1Conn);

        var revealRes = await _roomService.RevealCurrentQuestionAsync(code, hostConn);
        Assert.True(revealRes.IsSuccess);
        Assert.DoesNotContain("DroppedPlayer", revealRes.Value!.NewlyEliminatedNicknames);
        Assert.Contains("SilentConnected", revealRes.Value.NewlyEliminatedNicknames);
    }

    [Fact]
    public async Task CleanupExpiredRooms_ZeroTtlEvictsAbandonedRooms()
    {
        var createRes = await _roomService.CreateRoomAsync(new CreateRoomRequest(_quizId, "Host", GameMode.ClassicSpeed, false), "conn-evict");
        string code = createRes.Value!.RoomCode;
        Assert.Contains(_roomService.GetOpenRooms(), r => r.RoomCode == code);

        int evicted = _roomService.CleanupExpiredRooms(TimeSpan.Zero, TimeSpan.Zero);
        Assert.Equal(1, evicted);
        Assert.DoesNotContain(_roomService.GetOpenRooms(), r => r.RoomCode == code);
    }

    private sealed class FakeBroadcaster : IGameRoomBroadcaster
    {
        public List<(string RoomCode, AnswerProgressDto Progress)> AnswerProgressEvents { get; } = [];
        public List<(string RoomCode, bool IsPaused, int RemainingSeconds)> PauseEvents { get; } = [];
        public List<QuestionRevealDto> RevealedEvents { get; } = [];
        public List<SessionHistoryDto> EndedSessions { get; } = [];
        public List<ReactionEventDto> ReactionEvents { get; } = [];

        public Task RoomStateUpdated(string roomCode, RoomSnapshotDto snapshot) => Task.CompletedTask;
        public Task PlayerJoined(string roomCode, PlayerStandingDto player) => Task.CompletedTask;
        public Task PlayerReconnected(string roomCode, PlayerStandingDto player) => Task.CompletedTask;
        public Task PlayerLeft(string roomCode, string playerId, string nickname) => Task.CompletedTask;
        public Task QuestionStarted(string roomCode, LiveQuestionViewDto question, int remainingSeconds) => Task.CompletedTask;
        public Task TimerTicked(string roomCode, int remainingSeconds) => Task.CompletedTask;

        public Task AnswerProgressUpdated(string roomCode, AnswerProgressDto progress)
        {
            AnswerProgressEvents.Add((roomCode, progress));
            return Task.CompletedTask;
        }

        public Task PauseStateChanged(string roomCode, bool isPaused, int remainingSeconds)
        {
            PauseEvents.Add((roomCode, isPaused, remainingSeconds));
            return Task.CompletedTask;
        }

        public Task QuestionRevealed(string roomCode, QuestionRevealDto reveal)
        {
            RevealedEvents.Add(reveal);
            return Task.CompletedTask;
        }

        public Task GameEnded(string roomCode, SessionHistoryDto summary)
        {
            EndedSessions.Add(summary);
            return Task.CompletedTask;
        }

        public Task ReactionBroadcast(string roomCode, ReactionEventDto reaction)
        {
            ReactionEvents.Add(reaction);
            return Task.CompletedTask;
        }
    }
}
