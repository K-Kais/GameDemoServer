using System;
using System.Collections.Concurrent;

namespace GameDemoServer.Cores;

public sealed class Map
{
    private const float SyncEpsilon = 0.0001f;
    private readonly ConcurrentDictionary<string, EntityDataServer> _players = new();

    public string MapId { get; }
    public int MaxPlayers { get; }
    public int PlayerCount => _players.Count;
    public bool HasCapacity => PlayerCount < MaxPlayers;

    public Map(string mapId, int maxPlayers)
    {
        MapId = mapId;
        MaxPlayers = maxPlayers;
    }

    public bool TryAddPlayer(EntityDataServer player)
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

    public bool RemovePlayer(string playerId, out EntityDataServer? player)
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

    public bool TryUpdateInput(string playerId, float x, float y, float dirX, float dirY, string state, out bool changed)
    {
        changed = false;
        if (!_players.TryGetValue(playerId, out var player))
        {
            return false;
        }

        changed =
            MathF.Abs(player.X - x) > SyncEpsilon ||
            MathF.Abs(player.Y - y) > SyncEpsilon ||
            MathF.Abs(player.DirX - dirX) > SyncEpsilon ||
            MathF.Abs(player.DirY - dirY) > SyncEpsilon ||
            !string.Equals(player.State, state, StringComparison.Ordinal);

        if (!changed)
        {
            return true;
        }

        player.X = x;
        player.Y = y;
        player.DirX = dirX;
        player.DirY = dirY;
        player.State = state;
        return true;
    }

    public IReadOnlyCollection<string> GetPlayerIds()
    {
        return _players.Keys.ToArray();
    }

    public IReadOnlyCollection<MapPlayerSnapshot> GetSnapshot()
    {
        return _players.Values
            .Select(player => new MapPlayerSnapshot(
                player.PlayerId,
                player.UserName,
                player.X,
                player.Y,
                player.DirX,
                player.DirY,
                player.State))
            .ToArray();
    }
}
