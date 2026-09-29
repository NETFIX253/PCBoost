using PCBoost.Platform.Interop;

namespace PCBoost.Platform;

/// <summary>Requête PDH (compteurs ajoutés par leur nom anglais). Les compteurs absents ne sont simplement pas créés.</summary>
internal sealed class PdhQuery : IDisposable
{
    private readonly SafePdhQueryHandle _handle;

    private PdhQuery(SafePdhQueryHandle handle) => _handle = handle;

    public static PdhQuery? TryOpen()
    {
        var status = Pdh.PdhOpenQuery(null, 0, out var handle);
        if (status != Pdh.ERROR_SUCCESS || handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return new PdhQuery(handle);
    }

    public PdhCounter? TryAdd(string englishPath)
    {
        var status = Pdh.PdhAddEnglishCounter(_handle, englishPath, 0, out var counter);
        return status == Pdh.ERROR_SUCCESS && counter != 0 ? new PdhCounter(counter, englishPath) : null;
    }

    public static void Remove(PdhCounter? counter)
    {
        if (counter is not null) Pdh.PdhRemoveCounter(counter.Handle);
    }

    /// <summary>Collecte un échantillon. Les compteurs de débit nécessitent deux collectes pour être valides.</summary>
    public bool Collect() => Pdh.PdhCollectQueryData(_handle) == Pdh.ERROR_SUCCESS;

    public void Dispose() => _handle.Dispose();
}

internal sealed unsafe class PdhCounter
{
    public PdhCounter(nint handle, string path)
    {
        Handle = handle;
        Path = path;
    }

    public nint Handle { get; }

    public string Path { get; }

    /// <summary>Valeur formatée d'un compteur à instance unique, ou null si la donnée n'est pas (encore) valide.</summary>
    public double? ReadDouble(bool noCap100 = true)
    {
        var format = Pdh.PDH_FMT_DOUBLE | (noCap100 ? Pdh.PDH_FMT_NOCAP100 : 0);
        var status = Pdh.PdhGetFormattedCounterValue(Handle, format, out _, out var value);
        if (status != Pdh.ERROR_SUCCESS || !IsValid(value.CStatus)) return null;
        return double.IsFinite(value.doubleValue) ? value.doubleValue : null;
    }

    /// <summary>Valeurs valides de toutes les instances d'un compteur générique (« * »). Null si aucune n'est valide.</summary>
    public List<double>? ReadInstances(bool noCap100 = true)
    {
        var format = Pdh.PDH_FMT_DOUBLE | (noCap100 ? Pdh.PDH_FMT_NOCAP100 : 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint size = 0;
            var status = Pdh.PdhGetFormattedCounterArray(Handle, format, ref size, out _, null);
            if (status != Pdh.PDH_MORE_DATA || size == 0) return null;

            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                status = Pdh.PdhGetFormattedCounterArray(Handle, format, ref size, out var count, p);
                if (status == Pdh.PDH_MORE_DATA) continue; // Les instances ont changé entre les deux appels.
                if (status != Pdh.ERROR_SUCCESS) return null;

                var items = (Pdh.PDH_FMT_COUNTERVALUE_ITEM_W*)p;
                var values = new List<double>((int)count);
                for (var i = 0; i < count; i++)
                {
                    var value = items[i].FmtValue;
                    if (IsValid(value.CStatus) && double.IsFinite(value.doubleValue)) values.Add(value.doubleValue);
                }
                return values.Count > 0 ? values : null;
            }
        }
        return null;
    }

    private static bool IsValid(uint cstatus)
        => cstatus is Pdh.PDH_CSTATUS_VALID_DATA or Pdh.PDH_CSTATUS_NEW_DATA;
}
