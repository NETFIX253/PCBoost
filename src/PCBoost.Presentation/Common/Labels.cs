using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Processes;

namespace PCBoost.Presentation.Common;

/// <summary>Libellés et glyphes des niveaux communs (sévérité, risque, impact, confiance, confiance de signature).</summary>
public static class Labels
{
    /// <summary>La clé existe-t-elle dans une source de ressources (le localiseur renvoie « [clé] » sinon) ?</summary>
    public static bool Has(this ILocalizer localizer, string key)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var value = localizer.Get(key);
        return !string.IsNullOrEmpty(value) && value != $"[{key}]";
    }

    /// <summary>Chaîne de la clé si elle existe, sinon <paramref name="fallback"/>.</summary>
    public static string GetOr(this ILocalizer localizer, string key, string fallback)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var value = localizer.Get(key);
        return string.IsNullOrEmpty(value) || value == $"[{key}]" ? fallback : value;
    }

    public static string Severity(this ILocalizer l, Severity severity) => l.Get($"Common_Label_Severity_{severity}");

    public static string SeverityGlyph(Severity severity) => severity switch
    {
        Core.Common.Severity.Critical => Glyphs.Critical,
        Core.Common.Severity.High => Glyphs.Error,
        Core.Common.Severity.Medium => Glyphs.Warning,
        Core.Common.Severity.Low => Glyphs.Warning,
        _ => Glyphs.Info,
    };

    public static string Risk(this ILocalizer l, RiskLevel risk) => l.Get($"Common_Label_Risk_{risk}");

    public static string RiskGlyph(RiskLevel risk) => risk switch
    {
        RiskLevel.High => Glyphs.Error,
        RiskLevel.Medium => Glyphs.Warning,
        _ => Glyphs.Shield,
    };

    public static string Impact(this ILocalizer l, ImpactLevel impact) => l.Get($"Common_Label_Impact_{impact}");

    public static string Confidence(this ILocalizer l, ConfidenceLevel confidence) => l.Get($"Common_Label_Confidence_{confidence}");

    public static string Reversibility(this ILocalizer l, bool reversible)
        => l.Get(reversible ? "Common_Label_Reversible" : "Common_Label_Irreversible");

    public static string ReversibilityGlyph(bool reversible) => reversible ? Glyphs.Restore : Glyphs.Warning;

    public static string YesNo(this ILocalizer l, bool value) => l.Get(value ? "Common_Label_Yes" : "Common_Label_No");

    /// <summary>« Signé par Microsoft / Signé par X / Non signé / Signature invalide — à examiner / Inconnu ».</summary>
    public static string Trust(this ILocalizer l, TrustAssessment? trust)
    {
        ArgumentNullException.ThrowIfNull(l);
        if (trust is null) return l.Get("Common_Label_Trust_Unknown");
        return trust.Level switch
        {
            TrustLevel.WindowsComponent => l.Get("Common_Label_Trust_Microsoft"),
            TrustLevel.SignedPublisher when !string.IsNullOrWhiteSpace(trust.Signature.Signer ?? trust.Publisher)
                => l.Format("Common_Label_Trust_SignedBy", (trust.Signature.Signer ?? trust.Publisher)!),
            TrustLevel.SignedPublisher => l.Get("Common_Label_Trust_Signed"),
            TrustLevel.Unsigned => l.Get("Common_Label_Trust_Unsigned"),
            TrustLevel.InvalidSignature => l.Get("Common_Label_Trust_Invalid"),
            _ => l.Get("Common_Label_Trust_Unknown"),
        };
    }

    public static string TrustGlyph(TrustLevel level) => level switch
    {
        TrustLevel.WindowsComponent or TrustLevel.SignedPublisher => Glyphs.Shield,
        TrustLevel.InvalidSignature => Glyphs.Warning,
        _ => Glyphs.Unknown,
    };

    /// <summary>Texte d'une signature (entrées de démarrage).</summary>
    public static string Signature(this ILocalizer l, SignatureInfo? signature)
    {
        ArgumentNullException.ThrowIfNull(l);
        if (signature is null) return l.Get("Common_Label_Signature_NotChecked");
        return signature.Status switch
        {
            SignatureStatus.Signed when signature.IsMicrosoft => l.Get("Common_Label_Trust_Microsoft"),
            SignatureStatus.Signed when !string.IsNullOrWhiteSpace(signature.Signer) => l.Format("Common_Label_Trust_SignedBy", signature.Signer!),
            SignatureStatus.Signed => l.Get("Common_Label_Trust_Signed"),
            SignatureStatus.Unsigned => l.Get("Common_Label_Trust_Unsigned"),
            SignatureStatus.Invalid => l.Get("Common_Label_Trust_Invalid"),
            SignatureStatus.NotChecked => l.Get("Common_Label_Signature_NotChecked"),
            _ => l.Get("Common_Label_Trust_Unknown"),
        };
    }
}
