using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

public sealed partial class BenchmarkPage : ViewPage
{
    public BenchmarkPage()
    {
        ViewModel = App.GetService<BenchmarkViewModel>();
        InitializeComponent();
    }

    public BenchmarkViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;
}
