using System.Collections.Concurrent;

namespace GameDemoServer.Cores;

public sealed class Map
{
    private readonly ConcurrentDictionary<string, PlayerSession> _players = new();

    public string MapId { get; }
    public int MaxPlayers { get; }
    public int PlayerCount => _players.Count;
    public bool HasCapacity => PlayerCount < MaxPlayers;

    public Map(string mapId, int maxPlayers)
    {
        MapId = mapId;
        MaxPlayers = maxPlayers;
    }

    public bool TryAddPlayer(PlayerSession player)
    {
        if (_players.ContainsKey(player.PlayerId))
        {
            return true;
        }

        if (_players.Count >= MaxPlayers)
        {
            return false;
        }

        return _players.TryAdd(player.PlayerId, player);
    }

    public bool RemovePlayer(string playerId, out PlayerSession? player)
    {
        return _players.TryRemove(playerId, out player);
    }

    public bool ContainsPlayer(string playerId)
    {
        return _players.ContainsKey(playerId);
    }

    public bool TryUpdatePosition(string playerId, float x, float y)
    {
        if (!_players.TryGetValue(playerId, out var player))
        {
            return false;
        }

        player.X = x;
        player.Y = y;
        return true;
    }

    public IReadOnlyCollection<string> GetPlayerIds()
    {
        return _players.Keys.ToArray();
    }

    public IReadOnlyCollection<MapPlayerSnapshot> GetSnapshot()
    {
        return _players.Values
            .Select(player => new MapPlayerSnapshot(player.PlayerId, player.UserName, player.X, player.Y))
            .ToArray();
    }
}
