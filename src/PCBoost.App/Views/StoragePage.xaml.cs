using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.App.Views;

public sealed partial class StoragePage : ViewPage
{
    public StoragePage()
    {
        ViewModel = App.GetService<StorageViewModel>();
        InitializeComponent();
    }

    public StorageViewModel ViewModel { get; }

    protected override INavigationAware NavigationTarget => ViewModel;
}
