using ArenaQuiz.Shared.Models;

namespace ArenaQuiz.Shared.Contracts;

/// <summary>
/// Strongly-typed SignalR server-to-client push contract.
/// </summary>
public interface IGameHubClient
{
    Task RoomStateUpdated(RoomSnapshotDto snapshot);
    Task PlayerJoined(PlayerStandingDto player);
    Task PlayerReconnected(PlayerStandingDto player);
    Task PlayerLeft(string playerId, string nickname);
    Task QuestionStarted(LiveQuestionViewDto question, int remainingSeconds);
    Task TimerTicked(int remainingSeconds);
    Task AnswerProgressUpdated(AnswerProgressDto progress);
    Task PauseStateChanged(bool isPaused, int remainingSeconds);
    Task AnswerSubmittedAck(AnswerAckDto ack);
    Task QuestionRevealed(QuestionRevealDto reveal);
    Task GameEnded(SessionHistoryDto summary);
    Task ReactionBroadcast(ReactionEventDto reaction);
    Task ErrorOccurred(string message);
}

/// <summary>
/// SignalR client-to-server invocation contract.
/// </summary>
public interface IGameHubServer
{
    Task<Result<RoomSnapshotDto>> CreateRoom(CreateRoomRequest request);
    Task<Result<RoomSnapshotDto>> ReclaimHost(string roomCode, string hostControlToken);
    Task<Result<RoomSnapshotDto>> JoinRoom(JoinRoomRequest request);
    Task<Result<RoomSnapshotDto>> StartGame(string roomCode);
    Task<Result<bool>> TogglePauseQuestion(string roomCode);
    Task<Result<AnswerAckDto>> SubmitAnswer(SubmitAnswerRequest request);
    Task<Result<QuestionRevealDto>> RevealCurrentQuestion(string roomCode);
    Task<Result<RoomSnapshotDto>> NextQuestion(string roomCode);
    Task<Result<bool>> RemovePlayer(string roomCode, string playerId);
    Task SendReaction(string roomCode, string nickname, string reactionKey);
    Task<IReadOnlyList<OpenRoomSummaryDto>> GetOpenRooms();
}
