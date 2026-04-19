using System;

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

public sealed class EntitySyncData
{
    public float X { get; set; }
    public float Y { get; set; }
    public float DirX { get; set; }
    public float DirY { get; set; }
    public string State { get; set; } = string.Empty;
}

public sealed class InputSyncItem
{
    public string PlayerId { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float DirX { get; set; }
    public float DirY { get; set; }
    public string State { get; set; } = string.Empty;
}

public sealed class InputBatchMessage
{
    public string MapId { get; set; } = string.Empty;
    public InputSyncItem[] Players { get; set; } = Array.Empty<InputSyncItem>();
}
