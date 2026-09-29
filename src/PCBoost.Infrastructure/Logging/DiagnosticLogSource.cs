using PCBoost.Core.Privacy;
using PCBoost.Core.Services;

namespace PCBoost.Infrastructure.Logging;

/// <summary>
/// Lit la fin des fichiers journaux de PCBoost (les plus récents d'abord, au plus 2 Mo lus par fichier), masque à nouveau
/// les données personnelles et borne la longueur de chaque ligne. Lecture seule, partagée avec l'écriture en cours.
/// </summary>
public sealed class DiagnosticLogSource : IDiagnosticLogSource
{
    public const int MaxLineLength = 400;
    private const long MaxBytesPerFile = 2 * 1024 * 1024;
    private const int MaxFiles = 3;

    private readonly string _directory;
    private readonly SensitiveDataRedactor _redactor;

    public DiagnosticLogSource(IAppInfo appInfo)
        : this(appInfo?.LogDirectory ?? throw new ArgumentNullException(nameof(appInfo)), SensitiveDataRedactor.ForCurrentUser())
    {
    }

    public DiagnosticLogSource(string directory, SensitiveDataRedactor redactor)
    {
        _directory = directory ?? string.Empty;
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    }

    public async Task<IReadOnlyList<string>> ReadRecentLinesAsync(int maxLines, CancellationToken cancellationToken = default)
    {
        if (maxLines <= 0 || string.IsNullOrWhiteSpace(_directory) || !Directory.Exists(_directory)) return [];

        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(_directory).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxFiles).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }

        // Du plus récent au plus ancien : on remplit à rebours, puis on remet dans l'ordre chronologique.
        var collected = new List<string>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lines = await ReadTailAsync(file, cancellationToken).ConfigureAwait(false);
            for (var i = lines.Count - 1; i >= 0 && collected.Count < maxLines; i--) collected.Add(Clean(lines[i]));
            if (collected.Count >= maxLines) break;
        }
        collected.Reverse();
        return collected;
    }

    private string Clean(string line)
    {
        var text = _redactor.Redact(new string(line.Where(c => c == '\t' || !char.IsControl(c)).ToArray()));
        return text.Length <= MaxLineLength ? text : string.Concat(text.AsSpan(0, MaxLineLength), "…");
    }

    private static async Task<IReadOnlyList<string>> ReadTailAsync(FileInfo file, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var skipFirst = false;
            if (stream.Length > MaxBytesPerFile)
            {
                stream.Seek(-MaxBytesPerFile, SeekOrigin.End);
                skipFirst = true; // Ligne probablement coupée.
            }
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (skipFirst)
                {
                    skipFirst = false;
                    continue;
                }
                if (line.Length > 0) lines.Add(line);
            }
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
