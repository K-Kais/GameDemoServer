using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using GameDemoServer.Models;

namespace GameDemoServer.Cores;

public sealed class GameManager
{
    private const int MaxPlayersPerMap = 100;
    private const string DefaultMapId = "world";

    private readonly ConcurrentDictionary<string, EntityDataServer> _players = new();
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sendLocks = new();
    private readonly ConcurrentDictionary<string, Map> _maps = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, InputSyncItem>> _pendingInputByMap = new();
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
            _ => new EntityDataServer(playerId, userName),
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
                RemovePendingInput(previousMapId, playerId);
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

    public bool TryUpdatePlayerInput(
        string playerId,
        float x,
        float y,
        float dirX,
        float dirY,
        string state,
        out string? mapId)
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

        var normalizedState = state?.Trim() ?? string.Empty;
        if (!map.TryUpdateInput(playerId, x, y, dirX, dirY, normalizedState, out var changed))
        {
            return false;
        }

        mapId = map.MapId;

        if (!changed)
        {
            return true;
        }

        player.X = x;
        player.Y = y;
        player.DirX = dirX;
        player.DirY = dirY;
        player.State = normalizedState;
        EnqueueInputSync(map.MapId, playerId, x, y, dirX, dirY, normalizedState);
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
            RemovePendingInput(player.CurrentMapId, playerId);
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

    public async Task FlushPendingInputSyncAsync(CancellationToken cancellationToken = default)
    {
        foreach (var pending in _pendingInputByMap)
        {
            var mapPending = pending.Value;
            if (mapPending.IsEmpty)
            {
                _pendingInputByMap.TryRemove(pending.Key, out _);
                continue;
            }

            var items = new InputSyncItem[mapPending.Count];
            var index = 0;
            foreach (var playerSync in mapPending)
            {
                if (!mapPending.TryRemove(playerSync.Key, out var syncItem))
                {
                    continue;
                }

                items[index++] = syncItem;
            }

            if (index == 0)
            {
                continue;
            }

            if (index != items.Length)
            {
                Array.Resize(ref items, index);
            }

            await BroadcastToMapAsync(
                pending.Key,
                OpCode.Input,
                new InputBatchMessage
                {
                    MapId = pending.Key,
                    Players = items
                },
                cancellationToken: cancellationToken);

            if (mapPending.IsEmpty)
            {
                _pendingInputByMap.TryRemove(pending.Key, out _);
            }
        }
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

    private void EnqueueInputSync(string mapId, string playerId, float x, float y, float dirX, float dirY, string state)
    {
        var mapPending = _pendingInputByMap.GetOrAdd(mapId, _ => new ConcurrentDictionary<string, InputSyncItem>());
        mapPending[playerId] = new InputSyncItem
        {
            PlayerId = playerId,
            X = x,
            Y = y,
            DirX = dirX,
            DirY = dirY,
            State = state
        };
    }

    private void RemovePendingInput(string? mapId, string playerId)
    {
        if (string.IsNullOrWhiteSpace(mapId))
        {
            return;
        }

        if (!_pendingInputByMap.TryGetValue(mapId, out var mapPending))
        {
            return;
        }

        mapPending.TryRemove(playerId, out _);
        if (mapPending.IsEmpty)
        {
            _pendingInputByMap.TryRemove(mapId, out _);
        }
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
