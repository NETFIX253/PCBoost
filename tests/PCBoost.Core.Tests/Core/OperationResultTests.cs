using System.ComponentModel;
using System.Security;
using PCBoost.Core.Common;

namespace PCBoost.Core.Tests.Domain;

public sealed class OperationResultTests
{
    public static TheoryData<Exception, OperationErrorKind> Classifications => new()
    {
        { new OperationCanceledException(), OperationErrorKind.Cancelled },
        { new TaskCanceledException(), OperationErrorKind.Cancelled },
        { new UnauthorizedAccessException(), OperationErrorKind.AccessDenied },
        { new SecurityException(), OperationErrorKind.AccessDenied },
        { new FileNotFoundException(), OperationErrorKind.NotFound },
        { new DirectoryNotFoundException(), OperationErrorKind.NotFound },
        { new PathTooLongException(), OperationErrorKind.InvalidInput },
        { new TimeoutException(), OperationErrorKind.Timeout },
        { new PlatformNotSupportedException(), OperationErrorKind.NotSupported },
        { new NotSupportedException(), OperationErrorKind.NotSupported },
        { new Win32Exception(5), OperationErrorKind.AccessDenied },
        { new Win32Exception(1223), OperationErrorKind.ElevationCancelled },
        { new Win32Exception(2), OperationErrorKind.NotFound },
        { new Win32Exception(3), OperationErrorKind.NotFound },
        { new Win32Exception(1), OperationErrorKind.Failed },
        { new IOException("sharing violation", unchecked((int)0x80070020)), OperationErrorKind.InUse },
        { new IOException("lock violation", unchecked((int)0x80070021)), OperationErrorKind.InUse },
        { new IOException("disk full", unchecked((int)0x80070070)), OperationErrorKind.Failed },
        { new ArgumentException("bad"), OperationErrorKind.InvalidInput },
        { new ArgumentNullException("x"), OperationErrorKind.InvalidInput },
        { new InvalidOperationException(), OperationErrorKind.Failed },
    };

    [Theory]
    [MemberData(nameof(Classifications))]
    public void ClassifyException_maps_expected_failures(Exception exception, OperationErrorKind expected)
        => Assert.Equal(expected, OperationResult.ClassifyException(exception));

    [Fact]
    public void FromException_is_a_failure_with_technical_detail_only()
    {
        var result = OperationResult.FromException(new UnauthorizedAccessException("Accès refusé."));

        Assert.False(result.Success);
        Assert.Equal(OperationErrorKind.AccessDenied, result.Error);
        Assert.Null(result.Message);
        Assert.Contains("UnauthorizedAccessException", result.TechnicalDetail);
        Assert.Contains("HRESULT 0x", result.TechnicalDetail);
    }

    [Fact]
    public void FromException_rejects_null()
        => Assert.Throws<ArgumentNullException>(() => OperationResult.FromException(null!));

    [Fact]
    public void Ok_and_Fail_factories()
    {
        var ok = OperationResult.Ok(TextRef.Of("Done"));
        var fail = OperationResult.Fail(OperationErrorKind.InUse, TextRef.Of("Busy"), "detail");

        Assert.True(ok.Success);
        Assert.Equal(OperationErrorKind.None, ok.Error);
        Assert.Equal("Done", ok.Message?.Key);
        Assert.False(fail.Success);
        Assert.Equal(OperationErrorKind.InUse, fail.Error);
        Assert.Equal("detail", fail.TechnicalDetail);
    }

    [Fact]
    public void Typed_result_converts_and_classifies()
    {
        var ok = OperationResult<int>.Ok(42);
        var failed = OperationResult<int>.FromException(new TimeoutException());

        Assert.True(ok.Success);
        Assert.Equal(42, ok.Value);
        Assert.True(ok.ToResult().Success);
        Assert.False(failed.Success);
        Assert.Equal(0, failed.Value);
        Assert.Equal(OperationErrorKind.Timeout, failed.Error);
        Assert.Equal(OperationErrorKind.Timeout, failed.ToResult().Error);
    }
}
