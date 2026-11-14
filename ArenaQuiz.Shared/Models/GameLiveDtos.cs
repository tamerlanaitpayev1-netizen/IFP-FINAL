namespace ArenaQuiz.Shared.Models;

public sealed record CreateRoomRequest(
    Guid QuizId,
    string HostName,
    GameMode Mode,
    bool AutoAdvance);

public sealed record JoinRoomRequest(
    string RoomCode,
    string Nickname,
    TeamSide PreferredTeam,
    string? ReconnectToken);

public sealed record SubmitAnswerRequest(
    string RoomCode,
    string PlayerId,
    int QuestionIndex,
    int SelectedOptionIndex);

public sealed record PlayerStandingDto(
    string PlayerId,
    string Nickname,
    TeamSide Team,
    int TotalScore,
    int LastDeltaScore,
    int CurrentStreak,
    int CorrectCount,
    int AnsweredCount,
    double AverageResponseMs,
    bool IsConnected,
    bool IsEliminated,
    int Rank);

public sealed record TeamStandingDto(
    TeamSide Team,
    string TeamName,
    int TotalScore,
    int MemberCount,
    double AverageScore);

/// <summary>
/// Live question projection sent to players. Deliberately omits CorrectOptionIndex to prevent client-side frame inspection cheating.
/// </summary>
public sealed record LiveQuestionViewDto(
    int QuestionIndex,
    int TotalQuestions,
    string Prompt,
    string? CodeSnippet,
    IReadOnlyList<string> Options,
    int TimeLimitSeconds,
    int BasePoints,
    GameMode Mode);

public sealed record QuestionRevealDto(
    int QuestionIndex,
    int CorrectOptionIndex,
    string Explanation,
    IReadOnlyList<int> OptionVoteCounts,
    IReadOnlyList<PlayerStandingDto> Leaderboard,
    IReadOnlyList<TeamStandingDto> TeamLeaderboard,
    IReadOnlyList<string> NewlyEliminatedNicknames);

/// <summary>
/// Lightweight O(1) progress delta sent as players submit answers instead of re-broadcasting full room snapshots.
/// </summary>
public sealed record AnswerProgressDto(
    int QuestionIndex,
    int AnsweredCount,
    int TotalActivePlayers);

public sealed record RoomSnapshotDto(
    string RoomCode,
    string QuizTitle,
    string HostName,
    GameMode Mode,
    RoomPhase Phase,
    bool AutoAdvance,
    int CurrentQuestionIndex,
    int TotalQuestions,
    int RemainingSeconds,
    IReadOnlyList<PlayerStandingDto> Players,
    IReadOnlyList<TeamStandingDto> Teams,
    LiveQuestionViewDto? ActiveQuestion,
    QuestionRevealDto? LastReveal,
    string? ReconnectToken,
    bool IsPaused = false,
    int AnsweredCount = 0,
    int ActivePlayerCount = 0,
    string? HostControlToken = null,
    bool IsHostConnected = true);

public sealed record ReactionEventDto(
    string RoomCode,
    string SenderNickname,
    string ReactionKey,
    DateTime SentAtUtc);

public sealed record OpenRoomSummaryDto(
    string RoomCode,
    string QuizTitle,
    string HostName,
    GameMode Mode,
    RoomPhase Phase,
    int PlayerCount,
    int QuestionCount);

public sealed record AnswerAckDto(
    int QuestionIndex,
    int SelectedOptionIndex,
    double ServerElapsedMs,
    bool Accepted,
    string Message);
