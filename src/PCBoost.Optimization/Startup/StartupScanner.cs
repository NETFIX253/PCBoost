using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Models.Startup;
using PCBoost.Core.Services;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Startup;

/// <summary>
/// Inventaire des programmes lancés au démarrage (lecture seule) : clés Run (HKCU, HKLM 64 et 32 bits), dossiers Démarrage
/// (utilisateur et commun, raccourcis résolus), tâches planifiées d'ouverture de session non Microsoft. L'état activé/désactivé
/// provient de StartupApproved, exactement comme dans le Gestionnaire des tâches.
/// </summary>
public sealed class StartupScanner : IStartupProvider
{
    private readonly IRegistryProvider _registry;
    private readonly IFileSystemProvider _fileSystem;
    private readonly IShortcutResolver _shortcuts;
    private readonly IScheduledTaskProvider _tasks;
    private readonly ISecurityService _security;
    private readonly ILogger<StartupScanner> _logger;

    public StartupScanner(IRegistryProvider registry, IFileSystemProvider fileSystem, IShortcutResolver shortcuts, IScheduledTaskProvider tasks,
        ISecurityService security, ILogger<StartupScanner>? logger = null)
    {
        _registry = registry;
        _fileSystem = fileSystem;
        _shortcuts = shortcuts;
        _tasks = tasks;
        _security = security;
        _logger = logger ?? NullLogger<StartupScanner>.Instance;
    }

    public Task<IReadOnlyList<StartupEntry>> GetEntriesAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<StartupEntry>>(() => Scan(cancellationToken), cancellationToken);

    internal IReadOnlyList<StartupEntry> Scan(CancellationToken cancellationToken)
    {
        var entries = new List<StartupEntry>();
        Section(entries, () => ScanRun(StartupLocation.RegistryRunUser, @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"), "Run HKCU");
        cancellationToken.ThrowIfCancellationRequested();
        Section(entries, () => ScanRun(StartupLocation.RegistryRunMachine, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"), "Run HKLM");
        cancellationToken.ThrowIfCancellationRequested();
        Section(entries, () => ScanRun(StartupLocation.RegistryRunMachine32, @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"), "Run HKLM 32");
        cancellationToken.ThrowIfCancellationRequested();
        Section(entries, () => ScanFolder(StartupLocation.StartupFolderUser, KnownFolder.StartupUser), "Démarrage utilisateur");
        cancellationToken.ThrowIfCancellationRequested();
        Section(entries, () => ScanFolder(StartupLocation.StartupFolderCommon, KnownFolder.StartupCommon), "Démarrage commun");
        cancellationToken.ThrowIfCancellationRequested();
        Section(entries, ScanTasks, "Tâches planifiées");
        return entries;
    }

    private void Section(List<StartupEntry> entries, Func<IEnumerable<StartupEntry>> scan, string label)
    {
        try
        {
            entries.AddRange(scan());
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            _logger.LogWarning(ex, "Lecture des entrées de démarrage « {Section} » impossible", label);
        }
    }

    private IEnumerable<StartupEntry> ScanRun(StartupLocation location, string displayPath)
    {
        var run = StartupApproved.RunLocation(location)!;
        var approved = StartupApproved.ApprovedLocation(location)!;
        foreach (var name in _registry.GetValueNames(run))
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var data = _registry.GetValue(run, name);
            if (data?.Value is not string command || string.IsNullOrWhiteSpace(command)) continue;

            var executable = CommandLineParser.ExtractExecutable(command, _fileSystem);
            yield return Build(
                id: $"{location}:{name}",
                itemName: name,
                fallbackName: name,
                location: location,
                sourcePath: displayPath,
                command: command,
                executable: executable,
                enabled: StartupApproved.IsEnabled(_registry.GetValue(approved, name)),
                requiresElevation: run.Hive == RegistryHiveKind.LocalMachine);
        }
    }

    private IEnumerable<StartupEntry> ScanFolder(StartupLocation location, KnownFolder folder)
    {
        var directory = _fileSystem.GetKnownFolder(folder);
        if (string.IsNullOrWhiteSpace(directory) || !_fileSystem.DirectoryExists(directory)) yield break;
        var approved = StartupApproved.ApprovedLocation(location)!;

        foreach (var file in _fileSystem.EnumerateFiles(directory, recursive: false))
        {
            var fileName = PathUtil.FileName(file.Path);
            if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

            string? executable = null;
            string? command = file.Path;
            if (fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var target = _shortcuts.Resolve(file.Path);
                if (target is not null && !string.IsNullOrWhiteSpace(target.TargetPath))
                {
                    executable = CommandLineParser.ExpandEnvironment(target.TargetPath.Trim().Trim('"'), _fileSystem);
                    command = string.IsNullOrWhiteSpace(target.Arguments) ? $"\"{executable}\"" : $"\"{executable}\" {target.Arguments}";
                }
            }
            else if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                executable = file.Path;
            }

            yield return Build(
                id: $"{location}:{fileName}",
                itemName: fileName,
                fallbackName: PathUtil.WithoutExtension(fileName),
                location: location,
                sourcePath: directory,
                command: command,
                executable: executable,
                enabled: StartupApproved.IsEnabled(_registry.GetValue(approved, fileName)),
                requiresElevation: location == StartupLocation.StartupFolderCommon);
        }
    }

    private IEnumerable<StartupEntry> ScanTasks()
    {
        foreach (var task in _tasks.GetLogonTasks())
        {
            if (task.IsMicrosoft || ScheduledTaskWriter.IsMicrosoftTaskPath(task.Path)) continue;
            var executable = string.IsNullOrWhiteSpace(task.ExecutablePath)
                ? null
                : CommandLineParser.ExtractExecutable($"\"{task.ExecutablePath.Trim().Trim('"')}\"", _fileSystem);
            var command = executable is null ? null : string.IsNullOrWhiteSpace(task.Arguments) ? $"\"{executable}\"" : $"\"{executable}\" {task.Arguments}";
            yield return Build(
                id: $"{StartupLocation.ScheduledTaskLogon}:{task.Path}",
                itemName: task.Name,
                fallbackName: task.Name,
                location: StartupLocation.ScheduledTaskLogon,
                sourcePath: task.Path,
                command: command,
                executable: executable,
                enabled: task.Enabled,
                requiresElevation: false);
        }
    }

    private StartupEntry Build(string id, string itemName, string fallbackName, StartupLocation location, string sourcePath, string? command,
        string? executable, bool enabled, bool requiresElevation)
    {
        var exists = executable is not null && _fileSystem.FileExists(executable);
        var trust = exists ? _security.Assess(executable) : new TrustAssessment(TrustLevel.Unknown, SignatureInfo.Unknown, null, null, false, false);
        var name = string.IsNullOrWhiteSpace(trust.Description) ? fallbackName : trust.Description!;
        return new StartupEntry
        {
            Id = id,
            Name = name,
            Location = location,
            SourcePath = sourcePath,
            ItemName = itemName,
            Command = command,
            ExecutablePath = executable,
            ExecutableExists = exists,
            Publisher = trust.Publisher,
            Description = trust.Description,
            IsEnabled = enabled,
            RequiresElevation = requiresElevation,
            Signature = exists ? trust.Signature : SignatureInfo.NotChecked,
            IsMicrosoft = trust.Signature.IsMicrosoft,
            IsSecuritySoftware = KnownSoftware.IsSecuritySoftware(itemName, executable, trust.Publisher)
                                 || KnownSoftware.IsSecuritySoftware(name, null, null),
        };
    }
}
