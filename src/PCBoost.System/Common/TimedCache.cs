namespace PCBoost.Platform;

/// <summary>Valeur mise en cache pour une durée donnée (horloge monotone), thread-safe.</summary>
internal sealed class TimedCache<T>
{
    private readonly TimeSpan _lifetime;
    private readonly Func<T> _factory;
    private readonly Lock _lock = new();
    private T _value = default!;
    private bool _hasValue;
    private long _expiresAt;

    public TimedCache(TimeSpan lifetime, Func<T> factory)
    {
        _lifetime = lifetime;
        _factory = factory;
    }

    public T Get()
    {
        lock (_lock)
        {
            var now = Environment.TickCount64;
            if (!_hasValue || now >= _expiresAt)
            {
                _value = _factory();
                _hasValue = true;
                _expiresAt = now + (long)_lifetime.TotalMilliseconds;
            }
            return _value;
        }
    }

    public void Invalidate()
    {
        lock (_lock) _hasValue = false;
    }
}
