using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

/// <summary>Gros fichiers et doublons des dossiers personnels (Corbeille uniquement).</summary>
public sealed partial class FilesPage : ViewPage
{
    public FilesPage()
    {
        ViewModel = App.GetService<FilesViewModel>();
        InitializeComponent();
    }

    public FilesViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;
}
