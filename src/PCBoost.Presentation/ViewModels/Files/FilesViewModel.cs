using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Files;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;

namespace PCBoost.Presentation.ViewModels;

/// <summary>Fichier proposé (gros fichier ou copie en double), sélectionnable pour la Corbeille.</summary>
public sealed partial class FileItemViewModel : ObservableObject
{
    private readonly Action<FileItemViewModel>? _selectionChanged;
    private readonly Action<string> _reveal;

    public FileItemViewModel(string path, long size, DateTimeOffset lastWrite, ILocalizer localizer, IValueFormatter formatter,
        Action<string> reveal, Action<FileItemViewModel>? selectionChanged)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(formatter);
        Path = path;
        Size = size;
        _reveal = reveal;
        _selectionChanged = selectionChanged;
        var separator = path.LastIndexOfAny(['\\', '/']);
        Name = separator >= 0 ? path[(separator + 1)..] : path;
        FolderText = separator > 0 ? path[..separator] : string.Empty;
        SizeText = formatter.Bytes(size);
        DateText = localizer.Format("Files_Modified", formatter.DateTimeFull(lastWrite));
        AccessibleName = $"{Name}, {SizeText}, {FolderText}";
    }

    public string Path { get; }

    public long Size { get; }

    public string Name { get; }

    public string FolderText { get; }

    public string SizeText { get; }

    public string DateText { get; }

    public string AccessibleName { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    internal bool SuppressNotification { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!SuppressNotification) _selectionChanged?.Invoke(this);
    }

    internal void SetSelectedSilently(bool value)
    {
        SuppressNotification = true;
        try
        {
            IsSelected = value;
        }
        finally
        {
            SuppressNotification = false;
        }
    }

    [RelayCommand]
    private void Reveal() => _reveal(Path);
}

/// <summary>Groupe de copies identiques : au moins une copie reste toujours non sélectionnée.</summary>
public sealed partial class DuplicateGroupViewModel : ObservableObject
{
    private readonly Action _changed;

    public DuplicateGroupViewModel(DuplicateGroup group, ILocalizer localizer, IValueFormatter formatter, Action<string> reveal, Action changed)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(formatter);
        Model = group;
        _changed = changed;
        Files = new ObservableCollection<FileItemViewModel>(group.Files.Select(f => new FileItemViewModel(f.Path, group.Size, f.LastWriteUtc, localizer, formatter, reveal, OnFileSelectionChanged)));
        Title = Files[0].Name;
        DetailText = localizer.Format("Files_Duplicate_Detail", group.Files.Count, formatter.Bytes(group.Size), formatter.Bytes(group.RecoverableBytes));
        KeepHint = localizer.Get("Files_Duplicate_KeepHint");
    }

    public DuplicateGroup Model { get; }

    public string Title { get; }

    public string DetailText { get; }

    public ObservableCollection<FileItemViewModel> Files { get; }

    public string KeepHint { get; }

    /// <summary>L'utilisateur a tenté de sélectionner toutes les copies : la dernière reste conservée.</summary>
    [ObservableProperty]
    public partial bool ShowKeepHint { get; private set; }

    /// <summary>Sélectionne toutes les copies sauf la plus ancienne (conservée).</summary>
    [RelayCommand]
    private void SelectCopies()
    {
        for (var i = 0; i < Files.Count; i++) Files[i].SetSelectedSilently(i > 0);
        ShowKeepHint = false;
        _changed();
    }

    internal void ClearSelection()
    {
        foreach (var file in Files) file.SetSelectedSilently(false);
        ShowKeepHint = false;
    }

    private void OnFileSelectionChanged(FileItemViewModel file)
    {
        if (file.IsSelected && Files.All(f => f.IsSelected))
        {
            file.SetSelectedSilently(false);
            ShowKeepHint = true;
        }
        else if (!file.IsSelected)
        {
            ShowKeepHint = false;
        }
        _changed();
    }
}

