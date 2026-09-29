using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

/// <summary>Réglages du mode Gaming propres à un jeu et historique des FPS mesurés.</summary>
public sealed partial class GameProfilePage : ViewPage
{
    public GameProfilePage()
    {
        ViewModel = App.GetService<GameProfileViewModel>();
        InitializeComponent();
    }

    public GameProfileViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;
}
