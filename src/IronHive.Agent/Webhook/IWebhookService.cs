using System.Text.Json;
using System.Text.Json.Serialization;

namespace IronHive.Agent.Webhook;

/// <summary>
/// Event types that can trigger webhooks.
/// </summary>
public enum WebhookEventType
{
    /// <summary>Session started.</summary>
    SessionStarted,

    /// <summary>Session ended.</summary>
    SessionEnded,

    /// <summary>Tool execution started.</summary>
    ToolStarted,

    /// <summary>Tool execution completed.</summary>
    ToolCompleted,

    /// <summary>Tool execution failed.</summary>
    ToolFailed,

    /// <summary>Mode changed (Plan/Work/HITL).</summary>
    ModeChanged,

    /// <summary>HITL approval requested.</summary>
    ApprovalRequested,

    /// <summary>HITL approval granted.</summary>
    ApprovalGranted,

    /// <summary>HITL approval denied.</summary>
    ApprovalDenied,

    /// <summary>Error occurred.</summary>
    Error,

    /// <summary>Token limit warning.</summary>
    TokenLimitWarning,

    /// <summary>Cost limit warning.</summary>
    CostLimitWarning,

    /// <summary>Context compaction triggered.</summary>
    ContextCompacted,

    /// <summary>Agent completed task.</summary>
    TaskCompleted
}

/// <summary>
/// Webhook event payload.
/// </summary>
public class WebhookEvent
{
    /// <summary>
    /// Unique event ID.
    /// </summary>
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Event type.
    /// </summary>
    public WebhookEventType EventType { get; init; }

    /// <summary>
    /// Event timestamp.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Session ID (if applicable).
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Event-specific data.
    /// </summary>
    public Dictionary<string, object?> Data { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Serializes the event to JSON.
    /// </summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }
}

/// <summary>
/// Webhook delivery result.
/// </summary>
public record WebhookDeliveryResult
{
    /// <summary>
    /// Whether the delivery was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// HTTP status code (if applicable).
    /// </summary>
    public int? StatusCode { get; init; }

    /// <summary>
    /// Error message (if failed).
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Response time in milliseconds.
    /// </summary>
    public long ResponseTimeMs { get; init; }
}

/// <summary>
/// Service for sending webhook notifications.
/// </summary>
public interface IWebhookService
{
    /// <summary>
    /// Sends a webhook event to all configured endpoints.
    /// </summary>
    Task<IReadOnlyList<WebhookDeliveryResult>> SendAsync(
        WebhookEvent webhookEvent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an event of the specified type with optional data.
    /// </summary>
    Task<IReadOnlyList<WebhookDeliveryResult>> SendAsync(
        WebhookEventType eventType,
        string? sessionId = null,
        Dictionary<string, object?>? data = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if webhooks are configured.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Gets the number of configured endpoints.
    /// </summary>
    int EndpointCount { get; }
}
