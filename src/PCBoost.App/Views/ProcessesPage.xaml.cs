using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

/// <summary>Gestionnaire de processus avec protection des processus critiques (§13).</summary>
public sealed partial class ProcessesPage : ViewPage
{
    public ProcessesPage()
    {
        ViewModel = App.GetService<ProcessesViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) => UpdateDetailColumn();
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public ProcessesViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProcessesViewModel.HasSelection)) DispatcherQueue.TryEnqueue(UpdateDetailColumn);
    }

    private void OnMainAreaSizeChanged(object sender, SizeChangedEventArgs e) => UpdateDetailColumn();

    /// <summary>Le volet de détails n'occupe de place qu'une fois un processus sélectionné : la liste garde toute la largeur sinon.</summary>
    private void UpdateDetailColumn()
    {
        var shown = ViewModel.HasSelection;
        DetailColumn.Width = new GridLength(shown ? (MainArea.ActualWidth >= 900 ? 320 : 280) : 0);
        MainArea.ColumnSpacing = shown ? 16 : 0;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessList.SelectedItem is ProcessItemViewModel item)
            ViewModel.SelectedProcess = item;
    }
}
