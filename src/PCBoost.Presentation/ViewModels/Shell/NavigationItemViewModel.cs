using CommunityToolkit.Mvvm.ComponentModel;

namespace PCBoost.Presentation.ViewModels;

/// <summary>Élément du menu de navigation (clé de page, libellé localisé, glyphe Segoe Fluent Icons).</summary>
public sealed partial class NavigationItemViewModel : ObservableObject
{
    private readonly string _labelKey;
    private readonly string _toolTipKey;

    public NavigationItemViewModel(string pageKey, string labelKey, string toolTipKey, string iconGlyph)
    {
        PageKey = pageKey;
        _labelKey = labelKey;
        _toolTipKey = toolTipKey;
        IconGlyph = iconGlyph;
    }

    public string PageKey { get; }

    public string IconGlyph { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string Label { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ToolTip { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string AccessibleName => Label;

    internal void Refresh(Func<string, string> get)
    {
        Label = get(_labelKey);
        ToolTip = get(_toolTipKey);
    }
}
