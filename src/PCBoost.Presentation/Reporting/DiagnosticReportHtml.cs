using System.Globalization;
using System.Text;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.Reports;
using PCBoost.Core.Privacy;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.Presentation.Reporting;

/// <summary>
/// Rapport de diagnostic en HTML autonome (styles intégrés, aucune ressource externe, aucun script) : lisible dans
/// n'importe quel navigateur, imprimable et joignable à un ticket. Tout le texte est échappé ; les données personnelles
/// (profil, nom d'utilisateur et, sauf choix contraire, nom du PC) sont masquées. Seules des valeurs mesurées sont
/// présentées ; une valeur indisponible est signalée comme telle.
/// </summary>
public sealed class DiagnosticReportHtml
{
    public const int MaxSessions = 10;
    public const int MaxChangesPerSession = 12;
    public const int MaxJournalEntries = 40;

    private readonly ILocalizer _l;
    private readonly IValueFormatter _f;
    private readonly SensitiveDataRedactor _redactor;
    private readonly StringBuilder _html = new();

    public DiagnosticReportHtml(ILocalizer localizer, IValueFormatter formatter, SensitiveDataRedactor redactor)
    {
        _l = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _f = formatter ?? throw new ArgumentNullException(nameof(formatter));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    }

    /// <summary>Nom de fichier proposé : « Rapport-PCBoost-2026-09-29-1450 ».</summary>
    public static string SuggestedFileName(DateTimeOffset generatedAt, string productName)
        => string.Create(CultureInfo.InvariantCulture, $"Rapport-{SafeName(productName)}-{generatedAt.ToLocalTime():yyyy-MM-dd-HHmm}");

    public string Build(DiagnosticReportData data, DiagnosticReportOptions options)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(options);
        _html.Clear();

        var title = T("Report_Document_Title", data.ProductName);
        _html.Append("<!DOCTYPE html><html lang=\"").Append(E(_l.Culture.TwoLetterISOLanguageName)).Append("\"><head><meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>").Append(E(title)).Append("</title><style>")
            .Append(Css).Append("</style></head><body><main>");

        Header(data, options, title);
        Summary(data);
        SystemSection(data.Analysis);
        FindingsSection(data);
        HealthSection(data.Health);
        BootSection(data.Boot);
        if (options.IncludeHistory)
        {
            HistorySection(data.Sessions);
            JournalSection(data);
        }
        if (options.IncludeLogs) LogsSection(data.LogLines);

