namespace PCBoost.Core.Common;

/// <summary>
/// Lecture de capteur : soit une valeur mesurée, soit une indisponibilité explicite.
/// Les températures ou métriques absentes sont affichées « Non disponible », jamais estimées.
/// </summary>
public readonly record struct SensorReading(double? Value, Availability Availability, string? Source)
{
    public bool HasValue => Value.HasValue && Availability == Availability.Available;

    public static SensorReading Of(double value, string source) => new(value, Availability.Available, source);

    public static SensorReading Unavailable(Availability reason = Availability.Unavailable, string? source = null)
        => new(null, reason == Availability.Available ? Availability.Unavailable : reason, source);
}
