using ArenaQuiz.Shared.Functional;
using ArenaQuiz.Shared.Models;

namespace ArenaQuiz.Tests;

public sealed class ScoreEngineTests
{
    [Fact]
    public void CalculateAnswerScore_InstantCorrectAnswer_ReturnsFullBaseAndSpeedBonus()
    {
        var result = ScoreEngine.CalculateAnswerScore(
            isCorrect: true,
            basePoints: 1000,
            timeLimitSeconds: 20,
            elapsedMilliseconds: 0,
            currentStreak: 0,
            mode: GameMode.ClassicSpeed);

        Assert.Equal(500, result.BaseAwarded);
        Assert.Equal(500, result.SpeedBonus);
        Assert.Equal(0, result.StreakBonus);
        Assert.Equal(1000, result.TotalAwarded);
        Assert.Equal(1, result.NewStreak);
    }

    [Fact]
    public void CalculateAnswerScore_MidpointWithStreak_AppliesStreakMultiplierCappedAt50Percent()
    {
        var result = ScoreEngine.CalculateAnswerScore(
            isCorrect: true,
            basePoints: 1000,
            timeLimitSeconds: 20,
            elapsedMilliseconds: 10000,
            currentStreak: 7,
            mode: GameMode.ClassicSpeed);

        // At 50% elapsed: 500 base + 250 speed = 750 raw; streak >= 5 caps at +50% => 375 bonus => 1125 total.
        Assert.Equal(500, result.BaseAwarded);
        Assert.Equal(250, result.SpeedBonus);
        Assert.Equal(375, result.StreakBonus);
        Assert.Equal(1125, result.TotalAwarded);
        Assert.Equal(8, result.NewStreak);
    }

    [Fact]
    public void CalculateAnswerScore_WrongAnswer_ResetsStreakAndAwardsZero()
    {
        var result = ScoreEngine.CalculateAnswerScore(
            isCorrect: false,
            basePoints: 1000,
            timeLimitSeconds: 20,
            elapsedMilliseconds: 1200,
            currentStreak: 4,
            mode: GameMode.ClassicSpeed);

        Assert.Equal(0, result.TotalAwarded);
        Assert.Equal(0, result.NewStreak);
    }

    [Fact]
    public void CalculateAnswerScore_OvertimePastNetworkGrace_AwardsZero()
    {
        var result = ScoreEngine.CalculateAnswerScore(
            isCorrect: true,
            basePoints: 1000,
            timeLimitSeconds: 10,
            elapsedMilliseconds: 12000, // 10s + 1.5s grace = 11500ms max
            currentStreak: 2,
            mode: GameMode.ClassicSpeed);

        Assert.Equal(0, result.TotalAwarded);
        Assert.Equal(0, result.NewStreak);
    }

    [Fact]
    public void ComputeLeaderboard_BreaksTiesByFastestAverageResponseAndPlacesEliminatedLast()
    {
        var players = new[]
        {
            new PlayerStandingDto("p1", "Slow", TeamSide.None, 2000, 0, 1, 2, 2, 4200, true, false, 0),
            new PlayerStandingDto("p2", "EliminatedHigh", TeamSide.None, 3000, 0, 0, 3, 4, 1500, true, true, 0),
            new PlayerStandingDto("p3", "Fast", TeamSide.None, 2000, 0, 2, 2, 2, 1300, true, false, 0)
        };

        var ranked = ScoreEngine.ComputeLeaderboard(players);

        Assert.Equal("Fast", ranked[0].Nickname);
        Assert.Equal(1, ranked[0].Rank);
        Assert.Equal("Slow", ranked[1].Nickname);
        Assert.Equal(2, ranked[1].Rank);
        Assert.Equal("EliminatedHigh", ranked[2].Nickname);
        Assert.Equal(3, ranked[2].Rank);
    }

    [Fact]
    public void ComputeTeamStandings_AggregatesScoresAndAverages()
    {
        var players = new[]
        {
            new PlayerStandingDto("p1", "A1", TeamSide.Alpha, 1200, 0, 1, 1, 1, 1000, true, false, 1),
            new PlayerStandingDto("p2", "A2", TeamSide.Alpha, 800, 0, 1, 1, 1, 2000, true, false, 2),
            new PlayerStandingDto("p3", "B1", TeamSide.Beta, 1500, 0, 1, 1, 1, 900, true, false, 3)
        };

        var teams = ScoreEngine.ComputeTeamStandings(players);

        Assert.Equal(2, teams.Count);
        Assert.Equal(TeamSide.Alpha, teams[0].Team);
        Assert.Equal(2000, teams[0].TotalScore);
        Assert.Equal(1000.0, teams[0].AverageScore);
        Assert.Equal(TeamSide.Beta, teams[1].Team);
        Assert.Equal(1500, teams[1].TotalScore);
    }

    [Fact]
    public void BuildSessionCsv_EscapesCommasAndQuotesPerRfc4180()
    {
        var session = new SessionHistoryDto(
            Guid.NewGuid(),
            "AQ1234",
            "C#, Blazor & \"SignalR\"",
            "Host",
            GameMode.ClassicSpeed,
            1,
            "Amina, CS",
            1000,
            100,
            DateTime.UtcNow.AddMinutes(-5),
            DateTime.UtcNow,
            [
                new PlayerResultDto("Amina, CS", "Solo", 1, 1000, 5, 5, 5, 1234.5, false)
            ]);

        string csv = ScoreEngine.BuildSessionCsv(session);

        Assert.Contains("\"C#, Blazor & \"\"SignalR\"\"\"", csv);
        Assert.Contains("\"Amina, CS\"", csv);
    }

    [Fact]
    public void ResultMonad_MapAndMatch_TransformValuesWithoutExceptions()
    {
        var ok = Result<int>.Success(21).Map(x => x * 2);
        string text = ok.Match(v => $"Value={v}", err => $"Err={err}");

        var fail = Result<int>.Failure("Not found").Map(x => x * 2);
        string failText = fail.Match(v => $"Value={v}", err => $"Err={err}");

        Assert.Equal("Value=42", text);
        Assert.Equal("Err=Not found", failText);
    }
}

