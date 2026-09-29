namespace PCBoost.Presentation.Navigation;

/// <summary>
/// Cycle de vie d'une page : la vue appelle <see cref="OnNavigatedToAsync"/> à l'affichage
/// (paramètre de navigation éventuel) et <see cref="OnNavigatedFrom"/> en quittant la page
/// (désabonnements, arrêt des rafraîchissements, annulation des opérations de lecture).
/// </summary>
public interface INavigationAware
{
    Task OnNavigatedToAsync(object? parameter);

    void OnNavigatedFrom();
}
