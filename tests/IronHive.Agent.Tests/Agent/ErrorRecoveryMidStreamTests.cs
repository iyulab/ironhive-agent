using IronHive.Abstractions.Exceptions;
using IronHive.Agent.ErrorRecovery;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// A stream that fails after it started is a failure a retry can clear. IronHive raises it as
/// <see cref="ProviderResponseException"/> (a stream without its completion signal, or a vendor error with no documented
/// status), and a connection reset mid-stream surfaces as <see cref="HttpIOException"/>. Before, the first read as
/// <see cref="ErrorCategory.Unknown"/> and the second — an <see cref="IOException"/> — as
/// <see cref="ErrorCategory.FileSystem"/>, so a client was told about a disk problem.
/// </summary>
public class ErrorRecoveryMidStreamTests
{
    public static TheoryData<Exception> MidStreamFailures => new()
    {
        new ProviderResponseException("The chat completion stream ended without a finish_reason; the response is incomplete."),
        new ProviderResponseException("Upstream failed") { ErrorCode = "upstream_failure" },
        new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."),
        new InvalidOperationException("turn failed", new HttpIOException(HttpRequestError.ResponseEnded)),
    };

    [Theory]
    [MemberData(nameof(MidStreamFailures))]
    public void AFailureAfterTheStreamStarted_IsNetwork(Exception exception)
    {
        var analysis = new ErrorRecoveryService().AnalyzeException(exception);

        Assert.Equal(ErrorCategory.Network, analysis.Error.Category);
    }

    // Positive control: an IOException that is not an HTTP response stream is still a file-system failure.
    [Fact]
    public void AnOrdinaryIOException_StaysFileSystem()
        => Assert.Equal(ErrorCategory.FileSystem,
            new ErrorRecoveryService().AnalyzeException(new IOException("disk full")).Error.Category);
}
