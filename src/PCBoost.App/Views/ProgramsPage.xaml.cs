using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

/// <summary>Programmes installés : désinstallation assistée par le programme officiel de l'éditeur.</summary>
public sealed partial class ProgramsPage : ViewPage
{
    public ProgramsPage()
    {
        ViewModel = App.GetService<ProgramsViewModel>();
        InitializeComponent();
    }

    public ProgramsViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;
}
