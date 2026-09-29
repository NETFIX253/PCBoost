using PCBoost.Presentation.Abstractions;

namespace PCBoost.App.Services;

/// <summary>Visibilité de la fenêtre principale (pilote la surveillance adaptative) et sortie de l'application.</summary>
public sealed class AppLifecycle : IAppLifecycle
{
    private Action? _show;
    private Action? _exit;

    public bool IsWindowVisible { get; private set; }

    public event EventHandler<bool>? WindowVisibilityChanged;

    public void Attach(Action show, Action exit)
    {
        _show = show;
        _exit = exit;
    }

    public void SetVisible(bool visible)
    {
        if (IsWindowVisible == visible) return;
        IsWindowVisible = visible;
        WindowVisibilityChanged?.Invoke(this, visible);
    }

    public void ShowMainWindow() => _show?.Invoke();

    public void Exit() => _exit?.Invoke();
}
