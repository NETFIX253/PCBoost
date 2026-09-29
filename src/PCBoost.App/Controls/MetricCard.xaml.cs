using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Controls;

/// <summary>Tuile de mesure en direct (CPU, RAM, disque, GPU, températures). « Non disponible » si non mesuré.</summary>
public sealed partial class MetricCard : UserControl
{
    public static readonly DependencyProperty TileProperty =
        DependencyProperty.Register(nameof(Tile), typeof(MetricTileViewModel), typeof(MetricCard), new PropertyMetadata(null, OnTileChanged));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(MetricCard), new PropertyMetadata(string.Empty));

    /// <summary>Mesure secondaire facultative (ex. température sous l'utilisation), affichée seulement si elle est mesurée.</summary>
    public static readonly DependencyProperty SecondaryTileProperty =
        DependencyProperty.Register(nameof(SecondaryTile), typeof(MetricTileViewModel), typeof(MetricCard), new PropertyMetadata(null, OnSecondaryTileChanged));

    public MetricCard()
    {
        InitializeComponent();
        UpdateSecondary();
    }

    public MetricTileViewModel? SecondaryTile { get => (MetricTileViewModel?)GetValue(SecondaryTileProperty); set => SetValue(SecondaryTileProperty, value); }

    public MetricTileViewModel? Tile { get => (MetricTileViewModel?)GetValue(TileProperty); set => SetValue(TileProperty, value); }

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    private static void OnTileChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (MetricCard)d;
        if (e.OldValue is MetricTileViewModel old) old.PropertyChanged -= card.OnTilePropertyChanged;
        if (e.NewValue is MetricTileViewModel tile)
        {
            tile.PropertyChanged += card.OnTilePropertyChanged;
            AutomationProperties.SetName(card, tile.AccessibleName);
        }
    }

    private static void OnSecondaryTileChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (MetricCard)d;
        if (e.OldValue is MetricTileViewModel old) old.PropertyChanged -= card.OnSecondaryPropertyChanged;
        if (e.NewValue is MetricTileViewModel tile) tile.PropertyChanged += card.OnSecondaryPropertyChanged;
        card.UpdateSecondary();
    }

    private void OnSecondaryPropertyChanged(object? sender, PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(UpdateSecondary);

    private void UpdateSecondary()
    {
        var tile = SecondaryTile;
        var shown = tile is { IsAvailable: true };
        SecondaryPanel.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        SecondaryValue.Text = shown ? tile!.Value : string.Empty;
        AutomationProperties.SetName(SecondaryPanel, shown ? tile!.AccessibleName : string.Empty);
    }

    private void OnTilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Tile is not null && e.PropertyName is nameof(MetricTileViewModel.AccessibleName) or null)
            DispatcherQueue.TryEnqueue(() => AutomationProperties.SetName(this, Tile?.AccessibleName ?? string.Empty));
    }
}
