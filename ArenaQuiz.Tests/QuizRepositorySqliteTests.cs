using ArenaQuiz.Data;
using ArenaQuiz.Services;
using ArenaQuiz.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ArenaQuiz.Tests;

public sealed class QuizRepositorySqliteTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly ArenaDbContext _db;

    public QuizRepositorySqliteTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<ArenaDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new ArenaDbContext(options);
    }

    public async Task InitializeAsync()
    {
        await DbSeeder.SeedAsync(_db);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task SeedAsync_PopulatesThreeQuizzesAndThreeHistoricalSessions()
    {
        var repo = new QuizRepositoryService(_db);
        var quizzes = await repo.GetAllQuizSummariesAsync();
        var analytics = await repo.GetAnalyticsOverviewAsync();

        Assert.Equal(3, quizzes.Count);
        Assert.Equal(3, analytics.TotalSessionsPlayed);
        Assert.True(analytics.TotalParticipants >= 15);
    }

    [Fact]
    public async Task CreateQuizAsync_ValidRequest_PersistsAndDeletesCleanly()
    {
        var repo = new QuizRepositoryService(_db);
        var request = new CreateQuizRequest(
            "Custom Architecture Quiz",
            "Architecture",
            "Test description",
            "Диас",
            [
                new CreateQuestionRequest(
                    "Which protocol powers SignalR duplex messaging?",
                    null,
                    ["WebSockets", "FTP", "SMTP", "ICMP"],
                    0,
                    20,
                    1000,
                    "WebSockets provide full-duplex persistent framing.")
            ]);

        var created = await repo.CreateQuizAsync(request);
        Assert.True(created.IsSuccess);
        Assert.Equal("Custom Architecture Quiz", created.Value!.Title);

        var loaded = await repo.GetQuizDetailAsync(created.Value.Id);
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Questions);

        bool deleted = await repo.DeleteQuizAsync(created.Value.Id);
        Assert.True(deleted);
    }

    [Fact]
    public async Task CreateQuizAsync_EmptyTitle_ReturnsFailureResult()
    {
        var repo = new QuizRepositoryService(_db);
        var result = await repo.CreateQuizAsync(new CreateQuizRequest("", "Cat", "Desc", "Host", []));
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task AuthService_ValidatesCredentialsAndSupportsQuickSwitch()
    {
        var auth = new AuthService(_db);

        var valid = await auth.LoginAsync(new LoginRequest("tamerlan", "host123"));
        var invalid = await auth.LoginAsync(new LoginRequest("tamerlan", "wrong"));
        var playerSwitch = await auth.QuickSwitchAsync("student");

        Assert.True(valid.IsSuccess);
        Assert.Equal(UserRole.Host, valid.Value!.Role);
        Assert.False(invalid.IsSuccess);
        Assert.True(playerSwitch.IsSuccess);
        Assert.Equal(UserRole.Player, playerSwitch.Value!.Role);
    }
}

