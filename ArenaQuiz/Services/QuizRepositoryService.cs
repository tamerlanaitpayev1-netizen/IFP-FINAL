using System.Text.Json;
using ArenaQuiz.Data;
using ArenaQuiz.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace ArenaQuiz.Services;

/// <summary>
/// Data access service for quizzes, historical sessions, question difficulty metrics, and SQLite persistence.
/// </summary>
public sealed class QuizRepositoryService(ArenaDbContext db)
{
    public async Task<IReadOnlyList<QuizSummaryDto>> GetAllQuizSummariesAsync(CancellationToken ct = default)
    {
        // SQL projection avoids loading all question payloads and options JSON into server RAM.
        return await db.Quizzes
            .AsNoTracking()
            .OrderByDescending(q => q.CreatedAtUtc)
            .Select(q => new QuizSummaryDto(
                q.Id,
                q.Title,
                q.Category,
                q.Description,
                q.Questions.Count,
                q.Questions.Count > 0 ? (int)Math.Round(q.Questions.Average(qn => qn.TimeLimitSeconds)) : 20,
                q.CreatedBy,
                q.CreatedAtUtc))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Public preview projection omitting CorrectOptionIndex and Explanation to prevent answer leaks.
    /// </summary>
    public async Task<QuizPublicPreviewDto?> GetQuizPublicPreviewAsync(Guid quizId, CancellationToken ct = default)
    {
        var quiz = await db.Quizzes
            .AsNoTracking()
            .Include(q => q.Questions.OrderBy(qn => qn.OrderIndex))
            .FirstOrDefaultAsync(q => q.Id == quizId, ct);

        if (quiz is null)
        {
            return null;
        }

        var questions = quiz.Questions
            .OrderBy(q => q.OrderIndex)
            .Select(q => new QuestionPublicDto(
                q.Id,
                q.OrderIndex,
                q.Prompt,
                q.CodeSnippet,
                DeserializeOptions(q.OptionsJson),
                q.TimeLimitSeconds,
                q.BasePoints))
            .ToList();

        return new QuizPublicPreviewDto(quiz.Id, quiz.Title, quiz.Category, quiz.Description, quiz.CreatedBy, questions);
    }

    public async Task<QuizDetailDto?> GetQuizDetailAsync(Guid quizId, CancellationToken ct = default)
    {
        var quiz = await db.Quizzes
            .AsNoTracking()
            .Include(q => q.Questions.OrderBy(qn => qn.OrderIndex))
            .FirstOrDefaultAsync(q => q.Id == quizId, ct);

        if (quiz is null)
        {
            return null;
        }

        var questions = quiz.Questions
            .OrderBy(q => q.OrderIndex)
            .Select(q => new QuestionDto(
                q.Id,
                q.OrderIndex,
                q.Prompt,
                q.CodeSnippet,
                DeserializeOptions(q.OptionsJson),
                q.CorrectOptionIndex,
                q.TimeLimitSeconds,
                q.BasePoints,
                q.Explanation))
            .ToList();

        return new QuizDetailDto(quiz.Id, quiz.Title, quiz.Category, quiz.Description, quiz.CreatedBy, questions);
    }

    public async Task<Result<QuizSummaryDto>> CreateQuizAsync(CreateQuizRequest request, CancellationToken ct = default)
    {
        var validationError = ValidateQuizRequest(request);
        if (validationError is not null)
        {
            return Result<QuizSummaryDto>.Failure(validationError);
        }

        var quizId = Guid.NewGuid();
        var entity = new QuizEntity
        {
            Id = quizId,
            Title = request.Title.Trim(),
            Category = string.IsNullOrWhiteSpace(request.Category) ? "General" : request.Category.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            CreatedBy = string.IsNullOrWhiteSpace(request.CreatedBy) ? "Host" : request.CreatedBy.Trim(),
            CreatedAtUtc = DateTime.UtcNow,
            Questions = BuildQuestionEntities(quizId, request.Questions)
        };

        db.Quizzes.Add(entity);
        await db.SaveChangesAsync(ct);

        return Result<QuizSummaryDto>.Success(ToSummary(entity));
    }

    public async Task<Result<QuizSummaryDto>> UpdateQuizAsync(Guid quizId, CreateQuizRequest request, CancellationToken ct = default)
    {
        var validationError = ValidateQuizRequest(request);
        if (validationError is not null)
        {
            return Result<QuizSummaryDto>.Failure(validationError);
        }

        var quiz = await db.Quizzes
            .Include(q => q.Questions)
            .FirstOrDefaultAsync(q => q.Id == quizId, ct);

        if (quiz is null)
        {
            return Result<QuizSummaryDto>.Failure("Quiz not found.");
        }

        quiz.Title = request.Title.Trim();
        quiz.Category = string.IsNullOrWhiteSpace(request.Category) ? "General" : request.Category.Trim();
        quiz.Description = request.Description?.Trim() ?? string.Empty;

        db.Questions.RemoveRange(quiz.Questions);
        var updatedQuestions = BuildQuestionEntities(quiz.Id, request.Questions);
        db.Questions.AddRange(updatedQuestions);
        quiz.Questions = updatedQuestions;

        await db.SaveChangesAsync(ct);
        return Result<QuizSummaryDto>.Success(ToSummary(quiz));
    }

    public async Task<bool> DeleteQuizAsync(Guid quizId, CancellationToken ct = default)
    {
        var quiz = await db.Quizzes.FirstOrDefaultAsync(q => q.Id == quizId, ct);
        if (quiz is null)
        {
            return false;
        }

        db.Quizzes.Remove(quiz);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task SaveCompletedSessionAsync(
        SessionHistoryDto summary,
        Guid quizId,
        IReadOnlyList<(Guid QuestionId, string Prompt, int Attempts, int Correct)>? questionBreakdown = null,
        CancellationToken ct = default)
    {
        var entity = new GameSessionEntity
        {
            Id = summary.SessionId,
            RoomCode = summary.RoomCode,
            QuizId = quizId,
            QuizTitle = summary.QuizTitle,
            HostName = summary.HostName,
            Mode = summary.Mode,
            StartedAtUtc = summary.StartedAtUtc,
            FinishedAtUtc = summary.FinishedAtUtc,
            Results = summary.Results.Select(r => new PlayerResultEntity
            {
                Id = Guid.NewGuid(),
                GameSessionId = summary.SessionId,
                Nickname = r.Nickname,
                TeamName = r.TeamName,
                FinalRank = r.FinalRank,
                FinalScore = r.FinalScore,
                CorrectAnswers = r.CorrectAnswers,
                TotalQuestions = r.TotalQuestions,
                BestStreak = r.BestStreak,
                AverageResponseMs = r.AverageResponseMs,
                WasEliminated = r.WasEliminated
            }).ToList()
        };

        db.GameSessions.Add(entity);

        if (questionBreakdown is { Count: > 0 })
        {
            foreach (var item in questionBreakdown.Where(x => x.Attempts > 0))
            {
                var existing = await db.QuestionStats.FirstOrDefaultAsync(s => s.QuestionId == item.QuestionId, ct);
                if (existing is null)
                {
                    db.QuestionStats.Add(new QuestionStatEntity
                    {
                        QuestionId = item.QuestionId,
                        QuizTitle = summary.QuizTitle,
                        Prompt = item.Prompt,
                        TotalAttempts = item.Attempts,
                        CorrectAttempts = item.Correct
                    });
                }
                else
                {
                    existing.TotalAttempts += item.Attempts;
                    existing.CorrectAttempts += item.Correct;
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<AnalyticsOverviewDto> GetAnalyticsOverviewAsync(CancellationToken ct = default)
    {
        var sessions = await db.GameSessions
            .AsNoTracking()
            .Include(s => s.Results)
            .OrderByDescending(s => s.FinishedAtUtc)
            .Take(25)
            .ToListAsync(ct);

        var sessionDtos = sessions.Select(MapSession).ToList();
        int totalParticipants = sessionDtos.Sum(s => s.ParticipantCount);
        double globalAccuracy = sessionDtos.Count > 0
            ? Math.Round(sessionDtos.Average(s => s.AverageAccuracyPercent), 1)
            : 0.0;

        string popularQuiz = sessionDtos
            .GroupBy(s => s.QuizTitle)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? "N/A";

        var questionStats = await db.QuestionStats
            .AsNoTracking()
            .Where(s => s.TotalAttempts > 0)
            .OrderBy(s => (double)s.CorrectAttempts / s.TotalAttempts)
            .Take(10)
            .Select(s => new QuestionDifficultyDto(
                s.QuizTitle,
                s.Prompt,
                s.TotalAttempts,
                s.CorrectAttempts,
                Math.Round(s.CorrectAttempts * 100.0 / s.TotalAttempts, 1)))
            .ToListAsync(ct);

        return new AnalyticsOverviewDto(
            sessionDtos.Count,
            totalParticipants,
            globalAccuracy,
            popularQuiz,
            sessionDtos,
            questionStats);
    }

    public async Task<SessionHistoryDto?> GetSessionByIdAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await db.GameSessions
            .AsNoTracking()
            .Include(s => s.Results)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        return session is null ? null : MapSession(session);
    }

    private static string? ValidateQuizRequest(CreateQuizRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return "Quiz title is required.";
        }

        if (request.Questions is null || request.Questions.Count == 0)
        {
            return "Add at least one question.";
        }

        for (int i = 0; i < request.Questions.Count; i++)
        {
            var q = request.Questions[i];
            if (string.IsNullOrWhiteSpace(q.Prompt))
            {
                return $"Question #{i + 1} prompt cannot be empty.";
            }
            if (q.Options.Count < 2 || q.Options.Any(string.IsNullOrWhiteSpace))
            {
                return $"Question #{i + 1} must have non-empty answer options.";
            }
            if (q.CorrectOptionIndex < 0 || q.CorrectOptionIndex >= q.Options.Count)
            {
                return $"Question #{i + 1} has an invalid correct answer index.";
            }
        }

        return null;
    }

    private static List<QuestionEntity> BuildQuestionEntities(Guid quizId, List<CreateQuestionRequest> questions)
    {
        return questions.Select((q, idx) => new QuestionEntity
        {
            Id = Guid.NewGuid(),
            QuizId = quizId,
            OrderIndex = idx,
            Prompt = q.Prompt.Trim(),
            CodeSnippet = string.IsNullOrWhiteSpace(q.CodeSnippet) ? null : q.CodeSnippet.Trim(),
            OptionsJson = JsonSerializer.Serialize(q.Options.Select(o => o.Trim()).ToArray()),
            CorrectOptionIndex = q.CorrectOptionIndex,
            TimeLimitSeconds = Math.Clamp(q.TimeLimitSeconds, 5, 120),
            BasePoints = Math.Clamp(q.BasePoints, 100, 5000),
            Explanation = q.Explanation?.Trim() ?? string.Empty
        }).ToList();
    }

    private static QuizSummaryDto ToSummary(QuizEntity entity) => new(
        entity.Id,
        entity.Title,
        entity.Category,
        entity.Description,
        entity.Questions.Count,
        entity.Questions.Count > 0 ? (int)Math.Round(entity.Questions.Average(qn => qn.TimeLimitSeconds)) : 20,
        entity.CreatedBy,
        entity.CreatedAtUtc);

    private static SessionHistoryDto MapSession(GameSessionEntity s)
    {
        var orderedResults = s.Results
            .OrderBy(r => r.FinalRank)
            .Select(r => new PlayerResultDto(
                r.Nickname,
                r.TeamName,
                r.FinalRank,
                r.FinalScore,
                r.CorrectAnswers,
                r.TotalQuestions,
                r.BestStreak,
                r.AverageResponseMs,
                r.WasEliminated))
            .ToList();

        var winner = orderedResults.FirstOrDefault();
        double avgAccuracy = orderedResults.Count > 0
            ? Math.Round(orderedResults.Average(r => r.TotalQuestions > 0 ? (double)r.CorrectAnswers / r.TotalQuestions * 100.0 : 0.0), 1)
            : 0.0;

        return new SessionHistoryDto(
            s.Id,
            s.RoomCode,
            s.QuizTitle,
            s.HostName,
            s.Mode,
            orderedResults.Count,
            winner?.Nickname ?? "No Players",
            winner?.FinalScore ?? 0,
            avgAccuracy,
            s.StartedAtUtc,
            s.FinishedAtUtc,
            orderedResults);
    }

    private static IReadOnlyList<string> DeserializeOptions(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
