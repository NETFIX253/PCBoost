using System.Buffers.Binary;

namespace PCBoost.Platform.Gaming;

/// <summary>
/// Protocole binaire du canal nommé Elevator → application : suite d'enregistrements de 12 octets,
/// (int32 PID, double horodatage en millisecondes), petit-boutiste.
/// </summary>
internal static class FrameRecordCodec
{
    public const int RecordSize = sizeof(int) + sizeof(double);

    public static void Write(Span<byte> destination, int processId, double timestampMs)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination, processId);
        BinaryPrimitives.WriteDoubleLittleEndian(destination[sizeof(int)..], timestampMs);
    }

    public static (int ProcessId, double TimestampMs) Read(ReadOnlySpan<byte> source)
        => (BinaryPrimitives.ReadInt32LittleEndian(source), BinaryPrimitives.ReadDoubleLittleEndian(source[sizeof(int)..]));

    public static byte[] Encode(IReadOnlyList<(int ProcessId, double TimestampMs)> records)
    {
        var buffer = new byte[records.Count * RecordSize];
        for (var i = 0; i < records.Count; i++)
            Write(buffer.AsSpan(i * RecordSize, RecordSize), records[i].ProcessId, records[i].TimestampMs);
        return buffer;
    }

    /// <summary>
    /// Décode les enregistrements complets de <paramref name="data"/> ; renvoie le nombre d'octets consommés
    /// (un enregistrement incomplet reste en attente de la lecture suivante).
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> data, int processIdFilter, List<double> timestamps)
    {
        var complete = data.Length / RecordSize * RecordSize;
        for (var offset = 0; offset < complete; offset += RecordSize)
        {
            var (pid, timestamp) = Read(data.Slice(offset, RecordSize));
            if (pid == processIdFilter && double.IsFinite(timestamp) && timestamp >= 0) timestamps.Add(timestamp);
        }
        return complete;
    }
}
