using IronHive.Abstractions.Exceptions;
using IronHive.Agent.ErrorRecovery;
using System.Net.Sockets;

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

    // The shape the runtime hands over for a reset differs by operating system: an HttpIOException on one, a plain
    // IOException over a SocketException on another (errno 54 macOS, 104 Linux, 10054 Windows). The category follows the
    // exception type chain, so every shape - bare, wrapped once, wrapped twice - reads the same on every machine.
    public static TheoryData<Exception> TransportResetShapes()
    {
        var data = new TheoryData<Exception>();
        foreach (var errno in new[] { 54, 104, 10054 })
        {
            data.Add(new IOException("Unable to read data from the transport connection: Connection reset by peer.", new SocketException(errno)));
            data.Add(new InvalidOperationException("turn failed",
                new IOException("Unable to read data from the transport connection.", new SocketException(errno))));
            data.Add(new InvalidOperationException("turn failed",
                new InvalidOperationException("stream failed",
                    new IOException("Unable to read data from the transport connection.", new SocketException(errno)))));
        }

        data.Add(new IOException("Unable to read data from the transport connection.", new HttpRequestException("The SSL connection could not be established.")));
        data.Add(new SocketException(54));
        return data;
    }

    [Theory]
    [MemberData(nameof(TransportResetShapes))]
    public void ATransportFailure_IsNetwork_WhateverShapeTheOperatingSystemHandsOver(Exception exception)
        => Assert.Equal(ErrorCategory.Network, new ErrorRecoveryService().AnalyzeException(exception).Error.Category);

    // Positive controls: an IOException with no transport exception in its chain is still a file-system failure.
    [Fact]
    public void AnIOExceptionOverAnotherIOException_StaysFileSystem()
        => Assert.Equal(ErrorCategory.FileSystem,
            new ErrorRecoveryService().AnalyzeException(new IOException("copy failed", new IOException("disk full"))).Error.Category);

    [Fact]
    public void AFileNotFound_StaysFileSystem()
        => Assert.Equal(ErrorCategory.FileSystem,
            new ErrorRecoveryService().AnalyzeException(new FileNotFoundException("missing")).Error.Category);

    // Positive control: an IOException that is not an HTTP response stream is still a file-system failure.
    [Fact]
    public void AnOrdinaryIOException_StaysFileSystem()
        => Assert.Equal(ErrorCategory.FileSystem,
            new ErrorRecoveryService().AnalyzeException(new IOException("disk full")).Error.Category);
}
