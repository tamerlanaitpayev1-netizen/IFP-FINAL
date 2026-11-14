using System.Collections.Concurrent;
using ArenaQuiz.Data;
using ArenaQuiz.Security;
using ArenaQuiz.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace ArenaQuiz.Services;

/// <summary>
/// Role-Based Access Control (RBAC) service backed by SQLite UserAccounts,
/// PBKDF2-SHA256 salted password verification, HMAC-SHA256 bearer tokens,
/// self-registration, and per-account login brute-force rate limiting.
/// </summary>
public sealed class AuthService(ArenaDbContext db, TokenService tokenService)
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStartUtc)> FailedAttempts = new();

    public async Task<Result<UserSessionDto>> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Result<UserSessionDto>.Failure("Username and password are required.");
        }

        string normalized = request.Username.Trim().ToLowerInvariant();
        if (IsRateLimited(normalized))
        {
            return Result<UserSessionDto>.Failure("Too many failed login attempts. Please wait 1 minute.");
        }

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == normalized, ct);

        if (user is null || !TokenService.VerifyPassword(request.Password, user.PasswordHash))
        {
            RecordFailedAttempt(normalized);
            return Result<UserSessionDto>.Failure("Invalid username or password.");
        }

        FailedAttempts.TryRemove(normalized, out _);
        return Result<UserSessionDto>.Success(BuildSession(user));
    }

    public async Task<Result<UserSessionDto>> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        string username = request.Username?.Trim().ToLowerInvariant() ?? string.Empty;
        string fullName = request.FullName?.Trim() ?? string.Empty;
        string password = request.Password ?? string.Empty;

        if (username.Length is < 3 or > 24 || username.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
        {
            return Result<UserSessionDto>.Failure("Username must be 3-24 letters, digits, '_' or '-'.");
        }

        if (string.IsNullOrWhiteSpace(fullName) || fullName.Length > 48)
        {
            return Result<UserSessionDto>.Failure("Full name is required (max 48 characters).");
        }

        if (password.Length < 6)
        {
            return Result<UserSessionDto>.Failure("Password must be at least 6 characters.");
        }

        if (await db.Users.AnyAsync(u => u.Username == username, ct))
        {
            return Result<UserSessionDto>.Failure($"Username '{username}' is already taken.");
        }

        // Public self-registration strictly assigns Player role (prevents privilege escalation).
        var user = new UserAccountEntity
        {
            Id = Guid.NewGuid(),
            Username = username,
            FullName = fullName,
            PasswordHash = TokenService.HashPassword(password),
            Role = UserRole.Player
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

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

    private static bool IsRateLimited(string normalizedUsername)
    {
        if (!FailedAttempts.TryGetValue(normalizedUsername, out var entry))
        {
            return false;
        }

        if (DateTime.UtcNow - entry.WindowStartUtc > LockoutWindow)
        {
            FailedAttempts.TryRemove(normalizedUsername, out _);
            return false;
        }

        return entry.Count >= MaxFailedAttempts;
    }

    private static void RecordFailedAttempt(string normalizedUsername)
    {
        DateTime now = DateTime.UtcNow;
        FailedAttempts.AddOrUpdate(
            normalizedUsername,
            _ => (1, now),
            (_, prev) => (now - prev.WindowStartUtc > LockoutWindow)
                ? (1, now)
                : (prev.Count + 1, prev.WindowStartUtc));
    }
}
