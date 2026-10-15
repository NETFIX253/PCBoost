namespace PCBoost.Infrastructure.Updates;

/// <summary>
/// Clés publiques (ECDSA P-256, SubjectPublicKeyInfo en Base64) acceptées pour signer le manifeste des mises à jour de
/// PCBoost. Les clés privées correspondantes restent sur le poste de l'éditeur (hors du dépôt) : sans elles, personne ne
/// peut publier une mise à jour acceptée par PCBoost, même avec un accès au dépôt GitHub. La clé de secours permet de
/// continuer à signer si la clé principale est perdue ou doit être remplacée (un manifeste peut porter plusieurs signatures).
/// </summary>
public static class UpdateTrust
{
    public const string PrimaryKeyId = "pcboost-update-2026-1";
    public const string RecoveryKeyId = "pcboost-update-recovery";

    public static IReadOnlyDictionary<string, string> ProductionKeys { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [PrimaryKeyId] = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEyajIZ8x2w9ijl6/EnVKxHN/lyFELe19QpjC7+kvi/xmF+U7pLwj/H2UlurpQMAkQNK6rmEGeojlSlKYnUFnqXw==",
        [RecoveryKeyId] = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEcKOJFkaRddwols4YqOCbqLwfuv59/iDStsZM2KKBuopHNhyg+3a64vzEg5Kj13GJV8vT/VbceLvRRPAJooz9NQ==",
    };
}
