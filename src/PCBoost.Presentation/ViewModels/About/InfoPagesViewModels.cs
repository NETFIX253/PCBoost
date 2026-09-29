using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Branding;
using PCBoost.Core.Services;
using PCBoost.Presentation.Common;

namespace PCBoost.Presentation.ViewModels;

/// <summary>
/// Confidentialité (§32–33) : « Vos données restent sur votre ordinateur. », ce qui est stocké et où,
/// télémétrie désactivée (aucune n'existe), ouverture du dossier de données.
/// </summary>
public sealed partial class PrivacyViewModel : ViewModelBase
{
    private readonly IAppInfo _appInfo;
    private readonly IShellService _shell;

    public PrivacyViewModel(ViewModelContext context, IAppInfo appInfo, IShellService shell)
        : base(context)
    {
        _appInfo = appInfo;
        _shell = shell;
        DataDirectory = appInfo.DataDirectory;
        LogDirectory = appInfo.LogDirectory;
        StoredData =
        [
            new InfoRowViewModel(T("Privacy_Stored_Settings"), T("Privacy_Stored_SettingsDetail")),
            new InfoRowViewModel(T("Privacy_Stored_History"), T("Privacy_Stored_HistoryDetail")),
            new InfoRowViewModel(T("Privacy_Stored_Scans"), T("Privacy_Stored_ScansDetail")),
            new InfoRowViewModel(T("Privacy_Stored_Performance"), T("Privacy_Stored_PerformanceDetail")),
            new InfoRowViewModel(T("Privacy_Stored_Benchmarks"), T("Privacy_Stored_BenchmarksDetail")),
            new InfoRowViewModel(T("Privacy_Stored_Journal"), T("Privacy_Stored_JournalDetail")),
            new InfoRowViewModel(T("Privacy_Stored_Logs"), T("Privacy_Stored_LogsDetail", appInfo.LogDirectory)),
        ];
        NotCollected =
        [
            new TextItemViewModel(T("Privacy_NotCollected_Files"), Glyphs.Privacy),
            new TextItemViewModel(T("Privacy_NotCollected_Browsing"), Glyphs.Privacy),
            new TextItemViewModel(T("Privacy_NotCollected_Telemetry"), Glyphs.Privacy),
            new TextItemViewModel(T("Privacy_NotCollected_Account"), Glyphs.Privacy),
        ];
    }

    /// <summary>Dossier des données locales (base SQLite, réglages).</summary>
    public string DataDirectory { get; }

    public string LogDirectory { get; }

    /// <summary>Ce qui est stocké localement (libellé / détail).</summary>
    public ObservableCollection<InfoRowViewModel> StoredData { get; }

    /// <summary>Ce qui n'est jamais collecté ni envoyé.</summary>
    public ObservableCollection<TextItemViewModel> NotCollected { get; }

    /// <summary>Aucune télémétrie n'existe dans PCBoost : toujours false.</summary>
    public bool IsTelemetryEnabled => false;

    [RelayCommand]
    private void OpenDataFolder() => CheckResult(_shell.OpenFolder(_appInfo.DataDirectory));

    [RelayCommand]
    private void OpenLogFolder() => CheckResult(_shell.OpenFolder(_appInfo.LogDirectory));
}

/// <summary>À propos (§46) : nom, version, description, licence, versions .NET et Windows App SDK, éditeur.</summary>
public sealed partial class AboutViewModel : ViewModelBase
{
    private readonly BrandingOptions _branding;
    private readonly IShellService _shell;

    public AboutViewModel(ViewModelContext context, IAppInfo appInfo, IShellService shell, BrandingOptions? branding = null)
        : base(context)
    {
        ArgumentNullException.ThrowIfNull(appInfo);
        _shell = shell;
        _branding = branding ?? new BrandingOptions();
        ProductName = string.IsNullOrWhiteSpace(appInfo.ProductName) ? _branding.ProductName : appInfo.ProductName;
        VersionText = T("About_Version", VersionFormat.Short(appInfo.Version));
        Description = T("About_Description");
        Slogan = _branding.GetSlogan(Localizer.Culture.TwoLetterISOLanguageName);
        Details =
        [
            new InfoRowViewModel(T("About_Publisher"), _branding.Publisher),
            new InfoRowViewModel(T("About_License"), _branding.License),
            new InfoRowViewModel(T("About_DotNet"), string.IsNullOrWhiteSpace(appInfo.DotNetVersion) ? Formatter.NotAvailable : appInfo.DotNetVersion),
            new InfoRowViewModel(T("About_WindowsAppSdk"), string.IsNullOrWhiteSpace(appInfo.WindowsAppSdkVersion) ? Formatter.NotAvailable : appInfo.WindowsAppSdkVersion),
        ];
    }

    public string ProductName { get; }

    /// <summary>« Version 1.0.0 ».</summary>
    public string VersionText { get; }

    /// <summary>« Application locale d'analyse et d'optimisation des performances Windows. »</summary>
    public string Description { get; }

    public string Slogan { get; }

    /// <summary>Éditeur, licence, version .NET, Windows App SDK.</summary>
    public ObservableCollection<InfoRowViewModel> Details { get; }

    public bool HasWebsite => Uri.TryCreate(_branding.WebsiteUrl, UriKind.Absolute, out _);

    public bool HasSupport => Uri.TryCreate(_branding.SupportUrl, UriKind.Absolute, out _);

    [RelayCommand]
    private void OpenWebsite()
    {
        if (Uri.TryCreate(_branding.WebsiteUrl, UriKind.Absolute, out var uri)) CheckResult(_shell.OpenUri(uri));
    }

    [RelayCommand]
    private void OpenSupport()
    {
        if (Uri.TryCreate(_branding.SupportUrl, UriKind.Absolute, out var uri)) CheckResult(_shell.OpenUri(uri));
    }
}
