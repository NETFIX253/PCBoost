using System.Collections.ObjectModel;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Optimization;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;

namespace PCBoost.Presentation.ViewModels;

/// <summary>
/// Rapport final (§71) : actions effectuées, espace récupéré, démarrage -n, processus ajustés, profil, redémarrage,
/// résultat par optimisation, avertissements. Données uniquement : la restauration est portée par le ViewModel parent.
/// </summary>
public sealed class OptimizationReportViewModel
{
    public OptimizationReportViewModel(
        OptimizationRunReport report,
        ILocalizer localizer,
        IValueFormatter formatter,
        Func<string, TextRef?>? optimizationName = null,
        Func<string, TextRef?>? profileName = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(formatter);
        Model = report;
        SessionId = report.SessionId;

        IsSuccess = report.Status == SessionStatus.Completed && report.ActionsFailed == 0;
        IsFailed = report.Status == SessionStatus.Failed || (report.ActionsPerformed == 0 && report.ActionsFailed > 0);
        IsPartial = !IsSuccess && !IsFailed;
        StatusTitle = IsSuccess ? localizer.Get("Optimization_Report_Ready")
            : IsFailed ? localizer.Get("Optimization_Report_Failed")
            : localizer.Get("Optimization_Report_Partial");
        StatusGlyph = IsSuccess ? Glyphs.Success : IsFailed ? Glyphs.Error : Glyphs.Warning;
        if (report.ActionsPerformed == 0 && report.ActionsFailed == 0)
        {
            StatusTitle = localizer.Get("Optimization_Report_NothingDone");
            StatusGlyph = Glyphs.Info;
        }

        var lines = new List<InfoRowViewModel>
        {
            new(localizer.Get("Optimization_Report_Actions"), formatter.Number(report.ActionsPerformed)),
        };
        if (report.ActionsFailed > 0)
            lines.Add(new(localizer.Get("Optimization_Report_Failures"), formatter.Number(report.ActionsFailed)));
        if (report.BytesFreed > 0)
            lines.Add(new(localizer.Get("Optimization_Report_SpaceFreed"), formatter.Bytes(report.BytesFreed)));
        if (report.StartupItemsDisabled > 0)
            lines.Add(new(localizer.Get("Optimization_Report_Startup"), localizer.Format("Optimization_Report_StartupValue", report.StartupItemsDisabled)));
        if (report.ProcessesAdjusted > 0)
            lines.Add(new(localizer.Get("Optimization_Report_Processes"), formatter.Number(report.ProcessesAdjusted)));
        if (report.ProfileId is { } pid)
        {
            var name = profileName?.Invoke(pid);
            lines.Add(new(localizer.Get("Optimization_Report_Profile"), name is null ? pid : localizer.Format(name)));
        }

        lines.Add(new(localizer.Get("Optimization_Report_Restart"), localizer.YesNo(report.RequiresRestart)));
        Lines = new ObservableCollection<InfoRowViewModel>(lines);

        RequiresRestart = report.RequiresRestart;
        RestartText = report.RequiresRestart ? localizer.Get("Optimization_Report_RestartNotice") : string.Empty;

        Results = new ObservableCollection<TextItemViewModel>(report.Results.Select(r =>
        {
            var label = optimizationName?.Invoke(r.OptimizationId) is { } n ? localizer.Format(n) : r.OptimizationId;
            string status;
            string glyph;
            if (!r.Outcome.Success)
            {
                status = r.Outcome.Message is { } m ? localizer.Format(m) : localizer.Get($"Error_{(r.Outcome.Error == OperationErrorKind.None ? OperationErrorKind.Failed : r.Outcome.Error)}");
                glyph = Glyphs.Error;
            }
            else if (r.ChangesApplied == 0 && r.ChangesFailed == 0)
            {
                status = r.Messages.Count > 0 ? localizer.Format(r.Messages[0]) : localizer.Get("Optimization_Report_Skipped");
                glyph = Glyphs.Skipped;
            }
            else if (r.ChangesFailed > 0)
            {
                status = localizer.Format("Optimization_Report_ResultPartial", r.ChangesApplied, r.ChangesFailed);
                glyph = Glyphs.Warning;
            }
            else
            {
                status = r.ChangesApplied == 1
                    ? localizer.Get("Optimization_Report_ResultOne")
                    : localizer.Format("Optimization_Report_ResultMany", r.ChangesApplied);
                glyph = Glyphs.Success;
            }

            return new TextItemViewModel(label, glyph, status);
        }));

        Warnings = new ObservableCollection<TextItemViewModel>(report.Warnings.Select(w => new TextItemViewModel(localizer.Format(w), Glyphs.Warning)));
        CanRestore = report.SessionId != Guid.Empty && report.ActionsPerformed > 0;
        RestoreHint = localizer.Get("Optimization_Report_RestoreHint");
    }

    public OptimizationRunReport Model { get; }

    public Guid SessionId { get; }

    public bool IsSuccess { get; }

    public bool IsPartial { get; }

    public bool IsFailed { get; }

    /// <summary>« Votre PC est prêt. » / partiel / échec.</summary>
    public string StatusTitle { get; }

    public string StatusGlyph { get; }

    /// <summary>Lignes du rapport (libellé / valeur).</summary>
    public ObservableCollection<InfoRowViewModel> Lines { get; }

    public bool RequiresRestart { get; }

    public string RestartText { get; }

    /// <summary>Résultat par optimisation (Text = nom, Detail = état, IconGlyph = état).</summary>
    public ObservableCollection<TextItemViewModel> Results { get; }

    public ObservableCollection<TextItemViewModel> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    public bool CanRestore { get; }

    /// <summary>« Vous pouvez annuler ces modifications à tout moment depuis l'Historique. »</summary>
    public string RestoreHint { get; }
}

/// <summary>Étapes d'exécution : Analyse → Préparation → Sauvegarde → Optimisation → Vérification → Terminé.</summary>
internal static class OptimizationSteps
{
    public static ObservableCollection<ProgressStepViewModel> Create(ILocalizer localizer)
        => new(Enum.GetValues<OptimizationStage>().Select(s =>
            new ProgressStepViewModel((int)s, localizer.Get($"Optimization_Step_{s}"), st => localizer.Get($"Common_Label_Step_{st}"))));

    public static void Apply(IReadOnlyList<ProgressStepViewModel> steps, OptimizationProgress progress)
        => ProgressSteps.Advance(steps, (int)progress.Stage, progress.Stage == OptimizationStage.Completed);
}

/// <summary>Texte de résultat d'une restauration.</summary>
internal static class RestoreText
{
    public static string Describe(ILocalizer localizer, RollbackResult result)
    {
        var parts = new List<string>
        {
            result.Restored == 1 ? localizer.Get("History_Restore_OneRestored") : localizer.Format("History_Restore_Restored", result.Restored),
        };
        if (result.Failed > 0) parts.Add(localizer.Format("History_Restore_Failed", result.Failed));
        if (result.Irreversible > 0) parts.Add(localizer.Format("History_Restore_Irreversible", result.Irreversible));
        return string.Join(" ", parts);
    }
}
