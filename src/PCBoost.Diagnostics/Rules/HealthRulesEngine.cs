using Microsoft.Extensions.Logging;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Services;
using PCBoost.Core.Settings;

namespace PCBoost.Diagnostics.Rules;

/// <summary>
/// Exécute toutes les règles enregistrées et trie les constats par sévérité décroissante (ordre d'enregistrement
/// conservé à sévérité égale). Une règle qui échoue est journalisée et ignorée.
/// </summary>
public sealed class HealthRulesEngine : IHealthRulesEngine
{
    private readonly IHealthRule[] _rules;
    private readonly ILogger<HealthRulesEngine> _logger;

    public HealthRulesEngine(IEnumerable<IHealthRule> rules, ILogger<HealthRulesEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.ToArray();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyList<IHealthRule> Rules => _rules;

    public IReadOnlyList<HealthFinding> Evaluate(SystemAnalysisReport report, HealthThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(thresholds);

        var findings = new List<HealthFinding>(_rules.Length);
        foreach (var rule in _rules)
        {
            try
            {
                if (rule.Evaluate(report, thresholds) is { } finding) findings.Add(finding);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Règle de santé {RuleId} ignorée après une erreur : {ErrorType} {Message}",
                    SafeId(rule), ex.GetType().Name, PrivacyRedactor.Redact(ex.Message));
            }
        }

        // OrderByDescending est stable : l'ordre d'enregistrement départage les sévérités égales.
        return findings.OrderByDescending(f => f.Severity).ToList();
    }

    private static string SafeId(IHealthRule rule)
    {
        try
        {
            return rule.Id;
        }
        catch (Exception)
        {
            return rule.GetType().Name;
        }
    }
}
