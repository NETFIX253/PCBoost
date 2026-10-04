using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

/// <summary>Pilotes : mises à jour Windows Update, point de restauration obligatoire, retour au pilote précédent.</summary>
public sealed partial class DriversPage : ViewPage
{
    public DriversPage()
    {
        ViewModel = App.GetService<DriversViewModel>();
        InitializeComponent();
    }

    public DriversViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;
}
