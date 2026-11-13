using ArenaQuiz.Data;
using ArenaQuiz.Services;
using ArenaQuiz.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArenaQuiz.Tests;

public sealed class GameRoomServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly FakeBroadcaster _broadcaster = new();
    private readonly GameRoomService _roomService;

    public GameRoomServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ArenaDbContext>(opt => opt.UseSqlite(_connection));
        services.AddScoped<QuizRepositoryService>();
        _provider = services.BuildServiceProvider();

        _roomService = new GameRoomService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _broadcaster,
            NullLogger<GameRoomService>.Instance);
    }

    public async Task InitializeAsync()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArenaDbContext>();
        await DbSeeder.SeedAsync(db);
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
        var quizId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var created = await _roomService.CreateRoomAsync(
            new CreateRoomRequest(quizId, "Тамерлан", GameMode.ClassicSpeed, AutoAdvance: false),
            "host-conn-1");

        Assert.True(created.IsSuccess);
        string code = created.Value!.RoomCode;

        var p1Join = await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "Amina", TeamSide.None, null), "p1-conn");
        var p2Join = await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "Dias", TeamSide.None, null), "p2-conn");
        Assert.True(p1Join.IsSuccess);
        Assert.True(p2Join.IsSuccess);

        // LiveQuestionViewDto must NEVER leak CorrectOptionIndex.
        var started = await _roomService.StartGameAsync(code);
        Assert.True(started.IsSuccess);
        Assert.Equal(RoomPhase.QuestionActive, started.Value!.Phase);
        Assert.NotNull(started.Value.ActiveQuestion);

        string p1Id = p1Join.Value.Player.PlayerId;
        string p2Id = p2Join.Value.Player.PlayerId;

        // Submitting answers for all connected players auto-reveals the question.
        var ack1 = await _roomService.SubmitAnswerAsync(new SubmitAnswerRequest(code, p1Id, 0, 1)); // correct
        var ack2 = await _roomService.SubmitAnswerAsync(new SubmitAnswerRequest(code, p2Id, 0, 0)); // wrong

        Assert.True(ack1.IsSuccess);
        Assert.True(ack2.IsSuccess);
        Assert.NotNull(_broadcaster.LastReveal);
        Assert.Equal(1, _broadcaster.LastReveal!.CorrectOptionIndex);
        Assert.True(_broadcaster.LastReveal.Leaderboard.First(p => p.PlayerId == p1Id).TotalScore > 0);
        Assert.Equal(0, _broadcaster.LastReveal.Leaderboard.First(p => p.PlayerId == p2Id).TotalScore);

        // Advance through remaining questions to finish and persist.
        for (int i = 1; i < started.Value.TotalQuestions; i++)
        {
            var next = await _roomService.NextQuestionAsync(code);
            Assert.True(next.IsSuccess);
            await _roomService.RevealCurrentQuestionAsync(code);
        }

        var finish = await _roomService.NextQuestionAsync(code);
        Assert.True(finish.IsSuccess);
        Assert.Equal(RoomPhase.PodiumFinished, finish.Value!.Phase);
        Assert.NotNull(_broadcaster.LastEndedSession);

        using var scope = _provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<QuizRepositoryService>();
        var saved = await repo.GetSessionByIdAsync(_broadcaster.LastEndedSession!.SessionId);
        Assert.NotNull(saved);
        Assert.Equal(code, saved!.RoomCode);
    }

    [Fact]
    public async Task JoinRoom_ReconnectTokenRestoresDisconnectedPlayerScore()
    {
        var quizId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var room = await _roomService.CreateRoomAsync(new CreateRoomRequest(quizId, "Host", GameMode.ClassicSpeed, false), "host");
        string code = room.Value!.RoomCode;

        var firstJoin = await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "Alisher", TeamSide.None, null), "conn-1");
        string token = firstJoin.Value.Snapshot.ReconnectToken!;

        await _roomService.HandleDisconnectAsync("conn-1");
        var reconnect = await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "Alisher", TeamSide.None, token), "conn-2");

        Assert.True(reconnect.IsSuccess);
        Assert.True(reconnect.Value.IsReconnect);
        Assert.Equal(firstJoin.Value.Player.PlayerId, reconnect.Value.Player.PlayerId);
        Assert.True(reconnect.Value.Player.IsConnected);
    }

    [Fact]
    public async Task KnockoutMode_WrongAnswerEliminatesPlayer()
    {
        var quizId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var room = await _roomService.CreateRoomAsync(new CreateRoomRequest(quizId, "Host", GameMode.KnockoutElimination, false), "host");
        string code = room.Value!.RoomCode;

        var p1 = await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "Survivor", TeamSide.None, null), "c1");
        var p2 = await _roomService.JoinRoomAsync(new JoinRoomRequest(code, "OutPlayer", TeamSide.None, null), "c2");

        await _roomService.StartGameAsync(code);
        await _roomService.SubmitAnswerAsync(new SubmitAnswerRequest(code, p1.Value.Player.PlayerId, 0, 1)); // correct
        await _roomService.SubmitAnswerAsync(new SubmitAnswerRequest(code, p2.Value.Player.PlayerId, 0, 3)); // wrong

        Assert.Contains("OutPlayer", _broadcaster.LastReveal!.NewlyEliminatedNicknames);

        // Next question starts; eliminated player cannot submit answers.
        await _roomService.NextQuestionAsync(code);
        var blocked = await _roomService.SubmitAnswerAsync(new SubmitAnswerRequest(code, p2.Value.Player.PlayerId, 1, 1));
        Assert.False(blocked.IsSuccess);
    }

    private sealed class FakeBroadcaster : IGameRoomBroadcaster
    {
        public QuestionRevealDto? LastReveal { get; private set; }
        public SessionHistoryDto? LastEndedSession { get; private set; }

        public Task RoomStateUpdated(string roomCode, RoomSnapshotDto snapshot) => Task.CompletedTask;
        public Task PlayerJoined(string roomCode, PlayerStandingDto player) => Task.CompletedTask;
        public Task PlayerReconnected(string roomCode, PlayerStandingDto player) => Task.CompletedTask;
        public Task PlayerLeft(string roomCode, string playerId, string nickname) => Task.CompletedTask;
        public Task QuestionStarted(string roomCode, LiveQuestionViewDto question, int remainingSeconds) => Task.CompletedTask;
        public Task TimerTicked(string roomCode, int remainingSeconds) => Task.CompletedTask;
        public Task ReactionBroadcast(string roomCode, ReactionEventDto reaction) => Task.CompletedTask;

        public Task QuestionRevealed(string roomCode, QuestionRevealDto reveal)
        {
            LastReveal = reveal;
            return Task.CompletedTask;
        }

        public Task GameEnded(string roomCode, SessionHistoryDto summary)
        {
            LastEndedSession = summary;
            return Task.CompletedTask;
        }
    }
}

