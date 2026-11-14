using ArenaQuiz.Data;
using ArenaQuiz.Security;
using ArenaQuiz.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace ArenaQuiz.Services;

/// <summary>
/// Role-Based Access Control (RBAC) service backed by SQLite UserAccounts,
/// PBKDF2-SHA256 salted password verification, and HMAC-SHA256 signed bearer tokens.
/// </summary>
public sealed class AuthService(ArenaDbContext db, TokenService tokenService)
{
    public async Task<Result<UserSessionDto>> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Result<UserSessionDto>.Failure("Username and password are required.");
        }

        string normalized = request.Username.Trim().ToLowerInvariant();
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == normalized, ct);

        if (user is null || !TokenService.VerifyPassword(request.Password, user.PasswordHash))
        {
            return Result<UserSessionDto>.Failure("Invalid username or password.");
        }

        return Result<UserSessionDto>.Success(BuildSession(user));
    }

    /// <summary>
    /// Issues a guest/player session without password. Elevating to Host or Admin strictly requires LoginAsync.
    /// </summary>
    public async Task<Result<UserSessionDto>> QuickSwitchPlayerAsync(string username, CancellationToken ct = default)
    {
        string normalized = username.Trim().ToLowerInvariant();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == normalized, ct);
        if (user is null)
        {
            return Result<UserSessionDto>.Failure($"Account '{username}' not found.");
        }

        if (user.Role != UserRole.Player)
        {
            return Result<UserSessionDto>.Failure("Elevating to Host/Admin requires password authentication.");
        }

        return Result<UserSessionDto>.Success(BuildSession(user));
    }

    private UserSessionDto BuildSession(UserAccountEntity user)
    {
        var (token, expires) = tokenService.IssueToken(user.Username, user.FullName, user.Role);
        return new UserSessionDto(user.Username, user.FullName, user.Role, token, expires);
    }
}
