using System.ComponentModel;

namespace PCBoost.Core.Common;

public enum OperationErrorKind
{
    None = 0,
    AccessDenied,
    RequiresElevation,
    ElevationCancelled,
    NotFound,
    NotSupported,
    Cancelled,
    InUse,
    Blocked,
    Timeout,
    InvalidInput,
    Failed,
}

/// <summary>
/// Résultat d'une opération système. Ne lève pas d'exception pour les échecs attendus
/// (permission refusée, fichier verrouillé…) : l'UI traduit <see cref="Error"/> en message humain (§79),
/// <see cref="TechnicalDetail"/> n'est affiché qu'en mode Expert.
/// </summary>
public sealed record OperationResult(
    bool Success,
    OperationErrorKind Error = OperationErrorKind.None,
    TextRef? Message = null,
    string? TechnicalDetail = null)
{
    public static OperationResult Ok(TextRef? message = null) => new(true, OperationErrorKind.None, message);

    public static OperationResult Fail(OperationErrorKind error, TextRef? message = null, string? technicalDetail = null)
        => new(false, error, message, technicalDetail);

    public static OperationResult FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var kind = ClassifyException(exception);
        return new(false, kind, null, $"{exception.GetType().Name}: {exception.Message} (HRESULT 0x{exception.HResult:X8})");
    }

    public static OperationErrorKind ClassifyException(Exception exception) => exception switch
    {
        OperationCanceledException => OperationErrorKind.Cancelled,
        UnauthorizedAccessException => OperationErrorKind.AccessDenied,
        System.Security.SecurityException => OperationErrorKind.AccessDenied,
        FileNotFoundException => OperationErrorKind.NotFound,
        DirectoryNotFoundException => OperationErrorKind.NotFound,
        PathTooLongException => OperationErrorKind.InvalidInput,
        TimeoutException => OperationErrorKind.Timeout,
        PlatformNotSupportedException => OperationErrorKind.NotSupported,
        NotSupportedException => OperationErrorKind.NotSupported,
        Win32Exception { NativeErrorCode: 5 } => OperationErrorKind.AccessDenied,
        Win32Exception { NativeErrorCode: 1223 } => OperationErrorKind.ElevationCancelled,
        Win32Exception { NativeErrorCode: 2 or 3 } => OperationErrorKind.NotFound,
        IOException io when IsSharingViolation(io) => OperationErrorKind.InUse,
        ArgumentException => OperationErrorKind.InvalidInput,
        _ => OperationErrorKind.Failed,
    };

    private static bool IsSharingViolation(IOException exception)
    {
        // ERROR_SHARING_VIOLATION (32) / ERROR_LOCK_VIOLATION (33)
        var code = exception.HResult & 0xFFFF;
        return code is 32 or 33;
    }
}

/// <summary>Résultat typé.</summary>
public sealed record OperationResult<T>(
    bool Success,
    T? Value,
    OperationErrorKind Error = OperationErrorKind.None,
    TextRef? Message = null,
    string? TechnicalDetail = null)
{
    public static OperationResult<T> Ok(T value) => new(true, value);

    public static OperationResult<T> Fail(OperationErrorKind error, TextRef? message = null, string? technicalDetail = null)
        => new(false, default, error, message, technicalDetail);

    public static OperationResult<T> FromException(Exception exception)
    {
        var r = OperationResult.FromException(exception);
        return new(false, default, r.Error, r.Message, r.TechnicalDetail);
    }

    public OperationResult ToResult() => new(Success, Error, Message, TechnicalDetail);
}
