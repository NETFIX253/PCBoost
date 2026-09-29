using System.Text;
using System.Text.RegularExpressions;

namespace PCBoost.Core.Privacy;

/// <summary>
/// Masque les données personnelles avant écriture dans les journaux (§34) :
/// chemin du profil → <c>%USERPROFILE%</c>, nom d'utilisateur Windows → <c>&lt;user&gt;</c>, nom de machine → <c>&lt;machine&gt;</c>.
/// Le nom d'utilisateur et le nom de machine sont remplacés comme mots entiers (insensible à la casse) :
/// un nom très courant (ex. « admin ») est donc aussi masqué dans le texte ordinaire, ce qui est préférable à une fuite.
/// </summary>
public sealed class SensitiveDataRedactor
{
    public const string UserProfileToken = "%USERPROFILE%";
    public const string UserToken = "<user>";
    public const string MachineToken = "<machine>";
    internal const string RedactionFailedText = "[message masqué : filtrage des données personnelles impossible]";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private readonly (Regex Pattern, string Replacement)[] _rules;

    public SensitiveDataRedactor(string? userProfilePath, string? userName, string? machineName)
    {
        var rules = new List<(Regex, string)>();

        var profile = (userProfilePath ?? string.Empty).Trim().TrimEnd('\\', '/');
        if (profile.Length >= 3)
            rules.Add((new Regex(BuildPathPattern(profile) + @"(?![\p{L}\p{N}_])", Options, MatchTimeout), UserProfileToken));

        // Nom de machine avant le nom d'utilisateur : « BUREAU-AMIN » devient « <machine> » et non « BUREAU-<user> ».
        var user = (userName ?? string.Empty).Trim();
        var machine = (machineName ?? string.Empty).Trim();
        if (machine.Length >= 2 && !string.Equals(machine, user, StringComparison.OrdinalIgnoreCase))
            rules.Add((WholeWord(machine), MachineToken));

        if (user.Length >= 2)
            rules.Add((WholeWord(user), UserToken));

        _rules = rules.ToArray();
    }

    /// <summary>Filtre pour l'utilisateur et la machine courants.</summary>
    public static SensitiveDataRedactor ForCurrentUser()
        => new(Safe(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify)),
               Safe(() => Environment.UserName),
               Safe(() => Environment.MachineName));

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        try
        {
            foreach (var (pattern, replacement) in _rules)
                text = pattern.Replace(text, replacement);
            return text;
        }
        catch (RegexMatchTimeoutException)
        {
            // En cas de doute, rien n'est écrit plutôt qu'une donnée personnelle.
            return RedactionFailedText;
        }
    }

    /// <summary>Séparateurs « \ », « / » ou « \\ » (chemins échappés en JSON) acceptés entre les segments.</summary>
    private static string BuildPathPattern(string path)
    {
        var builder = new StringBuilder();
        var segments = path.Split('\\', '/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0) builder.Append(@"(?:\\\\|[\\/])");
            builder.Append(Regex.Escape(segments[i]));
        }
        return builder.ToString();
    }

    /// <summary>
    /// Mot entier, insensible à la casse ; un nom de moins de 4 caractères (ex. « hp ») est comparé en respectant la casse
    /// pour ne pas masquer un mot courant (« HP » fabricant) — il reste masqué dans les chemins par la règle du profil.
    /// </summary>
    private static Regex WholeWord(string word)
        => new(@"(?<![\p{L}\p{N}_])" + Regex.Escape(word) + @"(?![\p{L}\p{N}_])",
            word.Length < 4 ? Options & ~RegexOptions.IgnoreCase : Options, MatchTimeout);

    private static string? Safe(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
