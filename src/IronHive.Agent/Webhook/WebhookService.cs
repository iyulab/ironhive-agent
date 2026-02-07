using System.Diagnostics;
using System.Text;

namespace IronHive.Agent.Webhook;

/// <summary>
/// Configuration for a webhook endpoint.
/// </summary>
public class WebhookEndpoint
{
    /// <summary>
    /// Endpoint URL.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Optional secret for signing requests (HMAC-SHA256).
    /// </summary>
    public string? Secret { get; init; }

    /// <summary>
    /// Event types to filter (empty = all events).
    /// </summary>
    public HashSet<WebhookEventType> EventFilter { get; init; } = [];

    /// <summary>
    /// Custom headers to include.
    /// </summary>
    public Dictionary<string, string> Headers { get; init; } = [];

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Number of retry attempts on failure.
    /// </summary>
    public int RetryCount { get; init; } = 3;

    /// <summary>
    /// Whether this endpoint is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Webhook configuration.
/// </summary>
public class WebhookConfig
{
    /// <summary>
    /// Whether webhooks are enabled globally.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Configured webhook endpoints.
    /// </summary>
    public List<WebhookEndpoint> Endpoints { get; init; } = [];

    /// <summary>
    /// Global timeout in seconds (overridden by endpoint-specific settings).
    /// </summary>
    public int DefaultTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Whether to send webhooks asynchronously (fire-and-forget).
    /// </summary>
    public bool AsyncDelivery { get; init; } = true;
}

/// <summary>
/// HTTP-based webhook service implementation.
/// </summary>
public class WebhookService : IWebhookService, IDisposable
{
    private readonly WebhookConfig _config;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly List<WebhookEndpoint> _activeEndpoints;

    public WebhookService(WebhookConfig? config = null, HttpClient? httpClient = null)
    {
        _config = config ?? new WebhookConfig();
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _activeEndpoints = _config.Endpoints
            .Where(e => e.Enabled && !string.IsNullOrEmpty(e.Url))
            .ToList();
    }

    /// <inheritdoc />
    public bool IsConfigured => _config.Enabled && _activeEndpoints.Count > 0;

    /// <inheritdoc />
    public int EndpointCount => _activeEndpoints.Count;

    /// <inheritdoc />
    public Task<IReadOnlyList<WebhookDeliveryResult>> SendAsync(
        WebhookEventType eventType,
        string? sessionId = null,
        Dictionary<string, object?>? data = null,
        CancellationToken cancellationToken = default)
    {
        var webhookEvent = new WebhookEvent
        {
            EventType = eventType,
            SessionId = sessionId,
            Data = data ?? []
        };

        return SendAsync(webhookEvent, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebhookDeliveryResult>> SendAsync(
        WebhookEvent webhookEvent,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return [];
        }

        var results = new List<WebhookDeliveryResult>();
        var tasks = new List<Task<WebhookDeliveryResult>>();

        foreach (var endpoint in _activeEndpoints)
        {
            // Skip if event type is filtered
            if (endpoint.EventFilter.Count > 0 &&
                !endpoint.EventFilter.Contains(webhookEvent.EventType))
            {
                continue;
            }

            tasks.Add(DeliverToEndpointAsync(endpoint, webhookEvent, cancellationToken));
        }

        if (_config.AsyncDelivery)
        {
            // Fire-and-forget: start tasks but observe exceptions
            _ = Task.WhenAll(tasks).ContinueWith(
                t => Debug.WriteLine($"Webhook delivery failed: {t.Exception?.Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
            return [];
        }
        else
        {
            // Wait for all deliveries
            var completed = await Task.WhenAll(tasks);
            results.AddRange(completed);
        }

        return results;
    }

    private async Task<WebhookDeliveryResult> DeliverToEndpointAsync(
        WebhookEndpoint endpoint,
        WebhookEvent webhookEvent,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var retryCount = 0;
        Exception? lastError = null;

        while (retryCount <= endpoint.RetryCount)
        {
            try
            {
                var result = await TrySendAsync(endpoint, webhookEvent, cancellationToken);
                stopwatch.Stop();

                if (result.Success)
                {
                    return result with { ResponseTimeMs = stopwatch.ElapsedMilliseconds };
                }

                lastError = new HttpRequestException($"HTTP {result.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastError = ex;
            }

            retryCount++;
            if (retryCount <= endpoint.RetryCount)
            {
                // Exponential backoff: 1s, 2s, 4s...
                var delay = TimeSpan.FromSeconds(Math.Pow(2, retryCount - 1));
                await Task.Delay(delay, cancellationToken);
            }
        }

        stopwatch.Stop();
        return new WebhookDeliveryResult
        {
            Success = false,
            Error = lastError?.Message ?? "Unknown error",
            ResponseTimeMs = stopwatch.ElapsedMilliseconds
        };
    }

    private async Task<WebhookDeliveryResult> TrySendAsync(
        WebhookEndpoint endpoint,
        WebhookEvent webhookEvent,
        CancellationToken cancellationToken)
    {
        var json = webhookEvent.ToJson();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
        {
            Content = content
        };

        // Add custom headers
        foreach (var header in endpoint.Headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Add signature if secret is configured
        if (!string.IsNullOrEmpty(endpoint.Secret))
        {
            var signature = ComputeSignature(json, endpoint.Secret);
            request.Headers.TryAddWithoutValidation("X-Webhook-Signature", signature);
        }

        // Add standard headers
        request.Headers.TryAddWithoutValidation("X-Event-Type", webhookEvent.EventType.ToString());
        request.Headers.TryAddWithoutValidation("X-Event-Id", webhookEvent.EventId);

        var timeout = TimeSpan.FromSeconds(
            endpoint.TimeoutSeconds > 0 ? endpoint.TimeoutSeconds : _config.DefaultTimeoutSeconds);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var response = await _httpClient.SendAsync(request, cts.Token);

        return new WebhookDeliveryResult
        {
            Success = response.IsSuccessStatusCode,
            StatusCode = (int)response.StatusCode
        };
    }

    private static string ComputeSignature(string payload, string secret)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
