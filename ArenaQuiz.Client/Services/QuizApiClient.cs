using System.Net.Http.Headers;
using System.Net.Http.Json;
using ArenaQuiz.Shared.Models;

namespace ArenaQuiz.Client.Services;

/// <summary>
/// Typed HTTP client communicating with ArenaQuiz ASP.NET Core REST endpoints with signed Bearer auth.
/// </summary>
public sealed class QuizApiClient(HttpClient http)
{
    public void SetBearerToken(string? token)
    {
        http.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<IReadOnlyList<QuizSummaryDto>> GetQuizzesAsync() =>
        await http.GetFromJsonAsync<IReadOnlyList<QuizSummaryDto>>("api/quizzes") ?? [];

    public async Task<QuizPublicPreviewDto?> GetQuizPreviewAsync(Guid id) =>
        await http.GetFromJsonAsync<QuizPublicPreviewDto>($"api/quizzes/{id}");

    public async Task<Result<QuizDetailDto>> GetQuizForEditAsync(Guid id)
    {
        try
        {
            var response = await http.GetAsync($"api/quizzes/{id}/edit");
            if (response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadFromJsonAsync<QuizDetailDto>();
                return detail is not null
                    ? Result<QuizDetailDto>.Success(detail)
                    : Result<QuizDetailDto>.Failure("Empty quiz payload.");
            }
            return Result<QuizDetailDto>.Failure(response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "Sign in as a Host to edit quizzes."
                : $"Server returned {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return Result<QuizDetailDto>.Failure($"Network error: {ex.Message}");
        }
    }

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

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return Result<QuizSummaryDto>.Failure("Host sign-in required to save quizzes.");
            }

            var err = await response.Content.ReadFromJsonAsync<ApiErrorPayload>();
            return Result<QuizSummaryDto>.Failure(err?.Error ?? $"Server returned {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return Result<QuizSummaryDto>.Failure($"Network error: {ex.Message}");
        }
    }

    public async Task<Result<QuizSummaryDto>> UpdateQuizAsync(Guid id, CreateQuizRequest request)
    {
        try
        {
            var response = await http.PutAsJsonAsync($"api/quizzes/{id}", request);
            if (response.IsSuccessStatusCode)
            {
                var updated = await response.Content.ReadFromJsonAsync<QuizSummaryDto>();
                return updated is not null
                    ? Result<QuizSummaryDto>.Success(updated)
                    : Result<QuizSummaryDto>.Failure("Empty server response.");
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return Result<QuizSummaryDto>.Failure("Host sign-in required to update quizzes.");
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
                if (session is not null)
                {
                    SetBearerToken(session.Token);
                    return Result<UserSessionDto>.Success(session);
                }
                return Result<UserSessionDto>.Failure("Invalid session payload.");
            }

            var err = await response.Content.ReadFromJsonAsync<ApiErrorPayload>();
            return Result<UserSessionDto>.Failure(err?.Error ?? "Invalid username or password.");
        }
        catch (Exception ex)
        {
            return Result<UserSessionDto>.Failure($"Network error: {ex.Message}");
        }
    }

    public async Task<Result<UserSessionDto>> QuickSwitchPlayerAsync(string username)
    {
        try
        {
            var response = await http.PostAsJsonAsync("api/auth/quick-switch", new QuickSwitchRoleRequest(username));
            if (response.IsSuccessStatusCode)
            {
                var session = await response.Content.ReadFromJsonAsync<UserSessionDto>();
                if (session is not null)
                {
                    SetBearerToken(session.Token);
                    return Result<UserSessionDto>.Success(session);
                }
                return Result<UserSessionDto>.Failure("Invalid session payload.");
            }

            var err = await response.Content.ReadFromJsonAsync<ApiErrorPayload>();
            return Result<UserSessionDto>.Failure(err?.Error ?? "Could not switch account.");
        }
        catch (Exception ex)
        {
            return Result<UserSessionDto>.Failure($"Network error: {ex.Message}");
        }
    }

    public async Task<Result<UserSessionDto>> RegisterAsync(RegisterRequest request)
    {
        try
        {
            var response = await http.PostAsJsonAsync("api/auth/register", request);
            if (response.IsSuccessStatusCode)
            {
                var session = await response.Content.ReadFromJsonAsync<UserSessionDto>();
                if (session is not null)
                {
                    SetBearerToken(session.Token);
                    return Result<UserSessionDto>.Success(session);
                }
                return Result<UserSessionDto>.Failure("Invalid session payload.");
            }

            var err = await response.Content.ReadFromJsonAsync<ApiErrorPayload>();
            return Result<UserSessionDto>.Failure(err?.Error ?? "Registration failed.");
        }
        catch (Exception ex)
        {
            return Result<UserSessionDto>.Failure($"Network error: {ex.Message}");
        }
    }

    public async Task<Result<CreateQuizRequest>> GenerateAiQuizAsync(GenerateAiQuizRequest request)
    {
        try
        {
            var response = await http.PostAsJsonAsync("api/quizzes/generate-ai", request);
            if (response.IsSuccessStatusCode)
            {
                var draft = await response.Content.ReadFromJsonAsync<CreateQuizRequest>();
                return draft is not null
                    ? Result<CreateQuizRequest>.Success(draft)
                    : Result<CreateQuizRequest>.Failure("Empty generator payload.");
            }

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                return Result<CreateQuizRequest>.Failure("Host sign-in required to generate quizzes.");
            }

            return Result<CreateQuizRequest>.Failure($"Generator returned {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return Result<CreateQuizRequest>.Failure($"Network error: {ex.Message}");
        }
    }

    private sealed record ApiErrorPayload(string? Error);
}
