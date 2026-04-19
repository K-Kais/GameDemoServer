using GameDemoServer.Models;

namespace GameDemoServer.Cores;

public class SystemECS
{
    public virtual void Update(EntityDataServer data, Map map, float deltaTime)
    {
    }
}

public sealed class EntityDataServer
{
    private const float SyncEpsilon = 0.0001f;

    private readonly object _inputLock = new();
    private EntitySyncData? _pendingInput;

    public string PlayerId { get; }
    public string UserName { get; set; }
    public string CurrentMapId { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float DirX { get; set; }
    public float DirY { get; set; }
    public string State { get; set; } = string.Empty;
    public float CurrentHp;
    public float MaxHp;
    public float Damage;

    public SystemECS system { get; } = new PlayerSystem();

    public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;

    public EntityDataServer(string playerId, string userName)
    {
        PlayerId = playerId;
        UserName = userName;
        MaxHp = 100f;
        CurrentHp = 100f;
        Damage = 10f;
    }

    public void QueueInput(EntitySyncData input)
    {
        lock (_inputLock)
        {
            _pendingInput = new EntitySyncData
            {
                X = input.X,
                Y = input.Y,
                DirX = input.DirX,
                DirY = input.DirY,
                State = input.State,
                AttackEvent = input.AttackEvent,
                AttackHitEvent = input.AttackHitEvent,
                RespawnEvent = input.RespawnEvent
            };
        }
    }

    public bool TryConsumeInput(out EntitySyncData? input)
    {
        lock (_inputLock)
        {
            if (_pendingInput is null)
            {
                input = null;
                return false;
            }

            input = _pendingInput;
            _pendingInput = null;
            return true;
        }
    }

    public bool TryUpdateInput(EntitySyncData input, out bool changed)
    {
        changed = false;
        if (input is null)
        {
            return false;
        }

        var normalizedState = input.State?.Trim() ?? string.Empty;
        changed =
            input.AttackEvent ||
            input.AttackHitEvent ||
            input.RespawnEvent ||
            MathF.Abs(X - input.X) > SyncEpsilon ||
            MathF.Abs(Y - input.Y) > SyncEpsilon ||
            MathF.Abs(DirX - input.DirX) > SyncEpsilon ||
            MathF.Abs(DirY - input.DirY) > SyncEpsilon ||
            !string.Equals(State, normalizedState, StringComparison.Ordinal);

        if (!changed)
        {
            return true;
        }

        X = input.X;
        Y = input.Y;
        DirX = input.DirX;
        DirY = input.DirY;
        State = normalizedState;
        input.State = normalizedState;
        return true;
    }

    public bool TryUpdatePosition(float x, float y)
    {
        X = x;
        Y = y;
        return true;
    }
}

public sealed record MapPlayerSnapshot(
    string PlayerId,
    string UserName,
    float X,
    float Y,
    float DirX,
    float DirY,
    string State,
    float CurrentHp,
    float MaxHp);
public sealed record PlayerDisconnectInfo(string PlayerId, string UserName, string? MapId);
