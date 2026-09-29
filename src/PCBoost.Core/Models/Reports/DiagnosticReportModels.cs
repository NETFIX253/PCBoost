using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.Optimization;

namespace PCBoost.Core.Models.Reports;

/// <summary>
/// Contenu choisi par l'utilisateur pour le rapport de diagnostic. Par défaut, le nom du PC n'est pas inclus ;
/// les journaux techniques sont inclus après masquage des données personnelles.
/// </summary>
public sealed record DiagnosticReportOptions(bool IncludeComputerName = false, bool IncludeLogs = true, bool IncludeHistory = true);

/// <summary>Données rassemblées localement pour le rapport (aucune n'est envoyée sur le réseau).</summary>
public sealed record DiagnosticReportData(
    DateTimeOffset GeneratedAt,
    string ProductName,
    Version AppVersion,
    string? ComputerName,
    SystemAnalysisReport? Analysis,
    PerformanceScore? Score,
    IReadOnlyList<HealthFinding> Findings,
    HardwareHealthReport? Health,
    BootTimeReport? Boot,
    IReadOnlyList<OptimizationSession> Sessions,
    IReadOnlyList<ActivityLogEntry> Journal,
    IReadOnlyList<string> LogLines);
