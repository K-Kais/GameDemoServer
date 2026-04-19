using System;

namespace GameDemoServer.Cores;

public static class AnimationStateNames
{
    public const string Idle = "Idle";
    public const string Dead = "Dead";
    public const string Walk = "Walk";
    public const string Attack = "Attack";

    public static bool IsDead(string? state)
    {
        return string.Equals(state, Dead, StringComparison.OrdinalIgnoreCase);
    }
}
