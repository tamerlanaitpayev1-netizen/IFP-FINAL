using ArenaQuiz.Services;
using ArenaQuiz.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace ArenaQuiz.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class AuthController(AuthService authService) : ControllerBase
{
    [HttpPost("login")]
    public async Task<ActionResult<UserSessionDto>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await authService.LoginAsync(request, ct);
        return result.IsSuccess && result.Value is not null
            ? Ok(result.Value)
            : Unauthorized(new { error = result.Error });
    }

    /// <summary>
    /// Passwordless switch is strictly limited to Player accounts; Host elevation requires Login.
    /// </summary>
    [HttpPost("quick-switch")]
    public async Task<ActionResult<UserSessionDto>> QuickSwitch([FromBody] QuickSwitchRoleRequest request, CancellationToken ct)
    {
        var result = await authService.QuickSwitchPlayerAsync(request.Username, ct);
        return result.IsSuccess && result.Value is not null
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
    }
}
