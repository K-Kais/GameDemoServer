using GameDemoServer.Cores;

namespace GameDemoServer.Services;

public sealed class InputSyncBroadcastService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(16);
    private readonly GameManager _gameManager;
    private readonly ILogger<InputSyncBroadcastService> _logger;

    public InputSyncBroadcastService(GameManager gameManager, ILogger<InputSyncBroadcastService> logger)
    {
        _gameManager = gameManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        var lastUpdate = DateTime.UtcNow;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = DateTime.UtcNow;
                var deltaTime = (float)(now - lastUpdate).TotalSeconds;
                lastUpdate = now;

                if (deltaTime <= 0f || deltaTime > 1f)
                {
                    deltaTime = (float)TickInterval.TotalSeconds;
                }

                _gameManager.UpdateEcsSystems(deltaTime);
                await _gameManager.FlushPendingInputSyncAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Input sync broadcaster stopped.");
        }
    }
}
