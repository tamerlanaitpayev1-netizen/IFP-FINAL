using System.Text;
using ArenaQuiz.Services;
using ArenaQuiz.Shared.Functional;
using ArenaQuiz.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace ArenaQuiz.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class SessionsController(QuizRepositoryService repo) : ControllerBase
{
    [HttpGet("analytics")]
    public async Task<ActionResult<AnalyticsOverviewDto>> GetAnalytics(CancellationToken ct) =>
        Ok(await repo.GetAnalyticsOverviewAsync(ct));

    [HttpGet("{id:guid}/export.csv")]
    public async Task<IActionResult> ExportCsv(Guid id, CancellationToken ct)
    {
        var session = await repo.GetSessionByIdAsync(id, ct);
        if (session is null)
        {
            return NotFound();
        }

        string csv = ScoreEngine.BuildSessionCsv(session);
        byte[] preamble = Encoding.UTF8.GetPreamble();
        byte[] body = Encoding.UTF8.GetBytes(csv);
        byte[] bytes = [.. preamble, .. body];

        return File(bytes, "text/csv; charset=utf-8", $"arenaquiz-{session.RoomCode}-{session.StartedAtUtc:yyyyMMdd}.csv");
    }
}

