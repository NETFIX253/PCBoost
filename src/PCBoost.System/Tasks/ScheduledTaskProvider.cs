using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;

namespace PCBoost.Platform;

/// <summary>
/// Tâches planifiées déclenchées au démarrage ou à l'ouverture de session (Planificateur de tâches 2.0, COM tardif
/// « Schedule.Service »). Les tâches Microsoft (dossier \Microsoft\ ou auteur Microsoft) ne sont jamais modifiées.
/// Chaque objet COM obtenu est libéré explicitement.
/// </summary>
public sealed class ScheduledTaskProvider : IScheduledTaskProvider
{
    private const int TASK_ENUM_HIDDEN = 1;
    private const int TASK_TRIGGER_BOOT = 8;
    private const int TASK_TRIGGER_LOGON = 9;
    private const int TASK_ACTION_EXEC = 0;
    private const int MaxFolderDepth = 32;

    private readonly ILogger<ScheduledTaskProvider> _logger;

    public ScheduledTaskProvider(ILogger<ScheduledTaskProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<ScheduledTaskProvider>.Instance;
    }

    public IReadOnlyList<ScheduledTaskInfo> GetLogonTasks()
    {
        var result = new List<ScheduledTaskInfo>();
        var releaser = new ComReleaser();
        try
        {
            var service = Connect(releaser);
            if (service is null) return result;
            dynamic root = releaser.Track(service.GetFolder("\\"));
            CollectFolder(root, result, releaser, depth: 0);
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            _logger.LogWarning(ex, "Lecture du Planificateur de tâches impossible");
        }
        finally
        {
            releaser.Dispose();
        }
        return result;
    }

    public bool? IsEnabled(string taskPath)
    {
        if (!IsValidTaskPath(taskPath)) return null;
        var releaser = new ComReleaser();
        try
        {
            var service = Connect(releaser);
            if (service is null) return null;
            dynamic root = releaser.Track(service.GetFolder("\\"));
            dynamic task = releaser.Track(root.GetTask(taskPath));
            return (bool)task.Enabled;
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            return null;
        }
        finally
        {
            releaser.Dispose();
        }
    }

    public OperationResult SetEnabled(string taskPath, bool enabled)
    {
        if (!IsValidTaskPath(taskPath)) return OperationResult.Fail(OperationErrorKind.InvalidInput);
        if (IsMicrosoftPath(taskPath))
            return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Sys_MicrosoftTaskBlocked"));

        var releaser = new ComReleaser();
        try
        {
            var service = Connect(releaser);
            if (service is null) return OperationResult.Fail(OperationErrorKind.NotSupported);
            dynamic root = releaser.Track(service.GetFolder("\\"));
            dynamic task = releaser.Track(root.GetTask(taskPath));
            dynamic definition = releaser.Track(task.Definition);
            dynamic registration = releaser.Track(definition.RegistrationInfo);
            string? author = registration.Author as string;
            if (IsMicrosoftAuthor(author))
                return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Sys_MicrosoftTaskBlocked"));

            task.Enabled = enabled;
            _logger.LogInformation("Tâche planifiée {Task} : {Enabled}", taskPath, enabled);
            return OperationResult.Ok();
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            return ClassifyCom(ex);
        }
        finally
        {
            releaser.Dispose();
        }
    }

    /// <summary>Chemin absolu du Planificateur : commence par « \ », sans « .. » ni caractère de contrôle.</summary>
    internal static bool IsValidTaskPath(string? taskPath)
        => !string.IsNullOrWhiteSpace(taskPath)
           && taskPath.Length <= 1024
           && taskPath.StartsWith('\\')
           && !taskPath.Contains("..", StringComparison.Ordinal)
           && !taskPath.Contains('/')
           && !taskPath.Any(char.IsControl);

