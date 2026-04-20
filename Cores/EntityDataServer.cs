using GameDemoServer.Models;

namespace GameDemoServer.Cores;

public class SystemECS
{
    public virtual void Update(EntityDataServer data, Map map, float deltaTime)
    {
    }
}

public sealed class EntityCombatData
{
    public const float DefaultAttackRange = 6f;
    public const float DefaultMaxHp = 100f;
    public const float DefaultDamage = 10f;

    public float AttackRange { get; set; } = DefaultAttackRange;
    public float CurrentHp { get; set; } = DefaultMaxHp;
    public float MaxHp { get; set; } = DefaultMaxHp;
    public float Damage { get; set; } = DefaultDamage;
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
    public int CharacterIndex { get; set; } = -1;
    public string State { get; set; } = string.Empty;
    public EntityCombatData CombatData { get; } = new();

    public float CurrentHp
    {
        get => CombatData.CurrentHp;
        set => CombatData.CurrentHp = value;
    }

    public float MaxHp
    {
        get => CombatData.MaxHp;
        set => CombatData.MaxHp = value;
    }

    public float Damage
    {
        get => CombatData.Damage;
        set => CombatData.Damage = value;
    }

    public SystemECS system { get; } = new PlayerSystem();

    public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;

    public EntityDataServer(string playerId, string userName)
    {
        PlayerId = playerId;
        UserName = userName;
    }

    public void QueueInput(EntitySyncData input)
    {
        if (input is null)
        {
            return;
        }

        lock (_inputLock)
        {
            if (_pendingInput is null)
            {
                _pendingInput = new EntitySyncData();
            }

            _pendingInput.X = input.X;
            _pendingInput.Y = input.Y;
            _pendingInput.DirX = input.DirX;
            _pendingInput.DirY = input.DirY;
            _pendingInput.State = input.State;

            if (input.CharacterIndex.HasValue)
            {
                _pendingInput.CharacterIndex = input.CharacterIndex;
            }

            // Preserve one-shot events until ECS consumes pending input.
            _pendingInput.AttackEvent |= input.AttackEvent;
            _pendingInput.AttackHitEvent |= input.AttackHitEvent;
            _pendingInput.RespawnEvent |= input.RespawnEvent;
            _pendingInput.Skill1 |= input.Skill1;
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
            input.Skill1 ||
            MathF.Abs(X - input.X) > SyncEpsilon ||
            MathF.Abs(Y - input.Y) > SyncEpsilon ||
            MathF.Abs(DirX - input.DirX) > SyncEpsilon ||
            MathF.Abs(DirY - input.DirY) > SyncEpsilon ||
            (input.CharacterIndex.HasValue && input.CharacterIndex.Value >= 0 && CharacterIndex != input.CharacterIndex.Value) ||
            !string.Equals(State, normalizedState, StringComparison.Ordinal);

        if (!changed)
        {
            return true;
        }

        X = input.X;
        Y = input.Y;
        DirX = input.DirX;
        DirY = input.DirY;
        if (input.CharacterIndex.HasValue && input.CharacterIndex.Value >= 0)
        {
            CharacterIndex = input.CharacterIndex.Value;
        }

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
    int CharacterIndex,
    string State,
    float CurrentHp,
    float MaxHp);
public sealed record PlayerDisconnectInfo(string PlayerId, string UserName, string? MapId);
