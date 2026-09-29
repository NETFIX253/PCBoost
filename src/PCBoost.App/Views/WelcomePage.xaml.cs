using PCBoost.App.Helpers;
using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

/// <summary>Assistant du premier lancement (§61).</summary>
public sealed partial class WelcomePage : ViewPage
{
    public WelcomePage()
    {
        ViewModel = App.GetService<WelcomeViewModel>();
        Heading = Str.Format("App_Welcome_Heading", App.GetService<ShellViewModel>().ProductName);
        InitializeComponent();
    }

    public WelcomeViewModel ViewModel { get; }

    public string Heading { get; }

    protected override INavigationAware NavigationTarget => ViewModel;
}
