namespace PCBoost.Platform;

/// <summary>Calculs purs des métriques (testables hors Windows).</summary>
internal static class MetricMath
{
    /// <summary>
    /// Charge CPU entre deux lectures de GetSystemTimes (unités de 100 ns). Le temps noyau inclut le temps d'inactivité.
    /// </summary>
    public static double CpuPercent(long previousIdle, long previousKernel, long previousUser, long idle, long kernel, long user)
    {
        var idleDelta = idle - previousIdle;
        var totalDelta = (kernel - previousKernel) + (user - previousUser);
        if (totalDelta <= 0 || idleDelta < 0) return 0;
        return Clamp((totalDelta - idleDelta) * 100d / totalDelta);
    }

    /// <summary>Disque actif = 100 − % d'inactivité, borné à [0, 100].</summary>
    public static double? DiskActivePercent(double? idlePercent)
        => idlePercent is null ? null : Clamp(100d - idlePercent.Value);

    public static double? SumClamped(IReadOnlyList<double>? values, double max = 100)
        => values is null ? null : Math.Clamp(values.Sum(), 0, max);

    public static double? Sum(IReadOnlyList<double>? values)
        => values is null ? null : Math.Max(0, values.Sum());

    public static double? NonNegative(double? value)
        => value is null ? null : Math.Max(0, value.Value);

    public static double Clamp(double percent)
        => double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0;
}
