namespace ArenaQuiz.Services;

/// <summary>
/// Periodic background worker evicting finished or abandoned quiz rooms to prevent unbounded memory growth.
/// </summary>
public sealed class RoomCleanupHostedService(
    GameRoomService roomService,
    ILogger<RoomCleanupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            int evicted = roomService.CleanupExpiredRooms();
            if (evicted > 0)
            {
                logger.LogInformation("Evicted {Count} expired quiz rooms from memory.", evicted);
            }
        }
    }
}
