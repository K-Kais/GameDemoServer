namespace GameDemoServer.Cores;

public enum OpCode : byte
{
    Ping = 0,
    Pong = 1,
    JoinMap = 2,
    PlayerJoinedMap = 3,
    PlayerLeftMap = 4,
    Move = 5,
    MapSnapshot = 6,
    Chat = 7,
    Error = 255
}
