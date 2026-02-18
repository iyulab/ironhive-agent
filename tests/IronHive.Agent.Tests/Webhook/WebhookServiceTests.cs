using System.Net;
using IronHive.Agent.Webhook;
using Microsoft.Extensions.Logging;

namespace IronHive.Agent.Tests.Webhook;

public class WebhookServiceTests
{
    [Fact]
    public void IsConfigured_NoEndpoints_ReturnsFalse()
    {
        var config = new WebhookConfig { Endpoints = [] };
        var service = new WebhookService(config);

        Assert.False(service.IsConfigured);
        Assert.Equal(0, service.EndpointCount);
    }

    [Fact]
    public void IsConfigured_WithEndpoints_ReturnsTrue()
    {
        var config = new WebhookConfig
        {
            Endpoints =
            [
                new WebhookEndpoint { Url = "https://example.com/webhook" }
            ]
        };
        var service = new WebhookService(config);

        Assert.True(service.IsConfigured);
        Assert.Equal(1, service.EndpointCount);
    }

    [Fact]
    public void IsConfigured_DisabledEndpoints_ReturnsFalse()
    {
        var config = new WebhookConfig
        {
            Endpoints =
            [
                new WebhookEndpoint { Url = "https://example.com/webhook", Enabled = false }
            ]
        };
        var service = new WebhookService(config);

        Assert.False(service.IsConfigured);
    }

    [Fact]
    public async Task SendAsync_NotConfigured_ReturnsEmpty()
    {
        var service = new WebhookService();

        var results = await service.SendAsync(WebhookEventType.SessionStarted);

        Assert.Empty(results);
    }

    [Fact]
    public void WebhookEvent_ToJson_SerializesCorrectly()
    {
        var webhookEvent = new WebhookEvent
        {
            EventType = WebhookEventType.ToolCompleted,
            SessionId = "test-session",
            Data = new Dictionary<string, object?>
            {
                ["toolName"] = "shell",
                ["duration"] = 100
            }
        };

        var json = webhookEvent.ToJson();

        Assert.Contains("\"eventType\":\"ToolCompleted\"", json);
        Assert.Contains("\"sessionId\":\"test-session\"", json);
        Assert.Contains("\"toolName\":\"shell\"", json);
    }

    [Fact]
    public void WebhookEvent_DefaultValues_ArePopulated()
    {
        var webhookEvent = new WebhookEvent
        {
            EventType = WebhookEventType.SessionStarted
        };

        Assert.NotNull(webhookEvent.EventId);
        Assert.NotEmpty(webhookEvent.EventId);
        Assert.True(webhookEvent.Timestamp <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void WebhookEndpoint_DefaultValues_AreCorrect()
    {
        var endpoint = new WebhookEndpoint { Url = "https://example.com" };

        Assert.True(endpoint.Enabled);
        Assert.Equal(30, endpoint.TimeoutSeconds);
        Assert.Equal(3, endpoint.RetryCount);
        Assert.Empty(endpoint.EventFilter);
        Assert.Empty(endpoint.Headers);
    }

    [Fact]
    public void WebhookConfig_DefaultValues_AreCorrect()
    {
        var config = new WebhookConfig();

        Assert.True(config.Enabled);
        Assert.True(config.AsyncDelivery);
        Assert.Equal(30, config.DefaultTimeoutSeconds);
        Assert.Empty(config.Endpoints);
    }

    [Fact]
    public async Task SendAsync_WithEventFilter_SkipsFilteredEvents()
    {
        var config = new WebhookConfig
        {
            AsyncDelivery = false, // Synchronous for testing
            Endpoints =
            [
                new WebhookEndpoint
                {
                    Url = "https://example.com/webhook",
                    EventFilter = [WebhookEventType.SessionStarted]
                }
            ]
        };

        // Create a mock handler that tracks calls
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        var service = new WebhookService(config, httpClient);

        // Send an event that should be filtered out
        await service.SendAsync(WebhookEventType.ToolCompleted);

        // Should not make any requests
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SendAsync_WithEventFilter_SendsMatchingEvents()
    {
        var config = new WebhookConfig
        {
            AsyncDelivery = false,
            Endpoints =
            [
                new WebhookEndpoint
                {
                    Url = "https://example.com/webhook",
                    EventFilter = [WebhookEventType.SessionStarted]
                }
            ]
        };

        var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        var service = new WebhookService(config, httpClient);

        await service.SendAsync(WebhookEventType.SessionStarted);

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SendAsync_WithSecret_AddsSignatureHeader()
    {
        var config = new WebhookConfig
        {
            AsyncDelivery = false,
            Endpoints =
            [
                new WebhookEndpoint
                {
                    Url = "https://example.com/webhook",
                    Secret = "test-secret"
                }
            ]
        };

        var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        var service = new WebhookService(config, httpClient);

        await service.SendAsync(WebhookEventType.SessionStarted);

        Assert.NotNull(handler.LastRequest);
        Assert.Contains("X-Webhook-Signature", handler.LastRequest.Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task SendAsync_WithCustomHeaders_IncludesHeaders()
    {
        var config = new WebhookConfig
        {
            AsyncDelivery = false,
            Endpoints =
            [
                new WebhookEndpoint
                {
                    Url = "https://example.com/webhook",
                    Headers = new Dictionary<string, string>
                    {
                        ["X-Custom-Header"] = "custom-value"
                    }
                }
            ]
        };

        var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        var service = new WebhookService(config, httpClient);

        await service.SendAsync(WebhookEventType.SessionStarted);

        Assert.NotNull(handler.LastRequest);
        Assert.Contains("X-Custom-Header", handler.LastRequest.Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task SendAsync_AsyncDeliveryFailure_LogsWarning()
    {
        var config = new WebhookConfig
        {
            AsyncDelivery = true,
            Endpoints =
            [
                new WebhookEndpoint
                {
                    Url = "https://example.com/webhook",
                    RetryCount = 0
                }
            ]
        };

        var handler = new FailingHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var logger = new TestLogger();
        var service = new WebhookService(config, httpClient, logger);

        // Fire-and-forget returns empty immediately
        var results = await service.SendAsync(WebhookEventType.SessionStarted);
        Assert.Empty(results);

        // Wait briefly for the fire-and-forget task to complete
        await Task.Delay(500);

        // Logger should have captured the warning
        Assert.True(logger.WarningLogged, "Expected a warning to be logged for async delivery failure");
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        public MockHttpMessageHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_statusCode));
        }
    }

    private sealed class FailingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Throw an unexpected exception type that DeliverToEndpointAsync won't catch,
            // causing the task to fault and trigger the ContinueWith(OnlyOnFaulted) path
            throw new InvalidOperationException("Simulated unexpected failure");
        }
    }

    private sealed class TestLogger : ILogger<WebhookService>
    {
        public bool WarningLogged { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                WarningLogged = true;
            }
        }
    }
}
