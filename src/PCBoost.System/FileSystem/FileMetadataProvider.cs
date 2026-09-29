using System.Diagnostics;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Models.Processes;

namespace PCBoost.Platform;

/// <summary>Informations de version d'un exécutable (FileVersionInfo).</summary>
public sealed class FileMetadataProvider : IFileMetadataProvider
{
    public FileVersionMetadata? GetVersionInfo(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var metadata = new FileVersionMetadata(
                Clean(info.CompanyName),
                Clean(info.ProductName),
                Clean(info.FileDescription),
                Clean(info.FileVersion));
            return metadata.CompanyName is null && metadata.ProductName is null && metadata.FileDescription is null && metadata.FileVersion is null
                ? null
                : metadata;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim().TrimEnd('\0');
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
