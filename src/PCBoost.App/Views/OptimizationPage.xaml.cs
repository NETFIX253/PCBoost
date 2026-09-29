using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

/// <summary>Optimisation : un clic (aperçu → application → rapport), profils, ancien PC.</summary>
public sealed partial class OptimizationPage : ViewPage
{
    private bool _syncing;

    public OptimizationPage()
    {
        ViewModel = App.GetService<OptimizationViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        // Seule source de l'onglet affiché : le ViewModel. Aucun IsSelected dans le XAML : le SelectorBar l'appliquait après
        // coup et son SelectionChanged écrasait l'onglet demandé par la navigation (ex. « Ancien PC », « Journal »).
        Loaded += (_, _) => ApplySection(ViewModel.SelectedSectionIndex);
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public OptimizationViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;

    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncing) return;
        var index = sender.SelectedItem == ProfilesItem ? 1 : sender.SelectedItem == OldPcItem ? 2 : 0;
        ViewModel.SelectedSectionIndex = index;
        ApplySection(index);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OptimizationViewModel.SelectedSectionIndex))
            DispatcherQueue.TryEnqueue(() => ApplySection(ViewModel.SelectedSectionIndex));
    }

    private void ApplySection(int index)
    {
        _syncing = true;
        Sections.SelectedItem = index switch { 1 => ProfilesItem, 2 => OldPcItem, _ => OneClickItem };
        _syncing = false;
        OneClickPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        OldPcPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
    }
}
