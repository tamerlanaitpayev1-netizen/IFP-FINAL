using ArenaQuiz.Shared.Models;

namespace ArenaQuiz.Data;

public sealed class QuizEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<QuestionEntity> Questions { get; set; } = [];
}

public sealed class QuestionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid QuizId { get; set; }
    public QuizEntity? Quiz { get; set; }
    public int OrderIndex { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string? CodeSnippet { get; set; }
    public string OptionsJson { get; set; } = "[]";
    public int CorrectOptionIndex { get; set; }
    public int TimeLimitSeconds { get; set; } = 20;
    public int BasePoints { get; set; } = 1000;
    public string Explanation { get; set; } = string.Empty;
}

public sealed class GameSessionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RoomCode { get; set; } = string.Empty;
    public Guid QuizId { get; set; }
    public string QuizTitle { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public GameMode Mode { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
    public List<PlayerResultEntity> Results { get; set; } = [];
}

public sealed class PlayerResultEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameSessionId { get; set; }
    public GameSessionEntity? GameSession { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public string TeamName { get; set; } = "Solo";
    public int FinalRank { get; set; }
    public int FinalScore { get; set; }
    public int CorrectAnswers { get; set; }
    public int TotalQuestions { get; set; }
    public int BestStreak { get; set; }
    public double AverageResponseMs { get; set; }
    public bool WasEliminated { get; set; }
}

public sealed class UserAccountEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; }
}

