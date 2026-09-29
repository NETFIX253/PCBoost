using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Branding;
using PCBoost.Gaming.Common;

namespace PCBoost.Gaming.Detection;

/// <summary>
/// Base de signatures des jeux : exécutables connus (nom → nom affiché), exécutables à ne jamais considérer comme des jeux
/// (lanceurs, utilitaires, installateurs) et processus anti-triche (jamais modifiés).
/// La base embarquée ne peut pas être réduite : le fichier utilisateur
/// <c>%LOCALAPPDATA%\PCBoost\game-signatures.user.json</c> peut uniquement ajouter des entrées.
/// </summary>
public sealed class GameSignatureDatabase
{
    internal const string EmbeddedResourceName = "PCBoost.Gaming.Data.game-signatures.json";
    internal const string UserFileName = "game-signatures.user.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, string> _games = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _userGames = new(StringComparer.OrdinalIgnoreCase);
    private readonly NamePatternSet _nonGames = new();
    private readonly NamePatternSet _applications = new();
    private readonly NamePatternSet _antiCheat = new();

    /// <summary>Charge la base embarquée et, si présent, le fichier d'extension de l'utilisateur.</summary>
    public GameSignatureDatabase(IFileSystemProvider fileSystem, BrandingOptions? branding = null, ILogger<GameSignatureDatabase>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        var log = logger ?? NullLogger<GameSignatureDatabase>.Instance;
        Merge(LoadEmbedded(), isUser: false);

        var localAppData = fileSystem.GetKnownFolder(KnownFolder.LocalAppData);
        if (string.IsNullOrEmpty(localAppData)) return;
        var userFile = WinPath.Combine(localAppData, branding?.DataFolderName ?? "PCBoost", UserFileName);
        UserFilePath = userFile;
        if (!fileSystem.FileExists(userFile)) return;
        try
        {
            var json = fileSystem.ReadAllText(userFile);
            var user = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<SignatureFile>(json, JsonOptions);
            if (user is not null)
            {
                Merge(user, isUser: true);
                log.LogInformation("Signatures de jeux utilisateur chargées : {Games} jeux, {Excluded} exclusions",
                    user.Games?.Count ?? 0, (user.NonGames?.Count ?? 0) + (user.Applications?.Count ?? 0) + (user.AntiCheat?.Count ?? 0));
            }
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Fichier de signatures de jeux utilisateur invalide : ignoré");
        }
    }

    /// <summary>Base embarquée seule (sans extension utilisateur).</summary>
    internal GameSignatureDatabase(SignatureFile data)
    {
        Merge(data, isUser: false);
    }

    /// <summary>Chemin du fichier d'extension de l'utilisateur (même s'il n'existe pas), ou null si le dossier est inconnu.</summary>
    public string? UserFilePath { get; }

    public int GameCount => _games.Count;

    public IReadOnlyDictionary<string, string> Games => _games;

    /// <summary>Nom affiché du jeu correspondant à cet exécutable, s'il est connu.</summary>
    public bool TryGetGameName(string executableName, out string displayName)
    {
        var name = Normalize(executableName);
        if (_games.TryGetValue(name, out var found) && !IsExcludedCore(name, allowUserOverride: true))
        {
            displayName = found;
            return true;
        }
        displayName = string.Empty;
        return false;
    }

    /// <summary>
    /// Exécutable à ne jamais considérer comme un jeu (lanceur, utilitaire, installateur, anti-triche).
    /// Un jeu ajouté explicitement par l'utilisateur l'emporte sur un motif générique (« *setup*.exe »), jamais sur un nom exact.
    /// </summary>
    public bool IsExcluded(string executableName) => IsExcludedCore(Normalize(executableName), allowUserOverride: true);

    /// <summary>Processus anti-triche : jamais modifié (priorité, mode efficacité), jamais considéré comme un jeu.</summary>
    public bool IsAntiCheat(string executableName) => _antiCheat.Matches(Normalize(executableName));

    /// <summary>Lanceur ou utilitaire de jeu (hors anti-triche).</summary>
    public bool IsLauncherOrUtility(string executableName) => _nonGames.Matches(Normalize(executableName));

    internal static string Normalize(string? executableName)
    {
        var n = WinPath.GetFileName(executableName).Trim().ToLowerInvariant();
        if (n.Length == 0) return n;
        return n.EndsWith(".exe", StringComparison.Ordinal) ? n : n + ".exe";
    }

    private bool IsExcludedCore(string name, bool allowUserOverride)
    {
        if (_antiCheat.Matches(name)) return true;
        if (_nonGames.MatchesExact(name) || _applications.MatchesExact(name)) return true;
        if (allowUserOverride && _userGames.Contains(name)) return false;
        return _nonGames.MatchesPattern(name) || _applications.MatchesPattern(name);
    }

    private void Merge(SignatureFile data, bool isUser)
    {
        if (data.Games is not null)
        {
            foreach (var (exe, display) in data.Games)
            {
                var name = Normalize(exe);
                if (name.Length == 0 || string.IsNullOrWhiteSpace(display)) continue;
                _games[name] = display.Trim();
                if (isUser) _userGames.Add(name);
            }
        }
        if (data.NonGames is not null) foreach (var p in data.NonGames) _nonGames.Add(p);
        if (data.Applications is not null) foreach (var p in data.Applications) _applications.Add(p);
        if (data.AntiCheat is not null) foreach (var p in data.AntiCheat) _antiCheat.Add(p);
    }

    internal static SignatureFile LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException("Base de signatures de jeux embarquée introuvable.");
        return JsonSerializer.Deserialize<SignatureFile>(stream, JsonOptions)
            ?? throw new InvalidOperationException("Base de signatures de jeux embarquée invalide.");
    }

    internal sealed class SignatureFile
    {
        public Dictionary<string, string>? Games { get; set; }
        public List<string>? NonGames { get; set; }

        /// <summary>Applications courantes (navigateurs, lecteurs, messageries…) : jamais des jeux, mais optimisables en arrière-plan.</summary>
        public List<string>? Applications { get; set; }

        public List<string>? AntiCheat { get; set; }
    }
}

/// <summary>Ensemble de noms d'exécutables exacts et de motifs « * » (insensibles à la casse).</summary>
internal sealed class NamePatternSet
{
    private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _patterns = [];

    public void Add(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return;
        var e = entry.Trim().ToLowerInvariant();
        if (e.Contains('*')) { if (!_patterns.Contains(e)) _patterns.Add(e); }
        else _exact.Add(e.EndsWith(".exe", StringComparison.Ordinal) ? e : e + ".exe");
    }

    public bool Matches(string name) => MatchesExact(name) || MatchesPattern(name);

    public bool MatchesExact(string name) => _exact.Contains(name);

    public bool MatchesPattern(string name)
    {
        foreach (var p in _patterns)
            if (Wildcard.IsMatch(name, p)) return true;
        return false;
    }
}

/// <summary>Correspondance de motifs avec « * » (toute suite de caractères) et « ? » (un caractère).</summary>
internal static class Wildcard
{
    public static bool IsMatch(string text, string pattern)
    {
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])))
            {
                t++; p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
