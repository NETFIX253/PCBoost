using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Analysis;
using PCBoost.Presentation.Common;

namespace PCBoost.Presentation.ViewModels;

/// <summary>Facteur explicable du score (§40) : libellé, explication, points/max, statut texte + glyphe, action.</summary>
public sealed partial class ScoreFactorItemViewModel
{
    private readonly Action<string>? _navigate;

    public ScoreFactorItemViewModel(ScoreFactor factor, ILocalizer localizer, Action<string>? navigate)
    {
        ArgumentNullException.ThrowIfNull(factor);
        ArgumentNullException.ThrowIfNull(localizer);
        _navigate = navigate;
        Id = factor.Id;
        Label = localizer.Format(factor.Label);
        Explanation = localizer.Format(factor.Explanation);
        Points = factor.Points;
        MaxPoints = factor.MaxPoints;
        PointsText = localizer.Format("Home_Factor_Points", factor.Points, factor.MaxPoints);
        Status = factor.Status;
        StatusText = localizer.Get($"Home_FactorStatus_{factor.Status}");
        IconGlyph = factor.Status switch
        {
            FactorStatus.Good => Glyphs.Success,
            FactorStatus.Fair => Glyphs.Warning,
            FactorStatus.Poor => Glyphs.Error,
            _ => Glyphs.Unknown,
        };
        NavigationTarget = factor.NavigationTarget;
        ActionLabel = localizer.Get("Home_Factor_Action");
        AccessibleName = localizer.Format("Home_Factor_Accessible", Label, PointsText, StatusText, Explanation);
    }

    public string Id { get; }

    public string Label { get; }

    public string Explanation { get; }

    public int Points { get; }

    public int MaxPoints { get; }

    /// <summary>« 18 / 20 ».</summary>
    public string PointsText { get; }

    /// <summary>Remplissage 0–100 (barre de progression).</summary>
    public double Percent => MaxPoints <= 0 ? 0 : Math.Clamp(Points * 100d / MaxPoints, 0, 100);

    public FactorStatus Status { get; }

    public string StatusText { get; }

    public string IconGlyph { get; }

    public bool IsGood => Status == FactorStatus.Good;

    public bool IsImprovable => Status is FactorStatus.Fair or FactorStatus.Poor;

    public string? NavigationTarget { get; }

    public bool HasAction => !string.IsNullOrEmpty(NavigationTarget) && _navigate is not null;

    public string ActionLabel { get; }

    public string AccessibleName { get; }

    [RelayCommand(CanExecute = nameof(HasAction))]
    private void Open()
    {
        if (NavigationTarget is { } target) _navigate?.Invoke(target);
    }
}

/// <summary>Tuile de mesure en direct (CPU, RAM, disque, GPU, température). « Non disponible » si non mesuré.</summary>
public sealed partial class MetricTileViewModel : ObservableObject
{
    public MetricTileViewModel(string title, string notAvailable)
    {
        Title = title;
        Value = notAvailable;
    }

    public string Title { get; }

    /// <summary>Valeur principale (« 34 % », « 62 °C », « Non disponible »).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string Value { get; private set; }

    /// <summary>Détail (« 9,8 Go sur 16 Go ») ; vide si aucun.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail), nameof(AccessibleName))]
    public partial string Detail { get; private set; } = string.Empty;

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    [ObservableProperty]
    public partial bool IsAvailable { get; private set; }

    /// <summary>Une jauge 0–100 a un sens pour cette valeur (pourcentage, température).</summary>
    [ObservableProperty]
    public partial bool HasGauge { get; private set; }

    /// <summary>Remplissage 0–100 pour une jauge (0 si non disponible ou sans jauge).</summary>
    [ObservableProperty]
    public partial double Percent { get; private set; }

    public string AccessibleName => HasDetail ? $"{Title} : {Value}, {Detail}" : $"{Title} : {Value}";

    /// <summary>Valeur mesurée ; <paramref name="gaugePercent"/> renseigné si une jauge a un sens.</summary>
    public void Set(string value, string? detail = null, double? gaugePercent = null)
    {
        if (Value != value) Value = value;
        var d = detail ?? string.Empty;
        if (Detail != d) Detail = d;
        if (!IsAvailable) IsAvailable = true;
        var hasGauge = gaugePercent is { } g && double.IsFinite(g);
        if (HasGauge != hasGauge) HasGauge = hasGauge;
        var p = hasGauge ? Math.Clamp(gaugePercent!.Value, 0, 100) : 0;
        if (Math.Abs(Percent - p) > 0.05) Percent = p;
    }

    /// <summary>« Non disponible » + raison facultative (affichée en détail).</summary>
    public void SetUnavailable(string notAvailable, string? reason = null)
    {
        if (Value != notAvailable) Value = notAvailable;
        var d = reason ?? string.Empty;
        if (Detail != d) Detail = d;
        IsAvailable = false;
        HasGauge = false;
        Percent = 0;
    }
}

