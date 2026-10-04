using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace PCBoost.Platform.Health;

/// <summary>
/// Lève temporairement la limite de fréquence des points de restauration (valeur documentée
/// <c>SystemRestorePointCreationFrequency</c>, en minutes, sous HKLM\…\Windows NT\CurrentVersion\SystemRestore) le temps
/// d'une seule création, puis rétablit exactement l'état d'origine (valeur DWORD précédente, ou absence de valeur).
/// <list type="bullet">
/// <item>Un verrou système nommé sérialise les créations (deux assistants administrateur ne se chevauchent jamais).</item>
/// <item>L'état d'origine est d'abord noté dans une clé de PCBoost sous HKLM (accessible aux seuls administrateurs) : si
/// l'assistant s'arrête avant de rétablir la valeur, le prochain assistant la rétablit (<see cref="RecoverInterrupted"/>).
/// Sans cette note, la valeur n'est pas modifiée.</item>
/// <item>Une valeur d'origine d'un autre type que DWORD n'est jamais touchée.</item>
/// </list>
/// Utilisé uniquement par l'assistant administrateur, juste avant une mise à jour de pilotes.
/// </summary>
internal sealed class RestorePointFrequencyOverride : IDisposable
{
    public const string KeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    public const string ValueName = "SystemRestorePointCreationFrequency";

    /// <summary>Note de l'état d'origine (clé propre à PCBoost, supprimée une fois la valeur rétablie).</summary>
    public const string MarkerParentPath = @"SOFTWARE\PCBoost";
    public const string MarkerKeyName = "RestorePointFrequency";
    public const string MarkerKeyPath = MarkerParentPath + @"\" + MarkerKeyName;
    private const string MarkerActive = "OverrideActive";
    private const string MarkerOriginal = "OriginalValue";

    private const string MutexName = @"Global\PCBoost.RestorePointFrequency";
    private static readonly TimeSpan MutexWait = TimeSpan.FromMinutes(2);

    private readonly ILogger _logger;
    private readonly Mutex _mutex;
    private readonly int? _original;
    private bool _disposed;

    private RestorePointFrequencyOverride(ILogger logger, Mutex mutex, int? original)
    {
        _logger = logger;
        _mutex = mutex;
        _original = original;
    }

    /// <summary>
    /// Met la valeur à 0 ; null si le verrou n'est pas obtenu, si la note ne peut pas être écrite ou si la valeur n'est pas
    /// modifiable (la création suit alors la règle de Windows : au plus un point par 24 heures).
    /// </summary>
    public static RestorePointFrequencyOverride? Begin(ILogger logger)
    {
        var mutex = AcquireMutex(logger, MutexWait);
        if (mutex is null) return null;
        try
        {
            RecoverInterruptedCore(logger);
            // Note encore présente : la valeur d'origine n'a pas pu être rétablie ; la valeur actuelle n'est donc pas la
            // valeur d'origine et ne doit surtout pas être notée comme telle.
            if (MarkerExists())
            {
                logger.LogWarning("Fréquence des points de restauration d'origine non rétablie : aucune nouvelle modification");
                Release(mutex);
                return null;
            }
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
            if (key is null)
            {
                Release(mutex);
                return null;
            }
            var current = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            int? original = null;
            if (current is not null)
            {
                if (key.GetValueKind(ValueName) != RegistryValueKind.DWord || current is not int value)
                {
                    logger.LogDebug("Fréquence des points de restauration d'un type inattendu : non modifiée");
                    Release(mutex);
                    return null;
                }
                original = value;
            }

            // La note d'abord : sans elle, la valeur n'est jamais modifiée.
            using (var marker = Registry.LocalMachine.CreateSubKey(MarkerKeyPath, writable: true))
            {
                if (original is { } o) marker.SetValue(MarkerOriginal, o, RegistryValueKind.DWord);
                else marker.DeleteValue(MarkerOriginal, throwOnMissingValue: false);
                marker.SetValue(MarkerActive, 1, RegistryValueKind.DWord);
            }
            key.SetValue(ValueName, 0, RegistryValueKind.DWord);
            return new RestorePointFrequencyOverride(logger, mutex, original);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            logger.LogDebug(ex, "Fréquence des points de restauration non modifiable");
            // Valeur peut-être déjà modifiée : la note (si elle existe) permet de la rétablir tout de suite.
            RecoverInterruptedCore(logger);
            Release(mutex);
            return null;
        }
    }

