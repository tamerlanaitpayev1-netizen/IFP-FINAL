using ArenaQuiz.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;

namespace ArenaQuiz.Client.Services;

/// <summary>
/// Centralized reactive Blazor state container managing SignalR Hub connection, live room snapshot,
/// active user RBAC session, and session-storage reconnection tokens.
/// </summary>
public sealed class GameStateContainer(
    NavigationManager navigation,
    IJSRuntime js,
    SoundService sound) : IAsyncDisposable
{
    private HubConnection? _hub;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    public event Action? OnStateChanged;

    public RoomSnapshotDto? CurrentRoom { get; private set; }
    public SessionHistoryDto? LastSessionSummary { get; private set; }
    public string? CurrentPlayerId { get; private set; }
    public string? CurrentNickname { get; private set; }
    public string? ReconnectToken { get; private set; }
    public bool IsHostOfCurrentRoom { get; private set; }

    public int? SelectedOptionIndex { get; private set; }
    public bool HasAnsweredCurrentQuestion { get; private set; }
    public AnswerAckDto? LastAnswerAck { get; private set; }
    public string? LastError { get; private set; }
    public bool IsBusy { get; private set; }

    public List<ReactionEventDto> RecentReactions { get; } = [];

    public UserSessionDto CurrentUser { get; private set; } =
        new("tamerlan", "Тамерлан (Host)", UserRole.Host, "default-host-session");

    public HubConnectionState ConnectionState =>
        _hub?.State ?? HubConnectionState.Disconnected;

    public bool IsEliminated =>
        CurrentPlayerId is not null &&
        (CurrentRoom?.Players.FirstOrDefault(p => p.PlayerId == CurrentPlayerId)?.IsEliminated ?? false);

    public PlayerStandingDto? CurrentPlayerStanding =>
        CurrentPlayerId is null
            ? null
            : CurrentRoom?.Players.FirstOrDefault(p => p.PlayerId == CurrentPlayerId);

    public void SetUserSession(UserSessionDto session)
    {
        CurrentUser = session;
        NotifyStateChanged();
    }

    public void ClearError()
    {
        LastError = null;
        NotifyStateChanged();
    }

    public async Task EnsureConnectedAsync()
    {
        if (_hub is { State: HubConnectionState.Connected })
        {
            return;
        }

        await _connectLock.WaitAsync();
        try
        {
            if (_hub is null)
            {
                _hub = new HubConnectionBuilder()
                    .WithUrl(navigation.ToAbsoluteUri("/gamehub"))
                    .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)])
                    .Build();

                RegisterHubHandlers(_hub);
            }

            if (_hub.State == HubConnectionState.Disconnected)
            {
                await _hub.StartAsync();
                NotifyStateChanged();
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<Result<RoomSnapshotDto>> CreateRoomAsync(Guid quizId, GameMode mode, bool autoAdvance)
    {
        IsBusy = true;
        LastError = null;
        NotifyStateChanged();

        try
        {
            await EnsureConnectedAsync();
            var request = new CreateRoomRequest(quizId, CurrentUser.FullName, mode, autoAdvance);
            var result = await _hub!.InvokeAsync<Result<RoomSnapshotDto>>("CreateRoom", request);
            if (result.IsSuccess && result.Value is not null)
            {
                CurrentRoom = result.Value;
                IsHostOfCurrentRoom = true;
                CurrentPlayerId = null;
                LastSessionSummary = null;
                ResetQuestionSelection();
                await sound.PlayAsync("join");
            }
            else
            {
                LastError = result.Error ?? "Failed to create room.";
            }

            return result;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return Result<RoomSnapshotDto>.Failure(ex.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyStateChanged();
        }
    }

    public async Task<Result<RoomSnapshotDto>> JoinRoomAsync(string roomCode, string nickname, TeamSide team = TeamSide.None, string? reconnectToken = null)
    {
        IsBusy = true;
        LastError = null;
        NotifyStateChanged();

        try
        {
            await EnsureConnectedAsync();
            var request = new JoinRoomRequest(roomCode.Trim().ToUpperInvariant(), nickname.Trim(), team, reconnectToken);
            var result = await _hub!.InvokeAsync<Result<RoomSnapshotDto>>("JoinRoom", request);

            if (result.IsSuccess && result.Value is not null)
            {
                CurrentRoom = result.Value;
                IsHostOfCurrentRoom = false;
                CurrentNickname = request.Nickname;
                ReconnectToken = result.Value.ReconnectToken;
                LastSessionSummary = null;

                var self = result.Value.Players.FirstOrDefault(p =>
                    string.Equals(p.Nickname, request.Nickname, StringComparison.OrdinalIgnoreCase));
                CurrentPlayerId = self?.PlayerId;

                await PersistSessionKeysAsync(result.Value.RoomCode, request.Nickname, ReconnectToken);
                await sound.PlayAsync("join");
            }
            else
            {
                LastError = result.Error ?? "Failed to join room.";
            }

            return result;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return Result<RoomSnapshotDto>.Failure(ex.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyStateChanged();
        }
    }

    public async Task<bool> TryRecoverSessionAsync(string? targetRoomCode = null)
    {
        try
        {
            string? savedCode = await js.InvokeAsync<string?>("arenaStorage.get", "aq_room");
            string? savedNick = await js.InvokeAsync<string?>("arenaStorage.get", "aq_nick");
            string? savedToken = await js.InvokeAsync<string?>("arenaStorage.get", "aq_token");

            if (string.IsNullOrWhiteSpace(savedCode) || string.IsNullOrWhiteSpace(savedNick) || string.IsNullOrWhiteSpace(savedToken))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(targetRoomCode) &&
                !string.Equals(savedCode, targetRoomCode.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var res = await JoinRoomAsync(savedCode, savedNick, TeamSide.None, savedToken);
            return res.IsSuccess;
        }
        catch
        {
            return false;
        }
    }

    public async Task<Result<RoomSnapshotDto>> StartGameAsync()
    {
        if (CurrentRoom is null || _hub is null)
        {
            return Result<RoomSnapshotDto>.Failure("No active room.");
        }

        var result = await _hub.InvokeAsync<Result<RoomSnapshotDto>>("StartGame", CurrentRoom.RoomCode);
        if (!result.IsSuccess)
        {
            LastError = result.Error;
            NotifyStateChanged();
        }
        return result;
    }

    public async Task<Result<AnswerAckDto>> SubmitAnswerAsync(int optionIndex)
    {
        if (CurrentRoom?.ActiveQuestion is null || CurrentPlayerId is null || _hub is null)
        {
            return Result<AnswerAckDto>.Failure("No active question or player session.");
        }

        if (HasAnsweredCurrentQuestion)
        {
            return Result<AnswerAckDto>.Failure("Already answered.");
        }

        SelectedOptionIndex = optionIndex;
        HasAnsweredCurrentQuestion = true;
        NotifyStateChanged();

        var request = new SubmitAnswerRequest(
            CurrentRoom.RoomCode,
            CurrentPlayerId,
            CurrentRoom.ActiveQuestion.QuestionIndex,
            optionIndex);

        var result = await _hub.InvokeAsync<Result<AnswerAckDto>>("SubmitAnswer", request);
        if (result.IsSuccess && result.Value is not null)
        {
            LastAnswerAck = result.Value;
        }
        else
        {
            HasAnsweredCurrentQuestion = false;
            SelectedOptionIndex = null;
            LastError = result.Error;
        }

        NotifyStateChanged();
        return result;
    }

    public async Task RevealCurrentQuestionAsync()
    {
        if (CurrentRoom is null || _hub is null) return;
        var res = await _hub.InvokeAsync<Result<QuestionRevealDto>>("RevealCurrentQuestion", CurrentRoom.RoomCode);
        if (!res.IsSuccess)
        {
            LastError = res.Error;
            NotifyStateChanged();
        }
    }

    public async Task NextQuestionAsync()
    {
        if (CurrentRoom is null || _hub is null) return;
        var res = await _hub.InvokeAsync<Result<RoomSnapshotDto>>("NextQuestion", CurrentRoom.RoomCode);
        if (!res.IsSuccess)
        {
            LastError = res.Error;
            NotifyStateChanged();
        }
    }

    public async Task RemovePlayerAsync(string playerId)
    {
        if (CurrentRoom is null || _hub is null) return;
        await _hub.InvokeAsync<Result<bool>>("RemovePlayer", CurrentRoom.RoomCode, playerId);
    }

    public async Task SendReactionAsync(string reactionKey)
    {
        if (CurrentRoom is null || _hub is null) return;
        string sender = CurrentNickname ?? CurrentUser.FullName;
        await _hub.InvokeAsync("SendReaction", CurrentRoom.RoomCode, sender, reactionKey);
    }

    public async Task LeaveRoomAsync()
    {
        CurrentRoom = null;
        CurrentPlayerId = null;
        ReconnectToken = null;
        IsHostOfCurrentRoom = false;
        LastSessionSummary = null;
        RecentReactions.Clear();
        ResetQuestionSelection();

        try
        {
            await js.InvokeVoidAsync("arenaStorage.clear");
        }
        catch
        {
            // Ignore JS errors on disposal/navigation.
        }

        NotifyStateChanged();
    }

    private void RegisterHubHandlers(HubConnection hub)
    {
        hub.On<RoomSnapshotDto>("RoomStateUpdated", snapshot =>
        {
            CurrentRoom = snapshot;
            NotifyStateChanged();
        });

        hub.On<PlayerStandingDto>("PlayerJoined", async _ =>
        {
            await sound.PlayAsync("join");
            NotifyStateChanged();
        });

        hub.On<PlayerStandingDto>("PlayerReconnected", _ => NotifyStateChanged());

        hub.On<string, string>("PlayerLeft", (playerId, _) =>
        {
            if (CurrentPlayerId == playerId && !IsHostOfCurrentRoom)
            {
                LastError = "You were disconnected or removed from the room.";
            }
            NotifyStateChanged();
        });

        hub.On<LiveQuestionViewDto, int>("QuestionStarted", (question, remaining) =>
        {
            ResetQuestionSelection();
            if (CurrentRoom is not null)
            {
                CurrentRoom = CurrentRoom with
                {
                    Phase = RoomPhase.QuestionActive,
                    CurrentQuestionIndex = question.QuestionIndex,
                    ActiveQuestion = question,
                    LastReveal = null,
                    RemainingSeconds = remaining
                };
            }
            NotifyStateChanged();
        });

        hub.On<int>("TimerTicked", async remaining =>
        {
            if (CurrentRoom is not null)
            {
                CurrentRoom = CurrentRoom with { RemainingSeconds = remaining };
            }
            if (remaining is > 0 and <= 5)
            {
                await sound.PlayAsync("tick");
            }
            NotifyStateChanged();
        });

        hub.On<AnswerAckDto>("AnswerSubmittedAck", ack =>
        {
            LastAnswerAck = ack;
            NotifyStateChanged();
        });

        hub.On<QuestionRevealDto>("QuestionRevealed", async reveal =>
        {
            if (CurrentRoom is not null)
            {
                CurrentRoom = CurrentRoom with
                {
                    Phase = RoomPhase.QuestionReveal,
                    LastReveal = reveal,
                    RemainingSeconds = 0,
                    Players = reveal.Leaderboard,
                    Teams = reveal.TeamLeaderboard
                };
            }

            if (!IsHostOfCurrentRoom && SelectedOptionIndex.HasValue)
            {
                await sound.PlayAsync(SelectedOptionIndex.Value == reveal.CorrectOptionIndex ? "correct" : "wrong");
            }

            NotifyStateChanged();
        });

        hub.On<SessionHistoryDto>("GameEnded", async summary =>
        {
            LastSessionSummary = summary;
            if (CurrentRoom is not null)
            {
                CurrentRoom = CurrentRoom with { Phase = RoomPhase.PodiumFinished };
            }
            await sound.PlayAsync("podium");
            NotifyStateChanged();
        });

        hub.On<ReactionEventDto>("ReactionBroadcast", reaction =>
        {
            RecentReactions.Add(reaction);
            if (RecentReactions.Count > 12)
            {
                RecentReactions.RemoveAt(0);
            }
            NotifyStateChanged();
        });

        hub.On<string>("ErrorOccurred", message =>
        {
            LastError = message;
            NotifyStateChanged();
        });

        hub.Reconnecting += _ =>
        {
            NotifyStateChanged();
            return Task.CompletedTask;
        };

        hub.Reconnected += async _ =>
        {
            if (!IsHostOfCurrentRoom && CurrentRoom is not null && CurrentNickname is not null)
            {
                await JoinRoomAsync(CurrentRoom.RoomCode, CurrentNickname, TeamSide.None, ReconnectToken);
            }
            NotifyStateChanged();
        };

        hub.Closed += _ =>
        {
            NotifyStateChanged();
            return Task.CompletedTask;
        };
    }

    private void ResetQuestionSelection()
    {
        SelectedOptionIndex = null;
        HasAnsweredCurrentQuestion = false;
        LastAnswerAck = null;
    }

    private async Task PersistSessionKeysAsync(string roomCode, string nickname, string? token)
    {
        try
        {
            await js.InvokeVoidAsync("arenaStorage.set", "aq_room", roomCode);
            await js.InvokeVoidAsync("arenaStorage.set", "aq_nick", nickname);
            if (!string.IsNullOrWhiteSpace(token))
            {
                await js.InvokeVoidAsync("arenaStorage.set", "aq_token", token);
            }
        }
        catch
        {
            // Ignore when sessionStorage is unavailable.
        }
    }

    private void NotifyStateChanged() => OnStateChanged?.Invoke();

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
        }
        _connectLock.Dispose();
    }
}

