using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;
using PCBoost.Presentation.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PCBoost.App.Services;

/// <summary>
/// Captures automatiques des pages (tests d'interface / documentation) : « --capture-dir » et « --capture-pages ».
/// Rend l'arbre XAML de la fenêtre (RenderTargetBitmap) sans dépendre de l'écran physique, puis ferme l'application.
/// </summary>
public sealed class DevCaptureService
{
    private readonly NavigationService _navigation;
    private readonly ILogger<DevCaptureService> _logger;

    public DevCaptureService(NavigationService navigation, ILogger<DevCaptureService> logger)
    {
        _navigation = navigation;
        _logger = logger;
    }

    public async Task RunAsync(FrameworkElement root, string directory, IReadOnlyList<string> pages, Func<Task> exit)
    {
        try
        {
            Directory.CreateDirectory(directory);
            // Le fond Mica n'est pas rendu par RenderTargetBitmap : fond uni équivalent (SolidBackgroundFillColorBase).
            if (root is Microsoft.UI.Xaml.Controls.Panel panel) panel.Background = BackgroundFor(root);

            await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(true);
            await File.WriteAllTextAsync(Path.Combine(directory, "themes.txt"), DescribeThemes(root)).ConfigureAwait(true);
            var list = pages.Count > 0 ? pages : ["current"];
            foreach (var page in list)
            {
                if (page == "gaming-active")
                {
                    await CaptureGamingSessionAsync(root, directory).ConfigureAwait(true);
                    continue;
                }
                if (page == "drivers-sample")
                {
                    await CaptureDriversSampleAsync(root, directory).ConfigureAwait(true);
                    continue;
                }
                if (page.StartsWith(OldPcPreviewPrefix, StringComparison.Ordinal))
                {
                    await CaptureOldPcPreviewAsync(root, directory, page[OldPcPreviewPrefix.Length..]).ConfigureAwait(true);
                    continue;
                }
                if (page != "current") _navigation.Navigate(page);
                await Task.Delay(TimeSpan.FromSeconds(page switch { "home" or "analysis" => 9, "health" => 8, "drivers" => 90, "report" => 16, "files" => 40, "storage" => 25, "processes" => 7, _ => 4 })).ConfigureAwait(true);
                // L'aperçu WebView2 n'est pas rendu par RenderTargetBitmap : le document du rapport est enregistré à côté.
                if (FindDescendant<Microsoft.UI.Xaml.Controls.Frame>(root) is { Content: Views.ReportPage reportPage } && reportPage.ViewModel.HasPreview)
                {
                    await File.WriteAllTextAsync(Path.Combine(directory, "report.html"), reportPage.ViewModel.PreviewHtml).ConfigureAwait(true);
                    // Contrôle de l'export PDF réel (même chemin que le bouton « Enregistrer en PDF »).
                    var files = App.GetService<ReportFileService>();
                    var pdf = files.CanExportPdf
                        ? await files.SaveAsync(Path.Combine(directory, "report.pdf"), reportPage.ViewModel.PreviewHtml, Presentation.Abstractions.ReportFileFormat.Pdf).ConfigureAwait(true)
                        : Core.Common.OperationResult.Fail(Core.Common.OperationErrorKind.NotSupported);
                    await File.WriteAllTextAsync(Path.Combine(directory, "report-pdf.txt"), $"WebView2 : {files.CanExportPdf} ; PDF : {pdf.Success} {pdf.Error} {pdf.TechnicalDetail}").ConfigureAwait(true);
                }
                var theme = root.ActualTheme == ElementTheme.Dark ? "dark" : "light";
                await SaveAsync(root, Path.Combine(directory, $"{page}-{theme}.png")).ConfigureAwait(true);
                // Page entière (contenu du défilement principal), pour relire aussi ce qui est hors écran.
                if (FindDescendant<Microsoft.UI.Xaml.Controls.Frame>(root) is { } frame
                    && FindDescendant<Microsoft.UI.Xaml.Controls.ScrollViewer>(frame) is { Content: UIElement content } scroller
                    && scroller.ExtentHeight > scroller.ViewportHeight + 1)
                {
                    await SaveFullAsync(root, scroller, content, Path.Combine(directory, $"{page}-{theme}-full.png")).ConfigureAwait(true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Capture automatique : {Type} {Message}", ex.GetType().Name, ex.Message);
            await File.WriteAllTextAsync(Path.Combine(directory, "capture-error.txt"), ex.ToString()).ConfigureAwait(true);
        }
        finally
        {
            await exit().ConfigureAwait(true);
        }
    }

    /// <summary>Thème effectif des principaux éléments (diagnostic des captures).</summary>
    private static string DescribeThemes(FrameworkElement root)
    {
        var lines = new List<string> { $"Application.RequestedTheme={Application.Current.RequestedTheme}", $"root={root.RequestedTheme}/{root.ActualTheme}" };
        void Walk(DependencyObject node, int depth)
        {
            if (depth > 40) return;
            if (node is Microsoft.UI.Xaml.Controls.NavigationView or Microsoft.UI.Xaml.Controls.SplitView or Microsoft.UI.Xaml.Controls.NavigationViewItem or Microsoft.UI.Xaml.Controls.Frame
                && node is FrameworkElement fe)
            {
                var fg = fe is Microsoft.UI.Xaml.Controls.Control c && c.Foreground is SolidColorBrush b ? b.Color.ToString() : "-";
                lines.Add($"{new string(' ', depth)}{fe.GetType().Name} {fe.Name} requested={fe.RequestedTheme} actual={fe.ActualTheme} fg={fg}");
                if (node is Microsoft.UI.Xaml.Controls.NavigationViewItem) return;
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i), depth + 1);
        }
        Walk(root, 0);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Mode Gaming réel (test de bout en bout) : activation, capture de la bande de session active, désactivation et
    /// vérification de la restauration. Les réglages modifiés sont ceux du produit, enregistrés puis restaurés.
    /// </summary>
    private async Task CaptureGamingSessionAsync(FrameworkElement root, string directory)
    {
        _navigation.Navigate(PageKeys.Gaming);
        await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(true);
        if (FindDescendant<Microsoft.UI.Xaml.Controls.Frame>(root)?.Content is not Views.GamingPage page) return;
        var vm = page.ViewModel;
        var report = new List<string> { $"Avant : {vm.StateText}" };
        if (vm.ActivateCommand.CanExecute(null))
        {
            await vm.ActivateCommand.ExecuteAsync(null).ConfigureAwait(true);
            for (var i = 0; i < 30 && !vm.IsModeActive; i++) await Task.Delay(500).ConfigureAwait(true);
        }
        report.Add($"Activé : {vm.IsModeActive} — {vm.StateText} — {vm.ResultText}");
        report.AddRange(vm.ActiveOptimizations.Select(o => $"  {o.StatusText} : {o.Label} {o.Detail}"));
        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        var theme = root.ActualTheme == ElementTheme.Dark ? "dark" : "light";
        await SaveAsync(root, Path.Combine(directory, $"gaming-active-{theme}.png")).ConfigureAwait(true);
        if (FindDescendant<Microsoft.UI.Xaml.Controls.ScrollViewer>(page) is { Content: UIElement content } scroller)
            await SaveFullAsync(root, scroller, content, Path.Combine(directory, $"gaming-active-{theme}-full.png")).ConfigureAwait(true);
        if (vm.DeactivateCommand.CanExecute(null))
        {
            await vm.DeactivateCommand.ExecuteAsync(null).ConfigureAwait(true);
            for (var i = 0; i < 30 && vm.IsModeActive; i++) await Task.Delay(500).ConfigureAwait(true);
        }
        report.Add($"Après désactivation : {vm.StateText} — {vm.ResultText}");
        await File.WriteAllLinesAsync(Path.Combine(directory, "gaming-session.txt"), report).ConfigureAwait(true);
    }

    /// <summary>
    /// Page Pilotes avec des données d'exemple (pseudo-page drivers-sample) : la liste classée puis la carte de résultat,
    /// pour vérifier leur mise en page sur un PC dont les pilotes sont à jour. Rien n'est recherché ni installé en plus de
    /// la recherche normale de la page ; les données d'exemple ne quittent pas la capture.
    /// </summary>
    private async Task CaptureDriversSampleAsync(FrameworkElement root, string directory)
    {
        _navigation.Navigate(PageKeys.Drivers);
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        if (FindDescendant<Microsoft.UI.Xaml.Controls.Frame>(root)?.Content is not Views.DriversPage page) return;
        var vm = page.ViewModel;
        for (var i = 0; i < 180 && vm.IsScanning; i++) await Task.Delay(500).ConfigureAwait(true);
        var (scan, result) = DevCaptureSamples.Drivers(DateTimeOffset.UtcNow);
        vm.Apply(scan);
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        var theme = root.ActualTheme == ElementTheme.Dark ? "dark" : "light";
        await SaveAsync(root, Path.Combine(directory, $"drivers-sample-{theme}.png")).ConfigureAwait(true);
        if (FindDescendant<Microsoft.UI.Xaml.Controls.ScrollViewer>(page) is { Content: UIElement content } scroller && scroller.ExtentHeight > scroller.ViewportHeight + 1)
            await SaveFullAsync(root, scroller, content, Path.Combine(directory, $"drivers-sample-{theme}-full.png")).ConfigureAwait(true);

        // Mises à jour non proposées : section dépliée pour vérifier sa présentation.
        if (FindDescendant<Microsoft.UI.Xaml.Controls.Expander>(page) is { } excluded)
        {
            excluded.IsExpanded = true;
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
            if (FindDescendant<Microsoft.UI.Xaml.Controls.ScrollViewer>(page) is { Content: UIElement expandedContent } expandedScroller)
                await SaveFullAsync(root, expandedScroller, expandedContent, Path.Combine(directory, $"drivers-excluded-{theme}-full.png")).ConfigureAwait(true);
            excluded.IsExpanded = false;
        }

        await vm.ShowResultAsync(result, vm.Recommended.Concat(vm.Review).Take(2).ToList()).ConfigureAwait(true);
        await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(true);
        await SaveAsync(root, Path.Combine(directory, $"drivers-result-{theme}.png")).ConfigureAwait(true);
        var report = new List<string>
        {
            $"Recommandées : {vm.Recommended.Count} ; à examiner : {vm.Review.Count} ; non proposées : {vm.Excluded.Count}",
            $"Sélection : {vm.SelectionText} ; installation possible : {vm.InstallCommand.CanExecute(null)} ; blocage : {vm.InstallBlockedText}",
            $"Résultat : {vm.ResultTitle} — {vm.ResultMessage} — {vm.RestorePointText}",
        };
        report.AddRange(vm.Results.Select(r => $"  {r.DeviceName} {r.VersionsText} : {r.OutcomeText} (retour possible : {r.CanRollback})"));
        await File.WriteAllLinesAsync(Path.Combine(directory, "drivers-sample.txt"), report).ConfigureAwait(true);
    }

    private const string OldPcPreviewPrefix = "oldpc-";

    /// <summary>
    /// Assistant « PC ancien » (pseudo-pages oldpc-essential, oldpc-standard, oldpc-advanced) : ouvre l'aperçu du niveau,
    /// le capture et décrit son contenu, puis revient aux niveaux. Lecture seule : rien n'est appliqué.
    /// </summary>
    private async Task CaptureOldPcPreviewAsync(FrameworkElement root, string directory, string levelName)
    {
        _navigation.Navigate(PageKeys.OldPc);
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        if (FindDescendant<Microsoft.UI.Xaml.Controls.Frame>(root)?.Content is not Views.OptimizationPage page) return;
        var oldPc = page.ViewModel.OldPc;
        string Section() => $"section={page.ViewModel.SelectedSectionIndex} panneau AncienPC={(page.FindName("OldPcPanel") as UIElement)?.Visibility} panneau UnClic={(page.FindName("OneClickPanel") as UIElement)?.Visibility}";
        var diagnostics = new List<string> { $"+2 s : {Section()} ; phase={oldPc.Phase}" };
        if (oldPc.IsNotAssessed && oldPc.AssessCommand.CanExecute(null)) await oldPc.AssessCommand.ExecuteAsync(null).ConfigureAwait(true);
        for (var i = 0; i < 60 && !oldPc.IsAssessed; i++) await Task.Delay(500).ConfigureAwait(true);

        var report = new List<string> { $"Niveau demandé : {levelName} ; évaluation affichée : {oldPc.IsAssessed}" };
        diagnostics.Add($"évalué : {Section()} ; phase={oldPc.Phase}");
        var level = oldPc.Levels.FirstOrDefault(l => string.Equals(l.Level.ToString(), levelName, StringComparison.OrdinalIgnoreCase));
        if (level is not null && level.SelectCommand.CanExecute(null))
        {
            await level.SelectCommand.ExecuteAsync(null).ConfigureAwait(true);
            for (var i = 0; i < 60 && !oldPc.IsPreview; i++) await Task.Delay(500).ConfigureAwait(true);
        }

        report.Add($"Aperçu affiché : {oldPc.IsPreview} ; bouton Appliquer actif : {oldPc.ApplyCommand.CanExecute(null)} ; erreur : {oldPc.ErrorText ?? "aucune"}");
        if (oldPc.Preview is { } preview)
        {
            report.AddRange(preview.Items.Select(item => $"  {item.Name} : {item.SelectionSummary} (modifications prévues : {item.Changes.Count})"));
            report.AddRange(preview.NotApplicableItems.Select(item => $"  Non applicable : {item.Name} — {item.NotApplicableReason}"));
        }

        await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(true);
        diagnostics.Add($"avant capture : {Section()} ; phase={oldPc.Phase}");
        report.AddRange(diagnostics.Select(d => "  [diag] " + d));
        var theme = root.ActualTheme == ElementTheme.Dark ? "dark" : "light";
        await SaveAsync(root, Path.Combine(directory, $"oldpc-{levelName}-{theme}.png")).ConfigureAwait(true);
        if (FindDescendant<Microsoft.UI.Xaml.Controls.ScrollViewer>(page) is { Content: UIElement content } scroller && scroller.ExtentHeight > scroller.ViewportHeight + 1)
            await SaveFullAsync(root, scroller, content, Path.Combine(directory, $"oldpc-{levelName}-{theme}-full.png")).ConfigureAwait(true);

        oldPc.BackToLevelsCommand.Execute(null);
        await File.WriteAllLinesAsync(Path.Combine(directory, $"oldpc-{levelName}.txt"), report).ConfigureAwait(true);
    }

    /// <summary>Rend tout le contenu défilant sur le fond uni du thème (les listes virtualisées sont d'abord réalisées).</summary>
    private static async Task SaveFullAsync(FrameworkElement root, Microsoft.UI.Xaml.Controls.ScrollViewer scroller, UIElement content, string path)
    {
        scroller.ChangeView(null, scroller.ScrollableHeight, null, true);
        await Task.Delay(800).ConfigureAwait(true);
        var panel = content as Microsoft.UI.Xaml.Controls.Panel;
        var previous = panel?.Background;
        if (panel is not null) panel.Background = BackgroundFor(root);
        await Task.Delay(300).ConfigureAwait(true);
        await SaveAsync(content, path).ConfigureAwait(true);
        if (panel is not null) panel.Background = previous;
        scroller.ChangeView(null, 0, null, true);
    }

    private static SolidColorBrush BackgroundFor(FrameworkElement root) => new(root.ActualTheme == ElementTheme.Dark
        ? Windows.UI.Color.FromArgb(0xFF, 0x20, 0x20, 0x20)
        : Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3));

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match && (child is not FrameworkElement fe || fe.ActualHeight > 0)) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static async Task SaveAsync(UIElement element, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = await bitmap.GetPixelsAsync();
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var dpi = (element.XamlRoot?.RasterizationScale ?? 1.0) * 96;
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, dpi, dpi, pixels.ToArray());
        await encoder.FlushAsync();
        stream.Seek(0);
        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
        await File.WriteAllBytesAsync(path, bytes);
    }
}
