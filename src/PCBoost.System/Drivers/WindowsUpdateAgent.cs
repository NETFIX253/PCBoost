using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using PCBoost.Core.Common;
using PCBoost.Core.Drivers;
using PCBoost.Core.Models.Drivers;

namespace PCBoost.Platform.Drivers;

/// <summary>
/// Accès à l'agent Windows Update (COM « Microsoft.Update.* », liaison tardive) partagé par la recherche (application,
/// sans élévation) et l'installation (assistant administrateur). Aucun serveur n'est imposé : celui de l'organisation
/// (WSUS) est utilisé s'il est configuré ; les mises à jour masquées par l'utilisateur sont exclues.
/// </summary>
internal static class WindowsUpdateAgent
{
    /// <summary>Pilotes non installés et non masqués (catalogue Windows Update ciblé sur ce PC).</summary>
    public const string DriverCriteria = "IsInstalled=0 and Type='Driver' and IsHidden=0";

    public const string ClientApplicationId = "PCBoost";

    /// <summary>OperationResultCode de l'agent : 2 = réussi, 3 = réussi avec erreurs, 4 = échec, 5 = abandonné.</summary>
    public const int ResultSucceeded = 2;
    public const int ResultSucceededWithErrors = 3;

    private const int MaxOffers = 128;
    private const int MaxTextLength = 300;

    public static object CreateSession(ComTracker tracker)
    {
        dynamic session = tracker.Track(CreateObject("Microsoft.Update.Session"));
        session.ClientApplicationID = ClientApplicationId;
        return (object)session;
    }

