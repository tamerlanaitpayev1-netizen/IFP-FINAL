using System.Net.Http.Json;
using ArenaQuiz.Shared.Models;

namespace ArenaQuiz.Client.Services;

/// <summary>
/// Typed HTTP client communicating with ArenaQuiz ASP.NET Core REST endpoints.
/// </summary>
public sealed class QuizApiClient(HttpClient http)
{
    public async Task<IReadOnlyList<QuizSummaryDto>> GetQuizzesAsync() =>
        await http.GetFromJsonAsync<IReadOnlyList<QuizSummaryDto>>("api/quizzes") ?? [];

    public async Task<QuizDetailDto?> GetQuizAsync(Guid id) =>
        await http.GetFromJsonAsync<QuizDetailDto>($"api/quizzes/{id}");

    public async Task<Result<QuizSummaryDto>> CreateQuizAsync(CreateQuizRequest request)
    {
        try
        {
            var response = await http.PostAsJsonAsync("api/quizzes", request);
            if (response.IsSuccessStatusCode)
            {
                var created = await response.Content.ReadFromJsonAsync<QuizSummaryDto>();
                return created is not null
                    ? Result<QuizSummaryDto>.Success(created)
                    : Result<QuizSummaryDto>.Failure("Empty server response.");
            }

            var err = await response.Content.ReadFromJsonAsync<ApiErrorPayload>();
            return Result<QuizSummaryDto>.Failure(err?.Error ?? $"Server returned {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return Result<QuizSummaryDto>.Failure($"Network error: {ex.Message}");
        }
    }

    public async Task<bool> DeleteQuizAsync(Guid id)
    {
        var response = await http.DeleteAsync($"api/quizzes/{id}");
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<OpenRoomSummaryDto>> GetOpenRoomsAsync() =>
        await http.GetFromJsonAsync<IReadOnlyList<OpenRoomSummaryDto>>("api/rooms/open") ?? [];

    public async Task<AnalyticsOverviewDto> GetAnalyticsAsync() =>
        await http.GetFromJsonAsync<AnalyticsOverviewDto>("api/sessions/analytics")
        ?? new AnalyticsOverviewDto(0, 0, 0, "N/A", []);

    public async Task<Result<UserSessionDto>> LoginAsync(LoginRequest request)
    {
        try
        {
            var response = await http.PostAsJsonAsync("api/auth/login", request);
            if (response.IsSuccessStatusCode)
            {
                var session = await response.Content.ReadFromJsonAsync<UserSessionDto>();
                return session is not null
                    ? Result<UserSessionDto>.Success(session)
                    : Result<UserSessionDto>.Failure("Invalid session payload.");
            }

            var err = await response.Content.ReadFromJsonAsync<ApiErrorPayload>();
            return Result<UserSessionDto>.Failure(err?.Error ?? "Authentication failed.");
        }
        catch (Exception ex)
        {
            return Result<UserSessionDto>.Failure($"Network error: {ex.Message}");
        }
    }

    public async Task<Result<UserSessionDto>> QuickSwitchAsync(string username)
    {
        try
        {
            var response = await http.PostAsJsonAsync("api/auth/quick-switch", new QuickSwitchRoleRequest(username));
            if (response.IsSuccessStatusCode)
            {
                var session = await response.Content.ReadFromJsonAsync<UserSessionDto>();
                return session is not null
                    ? Result<UserSessionDto>.Success(session)
                    : Result<UserSessionDto>.Failure("Invalid session payload.");
            }

            return Result<UserSessionDto>.Failure("Could not switch account.");
        }
        catch (Exception ex)
        {
            return Result<UserSessionDto>.Failure($"Network error: {ex.Message}");
        }
    }

    private sealed record ApiErrorPayload(string? Error);
}

