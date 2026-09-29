using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;

namespace PCBoost.Presentation.ViewModels;

/// <summary>Entrée du journal des modifications (heure, type, message).</summary>
public sealed class JournalEntryItemViewModel
{
    public JournalEntryItemViewModel(ActivityLogEntry entry, ILocalizer localizer, IValueFormatter formatter)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(formatter);
        Model = entry;
        TimeText = formatter.DateTimeFull(entry.Timestamp);
        RelativeTimeText = formatter.DateTime(entry.Timestamp);
        Message = localizer.Format(entry.Message);
        KindText = localizer.Get($"Journal_Kind_{entry.Kind}");
        IconGlyph = entry.Kind switch
        {
            ActivityKind.Analysis => Glyphs.Analysis,
            ActivityKind.Optimization => Glyphs.Optimization,
            ActivityKind.Cleanup => Glyphs.Cleanup,
            ActivityKind.Startup => Glyphs.Startup,
            ActivityKind.Rollback => Glyphs.Restore,
            ActivityKind.Gaming => Glyphs.Gaming,
            ActivityKind.Warning => Glyphs.Warning,
            ActivityKind.Error => Glyphs.Error,
            _ => Glyphs.Info,
        };
    }

    public ActivityLogEntry Model { get; }

    public string TimeText { get; }

    public string RelativeTimeText { get; }

    public string Message { get; }

    public string KindText { get; }

    public string IconGlyph { get; }

    public string AccessibleName => $"{TimeText}, {KindText} : {Message}";
}

/// <summary>Journal des modifications visible par l'utilisateur (§78) ; nouvelles entrées ajoutées en direct.</summary>
public sealed partial class JournalViewModel : ViewModelBase
{
    private const int Limit = 200;
    private readonly IActivityJournal _journal;

    public JournalViewModel(ViewModelContext context, IActivityJournal journal)
        : base(context)
    {
        _journal = journal;
    }

    public ObservableCollection<JournalEntryItemViewModel> Entries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoaded { get; private set; }

    public bool ShowEmpty => IsLoaded && Entries.Count == 0;

    protected override async Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken)
    {
        _journal.EntryAdded += OnEntryAdded;
        await LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    protected override void OnDeactivated() => _journal.EntryAdded -= OnEntryAdded;

    [RelayCommand]
    private Task RefreshAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    private Task LoadAsync(CancellationToken cancellationToken) => RunSafeAsync(async ct =>
    {
        var entries = await _journal.GetRecentAsync(Limit, ct).ConfigureAwait(true);
        CollectionSync.Replace(Entries, entries.OrderByDescending(e => e.Timestamp).Select(e => new JournalEntryItemViewModel(e, Localizer, Formatter)));
        IsLoaded = true;
        OnPropertyChanged(nameof(ShowEmpty));
    }, cancellationToken);

    private void OnEntryAdded(object? sender, ActivityLogEntry entry) => OnUi(() =>
    {
        if (!IsActive || Entries.Any(e => e.Model.Id == entry.Id)) return;
        Entries.Insert(0, new JournalEntryItemViewModel(entry, Localizer, Formatter));
        while (Entries.Count > Limit) Entries.RemoveAt(Entries.Count - 1);
        OnPropertyChanged(nameof(ShowEmpty));
    });
}
