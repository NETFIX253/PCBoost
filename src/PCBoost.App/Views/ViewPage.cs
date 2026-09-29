using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PCBoost.Presentation.Navigation;

namespace PCBoost.App.Views;

/// <summary>
/// Base des pages : relaie le cycle de navigation au ViewModel (chargement à l'arrivée, annulation au départ).
/// Aucune logique métier dans le code-behind (§73).
/// </summary>
public partial class ViewPage : Page
{
    protected virtual INavigationAware? NavigationTarget => null;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (NavigationTarget is not { } target) return;
        try
        {
            await target.OnNavigatedToAsync(e.Parameter).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            App.TryGetService<ILogger<ViewPage>>()?.LogError("Navigation vers {Page} : {Message}", GetType().Name, ex.Message);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        NavigationTarget?.OnNavigatedFrom();
    }
}
