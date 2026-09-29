using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCBoost.Presentation.Common;

namespace PCBoost.App.Controls;

/// <summary>Carte de progression par étapes (Analyse → Préparation → Sauvegarde → Optimisation → Vérification → Terminé).</summary>
public sealed partial class ProgressCard : UserControl
{
    public static readonly DependencyProperty TitleProperty = Register(nameof(Title), typeof(string), string.Empty);
    public static readonly DependencyProperty MessageProperty = Register(nameof(Message), typeof(string), string.Empty);
    public static readonly DependencyProperty CurrentTextProperty = Register(nameof(CurrentText), typeof(string), string.Empty);
    public static readonly DependencyProperty PercentProperty = Register(nameof(Percent), typeof(double), 0d);
    public static readonly DependencyProperty IsIndeterminateProperty = Register(nameof(IsIndeterminate), typeof(bool), false);
    public static readonly DependencyProperty StepsProperty = Register(nameof(Steps), typeof(IEnumerable<ProgressStepViewModel>), null);
    public static readonly DependencyProperty StepsAccessibleNameProperty = Register(nameof(StepsAccessibleName), typeof(string), string.Empty);

    public ProgressCard() => InitializeComponent();

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }

    public string CurrentText { get => (string)GetValue(CurrentTextProperty); set => SetValue(CurrentTextProperty, value); }

    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }

    public bool IsIndeterminate { get => (bool)GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }

    public IEnumerable<ProgressStepViewModel>? Steps { get => (IEnumerable<ProgressStepViewModel>?)GetValue(StepsProperty); set => SetValue(StepsProperty, value); }

    public string StepsAccessibleName { get => (string)GetValue(StepsAccessibleNameProperty); set => SetValue(StepsAccessibleNameProperty, value); }

    private static DependencyProperty Register(string name, Type type, object? defaultValue)
        => DependencyProperty.Register(name, type, typeof(ProgressCard), new PropertyMetadata(defaultValue));
}
