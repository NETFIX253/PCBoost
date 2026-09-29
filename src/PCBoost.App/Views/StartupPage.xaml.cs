using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

public sealed partial class StartupPage : ViewPage
{
    public StartupPage()
    {
        ViewModel = App.GetService<StartupViewModel>();
        InitializeComponent();
    }

    public StartupViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;
}
