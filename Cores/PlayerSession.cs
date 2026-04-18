namespace GameDemoServer.Cores;

public sealed class PlayerSession
{
    public string PlayerId { get; }
    public string UserName { get; set; }
    public string CurrentMapId { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;

    public PlayerSession(string playerId, string userName)
    {
        PlayerId = playerId;
        UserName = userName;
    }
}

public sealed record MapPlayerSnapshot(string PlayerId, string UserName, float X, float Y);
public sealed record PlayerDisconnectInfo(string PlayerId, string UserName, string? MapId);
