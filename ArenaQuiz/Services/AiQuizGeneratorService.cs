using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ArenaQuiz.Shared.Models;

namespace ArenaQuiz.Services;

/// <summary>
/// Server-side AI quiz draft generator. Uses Anthropic Claude Messages API when Anthropic:ApiKey
/// is configured on the server, and falls back deterministically to a topic-aware builder for offline demos.
/// </summary>
public sealed class AiQuizGeneratorService(
    HttpClient http,
    IConfiguration configuration,
    ILogger<AiQuizGeneratorService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<CreateQuizRequest> GenerateQuizDraftAsync(
        GenerateAiQuizRequest request,
        string authorName,
        CancellationToken ct = default)
    {
        string topic = string.IsNullOrWhiteSpace(request.Topic) ? "Software Engineering" : request.Topic.Trim();
        int count = Math.Clamp(request.QuestionCount, 1, 10);
        string difficulty = string.IsNullOrWhiteSpace(request.Difficulty) ? "Intermediate" : request.Difficulty.Trim();

        string? apiKey = configuration["Anthropic:ApiKey"] ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var aiDraft = await TryGenerateViaClaudeAsync(apiKey, topic, count, difficulty, authorName, ct);
            if (aiDraft is not null)
            {
                return aiDraft;
            }
        }

        return BuildStructuredTopicDraft(topic, count, difficulty, authorName);
    }

    private async Task<CreateQuizRequest?> TryGenerateViaClaudeAsync(
        string apiKey,
        string topic,
        int count,
        string difficulty,
        string authorName,
        CancellationToken ct)
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            msg.Headers.Add("x-api-key", apiKey);
            msg.Headers.Add("anthropic-version", "2023-06-01");
            msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            string prompt = $$"""
                Generate a {{difficulty}} quiz about "{{topic}}" with exactly {{count}} questions.
                Respond ONLY with valid JSON matching this schema:
                {
                  "title": "Quiz Title",
                  "category": "{{topic}}",
                  "description": "One sentence summary",
                  "questions": [
                    {
                      "prompt": "Question text",
                      "codeSnippet": null,
                      "options": ["Option A", "Option B", "Option C", "Option D"],
                      "correctOptionIndex": 0,
                      "timeLimitSeconds": 20,
                      "basePoints": 1000,
                      "explanation": "Brief technical explanation"
                    }
                  ]
                }
                """;

            var body = new
            {
                model = "claude-3-5-haiku-latest",
                max_tokens = 1800,
                messages = new[] { new { role = "user", content = prompt } }
            };

            msg.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(msg, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            string? text = doc.RootElement
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString();

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return null;
            }

            var parsed = JsonSerializer.Deserialize<CreateQuizRequest>(text[start..(end + 1)], JsonOpts);
            if (parsed is null || parsed.Questions.Count == 0)
            {
                return null;
            }

            return parsed with { CreatedBy = authorName };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Anthropic Claude generation unavailable; falling back to local topic generator.");
            return null;
        }
    }

    private static CreateQuizRequest BuildStructuredTopicDraft(string topic, int count, string difficulty, string authorName)
    {
        var templates = new List<CreateQuestionRequest>
        {
            new(
                $"[{difficulty}] Which architectural guarantee best prevents client tampering in a real-time {topic} system?",
                null,
                [
                    "Authoritative server timestamps, role claims, and signed tokens",
                    "Storing expected answers inside client-side hidden DOM elements",
                    "Trusting elapsed milliseconds sent by the browser",
                    "Broadcasting full question keys at room startup"
                ],
                0,
                25,
                1000,
                $"In production {topic} engineering, security invariants are enforced exclusively on the server."),
            new(
                $"True or False: Rate-limiting authentication and isolating client connection IDs improves resilience in {topic} applications.",
                null,
                ["True", "False"],
                0,
                15,
                1000,
                "Throttling brute-force requests and binding connections prevents impersonation and denial of service."),
            new(
                $"[{difficulty}] Why should live state updates in {topic} emit compact progress deltas instead of full-state snapshots?",
                "// Delta: { QuestionIndex, Answered, ActivePlayers }",
                [
                    "To increase JSON serialization overhead per click",
                    "To eliminate WebSocket encryption",
                    "To reduce broadcast traffic from O(P^2) to O(P) per round",
                    "To allow clients to mutate other users' scores"
                ],
                2,
                20,
                1200,
                "Lightweight delta payloads avoid re-serializing entire participant lists on every player action."),
            new(
                $"[{difficulty}] Which approach ensures clean recovery if a participant drops connection mid-session in {topic}?",
                null,
                [
                    "Allow anyone typing the same nickname to take over without a token",
                    "Validate a secret cryptographic reconnect token before restoring player state",
                    "Delete the room immediately on the first disconnect",
                    "Store passwords in plain text inside localStorage"
                ],
                1,
                20,
                1000,
                "A per-session reconnect token restores identity safely while blocking nickname hijacking.")
        };

        var selected = templates.Take(count).ToList();
        while (selected.Count < count)
        {
            int num = selected.Count + 1;
            selected.Add(new CreateQuestionRequest(
                $"[{difficulty}] Key engineering trade-off #{num} in {topic}: which practice prioritizes maintainability?",
                null,
                [
                    "Separation of shared immutable contracts from transport adapters",
                    "Coupling database queries directly inside button click handlers",
                    "Hardcoding production signing secrets in client bundles",
                    "Disabling automated unit and integration tests"
                ],
                0,
                20,
                1000,
                "Modular separation and automated tests preserve correctness as features scale."));
        }

        return new CreateQuizRequest(
            $"{topic} ({difficulty} Arena)",
            topic,
            $"AI-scaffolded {difficulty.ToLowerInvariant()} assessment covering core {topic} principles.",
            authorName,
            selected);
    }
}
