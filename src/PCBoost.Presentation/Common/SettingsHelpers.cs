using PCBoost.Core.Services;
using PCBoost.Core.Settings;

namespace PCBoost.Presentation.Common;

internal static class SettingsHelpers
{
    /// <summary>Applique une modification sur une copie des réglages puis l'enregistre immédiatement.</summary>
    public static async Task UpdateAsync(this ISettingsService service, Action<AppSettings> mutate, CancellationToken cancellationToken = default)
    {
        var copy = service.Current.Clone();
        mutate(copy);
        await service.SaveAsync(copy, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Masque une recommandation (liste mémorisée dans les réglages, réaffichable depuis les Paramètres).</summary>
    public static Task DismissRecommendationAsync(this ISettingsService service, string recommendationId, CancellationToken cancellationToken = default)
        => service.UpdateAsync(s =>
        {
            if (!s.DismissedRecommendations.Contains(recommendationId, StringComparer.Ordinal))
                s.DismissedRecommendations.Add(recommendationId);
        }, cancellationToken);
}