        _html.Append("<footer>").Append(E(T("Report_Footer", data.ProductName, VersionText(data.AppVersion)))).Append("</footer>");
        _html.Append("</main></body></html>");
        return _html.ToString();
    }

    // ---- Sections ----

    private void Header(DiagnosticReportData data, DiagnosticReportOptions options, string title)
    {
        _html.Append("<header><h1>").Append(E(title)).Append("</h1><dl class=\"meta\">");
        Meta(T("Report_Meta_Generated"), _f.DateTimeFull(data.GeneratedAt));
        Meta(T("Report_Meta_Version"), $"{data.ProductName} {VersionText(data.AppVersion)}");
        // Nom du PC choisi explicitement : affiché tel quel (il contient souvent le nom de l'utilisateur, masqué ailleurs).
        if (options.IncludeComputerName && !string.IsNullOrWhiteSpace(data.ComputerName))
            _html.Append("<dt>").Append(E(T("Report_Meta_Computer"))).Append("</dt><dd>").Append(Escape(data.ComputerName.Trim())).Append("</dd>");
        else Meta(T("Report_Meta_Computer"), T("Report_Meta_ComputerHidden"));
        if (data.Analysis is { } a) Meta(T("Analysis_Hw_Windows"), a.Os.ProductName + (string.IsNullOrWhiteSpace(a.Os.DisplayVersion) ? string.Empty : $" {a.Os.DisplayVersion}"));
        _html.Append("</dl><p class=\"note\">").Append(E(T("Report_PrivacyNote"))).Append("</p></header>");
    }

    private void Summary(DiagnosticReportData data)
    {
        Section(T("Report_Section_Summary"));
        _html.Append("<div class=\"summary\">");
        if (data.Score is { } score)
        {
            var value = Math.Clamp(score.Value, 0, 100);
            var level = value >= 80 ? "Good" : value >= 50 ? "Fair" : "Poor";
            _html.Append("<div class=\"score ").Append(level.ToLowerInvariant()).Append("\"><span class=\"big\">").Append(E(_f.Number(value)))
                .Append("</span><span>/ 100</span><strong>").Append(E(T($"Home_ScoreLevel_{level}"))).Append("</strong></div>");
        }
        else
        {
            _html.Append("<div class=\"score\"><strong>").Append(E(T("Report_NoAnalysis"))).Append("</strong></div>");
        }

        _html.Append("<ul class=\"facts\">");
        var relevant = data.Findings.Where(f => f.Severity > Severity.Info).ToList();
        Fact(relevant.Count == 0 ? T("Report_Summary_NoFinding") : T("Report_Summary_Findings", relevant.Count));
        if (relevant.Any(f => f.Severity >= Severity.High)) Fact(T("Report_Summary_Important", relevant.Count(f => f.Severity >= Severity.High)));
        if (data.Health is { } h)
        {
            var critical = h.Disks.Count(d => d.IsCritical);
            Fact(critical > 0 ? T("Report_Summary_DiskCritical", critical) : T("Report_Summary_DisksOk"));
            if (h.DeviceProblems.Count > 0) Fact(T("Health_Devices_Count", h.DeviceProblems.Count));
        }
        if (data.Boot?.LatestBoot is { } boot) Fact(T("Report_Summary_Boot", StartupViewModel.BootDuration(_f, _l, boot.BootTime)));
        _html.Append("</ul></div>");

        if (data.Score is { } s && s.Factors.Count > 0)
        {
            Table([T("Report_Col_Factor"), T("Report_Col_Status"), T("Report_Col_Points")],
                s.Factors.Select(f => new[] { _l.Format(f.Label), T($"Home_FactorStatus_{f.Status}"), $"{f.Points} / {f.MaxPoints}" }));
        }
        EndSection();
    }

    private void SystemSection(SystemAnalysisReport? r)
    {
        Section(T("Report_Section_System"));
        if (r is null)
        {
            Paragraph(T("Report_NoAnalysis"));
            EndSection();
            return;
        }

        var rows = new List<(string, string)>
        {
            (T("Analysis_Hw_Cpu"), Or(r.Cpu.Name)),
            (T("Analysis_Hw_Cores"), T("Analysis_Hw_CoresValue", r.Cpu.PhysicalCores, r.Cpu.LogicalProcessors)),
            (T("Analysis_Hw_Memory"), $"{_f.Bytes(r.Memory.TotalBytes)} ({T("Analysis_Hw_MemoryAvailable", _f.Bytes(r.Memory.AvailableBytes))})"),
            (T("Analysis_Hw_Architecture"), T($"Analysis_Arch_{r.Os.Architecture}")),
            (T("Analysis_Hw_Windows"), string.IsNullOrWhiteSpace(r.Os.DisplayVersion)
                ? $"{r.Os.ProductName} ({T("Home_Windows_Build", r.Os.BuildNumber)})"
                : $"{r.Os.ProductName} {r.Os.DisplayVersion} ({r.Os.BuildNumber}{(r.Os.UpdateBuildRevision > 0 ? "." + r.Os.UpdateBuildRevision.ToString(CultureInfo.InvariantCulture) : string.Empty)})"),
            (T("Analysis_Hw_Uptime"), _f.Duration(r.Os.Uptime)),
        };
        foreach (var gpu in r.Gpus.Where(g => !g.IsSoftwareAdapter))
            rows.Add((T("Analysis_Hw_Gpu"), gpu.DedicatedVideoMemoryBytes is { } vram && vram > 0 ? $"{gpu.Name} ({_f.Bytes(vram)})" : gpu.Name));
        foreach (var d in r.Drives.OrderByDescending(d => d.IsSystemDrive))
            rows.Add((T("Report_Drive", d.RootPath.TrimEnd('\\')), T("Report_DriveValue", _f.Bytes(d.FreeBytes), _f.Bytes(d.TotalBytes), T($"Analysis_Media_{d.MediaType}"))));
        rows.Add((T("Analysis_Sw_Startup"), T("Analysis_Sw_StartupValue", r.EnabledStartupCount, r.StartupEntries.Count)));
        rows.Add((T("Analysis_Sw_Processes"), _f.Number(r.RunningProcessCount)));
        rows.Add((T("Analysis_Sw_CpuLoad"), T("Analysis_Sw_CpuLoadValue", _f.Percent(r.Load.CpuAveragePercent), _f.Percent(r.Load.CpuMaxPercent))));
        rows.Add((T("Analysis_Sw_MemoryLoad"), _f.Percent(r.Load.MemoryUsedPercent)));
        rows.Add((T("Report_Temperature_Cpu"), r.Temperatures.Cpu.HasValue ? _f.Temperature(r.Temperatures.Cpu.Value) : T($"Common_Label_Availability_{r.Temperatures.Cpu.Availability}")));
        KeyValues(rows);

        if (r.TopMemoryProcesses.Count > 0)
        {
            SubHeading(T("Report_TopMemory"));
            // Noms de processus uniquement : les chemins d'accès ne figurent pas dans le rapport.
            Table([T("Report_Col_Process"), T("Report_Col_Memory")], r.TopMemoryProcesses.Take(8).Select(p => new[] { p.Name, _f.Bytes(p.MemoryBytes) }));
        }
        EndSection();
    }

    private void FindingsSection(DiagnosticReportData data)
    {
        Section(T("Report_Section_Findings"));
        var findings = data.Findings.Where(f => f.Severity > Severity.Info).OrderByDescending(f => f.Severity).ToList();
        if (findings.Count == 0) Paragraph(T("Report_NoFindings"));
        else
            Table([T("Report_Col_Severity"), T("Report_Col_Category"), T("Report_Col_Finding")],
                findings.Select(f => new[]
                {
                    T($"Common_Label_Severity_{f.Severity}"),
                    T("Analysis_Category_" + FindingCategories.ResourceSuffix(FindingCategories.Normalize(f.Category))),
                    _l.Format(f.Title) + " — " + _l.Format(f.Detail),
                }), severityColumn: 0, severities: findings.Select(f => f.Severity).ToList());
        EndSection();
    }

    private void HealthSection(HardwareHealthReport? h)
    {
        Section(T("Health_Title"));
        if (h is null)
        {
            Paragraph(T("Report_NoHealth"));
            EndSection();
            return;
        }

        SubHeading(T("Health_Section_Disks"));
        if (h.Disks.Count == 0) Paragraph(T(h.DiskAvailability == Availability.NotSupported ? "Health_Disks_NotSupported" : "Health_Disks_Unavailable"));
        else
        {
            Table([T("Report_Col_Disk"), T("Report_Col_Status"), T("Health_Disk_Wear"), T("Health_Disk_Temperature"), T("Health_Disk_PowerOn"), T("Health_Disk_Errors")],
                h.Disks.Select(d =>
                {
                    var r = d.Reliability;
                    string Value(string? text) => text ?? T(r is null ? "Health_NotRead" : "Health_NotProvided");
                    return new[]
                    {
                        $"{d.FriendlyName} ({T($"Analysis_Media_{d.MediaType}")}, {_f.Bytes(d.SizeBytes)})",
                        T($"Health_Level_{DiskHealthItemViewModel.LevelOf(d)}"),
                        Value(r?.WearPercent is { } w ? T("Health_Disk_WearValue", w) : null),
                        Value(r?.TemperatureCelsius is { } t ? _f.Temperature(t) : null),
                        Value(r?.PowerOnHours is { } hours ? T("Health_Disk_PowerOnValue", _f.Number(hours)) : null),
                        Value(r?.ReadErrorsUncorrected is { } e ? _f.Number(e) : null),
                    };
                }));
            Paragraph(h.ReliabilityMeasuredAt is { } at ? T("Health_Counters_MeasuredAt", _f.DateTimeFull(at)) : T("Health_Counters_NotRead"), "small");
        }

        SubHeading(T("Health_Section_Battery"));
        if (h.Batteries.Count == 0) Paragraph(T(h.BatteryAvailability == Availability.Available ? "Health_Battery_None" : "Health_Battery_Unavailable"));
        foreach (var b in h.Batteries)
        {
            var item = new BatteryHealthItemViewModel(b, _l, _f);
            var rows = new List<(string, string)> { (T("Report_Col_Status"), $"{item.StatusText} — {item.HealthText}") };
            rows.AddRange(item.Rows.Select(row => (row.Label, row.Value)));
            SubSubHeading(item.Name);
            KeyValues(rows);
        }

        SubHeading(T("Health_Section_Thermal"));
        if (h.Thermal.LastEpisode is { } ep)
            Paragraph(T("Health_Thermal_EpisodeDetail", _f.DateTimeFull(ep.StartedAt), _f.Duration(ep.Duration), _f.Percent(ep.AverageProcessorPerformancePercent), _f.Percent(ep.AverageCpuPercent)));
        else if (h.Thermal.FirmwareLimitEvents > 0)
            Paragraph(T("Health_Thermal_FirmwareDetail", h.Thermal.FirmwareLimitEvents, _f.DateTimeFull(h.Thermal.LastFirmwareLimitEvent)));
        else Paragraph(T("Health_Thermal_None"));

        SubHeading(T("Health_Section_Devices"));
        if (h.DeviceAvailability != Availability.Available) Paragraph(T("Health_Devices_Unavailable"));
        else if (h.DeviceProblems.Count == 0) Paragraph(T("Health_Devices_None"));
        else
            Table([T("Report_Col_Device"), T("Report_Col_Class"), T("Report_Col_Problem")],
                h.DeviceProblems.Select(d =>
                {
                    var item = new DeviceProblemItemViewModel(d, _l);
                    return new[] { item.Name, item.ClassText, item.ProblemText };
                }));
        EndSection();
    }

    private void BootSection(BootTimeReport? boot)
    {
        Section(T("Startup_Boot_Title"));
        if (boot is null)
        {
            Paragraph(T("Startup_Boot_NoMeasurement"));
            EndSection();
            return;
        }

        var measured = boot.Measurements?.Boots ?? [];
        if (measured.Count == 0) Paragraph(T("Startup_Boot_NoMeasurement"));
        else
            Table([T("Report_Col_Date"), T("Report_Col_Total"), T("Report_Col_MainPath"), T("Report_Col_PostBoot")],
                measured.Take(10).Select(b => new[]
                {
                    _f.DateTimeFull(b.Timestamp),
                    StartupViewModel.BootDuration(_f, _l, b.BootTime),
                    StartupViewModel.BootDuration(_f, _l, b.MainPathBootTime),
                    StartupViewModel.BootDuration(_f, _l, b.PostBootTime),
                }));
        if (boot.Comparison is { } c)
            Paragraph(T("Startup_Boot_Comparison", _f.DateTimeFull(c.ChangedAt), StartupViewModel.BootDuration(_f, _l, c.AverageBefore), c.BootsBefore,
                StartupViewModel.BootDuration(_f, _l, c.AverageAfter), c.BootsAfter));
        var degradations = boot.Measurements?.Degradations.OrderByDescending(d => d.Timestamp).Take(10).ToList() ?? [];
        if (degradations.Count > 0)
        {
            SubHeading(T("Report_BootDegradations"));
            Table([T("Report_Col_Date"), T("Report_Col_Element"), T("Report_Col_Delay")],
                degradations.Select(d => new[] { _f.DateTimeFull(d.Timestamp), $"{d.Name} ({T($"Startup_Boot_DegradationKind_{d.Kind}")})", "+" + StartupViewModel.BootDuration(_f, _l, d.DegradationTime) }));
        }
        EndSection();
    }

    private void HistorySection(IReadOnlyList<OptimizationSession> sessions)
    {
        Section(T("Report_Section_History"));
        if (sessions.Count == 0)
        {
            Paragraph(T("Report_NoHistory"));
            EndSection();
            return;
        }
        foreach (var session in sessions.OrderByDescending(s => s.StartedAt).Take(MaxSessions))
        {
            SubSubHeading($"{_f.DateTimeFull(session.StartedAt)} — {T($"History_Type_{session.Type}")} ({T($"History_Status_{session.Status}")})");
            if (session.Changes.Count == 0)
            {
                Paragraph(T("Report_NoChange"), "small");
                continue;
            }
            // Modifications identiques regroupées (ex. même réglage appliqué à plusieurs processus d'un programme).
            var groups = session.Changes.OrderBy(c => c.Sequence)
                .GroupBy(c => (Text: _l.Format(c.Description), c.Status, c.Reversible))
                .ToList();
            _html.Append("<ul class=\"changes\">");
            foreach (var group in groups.Take(MaxChangesPerSession))
            {
                _html.Append("<li>").Append(E(group.Key.Text));
                if (group.Count() > 1) _html.Append(" <span class=\"small\">").Append(E(T("Report_Times", group.Count()))).Append("</span>");
                _html.Append(" <span class=\"tag\">").Append(E(T($"History_ChangeStatus_{group.Key.Status}"))).Append("</span>")
                    .Append(group.Key.Reversible ? string.Empty : " <span class=\"tag warn\">" + E(T("Report_Irreversible")) + "</span>")
                    .Append("</li>");
            }
            if (groups.Count > MaxChangesPerSession)
                _html.Append("<li class=\"small\">").Append(E(T("Report_MoreChanges", groups.Skip(MaxChangesPerSession).Sum(g => g.Count())))).Append("</li>");
            _html.Append("</ul>");
        }
        EndSection();
    }

    private void JournalSection(DiagnosticReportData data)
    {
        Section(T("Report_Section_Journal"));
        if (data.Journal.Count == 0) Paragraph(T("Report_NoJournal"));
        else
            Table([T("Report_Col_Date"), T("Report_Col_Type"), T("Report_Col_Event")],
                data.Journal.Take(MaxJournalEntries).Select(e => new[] { _f.DateTimeFull(e.Timestamp), T($"Journal_Kind_{e.Kind}"), _l.Format(e.Message) }));
        EndSection();
    }

    private void LogsSection(IReadOnlyList<string> lines)
    {
        Section(T("Report_Section_Logs"));
        Paragraph(T("Report_LogsNote"), "small");
        if (lines.Count == 0) Paragraph(T("Report_NoLogs"));
        else _html.Append("<pre>").Append(E(string.Join('\n', lines))).Append("</pre>");
        EndSection();
    }

    // ---- Briques HTML ----

    private void Section(string title) => _html.Append("<section><h2>").Append(E(title)).Append("</h2>");

    private void EndSection() => _html.Append("</section>");

    private void SubHeading(string text) => _html.Append("<h3>").Append(E(text)).Append("</h3>");

    private void SubSubHeading(string text) => _html.Append("<h4>").Append(E(text)).Append("</h4>");

    private void Paragraph(string text, string? cssClass = null)
        => _html.Append(cssClass is null ? "<p>" : $"<p class=\"{cssClass}\">").Append(E(text)).Append("</p>");

    private void Fact(string text) => _html.Append("<li>").Append(E(text)).Append("</li>");

    private void Meta(string label, string value) => _html.Append("<dt>").Append(E(label)).Append("</dt><dd>").Append(E(value)).Append("</dd>");

    private void KeyValues(IEnumerable<(string Label, string Value)> rows)
    {
        _html.Append("<table class=\"kv\"><tbody>");
        foreach (var (label, value) in rows) _html.Append("<tr><th scope=\"row\">").Append(E(label)).Append("</th><td>").Append(E(value)).Append("</td></tr>");
        _html.Append("</tbody></table>");
    }

    private void Table(IReadOnlyList<string> headers, IEnumerable<string[]> rows, int severityColumn = -1, IReadOnlyList<Severity>? severities = null)
    {
        _html.Append("<table><thead><tr>");
        foreach (var h in headers) _html.Append("<th scope=\"col\">").Append(E(h)).Append("</th>");
        _html.Append("</tr></thead><tbody>");
        var index = 0;
        foreach (var row in rows)
        {
            _html.Append("<tr>");
            for (var i = 0; i < row.Length; i++)
            {
                var css = i == severityColumn && severities is not null && index < severities.Count ? $" class=\"sev-{severities[index].ToString().ToLowerInvariant()}\"" : string.Empty;
                _html.Append("<td").Append(css).Append('>').Append(E(row[i])).Append("</td>");
            }
            _html.Append("</tr>");
            index++;
        }
        _html.Append("</tbody></table>");
    }

    // ---- Outils ----

    private string T(string key) => _l.Get(key);

    private string T(string key, params object[] args) => _l.Format(key, args);

    private string Or(string? value) => string.IsNullOrWhiteSpace(value) ? _f.NotAvailable : value.Trim();

    /// <summary>Échappe et masque (profil, utilisateur, nom du PC) : aucune donnée personnelle ne quitte ce point.</summary>
    private string E(string? text) => Escape(_redactor.Redact(text ?? string.Empty));

    /// <summary>Échappement HTML minimal (&amp; &lt; &gt; &quot; &#39;) : les caractères accentués restent lisibles tels quels (UTF-8).</summary>
    internal static string Escape(string text)
    {
        if (text.AsSpan().IndexOfAny("&<>\"'") < 0) return text;
        var builder = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => c.ToString(),
            });
        }
        return builder.ToString();
    }

    private static string VersionText(Version version) => version.Build >= 0 ? version.ToString(3) : version.ToString();

    private static string SafeName(string name)
    {
        var cleaned = new string((name ?? string.Empty).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return cleaned.Length == 0 ? "PCBoost" : cleaned;
    }

    private const string Css = """
        :root{color-scheme:light;--ink:#1b1f24;--muted:#5b6470;--line:#d9dee4;--soft:#f3f5f7;--accent:#0f7f8a;--good:#1e7b34;--fair:#8a5a00;--poor:#b42318}
        *{box-sizing:border-box}body{margin:0;background:#fff;color:var(--ink);font:14px/1.5 "Segoe UI",system-ui,sans-serif}
        main{max-width:980px;margin:0 auto;padding:32px 28px 40px}h1{font-size:26px;margin:0 0 12px;font-weight:600}
        h2{font-size:18px;margin:28px 0 10px;padding-bottom:6px;border-bottom:2px solid var(--accent);font-weight:600}
        h3{font-size:15px;margin:18px 0 8px;font-weight:600}h4{font-size:14px;margin:14px 0 6px;font-weight:600}
        dl.meta{display:grid;grid-template-columns:max-content 1fr;gap:2px 16px;margin:0 0 10px}dt{color:var(--muted)}dd{margin:0}
        .note{background:var(--soft);border-left:3px solid var(--accent);padding:8px 12px;margin:8px 0 0;color:var(--muted)}
        .summary{display:flex;gap:24px;align-items:center;flex-wrap:wrap;margin-bottom:12px}
        .score{border:1px solid var(--line);border-radius:10px;padding:12px 18px;display:flex;gap:8px;align-items:baseline;min-width:200px}
        .score .big{font-size:40px;font-weight:600;line-height:1}.score strong{margin-left:8px}
        .score.good strong{color:var(--good)}.score.fair strong{color:var(--fair)}.score.poor strong{color:var(--poor)}
        ul.facts{margin:0;padding-left:18px}table{width:100%;border-collapse:collapse;margin:6px 0 10px}
        th,td{text-align:left;vertical-align:top;padding:6px 8px;border-bottom:1px solid var(--line)}thead th{background:var(--soft);font-weight:600}
        table.kv th{width:34%;color:var(--muted);font-weight:400}.small{color:var(--muted);font-size:12.5px}
        .sev-critical,.sev-high{color:var(--poor);font-weight:600}.sev-medium{color:var(--fair);font-weight:600}
        ul.changes{margin:0 0 8px;padding-left:18px}.tag{font-size:12px;color:var(--muted);border:1px solid var(--line);border-radius:8px;padding:0 6px}
        .tag.warn{color:var(--fair);border-color:#e5c07b}
        pre{background:var(--soft);border:1px solid var(--line);border-radius:6px;padding:10px;font:11.5px/1.45 Consolas,"Cascadia Mono",monospace;white-space:pre-wrap;word-break:break-word}
        footer{margin-top:32px;padding-top:10px;border-top:1px solid var(--line);color:var(--muted);font-size:12px}
        @media print{main{padding:0}h2,h3,h4{break-after:avoid}table,pre{break-inside:auto}tr{break-inside:avoid}}
        """;
}
