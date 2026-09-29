using System.ComponentModel;
using PCBoost.Core.Common;

namespace PCBoost.Platform.Interop;

/// <summary>Codes d'erreur Win32 et classement en <see cref="OperationErrorKind"/>.</summary>
internal static class Win32Errors
{
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_HANDLE = 6;
    public const int ERROR_NOT_SUPPORTED = 50;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_LOCK_VIOLATION = 33;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_NO_MORE_ITEMS = 259;
    public const int ERROR_PARTIAL_COPY = 299;
    public const int ERROR_INVALID_WINDOW_HANDLE = 1400;
    public const int ERROR_CANCELLED = 1223;
    public const int ERROR_ELEVATION_REQUIRED = 740;

    public static OperationErrorKind Classify(int error) => error switch
    {
        ERROR_SUCCESS => OperationErrorKind.None,
        ERROR_ACCESS_DENIED => OperationErrorKind.AccessDenied,
        ERROR_ELEVATION_REQUIRED => OperationErrorKind.RequiresElevation,
        ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND => OperationErrorKind.NotFound,
        ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION => OperationErrorKind.InUse,
        ERROR_NOT_SUPPORTED => OperationErrorKind.NotSupported,
        ERROR_CANCELLED => OperationErrorKind.ElevationCancelled,
        ERROR_INVALID_PARAMETER => OperationErrorKind.InvalidInput,
        _ => OperationErrorKind.Failed,
    };

    public static string Describe(int error)
        => $"Win32 {error}: {new Win32Exception(error).Message}";

    public static OperationResult Fail(int error, TextRef? message = null)
        => OperationResult.Fail(Classify(error), message, Describe(error));

    public static OperationResult<T> Fail<T>(int error, TextRef? message = null)
        => OperationResult<T>.Fail(Classify(error), message, Describe(error));

    /// <summary>HRESULT_FROM_WIN32 inverse : extrait le code Win32 d'un HRESULT de type 0x8007xxxx.</summary>
    public static int FromHResult(int hresult)
        => (hresult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? hresult & 0xFFFF : hresult;
}
