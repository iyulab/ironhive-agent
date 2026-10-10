using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using IronHive.Abstractions.Exceptions;
using IronHive.Agent.ErrorRecovery;
using IronProw.Core;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// The agent loop and the IronProw gateway read a provider refusal the same way. A status the gateway would retry, the
/// agent retries; a status the gateway would not retry on the same provider, the agent does not retry either — except
/// capacity (429/503), which a single provider can only wait out where the gateway would fall back. Before, the agent
/// kept its own status table and read only <see cref="HttpRequestException"/>, so the OpenAI SDK's
/// <see cref="ClientResultException"/> fell through to message heuristics and a provider's <c>Retry-After</c> was ignored.
/// </summary>
public class ErrorRecoveryGatewayParityTests
{
    public static TheoryData<int> Statuses => new() { 400, 401, 402, 403, 404, 408, 409, 422, 429, 500, 501, 502, 503, 504, 529 };

    [Theory]
    [MemberData(nameof(Statuses))]
    public void TheAgentRetriesExactlyWhatTheGatewayRetries_PlusCapacity(int status)
    {
        foreach (var exception in new Exception[]
        {
            new HttpRequestException("refused", null, (HttpStatusCode)status),
            new ClientResultException(new FakeResponse(status)),
        })
        {
            var gatewayRetries = new DefaultErrorClassifier().Classify(exception) == ErrorClassification.Retryable;
            var agentAction = new ErrorRecoveryService().AnalyzeException(exception).RecommendedAction;
            var agentRetries = agentAction is RecoveryAction.WaitAndRetry or RecoveryAction.Retry;

            Assert.Equal(gatewayRetries || status is 429 or 503 or 529, agentRetries);
        }
    }

    // A vendor error inside a started stream (IronHive's ProviderResponseException) carries the status the vendor
    // documents for it outside a stream. The agent reads it as that HTTP error - with no failure reader registered, as
    // the host builds this service - so Anthropic's mid-stream overloaded_error (529) waits like a 503 instead of
    // being read as invalid input, and OpenAI's server_error (500) is retried.
    [Theory]
    [MemberData(nameof(Statuses))]
    public void AMidStreamFailure_IsHandledAsItsEquivalentHttpError(int status)
    {
        var midStream = new ErrorRecoveryService().AnalyzeException(
            new ProviderResponseException("stream failed") { EquivalentStatusCode = (HttpStatusCode)status });
        var outsideStream = new ErrorRecoveryService().AnalyzeException(
            new HttpRequestException("refused", null, (HttpStatusCode)status));

        Assert.Equal(outsideStream.Error.Category, midStream.Error.Category);
        Assert.Equal(outsideStream.RecommendedAction, midStream.RecommendedAction);
        Assert.Equal(status, midStream.Error.HttpStatusCode);
    }

    [Theory]
    [InlineData(401, RecoveryAction.Escalate)]
    [InlineData(400, RecoveryAction.TryAlternative)]
    [InlineData(500, RecoveryAction.WaitAndRetry)]
    public void TheOpenAiSdkRefusal_IsReadByItsStatus(int status, RecoveryAction expected)
    {
        var analysis = new ErrorRecoveryService().AnalyzeException(new ClientResultException(new FakeResponse(status)));

        Assert.Equal(expected, analysis.RecommendedAction);
        Assert.Equal(status, analysis.Error.HttpStatusCode);
    }

    // An account that cannot pay does not recover by waiting: whether it arrives as IronHive's typed exception (OpenAI's
    // exhausted quota is a 429 on the wire), a bare 402, or only the vendor's code in a message, the agent stops and says
    // so instead of waiting out a «rate limit».
    [Fact]
    public void ABillingRefusal_EscalatesInsteadOfWaiting()
    {
        foreach (var exception in new Exception[]
        {
            new BillingException("You exceeded your current quota"),
            new ClientResultException(new FakeResponse(402)),
            new InvalidOperationException("insufficient_quota: You exceeded your current quota"),
        })
        {
            var analysis = new ErrorRecoveryService().AnalyzeException(exception);

            Assert.Equal(ErrorCategory.Billing, analysis.Error.Category);
            Assert.Equal(RecoveryAction.Escalate, analysis.RecommendedAction);
            Assert.Null(analysis.RetryDelay);
        }
    }

    [Fact]
    public void ARateLimitWithARetryHint_WaitsAsLongAsTheProviderAsked()
    {
        var analysis = new ErrorRecoveryService().AnalyzeException(
            new ClientResultException(new FakeResponse(429, ("retry-after-ms", "2500"))));

        Assert.Equal(RecoveryAction.WaitAndRetry, analysis.RecommendedAction);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), analysis.RetryDelay);
    }

    [Fact]
    public void ARetryHintIsCappedAtTheConfiguredMaximum()
    {
        var service = new ErrorRecoveryService(new ErrorRecoveryConfig { MaxRetryDelay = TimeSpan.FromSeconds(10) });

        var analysis = service.AnalyzeException(new ClientResultException(new FakeResponse(503, ("Retry-After", "120"))));

        Assert.Equal(TimeSpan.FromSeconds(10), analysis.RetryDelay);
    }

    [Fact]
    public void ARateLimitWithoutAHint_BacksOff()
    {
        var analysis = new ErrorRecoveryService().AnalyzeException(new ClientResultException(new FakeResponse(429)));

        Assert.Equal(TimeSpan.FromSeconds(1), analysis.RetryDelay);
    }

    [Fact]
    public void AnExtraFailureReader_IsAsked()
    {
        var service = new ErrorRecoveryService(failureReaders: [new FixedReader(new HttpFailure(401, null))]);

        var analysis = service.AnalyzeException(new InvalidOperationException("provider-specific refusal"));

        Assert.Equal(RecoveryAction.Escalate, analysis.RecommendedAction);
    }

    private sealed class FixedReader(HttpFailure failure) : IHttpFailureReader
    {
        public HttpFailure? Read(Exception exception) => failure;
    }

    private sealed class FakeResponse(int status, params (string Name, string Value)[] headers) : PipelineResponse
    {
        public override int Status => status;
        public override string ReasonPhrase => "fake";
        public override Stream? ContentStream { get; set; }
        public override BinaryData Content => BinaryData.FromString("{}");
        protected override PipelineResponseHeaders HeadersCore { get; } = new FakeHeaders(headers);
        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => new(Content);
        public override void Dispose() { }
    }

    private sealed class FakeHeaders((string Name, string Value)[] headers) : PipelineResponseHeaders
    {
        public override bool TryGetValue(string name, out string? value)
        {
            foreach (var (n, v) in headers)
            {
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = v;
                    return true;
                }
            }

            value = null;
            return false;
        }

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            var found = TryGetValue(name, out var value);
            values = found ? [value!] : null;
            return found;
        }

        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator()
            => headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).GetEnumerator();
    }
}
