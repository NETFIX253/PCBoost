using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using PCBoost.Core.Settings;
using PCBoost.Presentation.Abstractions;
using Windows.UI;

namespace PCBoost.App.Services;

/// <summary>Thème Clair / Sombre / Système (défaut). Met aussi à jour les boutons de la barre de titre.</summary>
public sealed class ThemeService : IThemeService
{
    private Window? _window;

    public ThemePreference Current { get; private set; } = ThemePreference.System;

    public void Attach(Window window)
    {
        _window = window;
        if (window.Content is FrameworkElement root)
            root.ActualThemeChanged += (_, _) => UpdateTitleBar();
    }

    public void Apply(ThemePreference theme)
    {
        Current = theme;
        if (_window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                ThemePreference.Light => ElementTheme.Light,
                ThemePreference.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
        UpdateTitleBar();
    }

    private void UpdateTitleBar()
    {
        if (_window?.Content is not FrameworkElement root) return;
        try
        {
            var titleBar = _window.AppWindow.TitleBar;
            var dark = root.ActualTheme == ElementTheme.Dark;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = dark ? Colors.White : Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);
            titleBar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A) : Color.FromArgb(0xFF, 0x80, 0x80, 0x80);
            titleBar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0x00, 0x00, 0x00);
            titleBar.ButtonHoverForegroundColor = titleBar.ButtonForegroundColor;
            titleBar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x08, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedForegroundColor = titleBar.ButtonForegroundColor;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Barre de titre personnalisée non disponible (ancienne version de Windows) : sans incidence.
        }
    }
}
