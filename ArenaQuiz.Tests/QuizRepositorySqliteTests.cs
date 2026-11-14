using System.Security.Claims;
using ArenaQuiz.Data;
using ArenaQuiz.Security;
using ArenaQuiz.Services;
using ArenaQuiz.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ArenaQuiz.Tests;

public sealed class QuizRepositorySqliteTests : IAsyncLifetime
{
    private SqliteConnection _connection = default!;
    private ArenaDbContext _db = default!;
    private QuizRepositoryService _repo = default!;
    private TokenService _tokenService = default!;
    private AuthService _auth = default!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ArenaDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new ArenaDbContext(options);
        await DbSeeder.SeedAsync(_db);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:TokenSigningKey"] = "UnitTest_SigningKey_With_High_Entropy_2026!"
            })
            .Build();

        _tokenService = new TokenService(config);
        _repo = new QuizRepositoryService(_db);
        _auth = new AuthService(_db, _tokenService);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task SeedAsync_PopulatesThreeQuizzesAndThreeHistoricalSessions()
    {
        var quizzes = await _repo.GetAllQuizSummariesAsync();
        Assert.Equal(3, quizzes.Count);

        var analytics = await _repo.GetAnalyticsOverviewAsync();
        Assert.Equal(3, analytics.TotalSessionsPlayed);
        Assert.True(analytics.TotalParticipants >= 9);
        Assert.Equal(3, analytics.RecentSessions.Count);
        Assert.NotNull(analytics.QuestionStats);
        Assert.True(analytics.QuestionStats.Count > 0);
    }

    [Fact]
    public async Task CreateQuizAsync_ValidRequest_PersistsPreviewUpdatesAndDeletesCleanly()
    {
        var req = new CreateQuizRequest(
            "SQLite Concurrency Test Quiz",
            "Databases",
            "Testing EF Core roundtrips",
            "Tamerlan",
            [
                new(
                    "Which keyword makes records immutable by value equality in C#?",
                    null,
                    ["class", "record", "volatile", "dynamic"],
                    1,
                    25,
                    1000,
                    "C# record types provide value equality and init-only immutability.")
            ]);

        var created = await _repo.CreateQuizAsync(req);
        Assert.True(created.IsSuccess);
        Guid createdId = created.Value!.Id;

        // Public preview projection returns QuestionPublicDto without answer key properties
        var publicPreview = await _repo.GetQuizPublicPreviewAsync(createdId);
        Assert.NotNull(publicPreview);
        Assert.Single(publicPreview.Questions);
        Assert.Null(typeof(QuestionPublicDto).GetProperty("CorrectOptionIndex"));

        // Host edit detail includes CorrectOptionIndex
        var editDetail = await _repo.GetQuizDetailAsync(createdId);
        Assert.NotNull(editDetail);
        Assert.Equal(1, editDetail.Questions[0].CorrectOptionIndex);

        // Update quiz title & add second question
        var updateRes = await _repo.UpdateQuizAsync(createdId, req with
        {
            Title = "Updated SQLite Quiz",
            Questions =
            [
                req.Questions[0],
                new("Second added question?", null, ["A", "B", "C", "D"], 0, 15, 500, "Updated")
            ]
        });
        Assert.True(updateRes.IsSuccess);
        Assert.Equal("Updated SQLite Quiz", updateRes.Value!.Title);
        Assert.Equal(2, updateRes.Value.QuestionCount);

        Assert.True(await _repo.DeleteQuizAsync(createdId));
        Assert.Null(await _repo.GetQuizDetailAsync(createdId));
    }

    [Fact]
    public async Task CreateQuizAsync_EmptyTitle_ReturnsFailureResult()
    {
        var invalidReq = new CreateQuizRequest("  ", "Cat", "Desc", "Host", []);
        var result = await _repo.CreateQuizAsync(invalidReq);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task AuthService_ValidatesPbkdf2CredentialsAndRestrictsQuickSwitchToPlayers()
    {
        var validHost = await _auth.LoginAsync(new LoginRequest("tamerlan", "host123"));
        Assert.True(validHost.IsSuccess);
        Assert.Equal(UserRole.Host, validHost.Value!.Role);

        var principal = _tokenService.ValidateToken(validHost.Value.Token);
        Assert.NotNull(principal);
        Assert.True(principal.IsInRole(nameof(UserRole.Host)));
        Assert.Equal("tamerlan", principal.Identity?.Name);

        var invalidPass = await _auth.LoginAsync(new LoginRequest("tamerlan", "wrong-password"));
        Assert.False(invalidPass.IsSuccess);

        // Quick-switch only permits Player role
        var playerSwitch = await _auth.QuickSwitchPlayerAsync("student");
        Assert.True(playerSwitch.IsSuccess);
        Assert.Equal(UserRole.Player, playerSwitch.Value!.Role);

        // Passwordless elevation to Host MUST be rejected
        var blockedHostSwitch = await _auth.QuickSwitchPlayerAsync("tamerlan");
        Assert.False(blockedHostSwitch.IsSuccess);
        Assert.Contains("password", blockedHostSwitch.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegisterAsync_ForcesPlayerRoleAndAllowsTwoOptionQuestions()
    {
        string uniqueUser = $"player_{Guid.NewGuid():N}"[..16];
        var registerRes = await _auth.RegisterAsync(new RegisterRequest(uniqueUser, "New Student", "secret123", UserRole.Host));
        Assert.True(registerRes.IsSuccess);
        Assert.Equal(UserRole.Player, registerRes.Value!.Role);

        var twoOptReq = new CreateQuizRequest(
            "True/False Check",
            "Testing",
            "2-option quiz validation",
            "Host",
            [new CreateQuestionRequest("HTTP is stateless by default?", null, ["True", "False"], 0, 15, 1000, "Yes")]);

        var createRes = await _repo.CreateQuizAsync(twoOptReq);
        Assert.True(createRes.IsSuccess);
        var loaded = await _repo.GetQuizDetailAsync(createRes.Value!.Id);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Questions[0].Options.Count);
    }
}