    internal static bool IsMicrosoftPath(string taskPath)
        => taskPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)
           || string.Equals(taskPath, @"\Microsoft", StringComparison.OrdinalIgnoreCase);

    internal static bool IsMicrosoftAuthor(string? author)
        => author is not null && (author.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                                  || author.StartsWith("$(@%SystemRoot%", StringComparison.OrdinalIgnoreCase));

    private dynamic? Connect(ComReleaser releaser)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: false);
        if (type is null) return null;
        var instance = Activator.CreateInstance(type);
        if (instance is null) return null;
        dynamic service = releaser.Track(instance);
        service.Connect();
        return service;
    }

    private void CollectFolder(dynamic folder, List<ScheduledTaskInfo> result, ComReleaser releaser, int depth)
    {
        if (depth > MaxFolderDepth) return;
        string folderPath = folder.Path;
        var isMicrosoftFolder = IsMicrosoftPath(folderPath.TrimEnd('\\') + "\\");

        dynamic tasks;
        try
        {
            tasks = releaser.Track(folder.GetTasks(TASK_ENUM_HIDDEN));
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            return;
        }

        int count = tasks.Count;
        for (var i = 1; i <= count; i++)
        {
            try
            {
                dynamic task = releaser.Track(tasks[i]);
                ScheduledTaskInfo? info = ReadTask(task, isMicrosoftFolder, releaser);
                if (info is not null) result.Add(info);
            }
            catch (Exception ex) when (IsComFailure(ex))
            {
                // Tâche illisible (droits insuffisants) : ignorée.
            }
        }

        dynamic subFolders;
        try
        {
            subFolders = releaser.Track(folder.GetFolders(0));
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            return;
        }

        int folderCount = subFolders.Count;
        for (var i = 1; i <= folderCount; i++)
        {
            try
            {
                dynamic sub = releaser.Track(subFolders[i]);
                CollectFolder(sub, result, releaser, depth + 1);
            }
            catch (Exception ex) when (IsComFailure(ex))
            {
                // Dossier inaccessible : ignoré.
            }
        }
    }

    private static ScheduledTaskInfo? ReadTask(dynamic task, bool isMicrosoftFolder, ComReleaser releaser)
    {
        dynamic definition = releaser.Track(task.Definition);
        dynamic triggers = releaser.Track(definition.Triggers);
        var startup = false;
        int triggerCount = triggers.Count;
        for (var t = 1; t <= triggerCount && !startup; t++)
        {
            dynamic trigger = releaser.Track(triggers[t]);
            int type = trigger.Type;
            startup = type is TASK_TRIGGER_LOGON or TASK_TRIGGER_BOOT;
        }
        if (!startup) return null;

        string? executable = null, arguments = null;
        dynamic actions = releaser.Track(definition.Actions);
        int actionCount = actions.Count;
        for (var a = 1; a <= actionCount; a++)
        {
            dynamic action = releaser.Track(actions[a]);
            if ((int)action.Type != TASK_ACTION_EXEC) continue;
            executable = action.Path as string;
            arguments = action.Arguments as string;
            break;
        }

        dynamic registration = releaser.Track(definition.RegistrationInfo);
        string? author = registration.Author as string;
        string path = task.Path;
        string name = task.Name;
        bool enabled = task.Enabled;
        return new ScheduledTaskInfo(
            path,
            name,
            string.IsNullOrWhiteSpace(author) ? null : author,
            string.IsNullOrWhiteSpace(executable) ? null : Environment.ExpandEnvironmentVariables(executable.Trim().Trim('"')),
            string.IsNullOrWhiteSpace(arguments) ? null : arguments,
            enabled,
            isMicrosoftFolder || IsMicrosoftPath(path) || IsMicrosoftAuthor(author));
    }

    private static OperationResult ClassifyCom(Exception ex)
    {
        if (ex is COMException com)
        {
            var code = com.HResult;
            // 0x80070005 : accès refusé (tâche créée par un administrateur) ; 0x80070002 : tâche introuvable.
            if (code == unchecked((int)0x80070005)) return OperationResult.Fail(OperationErrorKind.RequiresElevation, null, ex.Message);
            if (code == unchecked((int)0x80070002)) return OperationResult.Fail(OperationErrorKind.NotFound, null, ex.Message);
        }
        if (ex is UnauthorizedAccessException) return OperationResult.Fail(OperationErrorKind.RequiresElevation, null, ex.Message);
        return OperationResult.FromException(ex);
    }

    private static bool IsComFailure(Exception ex)
        => ex is COMException or UnauthorizedAccessException or RuntimeBinderException or InvalidCastException
            or ArgumentException or InvalidComObjectException or FileNotFoundException;

    /// <summary>Libère tous les objets COM suivis (ordre inverse d'obtention).</summary>
    private sealed class ComReleaser : IDisposable
    {
        private readonly List<object> _objects = [];

        public object Track(object value)
        {
            if (value is not null && Marshal.IsComObject(value)) _objects.Add(value);
            return value!;
        }

        public void Dispose()
        {
            for (var i = _objects.Count - 1; i >= 0; i--)
            {
                try
                {
                    Marshal.FinalReleaseComObject(_objects[i]);
                }
                catch (ArgumentException)
                {
                    // Objet déjà libéré.
                }
            }
            _objects.Clear();
        }
    }
}
