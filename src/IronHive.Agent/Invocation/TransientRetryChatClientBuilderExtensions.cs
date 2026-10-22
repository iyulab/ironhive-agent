using IronHive.Agent.ErrorRecovery;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Invocation;

/// <summary>Installs a <see cref="TransientRetryChatClient"/> on a chat client pipeline.</summary>
public static class TransientRetryChatClientBuilderExtensions
{
    /// <summary>
    /// Adds <see cref="TransientRetryChatClient"/>: a model call that fails for a transient reason (a 502/504 from a gateway,
    /// a refused or reset connection, a stream that ended before its first update) is tried again instead of ending the turn.
    /// Call it last, so it sits directly around the provider client and inside function invocation, where each model call
    /// of a turn is retried on its own: <c>builder.UseToolInvocationPipeline(…).UseTextToolCallRecovery().UseTransientRetry()</c>.
    /// </summary>
    /// <param name="builder">The chat client builder.</param>
    /// <param name="maxRetries">How many times a failed call is tried again; see <see cref="TransientRetryChatClient.MaxRetries"/>.</param>
    /// <param name="maxDelay">The longest wait before a retry; null keeps <see cref="TransientRetryChatClient.MaxDelay"/>'s default.</param>
    /// <param name="recovery">Classifies a failure; null uses an <see cref="ErrorRecoveryService"/> with default settings.</param>
    public static ChatClientBuilder UseTransientRetry(
        this ChatClientBuilder builder, int maxRetries = 1, TimeSpan? maxDelay = null, IErrorRecoveryService? recovery = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);
        return builder.Use(inner => maxDelay is { } delay
            ? new TransientRetryChatClient(inner, recovery) { MaxRetries = maxRetries, MaxDelay = delay }
            : new TransientRetryChatClient(inner, recovery) { MaxRetries = maxRetries });
    }
}
