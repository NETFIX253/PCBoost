namespace PCBoost.Platform.Gaming;

/// <summary>
/// Événements ETW de présentation d'image retenus par la capture : Microsoft-Windows-DXGI (Present_Start = 42,
/// PresentMultiplaneOverlay_Start = 55) et Microsoft-Windows-D3D9 (Present_Start = 1).
/// </summary>
internal static class EtwPresentEvents
{
    public static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    public static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");

    public const int DxgiPresentStart = 42;
    public const int DxgiPresentMultiplaneOverlayStart = 55;
    public const int D3D9PresentStart = 1;

    public static readonly IReadOnlyList<int> DxgiEventIds = [DxgiPresentStart, DxgiPresentMultiplaneOverlayStart];
    public static readonly IReadOnlyList<int> D3D9EventIds = [D3D9PresentStart];

    public static bool IsPresentStart(Guid provider, int eventId)
        => (provider == DxgiProvider && eventId is DxgiPresentStart or DxgiPresentMultiplaneOverlayStart)
           || (provider == D3D9Provider && eventId == D3D9PresentStart);
}

/// <summary>
/// Évite le double comptage lorsqu'un même jeu émet des présentations via deux fournisseurs (D3D9 s'appuyant sur DXGI) :
/// seul le premier fournisseur observé est compté ; l'autre ne prend le relais qu'après 2 s de silence du premier.
/// Non thread-safe : appelé depuis l'unique fil de traitement ETW.
/// </summary>
internal sealed class PresentEventFilter
{
    public const double SwitchAfterSilenceMs = 2000;

    private Guid? _primary;
    private double _primaryLastSeenMs;

    public bool Accept(Guid provider, double timestampMs)
    {
        if (_primary is null || provider == _primary || timestampMs - _primaryLastSeenMs > SwitchAfterSilenceMs)
        {
            _primary = provider;
            _primaryLastSeenMs = timestampMs;
            return true;
        }
        return false;
    }
}
