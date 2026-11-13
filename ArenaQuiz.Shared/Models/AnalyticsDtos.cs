namespace ArenaQuiz.Shared.Models;

public sealed record SessionHistoryDto(
    Guid SessionId,
    string RoomCode,
    string QuizTitle,
    string HostName,
    GameMode Mode,
    int ParticipantCount,
    string WinnerNickname,
    int WinnerScore,
    double AverageAccuracyPercent,
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc,
    IReadOnlyList<PlayerResultDto> Results);

public sealed record PlayerResultDto(
    string Nickname,
    string TeamName,
    int FinalRank,
    int FinalScore,
    int CorrectAnswers,
    int TotalQuestions,
    int BestStreak,
    double AverageResponseMs,
    bool WasEliminated);

public sealed record AnalyticsOverviewDto(
    int TotalSessionsPlayed,
    int TotalParticipants,
    double GlobalAccuracyPercent,
    string MostPopularQuiz,
    IReadOnlyList<SessionHistoryDto> RecentSessions);

public sealed record UserSessionDto(
    string Username,
    string FullName,
    UserRole Role,
    string Token);

public sealed record LoginRequest(string Username, string Password);

public sealed record QuickSwitchRoleRequest(string Username);

