using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

public sealed partial class CleanupPage : ViewPage
{
    public CleanupPage()
    {
        ViewModel = App.GetService<CleanupViewModel>();
        InitializeComponent();
    }

    public CleanupViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;
}
