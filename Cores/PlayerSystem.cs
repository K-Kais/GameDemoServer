using GameDemoServer.Models;

namespace GameDemoServer.Cores;

public sealed class PlayerSystem : SystemECS
{
    private const float Skill1Range = 12f;
    private const float Skill1ProjectileSpeed = 14f;

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

        var skill1Target = input.Skill1
            ? FindAttackTarget(data, map, MathF.Max(data.CombatData.AttackRange, Skill1Range))
            : null;
        var skill1TargetPlayerId = skill1Target?.PlayerId ?? string.Empty;

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
            Skill1 = input.Skill1,
            Skill1TargetPlayerId = skill1TargetPlayerId,
            Skill1HitEvent = false,
            CurrentHp = data.CurrentHp,
            MaxHp = data.MaxHp
        });

        if (input.AttackHitEvent)
        {
            var attackTarget = FindAttackTarget(data, map, data.CombatData.AttackRange);
            if (attackTarget != null)
            {
                ApplyDamage(attackTarget, data.Damage);
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
                    Skill1 = false,
                    Skill1TargetPlayerId = string.Empty,
                    Skill1HitEvent = false,
                    CurrentHp = attackTarget.CurrentHp,
                    MaxHp = attackTarget.MaxHp
                });
            }
        }

        if (skill1Target != null)
        {
            var travelTime = CalculateSkill1TravelTime(data, skill1Target);
            map.GameManager.ScheduleSkill1Impact(
                map.MapId,
                data.PlayerId,
                skill1Target.PlayerId,
                travelTime,
                data.Damage);
        }
    }

    private static void ApplyDamage(EntityDataServer target, float damage)
    {
        target.CurrentHp = MathF.Max(0f, target.CurrentHp - MathF.Max(0f, damage));
        if (target.CurrentHp <= 0f)
        {
            target.State = AnimationStateNames.Dead;
        }
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

    private static float CalculateSkill1TravelTime(EntityDataServer caster, EntityDataServer target)
    {
        var deltaX = target.X - caster.X;
        var deltaY = target.Y - caster.Y;
        var distance = MathF.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        var speed = MathF.Max(0.1f, Skill1ProjectileSpeed);
        return MathF.Max(0.05f, distance / speed);
    }
}
