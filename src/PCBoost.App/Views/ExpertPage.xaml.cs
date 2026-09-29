using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

/// <summary>Mode expert : ChangeSets bruts (avant/après), journal technique, erreurs techniques.</summary>
public sealed partial class ExpertPage : ViewPage
{
    private bool _syncing;

    public ExpertPage()
    {
        ViewModel = App.GetService<ExpertViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        // Seule source de l'onglet affiché : le ViewModel. Aucun IsSelected dans le XAML : le SelectorBar l'appliquait après
        // coup et son SelectionChanged écrasait l'onglet demandé par la navigation (ex. « Ancien PC », « Journal »).
        Loaded += (_, _) => ApplyTab(ViewModel.SelectedTabIndex);
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public ExpertViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncing) return;
        var index = sender.SelectedItem == LogItem ? 1 : sender.SelectedItem == ErrorsItem ? 2 : 0;
        ViewModel.SelectedTabIndex = index;
        ApplyTab(index);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExpertViewModel.SelectedTabIndex))
            DispatcherQueue.TryEnqueue(() => ApplyTab(ViewModel.SelectedTabIndex));
    }

    private void ApplyTab(int index)
    {
        _syncing = true;
        Tabs.SelectedItem = index switch { 1 => LogItem, 2 => ErrorsItem, _ => SessionsItem };
        _syncing = false;
        SessionsPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        LogPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        ErrorsPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
    }
}
