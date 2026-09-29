using Microsoft.UI.Xaml;
using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

/// <summary>Tableau de bord « État de votre PC » (§7).</summary>
public sealed partial class HomePage : ViewPage
{
    public HomePage()
    {
        ViewModel = App.GetService<HomeViewModel>();
        InitializeComponent();
    }

    public HomeViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;

    /// <summary>Largeur de contenu à partir de laquelle le score et son détail tiennent côte à côte.</summary>
    private const double WideContentWidth = 900;

    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e)
        => VisualStateManager.GoToState(this, e.NewSize.Width >= WideContentWidth ? "Wide" : "Narrow", false);
}
