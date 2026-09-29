using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Analysis;
using PCBoost.Presentation.Common;

namespace PCBoost.Presentation.ViewModels;

/// <summary>Constat de santé prêt à afficher. La sévérité est donnée en texte ET en glyphe (jamais la couleur seule).</summary>
public sealed class FindingItemViewModel
{
    public FindingItemViewModel(HealthFinding finding, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(localizer);
        RuleId = finding.RuleId;
        Severity = finding.Severity;
        Title = localizer.Format(finding.Title);
        Detail = localizer.Format(finding.Detail);
        SeverityText = localizer.Severity(finding.Severity);
        IconGlyph = Labels.SeverityGlyph(finding.Severity);
        Category = finding.Category;
        AccessibleName = $"{SeverityText} : {Title}. {Detail}";
    }

    public string RuleId { get; }

    public Severity Severity { get; }

    public string Title { get; }

    public string Detail { get; }

    public string SeverityText { get; }

    public string IconGlyph { get; }

    public string Category { get; }

    public bool IsCritical => Severity >= Severity.High;

    public bool IsWarning => Severity is Severity.Low or Severity.Medium;

    public bool IsInformational => Severity == Severity.Info;

    public string AccessibleName { get; }
}
