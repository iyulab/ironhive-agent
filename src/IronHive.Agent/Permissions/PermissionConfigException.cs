namespace IronHive.Agent.Permissions;

/// <summary>
/// A permission configuration file exists but cannot be read as one. <see cref="PermissionConfigLoader"/> throws this
/// instead of falling back to <see cref="PermissionConfig.CreateDefault"/>: the defaults allow more than a restrictive
/// file would, so a typo must not silently widen what the agent may do.
/// </summary>
public sealed class PermissionConfigException : Exception
{
    /// <summary>Creates the exception for <paramref name="filePath"/>.</summary>
    /// <param name="filePath">The file that could not be read.</param>
    /// <param name="reason">What is wrong with it.</param>
    /// <param name="innerException">The parser's own exception, when there is one.</param>
    public PermissionConfigException(string filePath, string reason, Exception? innerException = null)
        : base($"Permission configuration '{filePath}' could not be read: {reason}", innerException)
    {
        FilePath = filePath;
    }

    /// <summary>The file that could not be read.</summary>
    public string FilePath { get; }
}
