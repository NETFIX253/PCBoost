using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

/// <summary>Analyse complète du système (§8, §62).</summary>
public sealed partial class AnalysisPage : ViewPage
{
    public AnalysisPage()
    {
        ViewModel = App.GetService<AnalysisViewModel>();
        InitializeComponent();
    }

    public AnalysisViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;
}
