using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

public sealed partial class DiagnosisPage : ViewPage
{
    public DiagnosisPage()
    {
        ViewModel = App.GetService<DiagnosisViewModel>();
        InitializeComponent();
    }

    public DiagnosisViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;
}
