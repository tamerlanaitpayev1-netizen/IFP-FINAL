using ArenaQuiz.Services;
using ArenaQuiz.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace ArenaQuiz.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class RoomsController(GameRoomService roomService) : ControllerBase
{
    [HttpGet("open")]
    public ActionResult<IReadOnlyList<OpenRoomSummaryDto>> GetOpenRooms() =>
        Ok(roomService.GetOpenRooms());
}

