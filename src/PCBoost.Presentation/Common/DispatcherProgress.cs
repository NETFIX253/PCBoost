using PCBoost.Presentation.Abstractions;

namespace PCBoost.Presentation.Common;

/// <summary>
/// <see cref="IProgress{T}"/> qui relaie les notifications sur le thread UI via <see cref="IUiDispatcher"/>
/// (indépendant du SynchronizationContext capturé, donc déterministe en test).
/// </summary>
public sealed class DispatcherProgress<T> : IProgress<T>
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<T> _handler;

    public DispatcherProgress(IUiDispatcher dispatcher, Action<T> handler)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public void Report(T value)
    {
        if (_dispatcher.HasThreadAccess) _handler(value);
        else _dispatcher.Post(() => _handler(value));
    }
}
