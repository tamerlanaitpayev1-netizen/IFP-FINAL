using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArenaQuiz.Shared.Models;

namespace ArenaQuiz.Security;

/// <summary>
/// Provides PBKDF2-SHA256 password hashing (100,000 iterations, 16-byte random salt)
/// and HMAC-SHA256 signed bearer tokens for stateless API & SignalR authorization.
/// </summary>
public sealed class TokenService
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 100_000;

    private readonly byte[] _signingKey;

    public TokenService(IConfiguration configuration)
    {
        string? configuredSecret = configuration["Security:TokenSigningKey"];
        if (string.IsNullOrWhiteSpace(configuredSecret))
        {
            configuredSecret = "ArenaQuiz_Dev_Signing_Key_Change_In_Prod_2026!";
        }
        _signingKey = SHA256.HashData(Encoding.UTF8.GetBytes(configuredSecret));
    }

    public static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashBytes);

        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        string[] parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out int iterations))
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expectedHash = Convert.FromBase64String(parts[3]);
            byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expectedHash.Length);

            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch
        {
            return false;
        }
    }

    public (string Token, DateTime ExpiresAtUtc) IssueToken(string username, string fullName, UserRole role, TimeSpan? lifetime = null)
    {
        DateTime expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(12));
        var payload = new TokenPayload(username, fullName, role.ToString(), expires.Ticks);
        string payloadJson = JsonSerializer.Serialize(payload);
        string payloadB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        string sigB64 = Base64UrlEncode(ComputeSignature(payloadB64));
        return ($"{payloadB64}.{sigB64}", expires);
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        string[] parts = token.Split('.');
        if (parts.Length != 2)
        {
            return null;
        }

        try
        {
            byte[] providedSig = Base64UrlDecode(parts[1]);
            byte[] expectedSig = ComputeSignature(parts[0]);
            if (!CryptographicOperations.FixedTimeEquals(providedSig, expectedSig))
            {
                return null;
            }

            string payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
            var payload = JsonSerializer.Deserialize<TokenPayload>(payloadJson);
            if (payload is null || DateTime.UtcNow.Ticks > payload.ExpiresTicks)
            {
                return null;
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.Name, payload.Username),
                new Claim("fullname", payload.FullName),
                new Claim(ClaimTypes.Role, payload.Role)
            };

            var identity = new ClaimsIdentity(claims, ArenaBearerAuthHandler.SchemeName);
            return new ClaimsPrincipal(identity);
        }
        catch
        {
            return null;
        }
    }

    private byte[] ComputeSignature(string payloadB64)
    {
        using var hmac = new HMACSHA256(_signingKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadB64));
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        string padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    private sealed record TokenPayload(string Username, string FullName, string Role, long ExpiresTicks);
}
