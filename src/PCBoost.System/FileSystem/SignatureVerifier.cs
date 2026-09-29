using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Models.Processes;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform;

/// <summary>
/// Vérification Authenticode par WinVerifyTrust (WINTRUST_ACTION_GENERIC_VERIFY_V2), sans interface, sans vérification
/// de révocation en ligne (WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL). Les fichiers sans signature
/// intégrée sont recherchés dans les catalogues de sécurité Windows (la plupart des binaires du système).
/// Résultats mis en cache par chemin et date de modification.
/// </summary>
public sealed unsafe class SignatureVerifier : ISignatureVerifier
{
    private const int MaxCacheEntries = 4096;

    private readonly ILogger<SignatureVerifier> _logger;
    private readonly ConcurrentDictionary<(string Path, DateTime LastWrite), SignatureInfo> _cache = new();

    public SignatureVerifier(ILogger<SignatureVerifier>? logger = null)
    {
        _logger = logger ?? NullLogger<SignatureVerifier>.Instance;
    }

    public SignatureInfo Verify(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return SignatureInfo.Unknown;
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return SignatureInfo.Unknown;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return SignatureInfo.Unknown;
        }

        var key = (info.FullName.ToUpperInvariant(), info.LastWriteTimeUtc);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        SignatureInfo result;
        try
        {
            result = VerifyCore(info.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug("Vérification de signature impossible ({Error})", ex.GetType().Name);
            result = SignatureInfo.Unknown;
        }

        if (_cache.Count >= MaxCacheEntries) _cache.Clear();
        _cache[key] = result;
        return result;
    }

    private static SignatureInfo VerifyCore(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
        var handle = stream.SafeFileHandle;

        var embedded = VerifyEmbedded(path, handle, out var embeddedSigner);
        if (embedded == 0) return new SignatureInfo(SignatureStatus.Signed, embeddedSigner);
        if (!IsNoSignature(embedded)) return new SignatureInfo(SignatureStatus.Invalid, embeddedSigner);

        // Pas de signature intégrée : recherche du fichier dans les catalogues (SHA-256 puis SHA-1 pour les anciens catalogues).
        foreach (var algorithm in new[] { "SHA256", "SHA1" })
        {
            var catalogResult = VerifyByCatalog(path, handle, algorithm, out var catalogSigner, out var catalogFound);
            if (!catalogFound) continue;
            return catalogResult == 0
                ? new SignatureInfo(SignatureStatus.Signed, catalogSigner)
                : new SignatureInfo(SignatureStatus.Invalid, catalogSigner);
        }
        return new SignatureInfo(SignatureStatus.Unsigned, null);
    }

    private static bool IsNoSignature(int result)
        => result is WinTrust.TRUST_E_NOSIGNATURE or WinTrust.TRUST_E_SUBJECT_FORM_UNKNOWN or WinTrust.TRUST_E_PROVIDER_UNKNOWN;

    private static int VerifyEmbedded(string path, SafeFileHandle file, out string? signer)
    {
        signer = null;
        fixed (char* pPath = path)
        {
            var fileInfo = new WinTrust.WINTRUST_FILE_INFO
            {
                cbStruct = (uint)sizeof(WinTrust.WINTRUST_FILE_INFO),
                pcwszFilePath = pPath,
                hFile = file.DangerousGetHandle(),
                pgKnownSubject = null,
            };
            var data = CreateTrustData(WinTrust.WTD_CHOICE_FILE, &fileInfo);
            return RunVerification(&data, out signer);
        }
    }

