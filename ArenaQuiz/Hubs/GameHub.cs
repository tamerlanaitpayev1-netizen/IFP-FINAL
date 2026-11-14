using ArenaQuiz.Services;
using ArenaQuiz.Shared.Contracts;
using ArenaQuiz.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace ArenaQuiz.Hubs;

/// <summary>
/// Real-time SignalR Hub mediating room lifecycle, host-verified control actions,
/// connection-bound answer submission, and live reactions.
/// </summary>
public sealed class GameHub(GameRoomService roomService) : Hub<IGameHubClient>, IGameHubServer
{
    public async Task<Result<RoomSnapshotDto>> CreateRoom(CreateRoomRequest request)
    {
        var result = await roomService.CreateRoomAsync(request, Context.ConnectionId);
        if (result.IsSuccess && result.Value is not null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, result.Value.RoomCode);
        }
        return result;
    }

    public async Task<Result<RoomSnapshotDto>> ReclaimHost(string roomCode, string hostControlToken)
    {
        var result = await roomService.ReclaimHostAsync(roomCode, hostControlToken, Context.ConnectionId);
        if (result.IsSuccess && result.Value is not null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, result.Value.RoomCode);
        }
        return result;
    }

    public async Task<Result<RoomSnapshotDto>> JoinRoom(JoinRoomRequest request)
    {
        var result = await roomService.JoinRoomAsync(request, Context.ConnectionId);
        if (result.IsSuccess)
        {
            string code = result.Value.Snapshot.RoomCode;
            await Groups.AddToGroupAsync(Context.ConnectionId, code);
            return Result<RoomSnapshotDto>.Success(result.Value.Snapshot);
        }
        return Result<RoomSnapshotDto>.Failure(result.Error ?? "Unable to join room.");
    }

    public Task<Result<RoomSnapshotDto>> StartGame(string roomCode) =>
        roomService.StartGameAsync(roomCode, Context.ConnectionId);

    public Task<Result<bool>> TogglePauseQuestion(string roomCode) =>
        roomService.TogglePauseQuestionAsync(roomCode, Context.ConnectionId);

    public async Task<Result<AnswerAckDto>> SubmitAnswer(SubmitAnswerRequest request)
    {
        var result = await roomService.SubmitAnswerAsync(request, Context.ConnectionId);
        if (result.IsSuccess && result.Value is not null)
        {
            await Clients.Caller.AnswerSubmittedAck(result.Value);
        }
        return result;
    }

    public Task<Result<QuestionRevealDto>> RevealCurrentQuestion(string roomCode) =>
        roomService.RevealCurrentQuestionAsync(roomCode, Context.ConnectionId);

    public Task<Result<RoomSnapshotDto>> NextQuestion(string roomCode) =>
        roomService.NextQuestionAsync(roomCode, Context.ConnectionId);

    public Task<Result<bool>> RemovePlayer(string roomCode, string playerId) =>
        roomService.RemovePlayerAsync(roomCode, playerId, Context.ConnectionId);

    public Task SendReaction(string roomCode, string nickname, string reactionKey) =>
        roomService.BroadcastReactionAsync(roomCode, Context.ConnectionId, reactionKey);

    public Task<IReadOnlyList<OpenRoomSummaryDto>> GetOpenRooms() =>
        Task.FromResult(roomService.GetOpenRooms());

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await roomService.HandleDisconnectAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
