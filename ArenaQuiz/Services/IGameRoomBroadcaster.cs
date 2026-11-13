using ArenaQuiz.Hubs;
using ArenaQuiz.Shared.Contracts;
using ArenaQuiz.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace ArenaQuiz.Services;

/// <summary>
/// Abstraction for pushing real-time room events to connected clients (SOLID Dependency Inversion).
/// </summary>
public interface IGameRoomBroadcaster
{
    Task RoomStateUpdated(string roomCode, RoomSnapshotDto snapshot);
    Task PlayerJoined(string roomCode, PlayerStandingDto player);
    Task PlayerReconnected(string roomCode, PlayerStandingDto player);
    Task PlayerLeft(string roomCode, string playerId, string nickname);
    Task QuestionStarted(string roomCode, LiveQuestionViewDto question, int remainingSeconds);
    Task TimerTicked(string roomCode, int remainingSeconds);
    Task QuestionRevealed(string roomCode, QuestionRevealDto reveal);
    Task GameEnded(string roomCode, SessionHistoryDto summary);
    Task ReactionBroadcast(string roomCode, ReactionEventDto reaction);
}

public sealed class SignalRGameRoomBroadcaster(IHubContext<GameHub, IGameHubClient> hubContext) : IGameRoomBroadcaster
{
    public Task RoomStateUpdated(string roomCode, RoomSnapshotDto snapshot) =>
        hubContext.Clients.Group(roomCode).RoomStateUpdated(snapshot);

    public Task PlayerJoined(string roomCode, PlayerStandingDto player) =>
        hubContext.Clients.Group(roomCode).PlayerJoined(player);

    public Task PlayerReconnected(string roomCode, PlayerStandingDto player) =>
        hubContext.Clients.Group(roomCode).PlayerReconnected(player);

    public Task PlayerLeft(string roomCode, string playerId, string nickname) =>
        hubContext.Clients.Group(roomCode).PlayerLeft(playerId, nickname);

    public Task QuestionStarted(string roomCode, LiveQuestionViewDto question, int remainingSeconds) =>
        hubContext.Clients.Group(roomCode).QuestionStarted(question, remainingSeconds);

    public Task TimerTicked(string roomCode, int remainingSeconds) =>
        hubContext.Clients.Group(roomCode).TimerTicked(remainingSeconds);

    public Task QuestionRevealed(string roomCode, QuestionRevealDto reveal) =>
        hubContext.Clients.Group(roomCode).QuestionRevealed(reveal);

    public Task GameEnded(string roomCode, SessionHistoryDto summary) =>
        hubContext.Clients.Group(roomCode).GameEnded(summary);

    public Task ReactionBroadcast(string roomCode, ReactionEventDto reaction) =>
        hubContext.Clients.Group(roomCode).ReactionBroadcast(reaction);
}

