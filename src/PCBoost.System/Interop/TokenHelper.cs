using System.Security.Principal;

namespace PCBoost.Platform.Interop;

/// <summary>Lecture des jetons d'accès (élévation du processus courant, utilisateur propriétaire d'un processus).</summary>
internal static unsafe class TokenHelper
{
    private static readonly Lazy<bool> CurrentElevated = new(ReadCurrentProcessElevation);
    private static readonly Lazy<SecurityIdentifier?> CurrentUser = new(ReadCurrentUserSid);

    /// <summary>Le processus courant s'exécute-t-il avec un jeton élevé (TokenElevation) ?</summary>
    public static bool IsCurrentProcessElevated => CurrentElevated.Value;

    public static SecurityIdentifier? CurrentUserSid => CurrentUser.Value;

    private static bool ReadCurrentProcessElevation()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var opened = Advapi32.OpenProcessToken(Kernel32.GetCurrentProcess(), Advapi32.TOKEN_QUERY, out var token);
        using (token)
        {
            if (!opened) return false;
            Advapi32.TOKEN_ELEVATION elevation;
            return Advapi32.GetTokenInformation(token, Advapi32.TokenElevation, &elevation, (uint)sizeof(Advapi32.TOKEN_ELEVATION), out _)
                && elevation.TokenIsElevated != 0;
        }
    }

    private static SecurityIdentifier? ReadCurrentUserSid()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>SID de l'utilisateur du jeton d'un processus ouvert, ou null si le jeton est inaccessible.</summary>
    public static SecurityIdentifier? GetProcessUserSid(SafeKernelHandle process)
    {
        var opened = Advapi32.OpenProcessToken(process, Advapi32.TOKEN_QUERY, out var token);
        using (token)
        {
            if (!opened) return null;
            Advapi32.GetTokenInformation(token, Advapi32.TokenUser, null, 0, out var needed);
            if (needed == 0 || needed > 4096) return null;
            var buffer = stackalloc byte[(int)needed];
            if (!Advapi32.GetTokenInformation(token, Advapi32.TokenUser, buffer, needed, out _))
                return null;
            // TOKEN_USER { SID_AND_ATTRIBUTES User { PSID Sid; DWORD Attributes; } } : le pointeur SID est en tête.
            var sid = *(nint*)buffer;
            if (sid == 0) return null;
            try
            {
                return new SecurityIdentifier(sid);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
