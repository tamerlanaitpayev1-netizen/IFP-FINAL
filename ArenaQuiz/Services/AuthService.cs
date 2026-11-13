using ArenaQuiz.Data;
using ArenaQuiz.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace ArenaQuiz.Services;

/// <summary>
/// Role-Based Access Control (RBAC) service backed by SQLite UserAccounts.
/// </summary>
public sealed class AuthService(ArenaDbContext db)
{
    public async Task<Result<UserSessionDto>> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Result<UserSessionDto>.Failure("Username and password are required.");
        }

        string normalized = request.Username.Trim().ToLowerInvariant();
        string hash = DbSeeder.HashPassword(request.Password);

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == normalized && u.PasswordHash == hash, ct);

        if (user is null)
        {
            return Result<UserSessionDto>.Failure("Invalid username or password.");
        }

        return Result<UserSessionDto>.Success(BuildSession(user));
    }

    public async Task<Result<UserSessionDto>> QuickSwitchAsync(string username, CancellationToken ct = default)
    {
        string normalized = username.Trim().ToLowerInvariant();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == normalized, ct);
        if (user is null)
        {
            return Result<UserSessionDto>.Failure($"Account '{username}' not found.");
        }

        return Result<UserSessionDto>.Success(BuildSession(user));
    }

    private static UserSessionDto BuildSession(UserAccountEntity user)
    {
        string token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user.Username}:{user.Role}:{DateTime.UtcNow.Ticks}"));
        return new UserSessionDto(user.Username, user.FullName, user.Role, token);
    }
}