    /// <summary>
    /// Rétablit la valeur d'origine si un assistant précédent s'est arrêté avant de le faire (note encore présente).
    /// Appelé au démarrage de chaque opération de l'assistant administrateur ; sans note, ne fait rien. N'attend jamais :
    /// si le verrou est détenu, un autre assistant crée un point de restauration et rétablira lui-même la valeur.
    /// </summary>
    public static void RecoverInterrupted(ILogger logger)
    {
        if (!MarkerExists()) return;
        var mutex = AcquireMutex(logger, TimeSpan.Zero);
        if (mutex is null) return;
        try
        {
            RecoverInterruptedCore(logger);
        }
        finally
        {
            Release(mutex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (Restore(_original, _logger)) DeleteMarker(_logger);
        }
        finally
        {
            Release(_mutex);
        }
    }

    private static bool MarkerExists()
    {
        try
        {
            using var marker = Registry.LocalMachine.OpenSubKey(MarkerKeyPath, writable: false);
            return marker is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static void RecoverInterruptedCore(ILogger logger)
    {
        try
        {
            using var marker = Registry.LocalMachine.OpenSubKey(MarkerKeyPath, writable: false);
            if (marker is null) return;
            if (marker.GetValue(MarkerActive) is int active && active == 1)
            {
                int? original = marker.GetValue(MarkerOriginal) is int value && marker.GetValueKind(MarkerOriginal) == RegistryValueKind.DWord ? value : null;
                logger.LogWarning("Fréquence des points de restauration rétablie après une interruption");
                if (!Restore(original, logger)) return;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogDebug(ex, "Note de fréquence des points de restauration illisible");
            return;
        }
        DeleteMarker(logger);
    }

    /// <summary>Valeur d'origine (ou absence) rétablie ; false si la clé de Windows n'a pas pu être écrite.</summary>
    private static bool Restore(int? original, ILogger logger)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
            if (key is null) return true;
            if (original is { } value) key.SetValue(ValueName, value, RegistryValueKind.DWord);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            logger.LogWarning(ex, "Fréquence des points de restauration non rétablie (la note est conservée pour réessayer)");
            return false;
        }
    }

    /// <summary>Supprime la note, puis la clé PCBoost si elle est vide.</summary>
    private static void DeleteMarker(ILogger logger)
    {
        try
        {
            using var parent = Registry.LocalMachine.OpenSubKey(MarkerParentPath, writable: true);
            if (parent is null) return;
            parent.DeleteSubKey(MarkerKeyName, throwOnMissingSubKey: false);
            if (parent.SubKeyCount == 0 && parent.ValueCount == 0)
                Registry.LocalMachine.DeleteSubKey(MarkerParentPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Note de fréquence des points de restauration non supprimée");
        }
    }

    private static Mutex? AcquireMutex(ILogger logger, TimeSpan wait)
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, MutexName);
            if (mutex.WaitOne(wait)) return mutex;
            logger.LogDebug("Verrou des points de restauration non obtenu");
            mutex.Dispose();
            return null;
        }
        catch (AbandonedMutexException)
        {
            // Assistant précédent arrêté brutalement : le verrou est obtenu ; la note permet de rétablir la valeur.
            return mutex;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            logger.LogDebug(ex, "Verrou des points de restauration indisponible");
            mutex?.Dispose();
            return null;
        }
    }

    private static void Release(Mutex mutex)
    {
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Verrou déjà libéré (non détenu par ce fil).
        }
        finally
        {
            mutex.Dispose();
        }
    }
}
