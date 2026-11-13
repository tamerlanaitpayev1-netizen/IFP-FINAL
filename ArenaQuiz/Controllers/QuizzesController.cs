using ArenaQuiz.Services;
using ArenaQuiz.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace ArenaQuiz.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class QuizzesController(QuizRepositoryService repo) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<QuizSummaryDto>>> GetAll(CancellationToken ct) =>
        Ok(await repo.GetAllQuizSummariesAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<QuizDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var quiz = await repo.GetQuizDetailAsync(id, ct);
        return quiz is null ? NotFound() : Ok(quiz);
    }

    [HttpPost]
    public async Task<ActionResult<QuizSummaryDto>> Create([FromBody] CreateQuizRequest request, CancellationToken ct)
    {
        var result = await repo.CreateQuizAsync(request, ct);
        return result.IsSuccess && result.Value is not null
            ? CreatedAtAction(nameof(GetById), new { id = result.Value.Id }, result.Value)
            : BadRequest(new { error = result.Error });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        bool deleted = await repo.DeleteQuizAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }
}

