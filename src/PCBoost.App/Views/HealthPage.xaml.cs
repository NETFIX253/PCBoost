using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

/// <summary>Santé du matériel : disques, batterie, limitation thermique, périphériques (lecture seule).</summary>
public sealed partial class HealthPage : ViewPage
{
    public HealthPage()
    {
        ViewModel = App.GetService<HealthViewModel>();
        InitializeComponent();
    }

    public HealthViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;
}
