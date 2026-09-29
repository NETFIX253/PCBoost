namespace PCBoost.TestUtilities;

/// <summary>Test exécuté uniquement sous Windows (ignoré ailleurs).</summary>
public sealed class WindowsFactAttribute : Xunit.FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Nécessite Windows.";
    }
}
