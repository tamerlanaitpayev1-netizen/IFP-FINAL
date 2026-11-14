using ArenaQuiz.Services;
using ArenaQuiz.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ArenaQuiz.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class QuizzesController(QuizRepositoryService repo, AiQuizGeneratorService aiGenerator) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<QuizSummaryDto>>> GetAll(CancellationToken ct) =>
        Ok(await repo.GetAllQuizSummariesAsync(ct));

    /// <summary>
    /// Public preview endpoint omitting CorrectOptionIndex and Explanation.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<QuizPublicPreviewDto>> GetById(Guid id, CancellationToken ct)
    {
        var quiz = await repo.GetQuizPublicPreviewAsync(id, ct);
        return quiz is null ? NotFound() : Ok(quiz);
    }

    /// <summary>
    /// Host-only endpoint returning full quiz questions including CorrectOptionIndex for editing.
    /// </summary>
    [Authorize(Roles = "Host,Admin")]
    [HttpGet("{id:guid}/edit")]
    public async Task<ActionResult<QuizDetailDto>> GetForEdit(Guid id, CancellationToken ct)
    {
        var quiz = await repo.GetQuizDetailAsync(id, ct);
        return quiz is null ? NotFound() : Ok(quiz);
    }

    [Authorize(Roles = "Host,Admin")]
    [HttpPost]
    public async Task<ActionResult<QuizSummaryDto>> Create([FromBody] CreateQuizRequest request, CancellationToken ct)
    {
        var createdBy = User.FindFirst("fullname")?.Value ?? User.Identity?.Name ?? request.CreatedBy;
        var enriched = request with { CreatedBy = createdBy };
        var result = await repo.CreateQuizAsync(enriched, ct);
        return result.IsSuccess && result.Value is not null
            ? CreatedAtAction(nameof(GetById), new { id = result.Value.Id }, result.Value)
            : BadRequest(new { error = result.Error });
    }

    [Authorize(Roles = "Host,Admin")]
    [HttpPost("generate-ai")]
    public async Task<ActionResult<CreateQuizRequest>> GenerateAiDraft(
        [FromBody] GenerateAiQuizRequest request,
        CancellationToken ct)
    {
        string author = User.FindFirst("fullname")?.Value ?? User.Identity?.Name ?? "Host";
        var draft = await aiGenerator.GenerateQuizDraftAsync(request, author, ct);
        return Ok(draft);
    }

    [Authorize(Roles = "Host,Admin")]
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<QuizSummaryDto>> Update(Guid id, [FromBody] CreateQuizRequest request, CancellationToken ct)
    {
        var result = await repo.UpdateQuizAsync(id, request, ct);
        return result.IsSuccess && result.Value is not null
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
    }

    [Authorize(Roles = "Host,Admin")]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        bool deleted = await repo.DeleteQuizAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }
}
