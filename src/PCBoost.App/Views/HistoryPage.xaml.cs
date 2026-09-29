using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

/// <summary>Historique : sessions d'optimisation restaurables et journal d'activité.</summary>
public sealed partial class HistoryPage : ViewPage
{
    private bool _syncing;

    public HistoryPage()
    {
        ViewModel = App.GetService<HistoryViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        // Seule source de l'onglet affiché : le ViewModel. Aucun IsSelected dans le XAML : le SelectorBar l'appliquait après
        // coup et son SelectionChanged écrasait l'onglet demandé par la navigation (ex. « Ancien PC », « Journal »).
        Loaded += (_, _) => ApplyTab(ViewModel.SelectedTabIndex);
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public HistoryViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncing) return;
        var index = sender.SelectedItem == JournalItem ? 1 : 0;
        ViewModel.SelectedTabIndex = index;
        ApplyTab(index);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HistoryViewModel.SelectedTabIndex))
            DispatcherQueue.TryEnqueue(() => ApplyTab(ViewModel.SelectedTabIndex));
    }

    private void ApplyTab(int index)
    {
        _syncing = true;
        Tabs.SelectedItem = index == 1 ? JournalItem : SessionsItem;
        _syncing = false;
        SessionsPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        JournalPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
    }
}
