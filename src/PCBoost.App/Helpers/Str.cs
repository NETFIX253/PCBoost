using Microsoft.UI.Xaml.Markup;
using PCBoost.Core.Localization;

namespace PCBoost.App.Helpers;

/// <summary>
/// Extension de balisage de localisation : <c>Text="{h:Str Key=Home_Title}"</c>.
/// Résout la clé via <see cref="ILocalizer"/> (ressources .resx de tous les modules). Aucune chaîne en dur dans le XAML (§43).
/// Les pages sont recréées après un changement de langue, ce qui réévalue ces extensions.
/// </summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class Str : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    protected override object ProvideValue() => Get(Key);

    public static string Get(string key)
    {
        var localizer = App.TryGetService<ILocalizer>();
        return localizer is null ? key : localizer.Get(key);
    }

    public static string Format(string key, params object[] args)
    {
        var localizer = App.TryGetService<ILocalizer>();
        return localizer is null ? key : localizer.Format(key, args);
    }
}
