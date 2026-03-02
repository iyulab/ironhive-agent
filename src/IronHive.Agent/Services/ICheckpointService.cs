namespace IronHive.Agent.Services;

/// <summary>
/// Provides pre-destructive-operation state snapshots and rollback capabilities.
/// Implementations manage backup storage for any resource type.
/// </summary>
public interface ICheckpointService : IAsyncDisposable
{
    /// <summary>
    /// Creates a checkpoint (backup) for the specified resource before a destructive operation.
    /// Returns the checkpoint ID, or <c>null</c> if the resource does not exist.
    /// </summary>
    Task<string?> CreateCheckpointAsync(string resourceId, string operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a resource from a checkpoint.
    /// Returns <c>true</c> if restored, <c>false</c> if checkpoint not found.
    /// </summary>
    Task<bool> RollbackAsync(string checkpointId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores all resources changed at or after the specified step.
    /// Returns the number of resources restored.
    /// </summary>
    Task<int> RollbackSinceStepAsync(int sinceStep, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all checkpoint entries in creation order.
    /// </summary>
    IReadOnlyList<CheckpointInfo> Checkpoints { get; }
}
