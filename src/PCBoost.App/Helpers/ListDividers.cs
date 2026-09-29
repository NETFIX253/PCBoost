using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PCBoost.App.Helpers;

/// <summary>
/// Séparateurs entre les éléments d'une liste dans une carte : chaque élément porte un trait inférieur, sauf le dernier
/// (pas de trait orphelin au bas de la carte). Usage : h:ListDividers.HideLast="True" sur l'ItemsControl ; la racine du
/// modèle d'élément (Border, Grid ou StackPanel) porte BorderThickness="0,0,0,1".
/// </summary>
public static class ListDividers
{
    private static readonly Thickness Divider = new(0, 0, 0, 1);
    private static readonly Thickness None = new(0);

    public static readonly DependencyProperty HideLastProperty =
        DependencyProperty.RegisterAttached("HideLast", typeof(bool), typeof(ListDividers), new PropertyMetadata(false, OnChanged));

    public static bool GetHideLast(DependencyObject element) => (bool)element.GetValue(HideLastProperty);

    public static void SetHideLast(DependencyObject element, bool value) => element.SetValue(HideLastProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list || e.NewValue is not true) return;
        void Schedule() => list.DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Update(list));
        list.Loaded += (_, _) => Schedule();
        list.Items.VectorChanged += (_, _) => Schedule();
        list.SizeChanged += (_, _) => Schedule();
    }

    private static void Update(ItemsControl list)
    {
        // Appel différé : la page a pu être déchargée entre-temps (navigation, fermeture).
        if (!list.IsLoaded || list.ItemsPanelRoot is not Panel panel) return;
        var count = panel.Children.Count;
        for (var i = 0; i < count; i++)
        {
            if (panel.Children[i] is not DependencyObject container || VisualTreeHelper.GetChildrenCount(container) == 0) continue;
            var thickness = i == count - 1 ? None : Divider;
            switch (VisualTreeHelper.GetChild(container, 0))
            {
                case Border border when border.BorderThickness != thickness: border.BorderThickness = thickness; break;
                case Grid grid when grid.BorderThickness != thickness: grid.BorderThickness = thickness; break;
                case StackPanel stack when stack.BorderThickness != thickness: stack.BorderThickness = thickness; break;
            }
        }
    }
}
