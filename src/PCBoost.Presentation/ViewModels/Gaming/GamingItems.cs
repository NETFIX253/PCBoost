using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Gaming;
using PCBoost.Presentation.Common;

namespace PCBoost.Presentation.ViewModels;

/// <summary>Optimisation du mode Gaming : appliquée (✓) ou ignorée, avec détail.</summary>
public sealed class GamingOptimizationItemViewModel
{
    public GamingOptimizationItemViewModel(ActiveGamingOptimization optimization, ILocalizer localizer, bool planned)
    {
        ArgumentNullException.ThrowIfNull(optimization);
        ArgumentNullException.ThrowIfNull(localizer);
        Id = optimization.OptimizationId;
        Label = localizer.Format(optimization.Label);
        Detail = optimization.Detail is { } d ? localizer.Format(d) : string.Empty;
        IsApplied = optimization.Applied;
        StatusText = planned
            ? localizer.Get(optimization.Applied ? "Gaming_Optimization_Planned" : "Gaming_Optimization_NotApplicable")
            : localizer.Get(optimization.Applied ? "Gaming_Optimization_Applied" : "Gaming_Optimization_Skipped");
        IconGlyph = optimization.Applied ? Glyphs.Success : Glyphs.Skipped;
        AccessibleName = string.IsNullOrEmpty(Detail) ? $"{Label} : {StatusText}" : $"{Label} : {StatusText}. {Detail}";
    }

    public string Id { get; }

    public string Label { get; }

    public string Detail { get; }

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public bool IsApplied { get; }

    /// <summary>« Appliquée » / « Ignorée » (ou « Prévue » / « Non applicable » avant activation).</summary>
    public string StatusText { get; }

    public string IconGlyph { get; }

    public string AccessibleName { get; }
}

/// <summary>Vérification d'un paramètre graphique Windows (mode Jeu, jeux fenêtrés, HAGS, GPU).</summary>
public sealed partial class GameSettingCheckItemViewModel
{
    private readonly Action<GameSettingCheckItemViewModel>? _fix;

    public GameSettingCheckItemViewModel(GameSettingCheck check, ILocalizer localizer, Action<GameSettingCheckItemViewModel>? fix)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(localizer);
        Model = check;
        _fix = fix;
        Id = check.Id;
        Label = localizer.Format(check.Label);
        Detail = localizer.Format(check.Detail);
        IsOk = check.IsRecommendedState;
        StatusText = localizer.Get(IsOk ? "Gaming_Check_Ok" : "Gaming_Check_ToReview");
        IconGlyph = IsOk ? Glyphs.Success : Glyphs.Warning;
        FixLabel = localizer.Get("Gaming_Check_OpenSettings");
    }

    public GameSettingCheck Model { get; }

    public string Id { get; }

    public string Label { get; }

    public string Detail { get; }

    public bool IsOk { get; }

    /// <summary>« Conforme » / « À vérifier ».</summary>
    public string StatusText { get; }

    public string IconGlyph { get; }

    /// <summary>Action proposée quand le paramètre n'est pas dans l'état recommandé.</summary>
    public bool CanFix => !IsOk && _fix is not null;

    /// <summary>« Ouvrir les paramètres Windows ».</summary>
    public string FixLabel { get; }

    public string AccessibleName => $"{Label} : {StatusText}. {Detail}";

    [RelayCommand(CanExecute = nameof(CanFix))]
    private void Fix() => _fix?.Invoke(this);
}

/// <summary>Jeu installé détecté.</summary>
public sealed class GameItemViewModel
{
    public GameItemViewModel(GameInfo game, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(localizer);
        Model = game;
        Id = game.Id;
        Name = game.Name;
        SourceText = localizer.Get($"Gaming_Source_{game.Source}");
        Location = game.InstallDirectory ?? game.ExecutablePath ?? string.Empty;
    }

    public GameInfo Model { get; }

    public string Id { get; }

    public string Name { get; }

    /// <summary>« Steam », « Epic Games », « Ajouté manuellement »…</summary>
    public string SourceText { get; }

    public string Location { get; }

    public bool HasLocation => !string.IsNullOrEmpty(Location);

    public string AccessibleName => $"{Name}, {SourceText}";
}
