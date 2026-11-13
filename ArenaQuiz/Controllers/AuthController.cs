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

    [HttpPost("quick-switch")]
    public async Task<ActionResult<UserSessionDto>> QuickSwitch([FromBody] QuickSwitchRoleRequest request, CancellationToken ct)
    {
        var result = await authService.QuickSwitchAsync(request.Username, ct);
        return result.IsSuccess && result.Value is not null
            ? Ok(result.Value)
            : NotFound(new { error = result.Error });
    }
}

