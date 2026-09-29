using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PCBoost.App.Helpers;

/// <summary>
/// Affichage d'un InfoBar piloté par le ViewModel : ouvre/ferme ET replie l'élément (pas d'espace vide dans les piles),
/// et rouvre correctement un InfoBar que l'utilisateur a fermé (la croix remplace sinon la liaison de IsOpen).
/// Usage : h:InfoBarHelper.IsShown="{x:Bind ViewModel.HasError, Mode=OneWay}".
/// </summary>
public static class InfoBarHelper
{
    public static readonly DependencyProperty IsShownProperty =
        DependencyProperty.RegisterAttached("IsShown", typeof(bool?), typeof(InfoBarHelper), new PropertyMetadata(null, OnIsShownChanged));

    public static bool? GetIsShown(DependencyObject element) => (bool?)element.GetValue(IsShownProperty);

    public static void SetIsShown(DependencyObject element, bool? value) => element.SetValue(IsShownProperty, value);

    private static void OnIsShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not InfoBar bar) return;
        var shown = e.NewValue is true;
        bar.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        bar.IsOpen = shown;
    }
}
