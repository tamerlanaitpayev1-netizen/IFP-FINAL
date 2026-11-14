using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ArenaQuiz.Security;

/// <summary>
/// ASP.NET Core authentication handler validating HMAC-SHA256 ArenaBearer tokens from the Authorization header.
/// </summary>
public sealed class ArenaBearerAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TokenService tokenService) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ArenaBearer";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? token = null;

        if (Request.Headers.TryGetValue("Authorization", out var headerValues))
        {
            string header = headerValues.ToString();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = header["Bearer ".Length..].Trim();
            }
        }

        // SignalR WebSockets and browser CSV downloads pass bearer tokens via ?access_token=...
        if (string.IsNullOrWhiteSpace(token) &&
            (Request.Path.StartsWithSegments("/gamehub") || Request.Path.StartsWithSegments("/api/sessions")) &&
            Request.Query.TryGetValue("access_token", out var accessToken))
        {
            token = accessToken.ToString().Trim();
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var principal = tokenService.ValidateToken(token);
        if (principal is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired ArenaQuiz bearer token."));
        }

        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
