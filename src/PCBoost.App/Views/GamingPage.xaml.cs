using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

public sealed partial class GamingPage : ViewPage
{
    public GamingPage()
    {
        ViewModel = App.GetService<GamingViewModel>();
        InitializeComponent();
    }

    public GamingViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;
}
