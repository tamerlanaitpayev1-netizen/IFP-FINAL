using System.Collections.Concurrent;
using ArenaQuiz.Shared.Functional;
using ArenaQuiz.Shared.Models;

namespace ArenaQuiz.Services;

/// <summary>
/// Thread-safe singleton engine managing concurrent SignalR quiz rooms, authoritative server timers,
/// anti-cheat timestamp validation, reconnection recovery, and SQLite session persistence.
/// </summary>
public sealed class GameRoomService(
    IServiceScopeFactory scopeFactory,
    IGameRoomBroadcaster broadcaster,
    ILogger<GameRoomService> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, ActiveRoomState> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string RoomCode, string PlayerId)> _connections = new();

    public async Task<Result<RoomSnapshotDto>> CreateRoomAsync(CreateRoomRequest request, string hostConnectionId)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<QuizRepositoryService>();
        var quiz = await repo.GetQuizDetailAsync(request.QuizId);

        if (quiz is null)
        {
            return Result<RoomSnapshotDto>.Failure("Selected quiz was not found.");
        }

        if (quiz.Questions.Count == 0)
        {
            return Result<RoomSnapshotDto>.Failure("Selected quiz has no questions.");
        }

        string code = GenerateUniqueRoomCode();
        var room = new ActiveRoomState(
            code,
            quiz,
            string.IsNullOrWhiteSpace(request.HostName) ? "Host" : request.HostName.Trim(),
            request.Mode,
            request.AutoAdvance,
            hostConnectionId);

        _rooms[code] = room;
        return Result<RoomSnapshotDto>.Success(room.BuildSnapshot());
    }

    public async Task<Result<(RoomSnapshotDto Snapshot, PlayerStandingDto Player, bool IsReconnect)>> JoinRoomAsync(
        JoinRoomRequest request,
        string connectionId)
    {
        string code = request.RoomCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!_rooms.TryGetValue(code, out var room))
        {
            return Result<(RoomSnapshotDto, PlayerStandingDto, bool)>.Failure($"Room '{code}' does not exist.");
        }

        string nickname = request.Nickname?.Trim() ?? string.Empty;
        if (nickname.Length < 2 || nickname.Length > 24)
        {
            return Result<(RoomSnapshotDto, PlayerStandingDto, bool)>.Failure("Nickname must be between 2 and 24 characters.");
        }

        RoomSnapshotDto snapshot;
        PlayerStandingDto standing;
        bool isReconnect = false;

        lock (room.SyncRoot)
        {
            if (room.Phase == RoomPhase.PodiumFinished)
            {
                return Result<(RoomSnapshotDto, PlayerStandingDto, bool)>.Failure("This game session has already finished.");
            }

            PlayerState? existing = null;
            if (!string.IsNullOrWhiteSpace(request.ReconnectToken))
            {
                existing = room.Players.Values.FirstOrDefault(p => p.ReconnectToken == request.ReconnectToken);
            }

            existing ??= room.Players.Values.FirstOrDefault(p =>
                string.Equals(p.Nickname, nickname, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                if (existing.IsConnected && existing.ConnectionId != connectionId &&
                    string.IsNullOrWhiteSpace(request.ReconnectToken))
                {
                    return Result<(RoomSnapshotDto, PlayerStandingDto, bool)>.Failure($"Nickname '{nickname}' is already active in this room.");
                }

                existing.ConnectionId = connectionId;
                existing.IsConnected = true;
                isReconnect = true;
                _connections[connectionId] = (code, existing.PlayerId);
                snapshot = room.BuildSnapshot(existing.ReconnectToken);
                standing = snapshot.Players.First(p => p.PlayerId == existing.PlayerId);
            }
            else
            {
                var team = room.Mode == GameMode.TeamBattle
                    ? ScoreEngine.AssignBalancedTeam(room.Players.Values.Select(p => p.ToDto(0)), request.PreferredTeam)
                    : TeamSide.None;

                string playerId = Guid.NewGuid().ToString("N")[..10];
                string token = Guid.NewGuid().ToString("N");
                var player = new PlayerState(playerId, nickname, team, token, connectionId);
                room.Players[playerId] = player;
                _connections[connectionId] = (code, playerId);

                snapshot = room.BuildSnapshot(token);
                standing = snapshot.Players.First(p => p.PlayerId == playerId);
            }
        }

        if (isReconnect)
        {
            await broadcaster.PlayerReconnected(code, standing);
        }
        else
        {
            await broadcaster.PlayerJoined(code, standing);
        }

        await broadcaster.RoomStateUpdated(code, snapshot with { ReconnectToken = null });
        return Result<(RoomSnapshotDto, PlayerStandingDto, bool)>.Success((snapshot, standing, isReconnect));
    }

    public async Task<Result<RoomSnapshotDto>> StartGameAsync(string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode.Trim().ToUpperInvariant(), out var room))
        {
            return Result<RoomSnapshotDto>.Failure("Room not found.");
        }

        RoomSnapshotDto snapshot;
        LiveQuestionViewDto questionView;
        int remaining;

        lock (room.SyncRoot)
        {
            if (room.Phase != RoomPhase.Lobby)
            {
                return Result<RoomSnapshotDto>.Failure("Game has already started.");
            }

            if (room.Players.IsEmpty)
            {
                return Result<RoomSnapshotDto>.Failure("Wait for at least one player to join before starting.");
            }

            room.StartedAtUtc = DateTime.UtcNow;
            StartQuestionLocked(room, 0);
            snapshot = room.BuildSnapshot();
            questionView = snapshot.ActiveQuestion!;
            remaining = snapshot.RemainingSeconds;
        }

        await broadcaster.QuestionStarted(room.RoomCode, questionView, remaining);
        await broadcaster.RoomStateUpdated(room.RoomCode, snapshot);
        return Result<RoomSnapshotDto>.Success(snapshot);
    }

    public async Task<Result<AnswerAckDto>> SubmitAnswerAsync(SubmitAnswerRequest request)
    {
        string code = request.RoomCode.Trim().ToUpperInvariant();
        if (!_rooms.TryGetValue(code, out var room))
        {
            return Result<AnswerAckDto>.Failure("Room not found.");
        }

        AnswerAckDto ack;
        bool shouldRevealImmediately = false;

        lock (room.SyncRoot)
        {
            if (room.Phase != RoomPhase.QuestionActive || request.QuestionIndex != room.CurrentQuestionIndex)
            {
                return Result<AnswerAckDto>.Failure("Question is no longer accepting answers.");
            }

            if (!room.Players.TryGetValue(request.PlayerId, out var player))
            {
                return Result<AnswerAckDto>.Failure("Player is not registered in this room.");
            }

            if (player.IsEliminated)
            {
                return Result<AnswerAckDto>.Failure("Eliminated players can spectate but cannot submit answers.");
            }

            if (room.CurrentQuestionSubmissions.ContainsKey(player.PlayerId))
            {
                return Result<AnswerAckDto>.Failure("Answer already submitted for this question.");
            }

            var question = room.Quiz.Questions[room.CurrentQuestionIndex];
            if (request.SelectedOptionIndex < 0 || request.SelectedOptionIndex >= question.Options.Count)
            {
                return Result<AnswerAckDto>.Failure("Invalid option index.");
            }

            double elapsedMs = (DateTime.UtcNow - room.QuestionStartedAtUtc).TotalMilliseconds;
            bool isCorrect = request.SelectedOptionIndex == question.CorrectOptionIndex;

            var breakdown = ScoreEngine.CalculateAnswerScore(
                isCorrect,
                question.BasePoints,
                question.TimeLimitSeconds,
                elapsedMs,
                player.CurrentStreak,
                room.Mode);

            room.CurrentQuestionSubmissions[player.PlayerId] = new PlayerSubmission(
                request.SelectedOptionIndex,
                elapsedMs,
                isCorrect,
                breakdown);

            ack = new AnswerAckDto(
                request.QuestionIndex,
                request.SelectedOptionIndex,
                Math.Round(elapsedMs, 1),
                true,
                "Answer locked in on server.");

            var activePlayers = room.Players.Values.Where(p => p.IsConnected && !p.IsEliminated).ToList();
            if (activePlayers.Count > 0 && activePlayers.All(p => room.CurrentQuestionSubmissions.ContainsKey(p.PlayerId)))
            {
                shouldRevealImmediately = true;
            }
        }

        if (shouldRevealImmediately)
        {
            await RevealCurrentQuestionAsync(code);
        }
        else
        {
            RoomSnapshotDto snap;
            lock (room.SyncRoot)
            {
                snap = room.BuildSnapshot();
            }
            await broadcaster.RoomStateUpdated(code, snap);
        }

        return Result<AnswerAckDto>.Success(ack);
    }

    public async Task<Result<QuestionRevealDto>> RevealCurrentQuestionAsync(string roomCode)
    {
        string code = roomCode.Trim().ToUpperInvariant();
        if (!_rooms.TryGetValue(code, out var room))
        {
            return Result<QuestionRevealDto>.Failure("Room not found.");
        }

        QuestionRevealDto? reveal;
        RoomSnapshotDto? snapshot;
        bool scheduleAutoAdvance = false;

        lock (room.SyncRoot)
        {
            if (room.Phase != RoomPhase.QuestionActive)
            {
                if (room.LastReveal is not null)
                {
                    return Result<QuestionRevealDto>.Success(room.LastReveal);
                }
                return Result<QuestionRevealDto>.Failure("No active question to reveal.");
            }

            reveal = FinalizeQuestionRevealLocked(room);
            snapshot = room.BuildSnapshot();
            scheduleAutoAdvance = room.AutoAdvance;
        }

        await broadcaster.QuestionRevealed(code, reveal);
        await broadcaster.RoomStateUpdated(code, snapshot);

        if (scheduleAutoAdvance)
        {
            ScheduleAutoAdvance(room);
        }

        return Result<QuestionRevealDto>.Success(reveal);
    }

    public async Task<Result<RoomSnapshotDto>> NextQuestionAsync(string roomCode)
    {
        string code = roomCode.Trim().ToUpperInvariant();
        if (!_rooms.TryGetValue(code, out var room))
        {
            return Result<RoomSnapshotDto>.Failure("Room not found.");
        }

        RoomSnapshotDto snapshot;
        LiveQuestionViewDto? nextQuestion = null;
        int remaining = 0;
        SessionHistoryDto? completedSession = null;

        lock (room.SyncRoot)
        {
            room.StopTimers();

            if (room.Phase == RoomPhase.PodiumFinished)
            {
                return Result<RoomSnapshotDto>.Success(room.BuildSnapshot());
            }

            if (room.Phase == RoomPhase.QuestionActive)
            {
                FinalizeQuestionRevealLocked(room);
            }

            int nextIdx = room.CurrentQuestionIndex + 1;
            int activeSurvivors = room.Players.Values.Count(p => !p.IsEliminated);
            bool knockoutEnded = room.Mode == GameMode.KnockoutElimination && room.Players.Count > 1 && activeSurvivors <= 1;

            if (nextIdx >= room.Quiz.Questions.Count || knockoutEnded)
            {
                room.Phase = RoomPhase.PodiumFinished;
                room.FinishedAtUtc = DateTime.UtcNow;
                completedSession = BuildSessionSummaryLocked(room);
                snapshot = room.BuildSnapshot();
            }
            else
            {
                StartQuestionLocked(room, nextIdx);
                snapshot = room.BuildSnapshot();
                nextQuestion = snapshot.ActiveQuestion;
                remaining = snapshot.RemainingSeconds;
            }
        }

        if (completedSession is not null)
        {
            await PersistSessionSafelyAsync(completedSession, room.Quiz.Id);
            await broadcaster.GameEnded(code, completedSession);
            await broadcaster.RoomStateUpdated(code, snapshot);
        }
        else if (nextQuestion is not null)
        {
            await broadcaster.QuestionStarted(code, nextQuestion, remaining);
            await broadcaster.RoomStateUpdated(code, snapshot);
        }

        return Result<RoomSnapshotDto>.Success(snapshot);
    }

    public async Task<Result<bool>> RemovePlayerAsync(string roomCode, string playerId)
    {
        string code = roomCode.Trim().ToUpperInvariant();
        if (!_rooms.TryGetValue(code, out var room))
        {
            return Result<bool>.Failure("Room not found.");
        }

        PlayerState? removed;
        RoomSnapshotDto snapshot;
        lock (room.SyncRoot)
        {
            if (!room.Players.TryRemove(playerId, out removed))
            {
                return Result<bool>.Failure("Player not found.");
            }

            room.CurrentQuestionSubmissions.Remove(playerId);
            snapshot = room.BuildSnapshot();
        }

        await broadcaster.PlayerLeft(code, removed.PlayerId, removed.Nickname);
        await broadcaster.RoomStateUpdated(code, snapshot);
        return Result<bool>.Success(true);
    }

    public async Task HandleDisconnectAsync(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var mapping))
        {
            return;
        }

        if (!_rooms.TryGetValue(mapping.RoomCode, out var room))
        {
            return;
        }

        RoomSnapshotDto? snapshot = null;
        string? nickname = null;

        lock (room.SyncRoot)
        {
            if (room.Players.TryGetValue(mapping.PlayerId, out var player) && player.ConnectionId == connectionId)
            {
                player.IsConnected = false;
                nickname = player.Nickname;
                snapshot = room.BuildSnapshot();
            }
        }

        if (snapshot is not null && nickname is not null)
        {
            await broadcaster.PlayerLeft(mapping.RoomCode, mapping.PlayerId, nickname);
            await broadcaster.RoomStateUpdated(mapping.RoomCode, snapshot);
        }
    }

    public IReadOnlyList<OpenRoomSummaryDto> GetOpenRooms()
    {
        return _rooms.Values
            .Select(r =>
            {
                lock (r.SyncRoot)
                {
                    return new OpenRoomSummaryDto(
                        r.RoomCode,
                        r.Quiz.Title,
                        r.HostName,
                        r.Mode,
                        r.Phase,
                        r.Players.Count,
                        r.Quiz.Questions.Count);
                }
            })
            .Where(r => r.Phase != RoomPhase.PodiumFinished)
            .OrderByDescending(r => r.PlayerCount)
            .ToList();
    }

    public async Task BroadcastReactionAsync(string roomCode, string nickname, string reactionKey)
    {
        string code = roomCode.Trim().ToUpperInvariant();
        if (_rooms.ContainsKey(code))
        {
            var evt = new ReactionEventDto(code, nickname, reactionKey, DateTime.UtcNow);
            await broadcaster.ReactionBroadcast(code, evt);
        }
    }

    private void StartQuestionLocked(ActiveRoomState room, int questionIndex)
    {
        room.StopTimers();
        room.CurrentQuestionIndex = questionIndex;
        room.Phase = RoomPhase.QuestionActive;
        room.LastReveal = null;
        room.CurrentQuestionSubmissions.Clear();

        var q = room.Quiz.Questions[questionIndex];
        room.RemainingSeconds = q.TimeLimitSeconds;
        room.QuestionStartedAtUtc = DateTime.UtcNow;

        foreach (var p in room.Players.Values)
        {
            p.LastDeltaScore = 0;
        }

        room.QuestionTimer = new Timer(_ => _ = OnQuestionTimerTickAsync(room.RoomCode), null, 1000, 1000);
    }

    private async Task OnQuestionTimerTickAsync(string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
        {
            return;
        }

        bool shouldReveal = false;
        int remaining;

        lock (room.SyncRoot)
        {
            if (room.Phase != RoomPhase.QuestionActive)
            {
                room.StopTimers();
                return;
            }

            room.RemainingSeconds = Math.Max(0, room.RemainingSeconds - 1);
            remaining = room.RemainingSeconds;
            if (remaining <= 0)
            {
                shouldReveal = true;
            }
        }

        try
        {
            await broadcaster.TimerTicked(roomCode, remaining);
            if (shouldReveal)
            {
                await RevealCurrentQuestionAsync(roomCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Timer tick error for room {RoomCode}", roomCode);
        }
    }

    private void ScheduleAutoAdvance(ActiveRoomState room)
    {
        lock (room.SyncRoot)
        {
            room.AutoAdvanceTimer?.Dispose();
            room.AutoAdvanceTimer = new Timer(_ => _ = SafeAutoAdvanceAsync(room.RoomCode), null, 5000, Timeout.Infinite);
        }
    }

    private async Task SafeAutoAdvanceAsync(string roomCode)
    {
        try
        {
            await NextQuestionAsync(roomCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-advance failed for room {RoomCode}", roomCode);
        }
    }

    private static QuestionRevealDto FinalizeQuestionRevealLocked(ActiveRoomState room)
    {
        room.StopTimers();
        room.Phase = RoomPhase.QuestionReveal;

        var question = room.Quiz.Questions[room.CurrentQuestionIndex];
        int[] voteCounts = new int[question.Options.Count];
        var newlyEliminated = new List<string>();

        foreach (var player in room.Players.Values)
        {
            if (player.IsEliminated)
            {
                continue;
            }

            if (room.CurrentQuestionSubmissions.TryGetValue(player.PlayerId, out var submission))
            {
                voteCounts[submission.SelectedOptionIndex]++;
                player.AnsweredCount++;
                player.TotalResponseMs += submission.ElapsedMs;

                if (submission.IsCorrect)
                {
                    player.CorrectCount++;
                    player.CurrentStreak = submission.Breakdown.NewStreak;
                    player.BestStreak = Math.Max(player.BestStreak, player.CurrentStreak);
                    player.TotalScore += submission.Breakdown.TotalAwarded;
                    player.LastDeltaScore = submission.Breakdown.TotalAwarded;
                }
                else
                {
                    player.CurrentStreak = 0;
                    player.LastDeltaScore = 0;
                    if (room.Mode == GameMode.KnockoutElimination)
                    {
                        player.IsEliminated = true;
                        newlyEliminated.Add(player.Nickname);
                    }
                }
            }
            else
            {
                player.CurrentStreak = 0;
                player.LastDeltaScore = 0;
                if (room.Mode == GameMode.KnockoutElimination)
                {
                    player.IsEliminated = true;
                    newlyEliminated.Add(player.Nickname);
                }
            }
        }

        var standings = ScoreEngine.ComputeLeaderboard(room.Players.Values.Select(p => p.ToDto(0)));
        var teams = ScoreEngine.ComputeTeamStandings(standings);

        var reveal = new QuestionRevealDto(
            room.CurrentQuestionIndex,
            question.CorrectOptionIndex,
            question.Explanation,
            voteCounts,
            standings,
            teams,
            newlyEliminated);

        room.LastReveal = reveal;
        return reveal;
    }

    private static SessionHistoryDto BuildSessionSummaryLocked(ActiveRoomState room)
    {
        var standings = ScoreEngine.ComputeLeaderboard(room.Players.Values.Select(p => p.ToDto(0)));
        int totalQuestions = room.Quiz.Questions.Count;

        var playerResults = standings.Select(s =>
        {
            var raw = room.Players[s.PlayerId];
            return new PlayerResultDto(
                s.Nickname,
                ScoreEngine.FormatTeamName(s.Team),
                s.Rank,
                s.TotalScore,
                s.CorrectCount,
                totalQuestions,
                raw.BestStreak,
                s.AverageResponseMs,
                s.IsEliminated);
        }).ToList();

        var winner = playerResults.FirstOrDefault();
        double avgAccuracy = playerResults.Count > 0
            ? Math.Round(playerResults.Average(r => totalQuestions > 0 ? (double)r.CorrectAnswers / totalQuestions * 100.0 : 0.0), 1)
            : 0.0;

        return new SessionHistoryDto(
            Guid.NewGuid(),
            room.RoomCode,
            room.Quiz.Title,
            room.HostName,
            room.Mode,
            playerResults.Count,
            winner?.Nickname ?? "No Players",
            winner?.FinalScore ?? 0,
            avgAccuracy,
            room.StartedAtUtc == default ? DateTime.UtcNow : room.StartedAtUtc,
            room.FinishedAtUtc == default ? DateTime.UtcNow : room.FinishedAtUtc,
            playerResults);
    }

    private async Task PersistSessionSafelyAsync(SessionHistoryDto summary, Guid quizId)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<QuizRepositoryService>();
            await repo.SaveCompletedSessionAsync(summary, quizId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist completed session for room {RoomCode}", summary.RoomCode);
        }
    }

    private string GenerateUniqueRoomCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> buffer = stackalloc char[4];
        while (true)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = chars[Random.Shared.Next(chars.Length)];
            }
            string candidate = $"AQ{new string(buffer)}";
            if (!_rooms.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }

    public void Dispose()
    {
        foreach (var room in _rooms.Values)
        {
            room.StopTimers();
        }
    }

    private sealed record PlayerSubmission(
        int SelectedOptionIndex,
        double ElapsedMs,
        bool IsCorrect,
        AnswerScoreBreakdown Breakdown);

    private sealed class PlayerState(
        string playerId,
        string nickname,
        TeamSide team,
        string reconnectToken,
        string connectionId)
    {
        public string PlayerId { get; } = playerId;
        public string Nickname { get; } = nickname;
        public TeamSide Team { get; } = team;
        public string ReconnectToken { get; } = reconnectToken;
        public string ConnectionId { get; set; } = connectionId;
        public bool IsConnected { get; set; } = true;
        public bool IsEliminated { get; set; }
        public int TotalScore { get; set; }
        public int LastDeltaScore { get; set; }
        public int CurrentStreak { get; set; }
        public int BestStreak { get; set; }
        public int CorrectCount { get; set; }
        public int AnsweredCount { get; set; }
        public double TotalResponseMs { get; set; }

        public PlayerStandingDto ToDto(int rank) => new(
            PlayerId,
            Nickname,
            Team,
            TotalScore,
            LastDeltaScore,
            CurrentStreak,
            CorrectCount,
            AnsweredCount,
            AnsweredCount > 0 ? Math.Round(TotalResponseMs / AnsweredCount, 1) : 0.0,
            IsConnected,
            IsEliminated,
            rank);
    }

    private sealed class ActiveRoomState(
        string roomCode,
        QuizDetailDto quiz,
        string hostName,
        GameMode mode,
        bool autoAdvance,
        string hostConnectionId)
    {
        public object SyncRoot { get; } = new();
        public string RoomCode { get; } = roomCode;
        public QuizDetailDto Quiz { get; } = quiz;
        public string HostName { get; } = hostName;
        public GameMode Mode { get; } = mode;
        public bool AutoAdvance { get; } = autoAdvance;
        public string HostConnectionId { get; } = hostConnectionId;
        public RoomPhase Phase { get; set; } = RoomPhase.Lobby;
        public int CurrentQuestionIndex { get; set; } = -1;
        public int RemainingSeconds { get; set; }
        public DateTime QuestionStartedAtUtc { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime FinishedAtUtc { get; set; }
        public QuestionRevealDto? LastReveal { get; set; }
        public ConcurrentDictionary<string, PlayerState> Players { get; } = new();
        public Dictionary<string, PlayerSubmission> CurrentQuestionSubmissions { get; } = new();
        public Timer? QuestionTimer { get; set; }
        public Timer? AutoAdvanceTimer { get; set; }

        public void StopTimers()
        {
            QuestionTimer?.Dispose();
            QuestionTimer = null;
            AutoAdvanceTimer?.Dispose();
            AutoAdvanceTimer = null;
        }

        public RoomSnapshotDto BuildSnapshot(string? reconnectToken = null)
        {
            var standings = ScoreEngine.ComputeLeaderboard(Players.Values.Select(p => p.ToDto(0)));
            var teams = ScoreEngine.ComputeTeamStandings(standings);

            LiveQuestionViewDto? activeQuestion = null;
            if (Phase is RoomPhase.QuestionActive or RoomPhase.QuestionReveal &&
                CurrentQuestionIndex >= 0 && CurrentQuestionIndex < Quiz.Questions.Count)
            {
                var q = Quiz.Questions[CurrentQuestionIndex];
                activeQuestion = new LiveQuestionViewDto(
                    CurrentQuestionIndex,
                    Quiz.Questions.Count,
                    q.Prompt,
                    q.CodeSnippet,
                    q.Options,
                    q.TimeLimitSeconds,
                    q.BasePoints,
                    Mode);
            }

            return new RoomSnapshotDto(
                RoomCode,
                Quiz.Title,
                HostName,
                Mode,
                Phase,
                AutoAdvance,
                CurrentQuestionIndex,
                Quiz.Questions.Count,
                RemainingSeconds,
                standings,
                teams,
                activeQuestion,
                LastReveal,
                reconnectToken);
        }
    }
}

