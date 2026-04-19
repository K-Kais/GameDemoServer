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

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await _gameManager.FlushPendingInputSyncAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Input sync broadcaster stopped.");
        }
    }
}
