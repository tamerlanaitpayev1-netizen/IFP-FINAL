using System.Text.Json;
using ArenaQuiz.Security;
using ArenaQuiz.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ArenaQuiz.Data;

public static class DbSeeder
{
    public static async Task SeedAsync(ArenaDbContext db)
    {
        await db.Database.EnsureCreatedAsync();

        if (await NeedsSchemaOrHashUpgradeAsync(db))
        {
            await db.Database.CloseConnectionAsync();
            SqliteConnection.ClearAllPools();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
        }

        bool isProd = string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase);
        bool seedDemoInProd = Environment.GetEnvironmentVariable("ARENAQUIZ_SEED_DEMO") == "1";

        if (!await db.Users.AnyAsync() && (!isProd || seedDemoInProd))
        {
            db.Users.AddRange(
                new UserAccountEntity
                {
                    Username = "tamerlan",
                    FullName = "Тамерлан (Host)",
                    PasswordHash = TokenService.HashPassword("host123"),
                    Role = UserRole.Host
                },
                new UserAccountEntity
                {
                    Username = "dias",
                    FullName = "Диас (Host)",
                    PasswordHash = TokenService.HashPassword("host123"),
                    Role = UserRole.Host
                },
                new UserAccountEntity
                {
                    Username = "student",
                    FullName = "Игрок АИТУ",
                    PasswordHash = TokenService.HashPassword("play123"),
                    Role = UserRole.Player
                });
            await db.SaveChangesAsync();
        }

        if (await db.Quizzes.AnyAsync())
        {
            return;
        }

