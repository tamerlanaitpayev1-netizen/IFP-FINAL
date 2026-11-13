using System.Text;
using ArenaQuiz.Shared.Models;

namespace ArenaQuiz.Shared.Functional;

/// <summary>
/// Breakdown of a single player's score calculation for a question.
/// </summary>
public sealed record AnswerScoreBreakdown(
    int BaseAwarded,
    int SpeedBonus,
    int StreakBonus,
    int TotalAwarded,
    int NewStreak);

/// <summary>
/// Pure stateless functional engine for scoring, leaderboard ranking, team aggregation, and RFC-4180 CSV serialization.
/// </summary>
public static class ScoreEngine
{
    public const int NetworkGraceMilliseconds = 1500;

    /// <summary>
    /// Computes speed-weighted and streak-multiplied score purely from server-measured elapsed time.
    /// Complexity: O(1) time and O(1) space.
    /// </summary>
    public static AnswerScoreBreakdown CalculateAnswerScore(
        bool isCorrect,
        int basePoints,
        int timeLimitSeconds,
        double elapsedMilliseconds,
        int currentStreak,
        GameMode mode)
    {
        if (!isCorrect || basePoints <= 0 || timeLimitSeconds <= 0)
        {
            return new AnswerScoreBreakdown(0, 0, 0, 0, 0);
        }

        double maxWindowMs = (timeLimitSeconds * 1000.0) + NetworkGraceMilliseconds;
        if (elapsedMilliseconds < 0 || elapsedMilliseconds > maxWindowMs)
        {
            return new AnswerScoreBreakdown(0, 0, 0, 0, 0);
        }

        double timeLimitMs = timeLimitSeconds * 1000.0;
        double clampedElapsed = Math.Clamp(elapsedMilliseconds, 0.0, timeLimitMs);
        double speedRatio = Math.Max(0.0, 1.0 - (clampedElapsed / timeLimitMs));

        int baseAwarded = (int)Math.Floor(basePoints * 0.5);
        int speedBonus = (int)Math.Floor(basePoints * 0.5 * speedRatio);
        int rawScore = baseAwarded + speedBonus;

        int newStreak = currentStreak + 1;
        double streakMultiplier = mode switch
        {
            GameMode.ClassicSpeed => Math.Min(0.50, currentStreak * 0.10),
            GameMode.TeamBattle => Math.Min(0.40, currentStreak * 0.10),
            GameMode.KnockoutElimination => Math.Min(0.50, currentStreak * 0.15),
            _ => 0.0
        };

        int streakBonus = (int)Math.Floor(rawScore * streakMultiplier);
        int totalAwarded = rawScore + streakBonus;

        return new AnswerScoreBreakdown(baseAwarded, speedBonus, streakBonus, totalAwarded, newStreak);
    }

    /// <summary>
    /// Ranks players deterministically: active players first, then highest score, then fastest average response.
    /// Complexity: O(P log P) time and O(P) space where P is player count.
    /// </summary>
    public static IReadOnlyList<PlayerStandingDto> ComputeLeaderboard(IEnumerable<PlayerStandingDto> players)
    {
        return players
            .OrderBy(p => p.IsEliminated ? 1 : 0)
            .ThenByDescending(p => p.TotalScore)
            .ThenBy(p => p.AverageResponseMs <= 0 ? double.MaxValue : p.AverageResponseMs)
            .ThenBy(p => p.Nickname, StringComparer.OrdinalIgnoreCase)
            .Select((player, index) => player with { Rank = index + 1 })
            .ToList();
    }

    /// <summary>
    /// Aggregates team standings using pure LINQ grouping.
    /// Complexity: O(P + T log T) time where T is number of teams.
    /// </summary>
    public static IReadOnlyList<TeamStandingDto> ComputeTeamStandings(IEnumerable<PlayerStandingDto> players)
    {
        return players
            .Where(p => p.Team != TeamSide.None)
            .GroupBy(p => p.Team)
            .Select(group =>
            {
                int count = group.Count();
                int total = group.Sum(x => x.TotalScore);
                double avg = count > 0 ? Math.Round((double)total / count, 1) : 0.0;
                return new TeamStandingDto(
                    group.Key,
                    FormatTeamName(group.Key),
                    total,
                    count,
                    avg);
            })
            .OrderByDescending(t => t.TotalScore)
            .ThenByDescending(t => t.AverageScore)
            .ToList();
    }

    /// <summary>
    /// Selects a balanced team for a new player using minimum current population.
    /// </summary>
    public static TeamSide AssignBalancedTeam(IEnumerable<PlayerStandingDto> existingPlayers, TeamSide preferred)
    {
        if (preferred is TeamSide.Alpha or TeamSide.Beta or TeamSide.Gamma)
        {
            return preferred;
        }

        TeamSide[] teams = [TeamSide.Alpha, TeamSide.Beta, TeamSide.Gamma];
        var counts = existingPlayers
            .Where(p => p.Team != TeamSide.None)
            .GroupBy(p => p.Team)
            .ToDictionary(g => g.Key, g => g.Count());

        return teams
            .OrderBy(t => counts.GetValueOrDefault(t, 0))
            .First();
    }

    public static string FormatTeamName(TeamSide side) => side switch
    {
        TeamSide.Alpha => "Team Alpha",
        TeamSide.Beta => "Team Beta",
        TeamSide.Gamma => "Team Gamma",
        _ => "Solo"
    };

    /// <summary>
    /// Builds an RFC-4180 compliant CSV document for a finished game session.
    /// </summary>
    public static string BuildSessionCsv(SessionHistoryDto session)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SessionId,RoomCode,QuizTitle,HostName,Mode,StartedAtUtc,FinishedAtUtc,Rank,Nickname,Team,FinalScore,CorrectAnswers,TotalQuestions,BestStreak,AverageResponseMs,Eliminated");

        foreach (var row in session.Results.OrderBy(r => r.FinalRank))
        {
            sb.AppendJoin(',',
                EscapeCsv(session.SessionId.ToString()),
                EscapeCsv(session.RoomCode),
                EscapeCsv(session.QuizTitle),
                EscapeCsv(session.HostName),
                EscapeCsv(session.Mode.ToString()),
                EscapeCsv(session.StartedAtUtc.ToString("O")),
                EscapeCsv(session.FinishedAtUtc.ToString("O")),
                row.FinalRank.ToString(),
                EscapeCsv(row.Nickname),
                EscapeCsv(row.TeamName),
                row.FinalScore.ToString(),
                row.CorrectAnswers.ToString(),
                row.TotalQuestions.ToString(),
                row.BestStreak.ToString(),
                row.AverageResponseMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                row.WasEliminated ? "Yes" : "No");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        bool mustQuote = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        if (!mustQuote)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}

