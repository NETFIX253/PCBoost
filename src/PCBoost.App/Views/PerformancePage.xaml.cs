using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

public sealed partial class PerformancePage : ViewPage
{
    public PerformancePage()
    {
        ViewModel = App.GetService<PerformanceViewModel>();
        InitializeComponent();
    }

    public PerformanceViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;
}
