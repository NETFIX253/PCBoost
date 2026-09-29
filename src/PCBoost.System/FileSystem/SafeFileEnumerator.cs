using System.IO.Enumeration;
using PCBoost.Core.Abstractions.Platform;

namespace PCBoost.Platform;

/// <summary>
/// Parcours de fichiers qui n'emprunte jamais de point d'analyse (jonction, lien symbolique, fichier cloud non local) :
/// <c>AttributesToSkip = ReparsePoint</c> et refus explicite de descendre dans un point d'analyse.
/// Les erreurs d'accès sont ignorées et comptées (<see cref="ErrorCount"/>) au lieu d'interrompre le parcours.
/// </summary>
internal sealed class SafeFileEnumerator : FileSystemEnumerator<FileEntry>
{
    public SafeFileEnumerator(string directory, bool recursive)
        : base(directory, CreateOptions(recursive))
    {
    }

    public int ErrorCount { get; private set; }

    /// <summary>
    /// IgnoreInaccessible est désactivé uniquement pour que chaque erreur passe par <see cref="ContinueOnError"/>,
    /// qui la compte et poursuit : le comportement reste « ignorer les éléments inaccessibles ».
    /// </summary>
    internal static EnumerationOptions CreateOptions(bool recursive) => new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
        RecurseSubdirectories = recursive,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
    };

    protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        => !entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) == 0;

    protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
        => (entry.Attributes & FileAttributes.ReparsePoint) == 0;

    protected override FileEntry TransformEntry(ref FileSystemEntry entry)
    {
        var attributes = entry.Attributes;
        return new FileEntry(
            entry.ToFullPath(),
            entry.Length,
            entry.LastWriteTimeUtc,
            (attributes & FileAttributes.ReadOnly) != 0,
            (attributes & FileAttributes.System) != 0,
            (attributes & FileAttributes.Hidden) != 0);
    }

    protected override bool ContinueOnError(int error)
    {
        ErrorCount++;
        return true;
    }
}
