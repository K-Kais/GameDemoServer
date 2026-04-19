using GameDemoServer.Models;

namespace GameDemoServer.Cores;

public sealed class PlayerSystem : SystemECS
{
    public override void Update(EntityDataServer data, Map map, float deltaTime)
    {
        data.TryConsumeInput(out var input);
        if (input is null)
        {
            return;
        }

        EnsureCombatStats(data);

        if (data.CurrentHp <= 0f &&
            input.CharacterIndex.HasValue &&
            input.CharacterIndex.Value >= 0)
        {
            data.CharacterIndex = input.CharacterIndex.Value;
        }

        if (input.RespawnEvent && data.CurrentHp <= 0f)
        {
            data.CurrentHp = data.MaxHp;
            data.State = AnimationStateNames.Idle;
            map.GameManager.EnqueueInputSync(map.MapId, new InputSyncItem
            {
                PlayerId = data.PlayerId,
                X = data.X,
                Y = data.Y,
                DirX = data.DirX,
                DirY = data.DirY,
                CharacterIndex = data.CharacterIndex,
                State = data.State,
                AttackEvent = false,
                CurrentHp = data.CurrentHp,
                MaxHp = data.MaxHp
            });
            return;
        }

        if (data.CurrentHp <= 0f)
        {
            data.State = AnimationStateNames.Dead;
            map.GameManager.EnqueueInputSync(map.MapId, new InputSyncItem
            {
                PlayerId = data.PlayerId,
                X = data.X,
                Y = data.Y,
                DirX = data.DirX,
                DirY = data.DirY,
                CharacterIndex = data.CharacterIndex,
                State = data.State,
                AttackEvent = false,
                CurrentHp = data.CurrentHp,
                MaxHp = data.MaxHp
            });
            return;
        }

        data.TryUpdateInput(input, out var changed);
        if (!changed)
        {
            return;
        }

        map.GameManager.EnqueueInputSync(map.MapId, new InputSyncItem
        {
            PlayerId = data.PlayerId,
            X = data.X,
            Y = data.Y,
            DirX = data.DirX,
            DirY = data.DirY,
            CharacterIndex = data.CharacterIndex,
            State = data.State,
            AttackEvent = input.AttackEvent,
            CurrentHp = data.CurrentHp,
            MaxHp = data.MaxHp
        });

        if (!input.AttackHitEvent)
        {
            return;
        }

        var attackTarget = FindAttackTarget(data, map, data.CombatData.AttackRange);
        if (attackTarget is null)
        {
            return;
        }

        attackTarget.CurrentHp = MathF.Max(0f, attackTarget.CurrentHp - data.Damage);
        if (attackTarget.CurrentHp <= 0f)
        {
            attackTarget.State = AnimationStateNames.Dead;
        }

        map.GameManager.EnqueueInputSync(map.MapId, new InputSyncItem
        {
            PlayerId = attackTarget.PlayerId,
            X = attackTarget.X,
            Y = attackTarget.Y,
            DirX = attackTarget.DirX,
            DirY = attackTarget.DirY,
            CharacterIndex = attackTarget.CharacterIndex,
            State = attackTarget.State,
            AttackEvent = false,
            CurrentHp = attackTarget.CurrentHp,
            MaxHp = attackTarget.MaxHp
        });
    }

    private static EntityDataServer? FindAttackTarget(EntityDataServer attacker, Map map, float range)
    {
        var rangeSquared = range * range;
        var attackerDirectionMagnitudeSquared =
            attacker.DirX * attacker.DirX +
            attacker.DirY * attacker.DirY;

        var bestTarget = default(EntityDataServer);
        var bestDistanceSquared = float.MaxValue;

        foreach (var target in map.Players.Values)
        {
            if (string.Equals(target.PlayerId, attacker.PlayerId, StringComparison.Ordinal))
            {
                continue;
            }

            EnsureCombatStats(target);
            if (target.CurrentHp <= 0f)
            {
                continue;
            }

            var deltaX = target.X - attacker.X;
            var deltaY = target.Y - attacker.Y;
            var distanceSquared = deltaX * deltaX + deltaY * deltaY;
            if (distanceSquared > rangeSquared)
            {
                continue;
            }

            if (attackerDirectionMagnitudeSquared > 0.0001f && distanceSquared > 0.0001f)
            {
                var toTargetMagnitude = MathF.Sqrt(distanceSquared);
                var toTargetX = deltaX / toTargetMagnitude;
                var toTargetY = deltaY / toTargetMagnitude;
                var attackerDirectionMagnitude = MathF.Sqrt(attackerDirectionMagnitudeSquared);
                var attackerDirX = attacker.DirX / attackerDirectionMagnitude;
                var attackerDirY = attacker.DirY / attackerDirectionMagnitude;
                var dot = attackerDirX * toTargetX + attackerDirY * toTargetY;

                if (dot < 0f)
                {
                    continue;
                }
            }

            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                bestTarget = target;
            }
        }

        return bestTarget;
    }

    private static void EnsureCombatStats(EntityDataServer data)
    {
        if (data.CombatData.MaxHp <= 0f)
        {
            data.CombatData.MaxHp = EntityCombatData.DefaultMaxHp;
            if (data.CombatData.CurrentHp <= 0f)
            {
                data.CombatData.CurrentHp = data.CombatData.MaxHp;
            }
        }

        if (data.CombatData.Damage <= 0f)
        {
            data.CombatData.Damage = EntityCombatData.DefaultDamage;
        }

        if (data.CombatData.AttackRange <= 0f)
        {
            data.CombatData.AttackRange = EntityCombatData.DefaultAttackRange;
        }
    }
}
