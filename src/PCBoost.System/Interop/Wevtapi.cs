using System.Runtime.InteropServices;

namespace PCBoost.Platform.Interop;

/// <summary>Déclarations wevtapi.dll (journaux d'événements Windows, lecture seule).</summary>
internal static partial class Wevtapi
{
    public const int EvtQueryChannelPath = 0x1;
    public const int EvtQueryReverseDirection = 0x200;
    public const int EvtRenderEventXml = 1;
    public const int ERROR_EVT_CHANNEL_NOT_FOUND = 15007;
    public const int ERROR_EVT_INVALID_QUERY = 15001;

    [LibraryImport("wevtapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint EvtQuery(nint session, string path, string query, int flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtNext(nint resultSet, int eventsSize, [Out] nint[] events, int timeout, int flags, out int returned);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtRender(nint context, nint fragment, int flags, int bufferSize, nint buffer, out int bufferUsed, out int propertyCount);

    [LibraryImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtClose(nint handle);
}

/// <summary>Résultat d'une lecture de journal : XML des événements (du plus récent au plus ancien) ou refus d'accès.</summary>
internal sealed record EventLogReadResult(IReadOnlyList<string> Events, bool AccessDenied, bool ChannelMissing)
{
    public static EventLogReadResult Empty { get; } = new([], false, false);
}

/// <summary>Lecture d'événements au format XML via wevtapi, sans dépendance supplémentaire.</summary>
internal static class EventLogXmlReader
{
    private const int BatchSize = 16;

    public static EventLogReadResult Read(string channel, string xpath, int maxEvents)
    {
        if (maxEvents <= 0) return EventLogReadResult.Empty;
        nint query;
        try
        {
            query = Wevtapi.EvtQuery(0, channel, xpath, Wevtapi.EvtQueryChannelPath | Wevtapi.EvtQueryReverseDirection);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new EventLogReadResult([], false, true);
        }

        if (query == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            return new EventLogReadResult([], error == Win32Errors.ERROR_ACCESS_DENIED,
                error is Wevtapi.ERROR_EVT_CHANNEL_NOT_FOUND or Win32Errors.ERROR_FILE_NOT_FOUND or Wevtapi.ERROR_EVT_INVALID_QUERY);
        }

        var results = new List<string>();
        try
        {
            var handles = new nint[BatchSize];
            while (results.Count < maxEvents)
            {
                if (!Wevtapi.EvtNext(query, BatchSize, handles, 1000, 0, out var returned))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == Win32Errors.ERROR_ACCESS_DENIED && results.Count == 0) return new EventLogReadResult([], true, false);
                    break; // ERROR_NO_MORE_ITEMS ou délai : fin de lecture.
                }

                for (var i = 0; i < returned; i++)
                {
                    try
                    {
                        if (results.Count < maxEvents && RenderXml(handles[i]) is { } xml) results.Add(xml);
                    }
                    finally
                    {
                        Wevtapi.EvtClose(handles[i]);
                    }
                }
            }
        }
        finally
        {
            Wevtapi.EvtClose(query);
        }
        return new EventLogReadResult(results, false, false);
    }

    private static string? RenderXml(nint handle)
    {
        Wevtapi.EvtRender(0, handle, Wevtapi.EvtRenderEventXml, 0, 0, out var used, out _);
        if (used <= 0 || used > 4 * 1024 * 1024) return null;
        var buffer = Marshal.AllocHGlobal(used);
        try
        {
            return Wevtapi.EvtRender(0, handle, Wevtapi.EvtRenderEventXml, used, buffer, out used, out _)
                ? Marshal.PtrToStringUni(buffer)?.TrimEnd('\0')
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