/// <summary>
/// Gros fichiers et doublons des dossiers personnels. L'utilisateur choisit lui-même les fichiers ; ils vont uniquement à
/// la Corbeille (restaurables), après confirmation. Une copie de chaque doublon est toujours conservée.
/// </summary>
public sealed partial class FilesViewModel : ViewModelBase
{
    private readonly IFileCleanupService _cleanup;
    private readonly IShellService _shell;
    private FileScanResult? _scan;

    public FilesViewModel(ViewModelContext context, IFileCleanupService cleanup, IShellService shell)
        : base(context)
    {
        _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
    }

    public ObservableCollection<FileItemViewModel> LargeFiles { get; } = [];

    public ObservableCollection<DuplicateGroupViewModel> DuplicateGroups { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(RecycleCommand))]
    public partial bool IsScanning { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(RecycleCommand))]
    public partial bool IsRecycling { get; private set; }

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasResult { get; private set; }

    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LimitedText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasLargeFiles { get; private set; }

    [ObservableProperty]
    public partial bool HasDuplicates { get; private set; }

    [ObservableProperty]
    public partial string DuplicatesSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RecycleCommand))]
    public partial int SelectedCount { get; private set; }

    public bool HasSelection => SelectedCount > 0;

    [ObservableProperty]
    public partial string SelectionText { get; private set; } = string.Empty;

    /// <summary>Première visite : analyse lancée par la commande (le bouton Annuler est alors disponible).</summary>
    protected override Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken)
        => _scan is null ? ScanCommand.ExecuteAsync(null) : Task.CompletedTask;

    protected override void OnDeactivated() => ScanCancelCommand.Execute(null);

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanScan))]
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        IsScanning = true;
        ProgressText = T("Files_Scanning");
        try
        {
            await RunSafeAsync(async ct =>
            {
                var progress = UiProgress<FileScanProgress>(p => ProgressText = p.Stage switch
                {
                    FileScanStage.Listing => T("Files_Progress_Listing", Formatter.Number(p.FilesListed)),
                    FileScanStage.Comparing => T("Files_Progress_Comparing", Formatter.Number(p.FilesCompared), Formatter.Number(p.FilesToCompare)),
                    _ => T("Files_Scanning"),
                });
                Apply(await _cleanup.ScanAsync(progress, ct).ConfigureAwait(true));
            }, cancellationToken, trackBusy: false).ConfigureAwait(true);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private bool CanScan() => !IsScanning && !IsRecycling;

    [RelayCommand(CanExecute = nameof(CanRecycle))]
    private async Task RecycleAsync()
    {
        if (_scan is null) return;
        var selected = SelectedFiles().ToList();
        if (selected.Count == 0) return;
        var bytes = selected.Sum(f => f.Size);
        var answer = await Dialogs.ConfirmAsync(new ConfirmationRequest(
            TextRef.Of("Files_Confirm_Title", selected.Count),
            TextRef.Of("Files_Confirm_Message", selected.Count, Formatter.Bytes(bytes)),
            TextRef.Of("Files_Confirm_Recycle"),
            CloseButton: TextRef.Of("Common_Action_Cancel"),
            Details: [TextRef.Of("Files_Confirm_Restorable"), TextRef.Of("Files_Confirm_NukeWarning")])).ConfigureAwait(true);
        if (answer != DialogResultKind.Primary) return;

        ErrorText = null;
        StatusMessage = null;
        IsRecycling = true;
        try
        {
            await RunSafeAsync(async ct =>
            {
                var report = await _cleanup.MoveToRecycleBinAsync(_scan, selected.Select(f => f.Path).ToList(), ct).ConfigureAwait(true);
                var failed = new HashSet<string>(report.Failures.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
                var moved = report.Cancelled
                    ? selected.Where(f => !failed.Contains(f.Path)).Take(report.Moved).ToList()
                    : selected.Where(f => !failed.Contains(f.Path)).ToList();
                RemoveMoved(moved.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase));
                if (report.Moved > 0) StatusMessage = T("Files_Recycled", report.Moved, Formatter.Bytes(report.MovedBytes));
                if (report.Failures.Count > 0)
                    ErrorText = T("Files_Failures", report.Failures.Count, DescribeError(report.Failures[0].Error));
                else if (report.Cancelled) ErrorText = T("Files_Cancelled");
            }, linkToPage: false, trackBusy: false).ConfigureAwait(true);
        }
        finally
        {
            IsRecycling = false;
            UpdateSelection();
        }
    }

    private bool CanRecycle() => HasSelection && !IsScanning && !IsRecycling;

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var file in LargeFiles) file.SetSelectedSilently(false);
        foreach (var group in DuplicateGroups) group.ClearSelection();
        UpdateSelection();
    }

    internal void Apply(FileScanResult scan)
    {
        _scan = scan;
        CollectionSync.Replace(LargeFiles, scan.LargeFiles.Select(f => new FileItemViewModel(f.Path, f.Size, f.LastWriteUtc, Localizer, Formatter, Reveal, _ => UpdateSelection())));
        CollectionSync.Replace(DuplicateGroups, scan.DuplicateGroups.Select(g => new DuplicateGroupViewModel(g, Localizer, Formatter, Reveal, UpdateSelection)));
        HasLargeFiles = LargeFiles.Count > 0;
        HasDuplicates = DuplicateGroups.Count > 0;
        SummaryText = T("Files_Summary", scan.FilesScanned, Formatter.DateTimeFull(scan.ScannedAt), Formatter.Number(scan.FilesScanned));
        DuplicatesSummary = HasDuplicates
            ? T("Files_Duplicates_Summary", DuplicateGroups.Count, Formatter.Bytes(scan.DuplicateGroups.Sum(g => g.RecoverableBytes)))
            : T("Files_Duplicates_None");
        LimitedText = scan.ComparisonLimited ? T("Files_Limited") : string.Empty;
        HasResult = true;
        UpdateSelection();
    }

    private IEnumerable<FileItemViewModel> SelectedFiles()
        => LargeFiles.Where(f => f.IsSelected).Concat(DuplicateGroups.SelectMany(g => g.Files).Where(f => f.IsSelected))
            .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase);

    private void UpdateSelection()
    {
        var selected = SelectedFiles().ToList();
        SelectedCount = selected.Count;
        SelectionText = selected.Count == 0 ? T("Files_Selection_None") : T("Files_Selection", selected.Count, Formatter.Bytes(selected.Sum(f => f.Size)));
    }

    private void RemoveMoved(HashSet<string> moved)
    {
        if (moved.Count == 0 || _scan is null) return;
        foreach (var file in LargeFiles.Where(f => moved.Contains(f.Path)).ToList()) LargeFiles.Remove(file);
        var groups = _scan.DuplicateGroups
            .Select(g => g with { Files = g.Files.Where(f => !moved.Contains(f.Path)).ToList() })
            .Where(g => g.Files.Count > 1).ToList();
        _scan = _scan with { LargeFiles = _scan.LargeFiles.Where(f => !moved.Contains(f.Path)).ToList(), DuplicateGroups = groups };
        CollectionSync.Replace(DuplicateGroups, groups.Select(g => new DuplicateGroupViewModel(g, Localizer, Formatter, Reveal, UpdateSelection)));
        HasLargeFiles = LargeFiles.Count > 0;
        HasDuplicates = DuplicateGroups.Count > 0;
        DuplicatesSummary = HasDuplicates
            ? T("Files_Duplicates_Summary", DuplicateGroups.Count, Formatter.Bytes(groups.Sum(g => g.RecoverableBytes)))
            : T("Files_Duplicates_None");
    }

    private void Reveal(string path) => CheckResult(_shell.RevealInExplorer(path));
}
