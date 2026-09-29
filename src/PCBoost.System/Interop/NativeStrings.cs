using System.Runtime.InteropServices;

namespace PCBoost.Platform.Interop;

internal static unsafe class NativeStrings
{
    /// <summary>Lit une chaîne UTF-16 terminée par un NUL dans un tampon de taille fixe.</summary>
    public static string FromFixedBuffer(char* buffer, int maxLength)
    {
        var length = 0;
        while (length < maxLength && buffer[length] != '\0') length++;
        return new string(buffer, 0, length);
    }

    public static string? FromNullTerminated(char* value)
        => value == null ? null : Marshal.PtrToStringUni((nint)value);
}
