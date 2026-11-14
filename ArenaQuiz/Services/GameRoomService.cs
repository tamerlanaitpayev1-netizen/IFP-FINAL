using System.Collections.Concurrent;
using System.Security.Cryptography;
using ArenaQuiz.Shared.Functional;
using ArenaQuiz.Shared.Models;

namespace ArenaQuiz.Services;

/// <summary>
/// Thread-safe singleton engine managing concurrent SignalR quiz rooms, wall-clock drift-free server timers,
/// host-only authorization, anti-impersonation connection binding, O(P) answer progress deltas, and room eviction.
/// </summary>
public sealed class GameRoomService(
    IServiceScopeFactory scopeFactory,
    IGameRoomBroadcaster broadcaster,
    ILogger<GameRoomService> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, ActiveRoomState> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string RoomCode, string PlayerId)> _connections = new();
    private readonly ConcurrentDictionary<string, DateTime> _reactionCooldowns = new();

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
        string hostControlToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var room = new ActiveRoomState(
            code,
            quiz,
            string.IsNullOrWhiteSpace(request.HostName) ? "Host" : request.HostName.Trim(),
            request.Mode,
            request.AutoAdvance,
            hostConnectionId,
            hostControlToken);

        _rooms[code] = room;
        return Result<RoomSnapshotDto>.Success(room.BuildSnapshot(hostControlToken: hostControlToken));
    }

    public async Task<Result<RoomSnapshotDto>> ReclaimHostAsync(string roomCode, string hostControlToken, string newConnectionId)
    {
        string code = roomCode.Trim().ToUpperInvariant();
        if (!_rooms.TryGetValue(code, out var room))
        {
            return Result<RoomSnapshotDto>.Failure("Room not found or already closed.");
        }

        RoomSnapshotDto hostSnapshot;
        RoomSnapshotDto broadcastSnapshot;
        lock (room.SyncRoot)
        {
            if (string.IsNullOrWhiteSpace(hostControlToken) ||
                !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(room.HostControlToken),
                    System.Text.Encoding.UTF8.GetBytes(hostControlToken.Trim())))
            {
                return Result<RoomSnapshotDto>.Failure("Unauthorized: invalid host recovery token.");
            }

            room.HostConnectionId = newConnectionId;
            room.IsHostConnected = true;
            room.LastActivityUtc = DateTime.UtcNow;
            hostSnapshot = room.BuildSnapshot(hostControlToken: room.HostControlToken);
            broadcastSnapshot = hostSnapshot with { HostControlToken = null };
        }

        await broadcaster.RoomStateUpdated(code, broadcastSnapshot);
        return Result<RoomSnapshotDto>.Success(hostSnapshot);
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

            room.LastActivityUtc = DateTime.UtcNow;

            PlayerState? existingByToken = !string.IsNullOrWhiteSpace(request.ReconnectToken)
                ? room.Players.Values.FirstOrDefault(p => p.ReconnectToken == request.ReconnectToken)
                : null;

            PlayerState? existingByNick = room.Players.Values.FirstOrDefault(p =>
                string.Equals(p.Nickname, nickname, StringComparison.OrdinalIgnoreCase));

            if (existingByToken is not null)
            {
                existingByToken.ConnectionId = connectionId;
                existingByToken.IsConnected = true;
                isReconnect = true;
                _connections[connectionId] = (code, existingByToken.PlayerId);
                snapshot = room.BuildSnapshot(reconnectToken: existingByToken.ReconnectToken);
                standing = snapshot.Players.First(p => p.PlayerId == existingByToken.PlayerId);
            }
            else if (existingByNick is not null)
            {
                // Security: prevent hijacking a disconnected player's nickname without their secret ReconnectToken.
                return Result<(RoomSnapshotDto, PlayerStandingDto, bool)>.Failure(
                    $"Nickname '{nickname}' is already registered in this room. Use your original browser session to reconnect.");
            }
            else
            {
                var team = room.Mode == GameMode.TeamBattle
                    ? ScoreEngine.AssignBalancedTeam(room.Players.Values.Select(p => p.ToDto(0)), request.PreferredTeam)
                    : TeamSide.None;

                string playerId = Guid.NewGuid().ToString("N")[..10];
                string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                var player = new PlayerState(playerId, nickname, team, token, connectionId);
                room.Players[playerId] = player;
                _connections[connectionId] = (code, playerId);

                snapshot = room.BuildSnapshot(reconnectToken: token);
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

        await broadcaster.RoomStateUpdated(code, snapshot with { ReconnectToken = null, HostControlToken = null });
        return Result<(RoomSnapshotDto, PlayerStandingDto, bool)>.Success((snapshot, standing, isReconnect));
    }

    public async Task<Result<RoomSnapshotDto>> StartGameAsync(string roomCode, string callerConnectionId)
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
            if (room.HostConnectionId != callerConnectionId)
            {
                return Result<RoomSnapshotDto>.Failure("Unauthorized: only the room host can start the game.");
            }

            if (room.Phase != RoomPhase.Lobby)
            {
                return Result<RoomSnapshotDto>.Failure("Game has already started.");
            }

            if (room.Players.IsEmpty)
            {
                return Result<RoomSnapshotDto>.Failure("Wait for at least one player to join before starting.");
            }

            room.StartedAtUtc = DateTime.UtcNow;
            room.LastActivityUtc = DateTime.UtcNow;
            StartQuestionLocked(room, 0);
            snapshot = room.BuildSnapshot();
            questionView = snapshot.ActiveQuestion!;
            remaining = snapshot.RemainingSeconds;
        }

        await broadcaster.QuestionStarted(room.RoomCode, questionView, remaining);
        await broadcaster.RoomStateUpdated(room.RoomCode, snapshot);
        return Result<RoomSnapshotDto>.Success(snapshot);
    }

    public async Task<Result<bool>> TogglePauseQuestionAsync(string roomCode, string callerConnectionId)
    {
        string code = roomCode.Trim().ToUpperInvariant();
        if (!_rooms.TryGetValue(code, out var room))
        {
            return Result<bool>.Failure("Room not found.");
        }

        bool isPaused;
        int remaining;
        lock (room.SyncRoot)
        {
            if (room.HostConnectionId != callerConnectionId)
            {
                return Result<bool>.Failure("Unauthorized: only the room host can pause or resume.");
            }

            if (room.Phase != RoomPhase.QuestionActive)
            {
                return Result<bool>.Failure("Pause is only available during an active question.");
            }

            room.LastActivityUtc = DateTime.UtcNow;
            if (!room.IsPaused)
            {
                room.IsPaused = true;
                room.PausedAtUtc = DateTime.UtcNow;
                room.RemainingSeconds = ComputeWallClockRemainingLocked(room);
            }
            else
            {
                if (room.PausedAtUtc.HasValue)
                {
                    room.TotalPausedDuration += DateTime.UtcNow - room.PausedAtUtc.Value;
                    room.PausedAtUtc = null;
                }
                room.IsPaused = false;
                room.RemainingSeconds = ComputeWallClockRemainingLocked(room);
            }

            isPaused = room.IsPaused;
            remaining = room.RemainingSeconds;
        }

        await broadcaster.PauseStateChanged(code, isPaused, remaining);
        return Result<bool>.Success(isPaused);
    }

    public async Task<Result<AnswerAckDto>> SubmitAnswerAsync(SubmitAnswerRequest request, string callerConnectionId)
    {
        string code = request.RoomCode.Trim().ToUpperInvariant();
        if (!_rooms.TryGetValue(code, out var room))
        {
            return Result<AnswerAckDto>.Failure("Room not found.");
        }

        // Security: verify caller connection actually owns the PlayerId in this room.
        if (!_connections.TryGetValue(callerConnectionId, out var connBinding) ||
            !string.Equals(connBinding.RoomCode, code, StringComparison.OrdinalIgnoreCase))
        {
            return Result<AnswerAckDto>.Failure("Unauthorized: connection is not a joined player in this room.");
        }

        if (!string.IsNullOrWhiteSpace(request.PlayerId) && request.PlayerId != connBinding.PlayerId)
        {
            return Result<AnswerAckDto>.Failure("Unauthorized: cannot submit an answer on behalf of another player.");
        }

        string authenticPlayerId = connBinding.PlayerId;
        AnswerAckDto ack;
        AnswerProgressDto progressDelta;
        bool shouldRevealImmediately = false;

        lock (room.SyncRoot)
        {
            if (room.Phase != RoomPhase.QuestionActive || request.QuestionIndex != room.CurrentQuestionIndex)
            {
                return Result<AnswerAckDto>.Failure("Question is no longer accepting answers.");
            }

            if (room.IsPaused)
            {
                return Result<AnswerAckDto>.Failure("Question timer is currently paused by the host.");
            }

            if (!room.Players.TryGetValue(authenticPlayerId, out var player))
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

            double elapsedMs = Math.Max(0.0, (DateTime.UtcNow - room.QuestionStartedAtUtc - room.TotalPausedDuration).TotalMilliseconds);
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

            room.LastActivityUtc = DateTime.UtcNow;

            ack = new AnswerAckDto(
                request.QuestionIndex,
                request.SelectedOptionIndex,
                Math.Round(elapsedMs, 1),
                true,
                "Answer locked in on server.");

            var activePlayers = room.Players.Values.Where(p => p.IsConnected && !p.IsEliminated).ToList();
            progressDelta = new AnswerProgressDto(
                room.CurrentQuestionIndex,
                room.CurrentQuestionSubmissions.Count,
                activePlayers.Count);

            if (activePlayers.Count > 0 && activePlayers.All(p => room.CurrentQuestionSubmissions.ContainsKey(p.PlayerId)))
            {
                shouldRevealImmediately = true;
            }
        }

        if (shouldRevealImmediately)
        {
            await RevealCurrentQuestionInternalAsync(room);
        }
        else
        {
            // O(1) delta broadcast instead of re-sending full RoomSnapshotDto (avoids O(P^2) traffic).
            await broadcaster.AnswerProgressUpdated(code, progressDelta);
        }

        return Result<AnswerAckDto>.Success(ack);
    }

    public async Task<Result<QuestionRevealDto>> RevealCurrentQuestionAsync(string roomCode, string callerConnectionId)
    {
        string code = roomCode.Trim().ToUpperInvariant();
        if (!_rooms.TryGetValue(code, out var room))
        {
            return Result<QuestionRevealDto>.Failure("Room not found.");
        }

        lock (room.SyncRoot)
        {
            if (room.HostConnectionId != callerConnectionId)
            {
                return Result<QuestionRevealDto>.Failure("Unauthorized: only the room host can reveal answers.");
            }
        }

        return await RevealCurrentQuestionInternalAsync(room);
    }

    private async Task<Result<QuestionRevealDto>> RevealCurrentQuestionInternalAsync(ActiveRoomState room)
    {
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

        await broadcaster.QuestionRevealed(room.RoomCode, reveal);
        await broadcaster.RoomStateUpdated(room.RoomCode, snapshot);

        if (scheduleAutoAdvance)
        {
            ScheduleAutoAdvance(room);
        }

        return Result<QuestionRevealDto>.Success(reveal);
    }

    public async Task<Result<RoomSnapshotDto>> NextQuestionAsync(string roomCode, string callerConnectionId)
    {
        string code = roomCode.Trim().ToUpperInvariant();
        if (!_rooms.TryGetValue(code, out var room))
        {
            return Result<RoomSnapshotDto>.Failure("Room not found.");
        }

        lock (room.SyncRoot)
        {
            if (room.HostConnectionId != callerConnectionId)
            {
                return Result<RoomSnapshotDto>.Failure("Unauthorized: only the room host can advance questions.");
            }
        }

        return await NextQuestionInternalAsync(room);
    }

    private async Task<Result<RoomSnapshotDto>> NextQuestionInternalAsync(ActiveRoomState room)
    {
        RoomSnapshotDto snapshot;
        LiveQuestionViewDto? nextQuestion = null;
        int remaining = 0;
        SessionHistoryDto? completedSession = null;
        List<(Guid QuestionId, string Prompt, int Attempts, int Correct)>? statsSnapshot = null;

        lock (room.SyncRoot)
        {
            room.StopTimers();
            room.LastActivityUtc = DateTime.UtcNow;

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
                statsSnapshot = [.. room.QuestionStatsBuffer];
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
            await PersistSessionSafelyAsync(completedSession, room.Quiz.Id, statsSnapshot);
            await broadcaster.GameEnded(room.RoomCode, completedSession);
            await broadcaster.RoomStateUpdated(room.RoomCode, snapshot);
        }
        else if (nextQuestion is not null)
        {
            await broadcaster.QuestionStarted(room.RoomCode, nextQuestion, remaining);
            await broadcaster.RoomStateUpdated(room.RoomCode, snapshot);
        }

        return Result<RoomSnapshotDto>.Success(snapshot);
    }

    public async Task<Result<bool>> RemovePlayerAsync(string roomCode, string playerId, string callerConnectionId)
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
            if (room.HostConnectionId != callerConnectionId)
            {
                return Result<bool>.Failure("Unauthorized: only the room host can remove players.");
            }

            if (!room.Players.TryRemove(playerId, out removed))
            {
                return Result<bool>.Failure("Player not found.");
            }

            room.CurrentQuestionSubmissions.Remove(playerId);
            _connections.TryRemove(removed.ConnectionId, out _);
            snapshot = room.BuildSnapshot();
        }

        await broadcaster.PlayerLeft(code, removed.PlayerId, removed.Nickname);
        await broadcaster.RoomStateUpdated(code, snapshot);
        return Result<bool>.Success(true);
    }

    public async Task HandleDisconnectAsync(string connectionId)
    {
        _reactionCooldowns.TryRemove(connectionId, out _);

        foreach (var candidateRoom in _rooms.Values)
        {
            RoomSnapshotDto? hostDropSnapshot = null;
            lock (candidateRoom.SyncRoot)
            {
                if (candidateRoom.HostConnectionId == connectionId && candidateRoom.Phase != RoomPhase.PodiumFinished)
                {
                    candidateRoom.IsHostConnected = false;
                    hostDropSnapshot = candidateRoom.BuildSnapshot();
                }
            }

            if (hostDropSnapshot is not null)
            {
                await broadcaster.RoomStateUpdated(candidateRoom.RoomCode, hostDropSnapshot);
            }
        }

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

    public async Task BroadcastReactionAsync(string roomCode, string callerConnectionId, string reactionKey)
    {
        string code = roomCode.Trim().ToUpperInvariant();
        if (!_rooms.TryGetValue(code, out var room))
        {
            return;
        }

        // Rate limit reactions to 1 per 1.5s per connection.
        DateTime now = DateTime.UtcNow;
        if (_reactionCooldowns.TryGetValue(callerConnectionId, out var lastSent) &&
            (now - lastSent).TotalMilliseconds < 1500)
        {
            return;
        }

        string? senderNickname = null;
        lock (room.SyncRoot)
        {
            if (room.HostConnectionId == callerConnectionId)
            {
                senderNickname = room.HostName;
            }
            else if (_connections.TryGetValue(callerConnectionId, out var binding) &&
                     string.Equals(binding.RoomCode, code, StringComparison.OrdinalIgnoreCase) &&
                     room.Players.TryGetValue(binding.PlayerId, out var player))
            {
                senderNickname = player.Nickname;
            }
        }

        if (string.IsNullOrWhiteSpace(senderNickname))
        {
            return;
        }

        _reactionCooldowns[callerConnectionId] = now;
        string sanitizedKey = reactionKey.Trim();
        if (sanitizedKey.Length > 24)
        {
            sanitizedKey = sanitizedKey[..24];
        }

        var evt = new ReactionEventDto(code, senderNickname, sanitizedKey, now);
        await broadcaster.ReactionBroadcast(code, evt);
    }

    /// <summary>
    /// Evicts finished (> 15 min) or abandoned (> 30 min) rooms and disposes their timers.
    /// </summary>
    public int CleanupExpiredRooms(TimeSpan? finishedTtl = null, TimeSpan? abandonedTtl = null)
    {
        DateTime now = DateTime.UtcNow;
        TimeSpan finTtl = finishedTtl ?? TimeSpan.FromMinutes(15);
        TimeSpan abnTtl = abandonedTtl ?? TimeSpan.FromMinutes(30);
        int removedCount = 0;

        foreach (var kvp in _rooms)
        {
            var room = kvp.Value;
            bool shouldRemove;
            lock (room.SyncRoot)
            {
                shouldRemove = room.Phase == RoomPhase.PodiumFinished
                    ? (now - room.FinishedAtUtc) > finTtl
                    : (now - room.LastActivityUtc) > abnTtl;
            }

            if (shouldRemove && _rooms.TryRemove(kvp.Key, out var evicted))
            {
                evicted.StopTimers();
                foreach (var player in evicted.Players.Values)
                {
                    _connections.TryRemove(player.ConnectionId, out _);
                }
                removedCount++;
            }
        }

        return removedCount;
    }

    private void StartQuestionLocked(ActiveRoomState room, int questionIndex)
    {
        room.StopTimers();
        room.CurrentQuestionIndex = questionIndex;
        room.Phase = RoomPhase.QuestionActive;
        room.IsPaused = false;
        room.PausedAtUtc = null;
        room.TotalPausedDuration = TimeSpan.Zero;
        room.LastReveal = null;
        room.CurrentQuestionSubmissions.Clear();

        var q = room.Quiz.Questions[questionIndex];
        room.RemainingSeconds = q.TimeLimitSeconds;
        room.QuestionStartedAtUtc = DateTime.UtcNow;

        foreach (var p in room.Players.Values)
        {
            p.LastDeltaScore = 0;
        }

        // Poll every 500ms against wall clock to avoid System.Threading.Timer drift.
        room.QuestionTimer = new Timer(_ => _ = OnQuestionTimerTickAsync(room.RoomCode), null, 500, 500);
    }

    private static int ComputeWallClockRemainingLocked(ActiveRoomState room)
    {
        var q = room.Quiz.Questions[room.CurrentQuestionIndex];
        DateTime effectiveNow = room.IsPaused && room.PausedAtUtc.HasValue ? room.PausedAtUtc.Value : DateTime.UtcNow;
        double elapsedSeconds = (effectiveNow - room.QuestionStartedAtUtc - room.TotalPausedDuration).TotalSeconds;
        return Math.Max(0, (int)Math.Ceiling(q.TimeLimitSeconds - elapsedSeconds));
    }

    private async Task OnQuestionTimerTickAsync(string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
        {
            return;
        }

        bool shouldReveal = false;
        bool shouldEmitTick = false;
        int remaining;

        lock (room.SyncRoot)
        {
            if (room.Phase != RoomPhase.QuestionActive)
            {
                room.StopTimers();
                return;
            }

            if (room.IsPaused)
            {
                return;
            }

            int computed = ComputeWallClockRemainingLocked(room);
            if (computed != room.RemainingSeconds)
            {
                room.RemainingSeconds = computed;
                shouldEmitTick = true;
            }

            remaining = room.RemainingSeconds;
            if (remaining <= 0)
            {
                shouldReveal = true;
            }
        }

        try
        {
            if (shouldEmitTick)
            {
                await broadcaster.TimerTicked(roomCode, remaining);
            }
            if (shouldReveal)
            {
                await RevealCurrentQuestionInternalAsync(room);
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
        if (!_rooms.TryGetValue(roomCode, out var room))
        {
            return;
        }

        try
        {
            await NextQuestionInternalAsync(room);
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
        room.IsPaused = false;
        room.PausedAtUtc = null;

        var question = room.Quiz.Questions[room.CurrentQuestionIndex];
        int[] voteCounts = new int[question.Options.Count];
        var newlyEliminated = new List<string>();
        int attempts = 0;
        int correctCount = 0;

        foreach (var player in room.Players.Values)
        {
            if (player.IsEliminated)
            {
                continue;
            }

            if (room.CurrentQuestionSubmissions.TryGetValue(player.PlayerId, out var submission))
            {
                attempts++;
                voteCounts[submission.SelectedOptionIndex]++;
                player.AnsweredCount++;
                player.TotalResponseMs += submission.ElapsedMs;

                if (submission.IsCorrect)
                {
                    correctCount++;
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
                // Fair Knockout: only eliminate players who were connected and timed out, not dropped connections.
                if (room.Mode == GameMode.KnockoutElimination && player.IsConnected)
                {
                    player.IsEliminated = true;
                    newlyEliminated.Add(player.Nickname);
                }
            }
        }

        room.QuestionStatsBuffer.Add((question.Id, question.Prompt, attempts, correctCount));

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

    private async Task PersistSessionSafelyAsync(
        SessionHistoryDto summary,
        Guid quizId,
        IReadOnlyList<(Guid QuestionId, string Prompt, int Attempts, int Correct)>? statsSnapshot)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<QuizRepositoryService>();
            await repo.SaveCompletedSessionAsync(summary, quizId, statsSnapshot);
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
                buffer[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
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
        string hostConnectionId,
        string hostControlToken)
    {
        public object SyncRoot { get; } = new();
        public string RoomCode { get; } = roomCode;
        public QuizDetailDto Quiz { get; } = quiz;
        public string HostName { get; } = hostName;
        public GameMode Mode { get; } = mode;
        public bool AutoAdvance { get; } = autoAdvance;
        public string HostConnectionId { get; set; } = hostConnectionId;
        public string HostControlToken { get; } = hostControlToken;
        public bool IsHostConnected { get; set; } = true;
        public RoomPhase Phase { get; set; } = RoomPhase.Lobby;
        public int CurrentQuestionIndex { get; set; } = -1;
        public int RemainingSeconds { get; set; }
        public bool IsPaused { get; set; }
        public DateTime? PausedAtUtc { get; set; }
        public TimeSpan TotalPausedDuration { get; set; } = TimeSpan.Zero;
        public DateTime QuestionStartedAtUtc { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime FinishedAtUtc { get; set; }
        public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
        public QuestionRevealDto? LastReveal { get; set; }
        public ConcurrentDictionary<string, PlayerState> Players { get; } = new();
        public Dictionary<string, PlayerSubmission> CurrentQuestionSubmissions { get; } = new();
        public List<(Guid QuestionId, string Prompt, int Attempts, int Correct)> QuestionStatsBuffer { get; } = [];
        public Timer? QuestionTimer { get; set; }
        public Timer? AutoAdvanceTimer { get; set; }

        public void StopTimers()
        {
            QuestionTimer?.Dispose();
            QuestionTimer = null;
            AutoAdvanceTimer?.Dispose();
            AutoAdvanceTimer = null;
        }

        public RoomSnapshotDto BuildSnapshot(string? reconnectToken = null, string? hostControlToken = null)
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

            int activeCount = Players.Values.Count(p => p.IsConnected && !p.IsEliminated);

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
                reconnectToken,
                IsPaused,
                CurrentQuestionSubmissions.Count,
                activeCount,
                hostControlToken,
                IsHostConnected);
        }
    }
}