    public static object CreateObject(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: false)
                   ?? throw new COMException($"{progId} indisponible", unchecked((int)0x80040154));
        return Activator.CreateInstance(type) ?? throw new COMException($"{progId} indisponible", unchecked((int)0x80040154));
    }

    /// <summary>Recherche les pilotes proposés ; renvoie les objets mises à jour (suivis par <paramref name="tracker"/>).</summary>
    public static (int ResultCode, IReadOnlyList<object> Updates) SearchDrivers(object sessionObject, ComTracker tracker)
    {
        dynamic session = sessionObject;
        dynamic searcher = tracker.Track(session.CreateUpdateSearcher());
        dynamic result = tracker.Track(searcher.Search(DriverCriteria));
        int code = result.ResultCode;
        dynamic updates = tracker.Track(result.Updates);
        int count = updates.Count;
        var list = new List<object>();
        for (var i = 0; i < count && i < MaxOffers; i++)
            list.Add(tracker.Track((object)updates[i]));
        return (code, list);
    }

    /// <summary>Description d'une mise à jour de pilote ; null si son identifiant est illisible.</summary>
    public static DriverUpdateOffer? ReadOffer(object updateObject, ComTracker tracker)
    {
        dynamic update = updateObject;
        dynamic identity = tracker.Track(update.Identity);
        string? id = Read(() => (string)identity.UpdateID)?.ToLowerInvariant();
        if (!DriverIdentifiers.IsValidUpdateId(id)) return null;
        var title = Text(Read(() => (string)update.Title)) ?? id!;
        dynamic? behavior = Read(() => (object)update.InstallationBehavior) is { } b ? tracker.Track(b) : null;
        return new DriverUpdateOffer(
            id!,
            Read(() => (int)identity.RevisionNumber) ?? 0,
            title,
            Text(Read(() => (string)update.DriverClass)),
            DeviceId(Read(() => (string)update.DriverHardwareID)),
            Text(Read(() => (string)update.DriverManufacturer)),
            Text(Read(() => (string)update.DriverModel)),
            Text(Read(() => (string)update.DriverProvider)),
            Date(Read(() => (DateTime)update.DriverVerDate)),
            DriverIdentifiers.VersionFromTitle(title),
            Read(() => (bool)update.BrowseOnly) ?? false,
            Time(Read(() => (DateTime)update.LastDeploymentChangeTime)),
            Size(Read(() => (object)update.MaxDownloadSize)),
            Read(() => (bool)update.EulaAccepted) ?? false,
            behavior is null ? false : Read(() => (bool)behavior.CanRequestUserInput) ?? false,
            behavior is null ? DriverRebootBehavior.Possible : RebootBehavior(Read(() => (int)behavior.RebootBehavior)));
    }

    public static string? ReadUpdateId(object updateObject, ComTracker tracker)
    {
        dynamic update = updateObject;
        dynamic identity = tracker.Track(update.Identity);
        string? id = Read(() => (string)identity.UpdateID)?.ToLowerInvariant();
        return DriverIdentifiers.IsValidUpdateId(id) ? id : null;
    }

    /// <summary>Windows Update signale un redémarrage en attente (Microsoft.Update.SystemInfo).</summary>
    public static bool IsRebootRequired(ComTracker tracker)
    {
        dynamic info = tracker.Track(CreateObject("Microsoft.Update.SystemInfo"));
        return Read(() => (bool)info.RebootRequired) ?? false;
    }

    /// <summary>Une installation Windows Update est en cours.</summary>
    public static bool IsInstallerBusy(object sessionObject, ComTracker tracker)
    {
        dynamic session = sessionObject;
        dynamic installer = tracker.Track(session.CreateUpdateInstaller());
        return Read(() => (bool)installer.IsBusy) ?? false;
    }

    public static bool IsComFailure(Exception ex)
        => ex is COMException or UnauthorizedAccessException or RuntimeBinderException or InvalidCastException
            or InvalidComObjectException or ArgumentException or NotSupportedException;

    /// <summary>Message humain d'un échec de l'agent Windows Update (HRESULT documentés).</summary>
    public static OperationResult Classify(Exception ex)
    {
        var hr = ex is COMException com ? com.HResult : ex.HResult;
        return Classify(hr, ex.GetType().Name);
    }

    public static OperationResult Classify(int hresult, string? detail = null)
    {
        var technical = string.Create(CultureInfo.InvariantCulture, $"0x{hresult:X8}{(detail is null ? string.Empty : " " + detail)}");
        return (uint)hresult switch
        {
            // ERROR_SERVICE_DISABLED (service Windows Update désactivé) : PCBoost ne le modifie pas.
            0x80070422 => OperationResult.Fail(OperationErrorKind.NotSupported, TextRef.Of("Drv_Error_ServiceDisabled"), technical),
            0x80070005 => OperationResult.Fail(OperationErrorKind.AccessDenied, TextRef.Of("Drv_Error_AccessDenied"), technical),
            // WU_E_WU_DISABLED / accès au service Microsoft interdit par l'organisation.
            0x8024002E or 0x8024500C => OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Drv_Error_Policy"), technical),
            // Pas de connexion, nom introuvable, serveur indisponible, délai dépassé.
            0x8024402C or 0x80244022 or 0x8024401C or 0x80244010 or 0x80240438 or 0x8024402F
                or 0x80072EE7 or 0x80072EFD or 0x80072EFE or 0x80072EE2 or 0x80072F8F or 0x80072EFF
                => OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Drv_Error_Offline"), technical),
            // Classe COM absente (agent Windows Update indisponible).
            0x80040154 => OperationResult.Fail(OperationErrorKind.NotSupported, TextRef.Of("Drv_Error_AgentMissing"), technical),
            _ => OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Drv_Error_SearchFailed"), technical),
        };
    }

    internal static T? Read<T>(Func<T> read) where T : notnull
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            return default;
        }
    }

    internal static int? Read(Func<int> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            return null;
        }
    }

    internal static bool? Read(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            return null;
        }
    }

    internal static DateTime? Read(Func<DateTime> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            return null;
        }
    }

    internal static string? Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length == 0 ? null : text.Length <= MaxTextLength ? text : text[..MaxTextLength];
    }

    internal static string? DeviceId(string? value) => DriverIdentifiers.IsValidHardwareId(value) ? value : null;

    /// <summary>DATE COM sans heure (date du pilote) ; avant 1980 = absente.</summary>
    internal static DateOnly? Date(DateTime? value) => value is { Year: >= 1980 and < 2200 } d ? DateOnly.FromDateTime(d) : null;

    /// <summary>Horodatage UTC de l'agent ; avant 2000 = absent.</summary>
    internal static DateTimeOffset? Time(DateTime? value)
        => value is { Year: >= 2000 and < 2200 } d ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)) : null;

    internal static long? Size(object? value)
    {
        try
        {
            var size = value is null ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            return size is > 0 and < long.MaxValue ? (long)size : null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    internal static DriverRebootBehavior RebootBehavior(int? value) => value switch
    {
        0 => DriverRebootBehavior.Never,
        1 => DriverRebootBehavior.Always,
        _ => DriverRebootBehavior.Possible,
    };
}

/// <summary>Libère tous les objets COM suivis, dans l'ordre inverse d'obtention.</summary>
internal sealed class ComTracker : IDisposable
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
