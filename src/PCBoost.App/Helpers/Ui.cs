using Microsoft.UI.Xaml;
using PCBoost.App.Controls;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Helpers;

/// <summary>Fonctions de conversion pour x:Bind (évite les convertisseurs et les liaisons par réflexion).</summary>
public static class Ui
{
    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility ShowAll(bool a, bool b) => a && b ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowAny(bool a, bool b) => a || b ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowText(string? value) => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility ShowCount(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowNone(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowNotNull(object? value) => value is null ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    public static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

    public static bool And(bool a, bool b) => a && b;

    /// <summary>Largeur proportionnelle d'une barre (0–100 → 0–max).</summary>
    public static double Scale(double percent, double max) => Math.Clamp(double.IsNaN(percent) ? 0 : percent, 0, 100) / 100d * max;

    public static double Clamp100(double percent) => Math.Clamp(double.IsNaN(percent) ? 0 : percent, 0, 100);

    public static double Ratio(int points, int max) => max <= 0 ? 0 : Math.Clamp(points * 100d / max, 0, 100);

    /// <summary>Chevron d'une section repliable (bas = replié, haut = déplié).</summary>
    public static string Chevron(bool expanded) => expanded ? "\uE70E" : "\uE70D";

    public static string Glyph(string? glyph) => string.IsNullOrEmpty(glyph) ? "" : glyph;

    public static double Opacity(bool enabled) => enabled ? 1.0 : 0.55;

    /// <summary>Atténue un élément en attente (étape non commencée…).</summary>
    public static double Dim(bool dimmed) => dimmed ? 0.55 : 1.0;

    /// <summary>Taille des chiffres de session : « Non disponible » ne doit jamais être tronqué à la taille d'un chiffre.</summary>
    public static double SessionNumeralSize(bool available) => available ? 44 : 20;

    /// <summary>Taille de la valeur d'une tuile : « Non disponible » reste lisible en entier.</summary>
    public static double MetricValueSize(bool available) => available ? 28 : 18;

    public static BadgeKind FactorKind(FactorStatus status) => status switch
    {
        FactorStatus.Good => BadgeKind.Success,
        FactorStatus.Fair => BadgeKind.Caution,
        FactorStatus.Poor => BadgeKind.Critical,
        _ => BadgeKind.Neutral,
    };

    public static BadgeKind SeverityKind(Severity severity) => severity switch
    {
        Severity.Critical or Severity.High => BadgeKind.Critical,
        Severity.Medium => BadgeKind.Caution,
        Severity.Low => BadgeKind.Info,
        _ => BadgeKind.Neutral,
    };

    public static BadgeKind GoodOrCaution(bool good) => good ? BadgeKind.Success : BadgeKind.Caution;

    public static BadgeKind GoodOrNeutral(bool good) => good ? BadgeKind.Success : BadgeKind.Neutral;

    public static BadgeKind CautionOrNeutral(bool caution) => caution ? BadgeKind.Caution : BadgeKind.Neutral;

    public static BadgeKind CriticalOrNeutral(bool critical) => critical ? BadgeKind.Critical : BadgeKind.Neutral;

    public static BadgeKind AccentOrNeutral(bool accent) => accent ? BadgeKind.Accent : BadgeKind.Neutral;

    public static BadgeKind SafetyKind(SafetyCategory safety) => safety switch
    {
        SafetyCategory.Safe => BadgeKind.Success,
        SafetyCategory.Caution => BadgeKind.Caution,
        _ => BadgeKind.Critical,
    };

    public static BadgeKind HealthKind(HealthLevel level) => level switch
    {
        HealthLevel.Good => BadgeKind.Success,
        HealthLevel.Warning => BadgeKind.Caution,
        HealthLevel.Critical => BadgeKind.Critical,
        _ => BadgeKind.Neutral,
    };

    /// <summary>Glyphe d'état (toujours accompagné du libellé) : coche, avertissement, erreur ou inconnu.</summary>
    public static string HealthGlyph(HealthLevel level) => level switch
    {
        HealthLevel.Good => "\uE73E",
        HealthLevel.Warning => "\uE7BA",
        HealthLevel.Critical => "\uEA39",
        _ => "\uE9CE",
    };
}
