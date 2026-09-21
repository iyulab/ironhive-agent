namespace IronHive.Agent.Tools;

/// <summary>
/// Runs around every write the built-in <c>WriteFile</c> tool performs — the place a host attaches
/// what should happen when the agent changes a file: a snapshot, an audit record, a policy check.
/// </summary>
/// <remarks>
/// The tool resolves the path and creates the directory first, then hands the write to the interceptor,
/// so an implementation sees the same absolute path the tool acts on and never has to resolve one itself.
/// Without an interceptor the tool simply writes.
/// </remarks>
public interface IFileWriteInterceptor
{
    /// <summary>
    /// Wraps one write.
    /// </summary>
    /// <param name="fullPath">The absolute path the tool is about to write.</param>
    /// <param name="write">Performs the write. Call it once; not calling it means the file is not written.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Text appended to the tool's success message (for example <c>" (snapshot saved)"</c>), or
    /// <see langword="null"/> for none. An exception is reported to the model as a failed write.
    /// </returns>
    Task<string?> InterceptAsync(string fullPath, Func<Task> write, CancellationToken cancellationToken = default);
}
