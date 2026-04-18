using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace GameDemoServer.Cores;

public sealed class GameManager
{
    private const int MaxPlayersPerMap = 100;
    private const string DefaultMapId = "world";

    private readonly ConcurrentDictionary<string, PlayerSession> _players = new();
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sendLocks = new();
    private readonly ConcurrentDictionary<string, Map> _maps = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _mapLock = new();
    private readonly ILogger<GameManager> _logger;

    public GameManager(ILogger<GameManager> logger)
    {
        _logger = logger;
    }

    public bool AddConnection(string playerId, string userName, WebSocket socket)
    {
        if (_connections.TryGetValue(playerId, out var existingSocket) && existingSocket != socket)
        {
            _ = CloseSocketSilentlyAsync(existingSocket);
        }

        _connections[playerId] = socket;
        _sendLocks.GetOrAdd(playerId, _ => new SemaphoreSlim(1, 1));

        _players.AddOrUpdate(
            playerId,
            _ => new PlayerSession(playerId, userName),
            (_, existing) =>
            {
                existing.UserName = userName;
                return existing;
            });

        return true;
    }

    public Map AssignPlayerToMap(string playerId, string? baseMapId, out string? previousMapId)
    {
        return MovePlayerToMap(playerId, baseMapId, out previousMapId);
    }

    public Map MovePlayerToMap(string playerId, string? baseMapId, out string? previousMapId)
    {
        lock (_mapLock)
        {
            if (!_players.TryGetValue(playerId, out var player))
            {
                throw new InvalidOperationException($"Player '{playerId}' is not connected.");
            }

            previousMapId = string.IsNullOrWhiteSpace(player.CurrentMapId) ? null : player.CurrentMapId;
            if (previousMapId is not null && _maps.TryGetValue(previousMapId, out var previousMap))
            {
                previousMap.RemovePlayer(playerId, out _);
            }

            var targetMap = GetOrCreateTargetMap(baseMapId);
            if (!targetMap.TryAddPlayer(player))
            {
                targetMap = CreateNextMapInstance(NormalizeBaseMapId(baseMapId));
                if (!targetMap.TryAddPlayer(player))
                {
                    throw new InvalidOperationException($"Could not add player '{playerId}' to map '{targetMap.MapId}'.");
                }
            }

            player.CurrentMapId = targetMap.MapId;
            return targetMap;
        }
    }

    public string? GetCurrentMapId(string playerId)
    {
        if (_players.TryGetValue(playerId, out var player) && !string.IsNullOrWhiteSpace(player.CurrentMapId))
        {
            return player.CurrentMapId;
        }

        return null;
    }

    public bool TryUpdatePlayerPosition(string playerId, float x, float y, out string? mapId)
    {
        mapId = null;
        if (!_players.TryGetValue(playerId, out var player))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(player.CurrentMapId))
        {
            return false;
        }

        if (!_maps.TryGetValue(player.CurrentMapId, out var map))
        {
            return false;
        }

        if (!map.TryUpdatePosition(playerId, x, y))
        {
            return false;
        }

        player.X = x;
        player.Y = y;
        mapId = map.MapId;
        return true;
    }

    public PlayerDisconnectInfo? RemoveConnection(string playerId)
    {
        _connections.TryRemove(playerId, out var socket);
        _sendLocks.TryRemove(playerId, out _);

        if (!_players.TryRemove(playerId, out var player))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(player.CurrentMapId) && _maps.TryGetValue(player.CurrentMapId, out var map))
        {
            map.RemovePlayer(playerId, out _);
        }

        if (socket is not null)
        {
            _ = CloseSocketSilentlyAsync(socket);
        }

        return new PlayerDisconnectInfo(player.PlayerId, player.UserName, player.CurrentMapId);
    }

    public IReadOnlyCollection<MapPlayerSnapshot> GetMapSnapshot(string mapId)
    {
        if (_maps.TryGetValue(mapId, out var map))
        {
            return map.GetSnapshot();
        }

        return Array.Empty<MapPlayerSnapshot>();
    }

    public async Task SendToPlayerAsync(string playerId, OpCode opCode, object payload, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(playerId, out var socket) || socket.State != WebSocketState.Open)
        {
            return;
        }

        var sendLock = _sendLocks.GetOrAdd(playerId, _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }

            var frame = BuildFrame(opCode, payload);
            await socket.SendAsync(
                new ArraySegment<byte>(frame),
                WebSocketMessageType.Binary,
                true,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Send failed for player {PlayerId}", playerId);
        }
        finally
        {
            sendLock.Release();
        }
    }

    public async Task BroadcastToMapAsync(
        string mapId,
        OpCode opCode,
        object payload,
        string? exceptPlayerId = null,
        CancellationToken cancellationToken = default)
    {
        if (!_maps.TryGetValue(mapId, out var map))
        {
            return;
        }

        var playerIds = map.GetPlayerIds();
        var tasks = new List<Task>(playerIds.Count);

        foreach (var playerId in playerIds)
        {
            if (string.Equals(playerId, exceptPlayerId, StringComparison.Ordinal))
            {
                continue;
            }

            tasks.Add(SendToPlayerAsync(playerId, opCode, payload, cancellationToken));
        }

        await Task.WhenAll(tasks);
    }

    private Map GetOrCreateTargetMap(string? baseMapId)
    {
        var normalizedBaseMapId = NormalizeBaseMapId(baseMapId);
        var prefix = $"{normalizedBaseMapId}-";
        var existingMaps = _maps.Values
            .Where(map => map.MapId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(map => map.MapId)
            .ToList();

        foreach (var map in existingMaps)
        {
            if (map.HasCapacity)
            {
                return map;
            }
        }

        if (existingMaps.Count == 0)
        {
            return _maps.GetOrAdd($"{normalizedBaseMapId}-1", id => new Map(id, MaxPlayersPerMap));
        }

        return CreateNextMapInstance(normalizedBaseMapId);
    }

    private Map CreateNextMapInstance(string normalizedBaseMapId)
    {
        var prefix = $"{normalizedBaseMapId}-";
        var maxSuffix = _maps.Keys
            .Where(mapId => mapId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(mapId =>
            {
                var parts = mapId.Split('-', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 1 && int.TryParse(parts[^1], out var suffix) ? suffix : 0;
            })
            .DefaultIfEmpty(0)
            .Max();

        var nextMapId = $"{normalizedBaseMapId}-{maxSuffix + 1}";
        return _maps.GetOrAdd(nextMapId, id => new Map(id, MaxPlayersPerMap));
    }

    private static string NormalizeBaseMapId(string? mapId)
    {
        if (string.IsNullOrWhiteSpace(mapId))
        {
            return DefaultMapId;
        }

        return mapId.Trim().ToLowerInvariant();
    }

    private byte[] BuildFrame(OpCode opCode, object payload)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions);
        var frame = new byte[payloadBytes.Length + 1];
        frame[0] = (byte)opCode;
        if (payloadBytes.Length > 0)
        {
            Buffer.BlockCopy(payloadBytes, 0, frame, 1, payloadBytes.Length);
        }

        return frame;
    }

    private async Task CloseSocketSilentlyAsync(WebSocket socket)
    {
        try
        {
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection replaced", CancellationToken.None);
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket close failed");
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug(ex, "WebSocket already disposed during close");
        }
    }
}