    private static int VerifyByCatalog(string path, SafeFileHandle file, string algorithm, out string? signer, out bool catalogFound)
    {
        signer = null;
        catalogFound = false;
        var subsystem = WinTrust.DRIVER_ACTION_VERIFY;
        if (!WinTrust.CryptCATAdminAcquireContext2(out var catAdmin, &subsystem, algorithm, 0, 0) || catAdmin == 0)
            return WinTrust.TRUST_E_NOSIGNATURE;

        nint catInfo = 0;
        try
        {
            // WinVerifyTrust a pu déplacer le pointeur du fichier : le hachage doit partir du début.
            Kernel32.SetFilePointerEx(file, 0, null, Kernel32.FILE_BEGIN);
            uint hashSize = 0;
            WinTrust.CryptCATAdminCalcHashFromFileHandle2(catAdmin, file.DangerousGetHandle(), ref hashSize, null, 0);
            if (hashSize == 0 || hashSize > 64) return WinTrust.TRUST_E_NOSIGNATURE;
            var hash = stackalloc byte[(int)hashSize];
            if (!WinTrust.CryptCATAdminCalcHashFromFileHandle2(catAdmin, file.DangerousGetHandle(), ref hashSize, hash, 0))
                return WinTrust.TRUST_E_NOSIGNATURE;

            catInfo = WinTrust.CryptCATAdminEnumCatalogFromHash(catAdmin, hash, hashSize, 0, null);
            if (catInfo == 0) return WinTrust.TRUST_E_NOSIGNATURE;

            var catalog = new WinTrust.CATALOG_INFO { cbStruct = (uint)sizeof(WinTrust.CATALOG_INFO) };
            if (!WinTrust.CryptCATCatalogInfoFromContext(catInfo, &catalog, 0)) return WinTrust.TRUST_E_NOSIGNATURE;
            catalogFound = true;

            var catalogPath = NativeStrings.FromFixedBuffer(catalog.wszCatalogFile, 260);
            var memberTag = Convert.ToHexString(new ReadOnlySpan<byte>(hash, (int)hashSize));
            fixed (char* pCatalog = catalogPath)
            fixed (char* pTag = memberTag)
            fixed (char* pPath = path)
            {
                var catalogInfo = new WinTrust.WINTRUST_CATALOG_INFO
                {
                    cbStruct = (uint)sizeof(WinTrust.WINTRUST_CATALOG_INFO),
                    dwCatalogVersion = 0,
                    pcwszCatalogFilePath = pCatalog,
                    pcwszMemberTag = pTag,
                    pcwszMemberFilePath = pPath,
                    hMemberFile = file.DangerousGetHandle(),
                    pbCalculatedFileHash = hash,
                    cbCalculatedFileHash = hashSize,
                    pcCatalogContext = 0,
                    hCatAdmin = catAdmin,
                };
                var data = CreateTrustData(WinTrust.WTD_CHOICE_CATALOG, &catalogInfo);
                return RunVerification(&data, out signer);
            }
        }
        finally
        {
            if (catInfo != 0) WinTrust.CryptCATAdminReleaseCatalogContext(catAdmin, catInfo, 0);
            WinTrust.CryptCATAdminReleaseContext(catAdmin, 0);
        }
    }

    private static WinTrust.WINTRUST_DATA CreateTrustData(uint unionChoice, void* union) => new()
    {
        cbStruct = (uint)sizeof(WinTrust.WINTRUST_DATA),
        dwUIChoice = WinTrust.WTD_UI_NONE,
        fdwRevocationChecks = WinTrust.WTD_REVOKE_NONE,
        dwUnionChoice = unionChoice,
        pUnion = union,
        dwStateAction = WinTrust.WTD_STATEACTION_VERIFY,
        dwProvFlags = WinTrust.WTD_REVOCATION_CHECK_NONE | WinTrust.WTD_CACHE_ONLY_URL_RETRIEVAL,
        dwUIContext = WinTrust.WTD_UICONTEXT_EXECUTE,
    };

    /// <summary>Vérifie, lit le signataire depuis les données d'état, puis ferme toujours l'état (WTD_STATEACTION_CLOSE).</summary>
    private static int RunVerification(WinTrust.WINTRUST_DATA* data, out string? signer)
    {
        signer = null;
        var action = WinTrust.WINTRUST_ACTION_GENERIC_VERIFY_V2;
        var result = WinTrust.WinVerifyTrust(WinTrust.NoInteractiveUser, &action, data);
        try
        {
            if (data->hWVTStateData != 0) signer = ReadSigner(data->hWVTStateData);
        }
        finally
        {
            data->dwStateAction = WinTrust.WTD_STATEACTION_CLOSE;
            WinTrust.WinVerifyTrust(WinTrust.NoInteractiveUser, &action, data);
        }
        return result;
    }

    private static string? ReadSigner(nint stateData)
    {
        var provider = WinTrust.WTHelperProvDataFromStateData(stateData);
        if (provider == 0) return null;
        var signerInfo = WinTrust.WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signerInfo == 0) return null;
        var providerCert = WinTrust.WTHelperGetProvCertFromChain(signerInfo, 0);
        if (providerCert == 0) return null;
        // CRYPT_PROVIDER_CERT { DWORD cbStruct; PCCERT_CONTEXT pCert; … } : pCert suit cbStruct aligné sur un pointeur.
        var certContext = *(nint*)((byte*)providerCert + sizeof(nint));
        if (certContext == 0) return null;

        var length = Crypt32.CertGetNameString(certContext, Crypt32.CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, null, null, 0);
        if (length <= 1 || length > 1024) return null;
        var buffer = stackalloc char[(int)length];
        var written = Crypt32.CertGetNameString(certContext, Crypt32.CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, null, buffer, length);
        return written > 1 ? new string(buffer, 0, (int)written - 1) : null;
    }
}
