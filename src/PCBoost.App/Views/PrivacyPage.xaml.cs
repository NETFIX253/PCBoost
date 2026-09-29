using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

public sealed partial class PrivacyPage : ViewPage
{
    public PrivacyPage()
    {
        ViewModel = App.GetService<PrivacyViewModel>();
        InitializeComponent();
    }

    public PrivacyViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;
}
