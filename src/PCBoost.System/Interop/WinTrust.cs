using System.Runtime.InteropServices;

namespace PCBoost.Platform.Interop;

/// <summary>Vérification Authenticode (wintrust.dll) et catalogues de sécurité Windows.</summary>
internal static unsafe partial class WinTrust
{
    public static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    public static readonly Guid DRIVER_ACTION_VERIFY = new("F750E6C3-38EE-11d1-85E5-00C04FC295EE");

    public const uint WTD_UI_NONE = 2;
    public const uint WTD_REVOKE_NONE = 0;
    public const uint WTD_CHOICE_FILE = 1;
    public const uint WTD_CHOICE_CATALOG = 2;
    public const uint WTD_STATEACTION_VERIFY = 1;
    public const uint WTD_STATEACTION_CLOSE = 2;
    public const uint WTD_REVOCATION_CHECK_NONE = 0x00000010;
    public const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;
    public const uint WTD_UICONTEXT_EXECUTE = 0;

    public const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
    public const int TRUST_E_ACTION_UNKNOWN = unchecked((int)0x800B0002);
    public const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    public const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);

    /// <summary>INVALID_HANDLE_VALUE : aucun utilisateur interactif, aucune interface ne peut être affichée.</summary>
    public static readonly nint NoInteractiveUser = -1;

    [StructLayout(LayoutKind.Sequential)]
    public struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public char* pcwszFilePath;
        public nint hFile;
        public Guid* pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public char* pcwszCatalogFilePath;
        public char* pcwszMemberTag;
        public char* pcwszMemberFilePath;
        public nint hMemberFile;
        public byte* pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public nint pcCatalogContext;
        public nint hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINTRUST_DATA
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        /// <summary>Union : WINTRUST_FILE_INFO* ou WINTRUST_CATALOG_INFO*.</summary>
        public void* pUnion;
        public uint dwStateAction;
        public nint hWVTStateData;
        public char* pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CATALOG_INFO
    {
        public uint cbStruct;
        public fixed char wszCatalogFile[260];
    }

    [LibraryImport("wintrust.dll")]
    public static partial int WinVerifyTrust(nint hwnd, Guid* pgActionID, WINTRUST_DATA* pWVTData);

    [LibraryImport("wintrust.dll")]
    public static partial nint WTHelperProvDataFromStateData(nint hStateData);

    [LibraryImport("wintrust.dll")]
    public static partial nint WTHelperGetProvSignerFromChain(nint pProvData, uint idxSigner, [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint idxCounterSigner);

    [LibraryImport("wintrust.dll")]
    public static partial nint WTHelperGetProvCertFromChain(nint pSgnr, uint idxCert);

    [LibraryImport("wintrust.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptCATAdminAcquireContext2(out nint phCatAdmin, Guid* pgSubsystem, string? pwszHashAlgorithm, nint pStrongHashPolicy, uint dwFlags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptCATAdminReleaseContext(nint hCatAdmin, uint dwFlags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptCATAdminCalcHashFromFileHandle2(nint hCatAdmin, nint hFile, ref uint pcbHash, byte* pbHash, uint dwFlags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    public static partial nint CryptCATAdminEnumCatalogFromHash(nint hCatAdmin, byte* pbHash, uint cbHash, uint dwFlags, nint* phPrevCatInfo);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptCATCatalogInfoFromContext(nint hCatInfo, CATALOG_INFO* psCatInfo, uint dwFlags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptCATAdminReleaseCatalogContext(nint hCatAdmin, nint hCatInfo, uint dwFlags);
}

internal static unsafe partial class Crypt32
{
    public const uint CERT_NAME_SIMPLE_DISPLAY_TYPE = 4;

    [LibraryImport("crypt32.dll", EntryPoint = "CertGetNameStringW")]
    public static partial uint CertGetNameString(nint pCertContext, uint dwType, uint dwFlags, void* pvTypePara, char* pszNameString, uint cchNameString);
}
