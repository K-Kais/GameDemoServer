using System.Collections.Concurrent;

namespace GameDemoServer.Cores;

public sealed class Map
{
    public readonly ConcurrentDictionary<string, EntityDataServer> Players = new();

    public GameManager GameManager { get; }
    public string MapId { get; }
    public int MaxPlayers { get; }
    public int PlayerCount => Players.Count;
    public bool HasCapacity => PlayerCount < MaxPlayers;


    public Map(string mapId, int maxPlayers, GameManager gameManager)
    {
        GameManager = gameManager;
        MapId = mapId;
        MaxPlayers = maxPlayers;
    }

    public bool TryAddPlayer(EntityDataServer player)
    {
        if (Players.ContainsKey(player.PlayerId))
        {
            return true;
        }

        if (Players.Count >= MaxPlayers)
        {
            return false;
        }

        return Players.TryAdd(player.PlayerId, player);
    }

    public bool RemovePlayer(string playerId, out EntityDataServer? player)
    {
        return Players.TryRemove(playerId, out player);
    }

    public bool ContainsPlayer(string playerId)
    {
        return Players.ContainsKey(playerId);
    }

    public IReadOnlyCollection<string> GetPlayerIds()
    {
        return Players.Keys.ToArray();
    }

    public IReadOnlyCollection<EntityDataServer> GetPlayers()
    {
        return Players.Values.ToArray();
    }

    public IReadOnlyCollection<MapPlayerSnapshot> GetSnapshot()
    {
        return Players.Values
            .Select(player => new MapPlayerSnapshot(
                player.PlayerId,
                player.UserName,
                player.X,
                player.Y,
                player.DirX,
                player.DirY,
                player.CharacterIndex,
                player.State,
                player.CurrentHp,
                player.MaxHp))
            .ToArray();
    }
}
