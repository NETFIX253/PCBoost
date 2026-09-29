using System.ComponentModel;
using Microsoft.UI.Xaml;
using PCBoost.App.Services;
using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

/// <summary>Rapport de diagnostic : options, aperçu exact (WebView2 isolé) et enregistrement en PDF ou HTML.</summary>
public sealed partial class ReportPage : ViewPage
{
    private readonly ReportFileService _files;
    private bool _previewReady;

    public ReportPage()
    {
        ViewModel = App.GetService<DiagnosticReportViewModel>();
        _files = App.GetService<ReportFileService>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public DiagnosticReportViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _previewReady = await _files.AttachPreviewAsync(Preview).ConfigureAwait(true);
        ShowPreview();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _files.DetachPreview(Preview);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiagnosticReportViewModel.PreviewHtml)) ShowPreview();
    }

    private void ShowPreview()
    {
        if (!_previewReady || Preview.CoreWebView2 is null || string.IsNullOrEmpty(ViewModel.PreviewHtml)) return;
        Preview.NavigateToString(ViewModel.PreviewHtml);
    }
}
