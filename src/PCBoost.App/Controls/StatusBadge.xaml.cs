using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace PCBoost.App.Controls;

public enum BadgeKind { Neutral = 0, Accent, Success, Caution, Critical, Info }

/// <summary>Pastille d'état : toujours un texte (et un glyphe facultatif), jamais la couleur seule.</summary>
public sealed partial class StatusBadge : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusBadge), new PropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(StatusBadge), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(BadgeKind), typeof(StatusBadge), new PropertyMetadata(BadgeKind.Neutral, OnKindChanged));

    public StatusBadge()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyKind(false);
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public BadgeKind Kind { get => (BadgeKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((StatusBadge)d).ApplyKind(true);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var badge = (StatusBadge)d;
        AutomationProperties.SetName(badge, badge.Text);
        badge.Visibility = string.IsNullOrEmpty(badge.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyKind(bool animate) => VisualStateManager.GoToState(this, Kind.ToString(), animate);
}
