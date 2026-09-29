using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

public sealed partial class AboutPage : ViewPage
{
    public AboutPage()
    {
        ViewModel = App.GetService<AboutViewModel>();
        InitializeComponent();
    }

    public AboutViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;
}
