using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PCBoost.Core.Models.Health;

namespace PCBoost.Platform.Health;

/// <summary>Lecture des événements Windows (XML) utiles aux diagnostics. Fonctions pures, testables hors Windows.</summary>
internal static class HealthEventParsers
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    public const int MaxNameLength = 200;

    public static XElement? Load(string xml)
    {
        try
        {
            return XElement.Parse(xml);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    public static int? EventId(XElement e)
        => int.TryParse(e.Element(Ns + "System")?.Element(Ns + "EventID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;

    public static DateTimeOffset? TimeCreated(XElement e)
    {
        var value = e.Element(Ns + "System")?.Element(Ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
    }

    public static string? Data(XElement e, string name)
        => e.Element(Ns + "EventData")?.Elements(Ns + "Data").FirstOrDefault(d => (string?)d.Attribute("Name") == name)?.Value?.Trim();

    public static long? DataInt64(XElement e, string name)
        => long.TryParse(Data(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Kernel-Boot 27 : BootType 0 = démarrage complet, 1 = démarrage rapide, 2 = reprise après veille prolongée.</summary>
    public static BootKind BootKindOf(long? bootType) => bootType switch
    {
        0 => BootKind.Cold,
        1 => BootKind.FastStartup,
        2 => BootKind.Resume,
        _ => BootKind.Unknown,
    };

    /// <summary>
    /// Démarrages (Kernel-Boot 27) associés à la première ouverture de session (Winlogon 7001) qui les suit, avant le
    /// démarrage suivant. Les deux listes peuvent être dans n'importe quel ordre ; le résultat va du plus récent au plus ancien.
    /// </summary>
    public static IReadOnlyList<BootSession> BootSessions(IEnumerable<string> kernelBootEvents, IEnumerable<string> logonEvents, int max)
    {
        var boots = kernelBootEvents.Select(Load).OfType<XElement>()
            .Select(e => (Time: TimeCreated(e), Kind: BootKindOf(DataInt64(e, "BootType"))))
            .Where(b => b.Time.HasValue)
            .Select(b => (Time: b.Time!.Value, b.Kind))
            .OrderBy(b => b.Time)
            .ToList();
        var logons = logonEvents.Select(Load).OfType<XElement>().Select(TimeCreated).OfType<DateTimeOffset>().OrderBy(t => t).ToList();

        var sessions = new List<BootSession>(boots.Count);
        for (var i = 0; i < boots.Count; i++)
        {
            var start = boots[i].Time;
            var next = i + 1 < boots.Count ? boots[i + 1].Time : DateTimeOffset.MaxValue;
            DateTimeOffset? logon = logons.FirstOrDefault(t => t >= start && t < next);
            if (logon == default(DateTimeOffset)) logon = null;
            sessions.Add(new BootSession(start, boots[i].Kind, logon));
        }
        sessions.Reverse();
        return sessions.Take(Math.Max(0, max)).ToList();
    }

    /// <summary>Diagnostics-Performance 100 : durées en millisecondes (BootTime = MainPathBootTime + BootPostBootTime).</summary>
    public static BootRecord? BootRecord(string xml)
    {
        if (Load(xml) is not { } e || EventId(e) != 100 || TimeCreated(e) is not { } time) return null;
        if (DataInt64(e, "BootTime") is not { } total || total <= 0 || total > TimeSpan.FromHours(2).TotalMilliseconds) return null;
        var main = DataInt64(e, "MainPathBootTime");
        var post = DataInt64(e, "BootPostBootTime");
        var apps = DataInt64(e, "BootNumStartupApps");
        return new BootRecord(time,
            TimeSpan.FromMilliseconds(total),
            TimeSpan.FromMilliseconds(Math.Clamp(main ?? 0, 0, total)),
            TimeSpan.FromMilliseconds(Math.Clamp(post ?? 0, 0, total)),
            apps is >= 0 and < 10_000 ? (int)apps.Value : null);
    }

    /// <summary>Diagnostics-Performance 101 à 110 : élément ayant ralenti le démarrage (application, pilote, service…).</summary>
    public static BootDegradation? Degradation(string xml)
    {
        if (Load(xml) is not { } e || EventId(e) is not (>= 101 and <= 110) || TimeCreated(e) is not { } time) return null;
        var id = EventId(e)!.Value;
        var friendly = Clean(Data(e, "FriendlyName"));
        var file = Clean(Data(e, "Name"));
        var name = friendly ?? file;
        if (name is null) return null;
        var total = DataInt64(e, "TotalTime") ?? 0;
        var degradation = DataInt64(e, "DegradationTime") ?? 0;
        if (degradation <= 0) return null;
        var kind = id switch
        {
            101 => BootDegradationKind.Application,
            102 => BootDegradationKind.Driver,
            103 => BootDegradationKind.Service,
            _ => BootDegradationKind.Other,
        };
        return new BootDegradation(time, kind, name, file, TimeSpan.FromMilliseconds(Math.Max(0, total)), TimeSpan.FromMilliseconds(degradation));
    }

    /// <summary>Texte d'événement réduit : pas de caractères de contrôle, longueur bornée.</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length == 0) return null;
        return text.Length <= MaxNameLength ? text : text[..MaxNameLength];
    }
}
