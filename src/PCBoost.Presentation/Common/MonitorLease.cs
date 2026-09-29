using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;

namespace PCBoost.Presentation.Common;

/// <summary>
/// Passe le moniteur en mode Active tant qu'une page de métriques est affichée ET que la fenêtre est visible,
/// puis rétablit le mode précédent. Démarre le moniteur s'il était arrêté et l'arrête à la sortie si la surveillance
/// est désactivée dans les réglages. Un mode « Paused » choisi par l'utilisateur est respecté.
/// </summary>
public sealed class MonitorLease : IDisposable
{
    private readonly IPerformanceMonitor _monitor;
    private readonly IAppLifecycle? _lifecycle;
    private readonly bool _stopOnRelease;
    private readonly MonitoringMode _restoreMode;
    private readonly bool _manageMode;
    private bool _disposed;

    private MonitorLease(IPerformanceMonitor monitor, IAppLifecycle? lifecycle, bool stopOnRelease, MonitoringMode restoreMode, bool manageMode)
    {
        _monitor = monitor;
        _lifecycle = lifecycle;
        _stopOnRelease = stopOnRelease;
        _restoreMode = restoreMode;
        _manageMode = manageMode;
        if (_lifecycle is not null) _lifecycle.WindowVisibilityChanged += OnWindowVisibilityChanged;
    }

    public static MonitorLease Acquire(IPerformanceMonitor monitor, bool monitoringEnabledInSettings, IAppLifecycle? lifecycle = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var previous = monitor.Mode;
        var startedHere = false;
        if (!monitor.IsRunning)
        {
            monitor.Start();
            startedHere = true;
        }

        // Pause utilisateur respectée, sauf si le moniteur vient d'être démarré pour cette page.
        var manage = previous != MonitoringMode.Paused || startedHere;
        var visible = lifecycle?.IsWindowVisible ?? true;
        if (manage && visible && monitor.Mode != MonitoringMode.Active) monitor.SetMode(MonitoringMode.Active);

        var restore = previous is MonitoringMode.Active or MonitoringMode.Paused ? MonitoringMode.Background : previous;
        return new MonitorLease(monitor, lifecycle, startedHere && !monitoringEnabledInSettings, restore, manage);
    }

    private void OnWindowVisibilityChanged(object? sender, bool visible)
    {
        if (_disposed || !_manageMode) return;
        var target = visible ? MonitoringMode.Active : _restoreMode;
        if (_monitor.Mode != target) _monitor.SetMode(target);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lifecycle is not null) _lifecycle.WindowVisibilityChanged -= OnWindowVisibilityChanged;
        if (_stopOnRelease)
        {
            _monitor.Stop();
            return;
        }

        if (_manageMode && _monitor.Mode == MonitoringMode.Active)
            _monitor.SetMode(_restoreMode);
    }
}
