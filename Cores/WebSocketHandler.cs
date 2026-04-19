using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using GameDemoServer.Models;
using GameDemoServer.Services;

namespace GameDemoServer.Cores;

public sealed class WebSocketHandler
{
    private readonly RequestDelegate _next;
    private readonly GameManager _gameManager;
    private readonly AuthService _authService;
    private readonly TokenService _tokenService;
    private readonly ILogger<WebSocketHandler> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public WebSocketHandler(
        RequestDelegate next,
        GameManager gameManager,
        AuthService authService,
        TokenService tokenService,
        ILogger<WebSocketHandler> logger)
    {
        _next = next;
        _gameManager = gameManager;
        _authService = authService;
        _tokenService = tokenService;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.Equals("/ws", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket request required");
            return;
        }

        var token = context.Request.Query["token"].ToString();
        var principal = _tokenService.ValidateToken(token);
        if (principal is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Invalid or expired token");
            return;
        }

        var playerId = principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ??
                       principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(playerId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Missing user identity");
            return;
        }

        AuthUserProfile? profile = null;
        var foundProfile = await _authService.TryGetUserProfileAsync(
            playerId,
            context.RequestAborted,
            user => profile = user);
        if (!foundProfile || profile is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Invalid user profile");
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.CharacterName))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Bạn cần tạo nhân vật trước khi vào game");
            return;
        }

        var userName = profile.CharacterName;
        using var socket = await context.WebSockets.AcceptWebSocketAsync();

        _gameManager.AddConnection(playerId, userName, socket);
        var map = _gameManager.AssignPlayerToMap(playerId, "world", out _);

        await _gameManager.SendToPlayerAsync(
            playerId,
            OpCode.MapSnapshot,
            new
            {
                mapId = map.MapId,
                selfPlayerId = playerId,
                players = _gameManager.GetMapSnapshot(map.MapId)
            },
            context.RequestAborted);

        await _gameManager.BroadcastToMapAsync(
            map.MapId,
            OpCode.PlayerJoinedMap,
            new { playerId, userName, mapId = map.MapId },
            exceptPlayerId: playerId,
            cancellationToken: context.RequestAborted);

        try
        {
            await HandleSocketAsync(socket, playerId, userName, context.RequestAborted);
        }
        finally
        {
            var disconnected = _gameManager.RemoveConnection(playerId);
            if (disconnected?.MapId is { Length: > 0 } mapId)
            {
                await _gameManager.BroadcastToMapAsync(
                    mapId,
                    OpCode.PlayerLeftMap,
                    new { playerId = disconnected.PlayerId, userName = disconnected.UserName, mapId },
                    cancellationToken: CancellationToken.None);
            }

            _logger.LogInformation("WebSocket disconnected: {PlayerId}", playerId);
        }
    }

    private async Task HandleSocketAsync(
        WebSocket socket,
        string playerId,
        string userName,
        CancellationToken cancellationToken)
    {
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var frame = await ReceiveFrameAsync(socket, cancellationToken);
                if (frame is null)
                {
                    return;
                }

                if (frame.Length == 0)
                {
                    continue;
                }

                var opCode = (OpCode)frame[0];
                var payload = frame.Length > 1 ? frame[1..] : Array.Empty<byte>();

                await HandleMessageAsync(playerId, userName, opCode, payload, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Request aborted or app shutdown.
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket closed unexpectedly: {PlayerId}", playerId);
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug(ex, "WebSocket already disposed: {PlayerId}", playerId);
        }
    }

    private async Task HandleMessageAsync(
        string playerId,
        string userName,
        OpCode opCode,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        switch (opCode)
        {
            case OpCode.Test:
                {
                    var message = ParseTestMessage(payload);

                    await _gameManager.SendToPlayerAsync(
                        playerId,
                        OpCode.Test,
                        new
                        {
                            playerId,
                            userName,
                            message,
                            serverTimeUtc = DateTime.UtcNow
                        },
                        cancellationToken);
                    return;
                }
            case OpCode.Ping:
                await _gameManager.SendToPlayerAsync(
                    playerId,
                    OpCode.Pong,
                    new { serverTimeUtc = DateTime.UtcNow },
                    cancellationToken);
                return;

            case OpCode.JoinMap:
                {
                    var request = Deserialize<JoinMapRequest>(payload) ?? new JoinMapRequest();
                    var targetMap = _gameManager.MovePlayerToMap(playerId, request.MapId, out var previousMapId);

                    if (!string.IsNullOrWhiteSpace(previousMapId) &&
                        !string.Equals(previousMapId, targetMap.MapId, StringComparison.OrdinalIgnoreCase))
                    {
                        await _gameManager.BroadcastToMapAsync(
                            previousMapId,
                            OpCode.PlayerLeftMap,
                            new { playerId, userName, mapId = previousMapId },
                            cancellationToken: cancellationToken);
                    }

                    await _gameManager.BroadcastToMapAsync(
                        targetMap.MapId,
                        OpCode.PlayerJoinedMap,
                        new { playerId, userName, mapId = targetMap.MapId },
                        exceptPlayerId: playerId,
                        cancellationToken: cancellationToken);

                    await _gameManager.SendToPlayerAsync(
                        playerId,
                        OpCode.MapSnapshot,
                        new
                        {
                            mapId = targetMap.MapId,
                            selfPlayerId = playerId,
                            players = _gameManager.GetMapSnapshot(targetMap.MapId)
                        },
                        cancellationToken);
                    return;
                }

            case OpCode.Move:
                {
                    var request = Deserialize<MoveRequest>(payload);
                    if (request is null)
                    {
                        await SendErrorAsync(playerId, "Invalid move payload", cancellationToken);
                        return;
                    }

                    if (_gameManager.TryUpdatePlayerPosition(playerId, request.X, request.Y, out var mapId) && mapId is not null)
                    {
                        await _gameManager.BroadcastToMapAsync(
                            mapId,
                            OpCode.Move,
                            new { playerId, x = request.X, y = request.Y },
                            exceptPlayerId: playerId,
                            cancellationToken: cancellationToken);
                    }

                    return;
                }

            case OpCode.Chat:
                {
                    var request = Deserialize<ChatRequest>(payload);
                    if (request is null || string.IsNullOrWhiteSpace(request.Message))
                    {
                        await SendErrorAsync(playerId, "Message cannot be empty", cancellationToken);
                        return;
                    }

                    var mapId = _gameManager.GetCurrentMapId(playerId);
                    if (mapId is null)
                    {
                        await SendErrorAsync(playerId, "Player is not in a map", cancellationToken);
                        return;
                    }

                    await _gameManager.BroadcastToMapAsync(
                        mapId,
                        OpCode.Chat,
                        new
                        {
                            from = playerId,
                            userName,
                            message = request.Message.Trim(),
                            sentAtUtc = DateTime.UtcNow
                        },
                        cancellationToken: cancellationToken);
                    return;
                }

            case OpCode.Input:
                {
                    var request = Deserialize<EntitySyncData>(payload);
                    if (request is null)
                    {
                        await SendErrorAsync(playerId, "Invalid input payload", cancellationToken);
                        return;
                    }

                    if (!_gameManager.TryUpdatePlayerInput(
                            playerId,
                            request,
                            out _))
                    {
                        await SendErrorAsync(playerId, "Player is not in a map", cancellationToken);
                    }

                    return;
                }

            default:
                await SendErrorAsync(playerId, $"Unsupported opcode: {(byte)opCode}", cancellationToken);
                return;
        }
    }

    private async Task SendErrorAsync(string playerId, string message, CancellationToken cancellationToken)
    {
        await _gameManager.SendToPlayerAsync(playerId, OpCode.Error, new { error = message }, cancellationToken);
    }

    private static T? Deserialize<T>(byte[] payload) where T : class
    {
        if (payload.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ParseTestMessage(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.String)
            {
                return root.GetString() ?? string.Empty;
            }

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String)
            {
                return messageElement.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Fallback for non-JSON text payloads.
        }

        return Encoding.UTF8.GetString(payload);
    }

    private static async Task<byte[]?> ReceiveFrameAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var memoryStream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
                }

                return null;
            }

            if (result.Count > 0)
            {
                await memoryStream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            }

            if (result.EndOfMessage)
            {
                return memoryStream.ToArray();
            }
        }
    }
}
