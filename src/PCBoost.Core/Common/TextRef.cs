namespace PCBoost.Core.Common;

/// <summary>
/// Référence vers un texte localisable. Les moteurs ne produisent jamais de chaînes affichées
/// directement : ils renvoient une clé de ressource et ses arguments, résolus par l'UI via ILocalizer.
/// </summary>
public sealed record TextRef(string Key, params object[] Args)
{
    /// <summary>Clé spéciale : l'argument 0 est affiché tel quel (noms de fichiers, d'applications…).</summary>
    public const string LiteralKey = "__literal";

    public static TextRef Of(string key, params object[] args) => new(key, args);

    public static TextRef Literal(string text) => new(LiteralKey, text);

    public bool IsLiteral => Key == LiteralKey;

    public override string ToString() => Args.Length == 0 ? Key : $"{Key}({string.Join(", ", Args)})";
}
