namespace GameDemoServer.Models;

public sealed class JoinMapRequest
{
    public string? MapId { get; set; }
}

public sealed class MoveRequest
{
    public float X { get; set; }
    public float Y { get; set; }
}

public sealed class ChatRequest
{
    public string Message { get; set; } = string.Empty;
}