        var quiz1Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var quiz2Id = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var quiz3Id = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var quizzes = new List<QuizEntity>
        {
            new()
            {
                Id = quiz1Id,
                Title = "Functional Programming & C# 13 (.NET 10)",
                Category = "Functional Programming",
                Description = "Immutability, records, pattern matching, higher-order functions, and LINQ pipelines.",
                CreatedBy = "Тамерлан",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-5),
                Questions =
                [
                    CreateQuestion(quiz1Id, 0,
                        "Which C# keyword creates a non-destructive copy of an immutable record with modified properties?",
                        "var next = player with { TotalScore = player.TotalScore + 500 };",
                        ["clone", "with", "mutate", "copyto"],
                        1, 20, 1000,
                        "The 'with' expression performs non-destructive mutation by shallow-copying the record and applying updated property values."),
                    CreateQuestion(quiz1Id, 1,
                        "What property defines a pure function in functional software design?",
                        "int Add(int a, int b) => a + b;",
                        ["It modifies at least one static field", "It always returns the same output for the same inputs with zero side effects", "It executes asynchronously on a thread pool", "It requires reflection at runtime"],
                        1, 20, 1000,
                        "Pure functions are deterministic and side-effect free, making them trivial to unit test and parallelize."),
                    CreateQuestion(quiz1Id, 2,
                        "When does a standard LINQ query built with .Where() and .Select() actually execute?",
                        "var query = scores.Where(x => x > 500).Select(x => x * 2);",
                        ["Immediately on the .Where() call", "Only when enumerated (e.g., .ToList(), foreach, or .Count())", "During JIT compilation", "Inside the garbage collector"],
                        1, 20, 1000,
                        "LINQ operators like Where and Select use deferred execution via IEnumerable<T> iterators until materialized."),
                    CreateQuestion(quiz1Id, 3,
                        "What does the switch expression below evaluate to when elapsedMs = 25000 and limitMs = 20000?",
                        "var status = (elapsedMs <= limitMs) switch { true => \"Accepted\", false => \"Expired\" };",
                        ["Accepted", "Expired", "NullReferenceException", "Compilation error"],
                        1, 15, 1000,
                        "Because 25000 <= 20000 is false, the pattern arm false => \"Expired\" matches."),
                    CreateQuestion(quiz1Id, 4,
                        "Which functional pattern wraps success or failure explicitly without throwing exceptions for normal validation errors?",
                        "Result<RoomSnapshotDto> JoinRoom(JoinRoomRequest req);",
                        ["Singleton Factory", "Result<T> Monad (Either pattern)", "Observer Polling", "Mutable Global Flag"],
                        1, 20, 1200,
                        "Result<T> models success/failure as an explicit return value with .Map() and .Match() combinators.")
                ]
            },
            new()
            {
                Id = quiz2Id,
                Title = "Real-Time Web Architecture: Blazor & SignalR",
                Category = "Web & Distributed Systems",
                Description = "WebSockets, SignalR Hub contracts, Blazor EventCallback, CascadingValue, and server-side anti-cheat timers.",
                CreatedBy = "Диас",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-3),
                Questions =
                [
                    CreateQuestion(quiz2Id, 0,
                        "Why does SignalR outperform standard HTTP polling for live quiz leaderboards?",
                        "await Clients.Group(roomCode).QuestionRevealed(reveal);",
                        ["HTTP polling cannot transmit JSON", "SignalR maintains a persistent full-duplex WebSocket connection for instant server push", "SignalR bypasses TCP entirely", "WebSockets encrypt data without TLS"],
                        1, 20, 1000,
                        "SignalR automatically negotiates WebSockets for low-latency bidirectional server-to-client push without repeated HTTP headers."),
                    CreateQuestion(quiz2Id, 1,
                        "Why must the round countdown timer and elapsed response time be measured on the ASP.NET Core server rather than in the browser?",
                        "double elapsedMs = (DateTime.UtcNow - room.QuestionStartedAtUtc).TotalMilliseconds;",
                        ["JavaScript has no Date object", "Client clocks and browser JS can be manipulated via DevTools to fake instant answers", "Blazor WebAssembly cannot run timers", "SQLite blocks client timers"],
                        1, 20, 1200,
                        "Server-authoritative timestamps prevent players from altering client clocks or tampering with elapsedMs payloads."),
                    CreateQuestion(quiz2Id, 2,
                        "In Blazor, how does a child component notify its parent component when a user clicks an answer button?",
                        "[Parameter] public EventCallback<int> OnOptionSelected { get; set; }",
                        ["By mutating window.localStorage directly", "Through an EventCallback<T> parameter invoked asynchronously", "By restarting the SignalR Hub", "Via static global variables"],
                        1, 20, 1000,
                        "EventCallback<T> provides type-safe child-to-parent event notification and triggers parent StateHasChanged automatically."),
                    CreateQuestion(quiz2Id, 3,
                        "Which Blazor mechanism passes shared state down an entire component tree without prop-drilling every intermediate component?",
                        "<CascadingValue Value=\"GameState\">@Body</CascadingValue>",
                        ["CascadingValue / [CascadingParameter]", "QueryString injection", "CSS Custom Properties", "Entity Framework Migrations"],
                        0, 20, 1000,
                        "CascadingValue provides ambient state to any descendant declaring a matching [CascadingParameter]."),
                    CreateQuestion(quiz2Id, 4,
                        "What infrastructure enables SignalR servers to scale horizontally to 1,000+ concurrent players across multiple instances?",
                        "builder.Services.AddSignalR().AddStackExchangeRedis(conn);",
                        ["SQLite WAL mode alone", "Redis Backplane or Azure SignalR Service", "Disabling WebSockets in favor of Long Polling", "Increasing client browser cache"],
                        1, 25, 1200,
                        "A Redis backplane or Azure SignalR Service routes Hub group broadcasts across multiple backend server nodes.")
                ]
            },
            new()
            {
                Id = quiz3Id,
                Title = "Algorithms, Data Structures & Complexity (Big-O)",
                Category = "Computer Science",
                Description = "Time and space complexity of ConcurrentDictionary lookups, sorting leaderboards, and graph algorithms.",
                CreatedBy = "Тамерлан",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
                Questions =
                [
                    CreateQuestion(quiz3Id, 0,
                        "What is the average time complexity of looking up an active quiz room by its 6-character PIN in ConcurrentDictionary<string, Room>?",
                        "_rooms.TryGetValue(roomCode, out var room);",
                        ["O(N)", "O(log N)", "O(1)", "O(N log N)"],
                        2, 15, 1000,
                        "Hash-table lookup by string key runs in amortized O(1) constant time regardless of active room count."),
                    CreateQuestion(quiz3Id, 1,
                        "What is the time complexity of sorting P players to build the live leaderboard after each question?",
                        "players.OrderByDescending(p => p.TotalScore).ToList();",
                        ["O(1)", "O(P)", "O(P log P)", "O(P^2)"],
                        2, 20, 1000,
                        "Comparison-based sorting in .NET LINQ OrderBy uses introspective QuickSort/MergeSort with O(P log P) average/worst-case time."),
                    CreateQuestion(quiz3Id, 2,
                        "Which data structure guarantees LIFO (Last-In, First-Out) ordering for undo/redo actions in a quiz builder?",
                        "var undoStack = new Stack<CreateQuestionRequest>();",
                        ["Queue<T>", "Stack<T>", "HashSet<T>", "LinkedList<T>"],
                        1, 15, 1000,
                        "A Stack supports O(1) Push and Pop at the top of the collection in Last-In, First-Out order."),
                    CreateQuestion(quiz3Id, 3,
                        "What is the time complexity of grouping P players into T teams using LINQ .GroupBy()?",
                        "players.GroupBy(p => p.Team).Select(...);",
                        ["O(P)", "O(P^2)", "O(2^P)", "O(log P)"],
                        0, 20, 1000,
                        "LINQ GroupBy hashes each element once into buckets, running in O(P) linear time."),
                    CreateQuestion(quiz3Id, 4,
                        "Why does sending LiveQuestionViewDto without CorrectOptionIndex protect game integrity?",
                        "public sealed record LiveQuestionViewDto(int QuestionIndex, string Prompt, IReadOnlyList<string> Options);",
                        ["It reduces SQLite disk usage", "Players inspecting WebSocket JSON payloads cannot read the right answer early", "It doubles CPU clock speed", "It converts HTTP to FTP"],
                        1, 20, 1200,
                        "Projecting out sensitive fields at the server boundary enforces zero-trust security on the wire.")
                ]
            }
        };

        db.Quizzes.AddRange(quizzes);

        var stats = new List<QuestionStatEntity>();
        foreach (var quiz in quizzes)
        {
            foreach (var q in quiz.Questions)
            {
                int attempts = 16;
                int correct = q.OrderIndex switch { 0 => 14, 1 => 12, 2 => 11, 3 => 9, _ => 10 };
                stats.Add(new QuestionStatEntity
                {
                    QuestionId = q.Id,
                    QuizTitle = quiz.Title,
                    Prompt = q.Prompt,
                    TotalAttempts = attempts,
                    CorrectAttempts = correct
                });
            }
        }
        db.QuestionStats.AddRange(stats);

        var seededSessions = new List<GameSessionEntity>
        {
            BuildSeededSession("AQ1024", quiz1Id, quizzes[0].Title, "Тамерлан (Host)", GameMode.ClassicSpeed, -48,
            [
                ("Amina_CS", "Solo", 1, 5680, 5, 5, 5, 1420, false),
                ("Dias_Dev", "Solo", 2, 5140, 5, 5, 3, 2310, false),
                ("Arman_99", "Solo", 3, 4210, 4, 5, 2, 2890, false),
                ("Madina_K", "Solo", 4, 3890, 4, 5, 2, 3450, false),
                ("Nurlan_X", "Solo", 5, 2640, 3, 5, 1, 4120, false),
                ("Sanzhar", "Solo", 6, 1780, 2, 5, 1, 5100, false)
            ]),
            BuildSeededSession("AQ2048", quiz2Id, quizzes[1].Title, "Диас (Host)", GameMode.TeamBattle, -24,
            [
                ("Tamerlan", "Team Alpha", 1, 5910, 5, 5, 5, 1180, false),
                ("Alisher", "Team Beta", 2, 5230, 5, 5, 4, 1950, false),
                ("Dana_UI", "Team Alpha", 3, 4690, 4, 5, 3, 2400, false),
                ("Erlan", "Team Gamma", 4, 4120, 4, 5, 2, 3100, false),
                ("Kamila", "Team Beta", 5, 3550, 3, 5, 2, 3600, false),
                ("Timur_Z", "Team Gamma", 6, 2810, 3, 5, 1, 4520, false)
            ]),
            BuildSeededSession("AQ4096", quiz3Id, quizzes[2].Title, "Тамерлан (Host)", GameMode.KnockoutElimination, -8,
            [
                ("Amina_CS", "Solo", 1, 5820, 5, 5, 5, 1340, false),
                ("Dias_Dev", "Solo", 2, 4870, 4, 5, 4, 1890, true),
                ("Beka_SE", "Solo", 3, 3310, 3, 5, 3, 2150, true),
                ("Zarina", "Solo", 4, 2190, 2, 5, 2, 2980, true),
                ("Olzhas", "Solo", 5, 980, 1, 5, 1, 3700, true)
            ])
        };

        db.GameSessions.AddRange(seededSessions);
        await db.SaveChangesAsync();
    }

    private static QuestionEntity CreateQuestion(
        Guid quizId,
        int orderIndex,
        string prompt,
        string? codeSnippet,
        string[] options,
        int correctIndex,
        int timeLimit,
        int basePoints,
        string explanation)
    {
        return new QuestionEntity
        {
            Id = Guid.NewGuid(),
            QuizId = quizId,
            OrderIndex = orderIndex,
            Prompt = prompt,
            CodeSnippet = codeSnippet,
            OptionsJson = JsonSerializer.Serialize(options),
            CorrectOptionIndex = correctIndex,
            TimeLimitSeconds = timeLimit,
            BasePoints = basePoints,
            Explanation = explanation
        };
    }

    private static GameSessionEntity BuildSeededSession(
        string roomCode,
        Guid quizId,
        string quizTitle,
        string hostName,
        GameMode mode,
        int hoursAgo,
        (string Nickname, string Team, int Rank, int Score, int Correct, int Total, int Streak, double AvgMs, bool Eliminated)[] rows)
    {
        var sessionId = Guid.NewGuid();
        var started = DateTime.UtcNow.AddHours(hoursAgo);
        return new GameSessionEntity
        {
            Id = sessionId,
            RoomCode = roomCode,
            QuizId = quizId,
            QuizTitle = quizTitle,
            HostName = hostName,
            Mode = mode,
            StartedAtUtc = started,
            FinishedAtUtc = started.AddMinutes(6),
            Results = rows.Select(r => new PlayerResultEntity
            {
                Id = Guid.NewGuid(),
                GameSessionId = sessionId,
                Nickname = r.Nickname,
                TeamName = r.Team,
                FinalRank = r.Rank,
                FinalScore = r.Score,
                CorrectAnswers = r.Correct,
                TotalQuestions = r.Total,
                BestStreak = r.Streak,
                AverageResponseMs = r.AvgMs,
                WasEliminated = r.Eliminated
            }).ToList()
        };
    }

    private static async Task<bool> NeedsSchemaOrHashUpgradeAsync(ArenaDbContext db)
    {
        try
        {
            _ = await db.QuestionStats.AnyAsync();
            return await db.Users.AnyAsync(u => !u.PasswordHash.StartsWith("pbkdf2$"));
        }
        catch
        {
            return true;
        }
    }
}
