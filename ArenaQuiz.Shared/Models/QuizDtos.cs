namespace ArenaQuiz.Shared.Models;

public sealed record QuizSummaryDto(
    Guid Id,
    string Title,
    string Category,
    string Description,
    int QuestionCount,
    int DefaultTimeLimitSeconds,
    string CreatedBy,
    DateTime CreatedAtUtc);

public sealed record QuizDetailDto(
    Guid Id,
    string Title,
    string Category,
    string Description,
    string CreatedBy,
    IReadOnlyList<QuestionDto> Questions);

public sealed record QuestionDto(
    Guid Id,
    int OrderIndex,
    string Prompt,
    string? CodeSnippet,
    IReadOnlyList<string> Options,
    int CorrectOptionIndex,
    int TimeLimitSeconds,
    int BasePoints,
    string Explanation);

public sealed record CreateQuizRequest(
    string Title,
    string Category,
    string Description,
    string CreatedBy,
    List<CreateQuestionRequest> Questions);

public sealed record CreateQuestionRequest(
    string Prompt,
    string? CodeSnippet,
    List<string> Options,
    int CorrectOptionIndex,
    int TimeLimitSeconds,
    int BasePoints,
    string Explanation);

