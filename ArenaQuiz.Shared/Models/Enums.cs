namespace ArenaQuiz.Shared.Models;

/// <summary>
/// Supported real-time quiz competition modes.
/// </summary>
public enum GameMode
{
    ClassicSpeed = 0,
    TeamBattle = 1,
    KnockoutElimination = 2
}

/// <summary>
/// Lifecycle phases of an active SignalR quiz room.
/// </summary>
public enum RoomPhase
{
    Lobby = 0,
    QuestionActive = 1,
    QuestionReveal = 2,
    PodiumFinished = 3
}

/// <summary>
/// Application role for Role-Based Access Control (RBAC).
/// </summary>
public enum UserRole
{
    Player = 0,
    Host = 1,
    Admin = 2
}

/// <summary>
/// Team assignment used in Team Battle mode.
/// </summary>
public enum TeamSide
{
    None = 0,
    Alpha = 1,
    Beta = 2,
    Gamma = 3
}

