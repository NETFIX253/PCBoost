using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PCBoost.App.Controls;

/// <summary>Ligne de réglage : glyphe, intitulé, description, contrôle à droite (style Paramètres de Windows 11).</summary>
public sealed partial class SettingRow : ContentControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty, OnChanged));

    private TextBlock? _description;
    private FontIcon? _glyph;

    public SettingRow() => DefaultStyleKey = typeof(SettingRow);

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _description = GetTemplateChild("DescriptionText") as TextBlock;
        _glyph = GetTemplateChild("GlyphIcon") as FontIcon;
        UpdateParts();
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SettingRow)d).UpdateParts();

    private void UpdateParts()
    {
        if (_description is not null) _description.Visibility = string.IsNullOrWhiteSpace(Description) ? Visibility.Collapsed : Visibility.Visible;
        if (_glyph is not null) _glyph.Visibility = string.IsNullOrWhiteSpace(Glyph) ? Visibility.Collapsed : Visibility.Visible;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, Header);
    }
}
