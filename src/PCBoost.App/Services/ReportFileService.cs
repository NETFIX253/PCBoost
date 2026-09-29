using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using PCBoost.Core.Common;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;

namespace PCBoost.App.Services;

/// <summary>
/// Enregistrement du rapport de diagnostic : boîte « Enregistrer sous » de Windows, HTML écrit tel quel (UTF-8), PDF
/// produit par l'aperçu WebView2 de la page Rapport (le PDF est donc identique à l'aperçu). Le moteur WebView2 utilise
/// un dossier de données propre à PCBoost, n'exécute aucun script et n'ouvre aucune page externe.
/// </summary>
public sealed class ReportFileService : IReportFileService
{
    private readonly ILogger<ReportFileService> _logger;
    private readonly string _webViewDataFolder;
    private Func<nint>? _windowHandle;
    private WeakReference<WebView2>? _preview;
    private Task<CoreWebView2Environment>? _environment;
    private bool? _canExportPdf;

    public ReportFileService(IAppInfo appInfo, ILogger<ReportFileService> logger)
    {
        ArgumentNullException.ThrowIfNull(appInfo);
        _logger = logger;
        _webViewDataFolder = Path.Combine(appInfo.DataDirectory, "WebView2");
    }

    public bool CanExportPdf => _canExportPdf ??= DetectWebView2();

    public void AttachWindow(Func<nint> windowHandle) => _windowHandle = windowHandle;

    /// <summary>Prépare l'aperçu (moteur WebView2 isolé, sans script ni navigation externe) et le retient pour l'export PDF.</summary>
    public async Task<bool> AttachPreviewAsync(WebView2 preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        _preview = new WeakReference<WebView2>(preview);
        if (!CanExportPdf) return false;
        try
        {
            _environment ??= CoreWebView2Environment.CreateWithOptionsAsync(null, _webViewDataFolder, new CoreWebView2EnvironmentOptions()).AsTask();
            await preview.EnsureCoreWebView2Async(await _environment.ConfigureAwait(true)).AsTask().ConfigureAwait(true);
            var core = preview.CoreWebView2;
            core.Settings.IsScriptEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationStarting += (_, e) =>
            {
                // Seul le document du rapport (chargé depuis la mémoire) est autorisé.
                if (!e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                    e.Cancel = true;
            };
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("Aperçu du rapport indisponible : {Message}", ex.Message);
            _canExportPdf = false;
            return false;
        }
    }

    public void DetachPreview(WebView2 preview)
    {
        if (_preview is not null && _preview.TryGetTarget(out var current) && ReferenceEquals(current, preview)) _preview = null;
    }

    public async Task<string?> PickSavePathAsync(string suggestedFileName, ReportFileFormat format)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedFileName,
        };
        if (format == ReportFileFormat.Pdf) picker.FileTypeChoices.Add("PDF", [".pdf"]);
        else picker.FileTypeChoices.Add("HTML", [".html"]);
        if (_windowHandle?.Invoke() is { } hwnd && hwnd != 0) WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public async Task<OperationResult> SaveAsync(string path, string html, ReportFileFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(html);
        try
        {
            if (format == ReportFileFormat.Html)
            {
                await File.WriteAllTextAsync(path, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(true);
                return OperationResult.Ok();
            }

            if (_preview is null || !_preview.TryGetTarget(out var preview) || preview.CoreWebView2 is null)
                return OperationResult.Fail(OperationErrorKind.NotSupported, TextRef.Of("App_Report_PdfUnavailable"));

            var core = preview.CoreWebView2;
            var settings = core.Environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.MarginTop = settings.MarginBottom = 0.5;
            settings.MarginLeft = settings.MarginRight = 0.5;
            var ok = await core.PrintToPdfAsync(path, settings).AsTask(cancellationToken).ConfigureAwait(true);
            return ok ? OperationResult.Ok() : OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("App_Report_PdfFailed"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Rapport non enregistré : {Type}", ex.GetType().Name);
            return OperationResult.Fail(ex is UnauthorizedAccessException ? OperationErrorKind.AccessDenied : OperationErrorKind.InUse);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException)
        {
            _logger.LogWarning("PDF non produit : {Message}", ex.Message);
            return OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("App_Report_PdfFailed"));
        }
    }

    private bool DetectWebView2()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FileNotFoundException or DllNotFoundException or TypeLoadException)
        {
            _logger.LogInformation("Moteur WebView2 absent : export PDF indisponible.");
            return false;
        }
    }
}
