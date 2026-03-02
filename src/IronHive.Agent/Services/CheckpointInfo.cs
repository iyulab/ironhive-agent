namespace IronHive.Agent.Services;

/// <summary>
/// Metadata about a single checkpoint.
/// </summary>
public sealed record CheckpointInfo(
    string Id,
    int Step,
    string ResourceId,
    string Operation,
    DateTimeOffset Timestamp);
