namespace IronHive.Agent.Exceptions;

/// <summary>
/// Thrown by <see cref="Loop.AgentLoop"/> when a configured <see cref="Tracking.UsageLimitsConfig"/>
/// session limit (token or cost) has been exceeded and <see cref="Tracking.UsageLimitsConfig.StopOnLimit"/>
/// is <c>true</c>.
/// </summary>
public class UsageLimitExceededException : Exception
{
    /// <summary>
    /// The limit check result that triggered this exception.
    /// </summary>
    public required Tracking.UsageLimitResult LimitResult { get; init; }

    public UsageLimitExceededException()
        : base("Session usage limit exceeded.")
    {
        LimitResult = new Tracking.UsageLimitResult();
    }

    public UsageLimitExceededException(string message)
        : base(message)
    {
        LimitResult = new Tracking.UsageLimitResult();
    }

    public UsageLimitExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
        LimitResult = new Tracking.UsageLimitResult();
    }

    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public UsageLimitExceededException(Tracking.UsageLimitResult limitResult)
        : base(limitResult.Message)
    {
        LimitResult = limitResult;
    }
}
