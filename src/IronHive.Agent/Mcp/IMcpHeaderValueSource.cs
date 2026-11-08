namespace IronHive.Agent.Mcp;

/// <summary>
/// Where an HTTP header's value comes from, asked once per request instead of fixed when the plugin connects - for a
/// credential that expires (an access token minutes old) and is refreshed by something else.
/// </summary>
/// <remarks>
/// Set it per header name on <see cref="McpPluginConfig.HeaderSources"/>. The manager asks the source before every HTTP
/// request of the plugin, so the value a request carries is the one the source holds at that moment. A source returning
/// null or an empty string fails that request (a missing credential must not turn into an unauthenticated call); the failure
/// names the header, never the value. Values are never logged. An implementation is called concurrently and should be cheap
/// (cache what it reads).
/// </remarks>
public interface IMcpHeaderValueSource
{
    /// <summary>The header's value for the request about to be sent, or null when there is none.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<string?> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The sources that need no code, and the text form a plugin file uses for them (<c>headerSources</c>).
/// </summary>
public static class McpHeaderSources
{
    /// <summary>The prefix of an environment-variable source in a plugin file: <c>env:NAME</c>.</summary>
    public const string EnvironmentPrefix = "env:";

    /// <summary>The prefix of a file source in a plugin file: <c>file:/path/to/token</c>.</summary>
    public const string FilePrefix = "file:";

    /// <summary>The value of the environment variable <paramref name="variableName"/>, read on every request.</summary>
    public static IMcpHeaderValueSource FromEnvironment(string variableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);
        return new EnvironmentHeaderSource(variableName);
    }

    /// <summary>
    /// The text of the file at <paramref name="path"/> with surrounding white space removed, re-read when the file changes
    /// (its last-write time or length) - so a sidecar that rewrites a token file keeps the plugin authenticated. The file holds
    /// the header's whole value (for <c>Authorization</c>: <c>Bearer &lt;token&gt;</c>).
    /// </summary>
    public static IMcpHeaderValueSource FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new FileHeaderSource(path);
    }

    /// <summary>Reads <c>env:NAME</c> or <c>file:PATH</c>; false (with the reason) for anything else.</summary>
    public static bool TryParse(string? text, out IMcpHeaderValueSource? source, out string? error)
    {
        source = null;
        error = null;
        if (text is not null && text.StartsWith(EnvironmentPrefix, StringComparison.Ordinal) && text.Length > EnvironmentPrefix.Length)
        {
            source = FromEnvironment(text[EnvironmentPrefix.Length..]);
            return true;
        }

        if (text is not null && text.StartsWith(FilePrefix, StringComparison.Ordinal) && text.Length > FilePrefix.Length)
        {
            source = FromFile(text[FilePrefix.Length..]);
            return true;
        }

        error = $"a header source must be '{EnvironmentPrefix}NAME' or '{FilePrefix}PATH'.";
        return false;
    }

    // Value equality, so a reloaded plugin file that names the same source is not a changed configuration.
    internal sealed record EnvironmentHeaderSource(string VariableName) : IMcpHeaderValueSource
    {
        public ValueTask<string?> GetAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Environment.GetEnvironmentVariable(VariableName));
    }

    internal sealed record FileHeaderSource(string Path) : IMcpHeaderValueSource
    {
        private readonly Lock _gate = new();
        private DateTime _writtenUtc;
        private long _length = -1;
        private string? _cached;

        public ValueTask<string?> GetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(Path);
            if (!info.Exists)
            {
                return ValueTask.FromResult<string?>(null);
            }

            lock (_gate)
            {
                if (_cached is null || info.LastWriteTimeUtc != _writtenUtc || info.Length != _length)
                {
                    // Shared read: the writer may still hold the file open.
                    using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    _cached = reader.ReadToEnd().Trim();
                    _writtenUtc = info.LastWriteTimeUtc;
                    _length = info.Length;
                }

                return ValueTask.FromResult<string?>(_cached);
            }
        }

        // The cache is not part of the source's identity.
        public bool Equals(FileHeaderSource? other) => other is not null && string.Equals(Path, other.Path, StringComparison.Ordinal);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Path);
    }
}

/// <summary>Puts the current value of each configured header on every request that goes through the HTTP client.</summary>
internal sealed class McpHeaderSourceHandler(string pluginName, IReadOnlyDictionary<string, IMcpHeaderValueSource> sources, HttpMessageHandler inner)
    : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        foreach (var (name, source) in sources)
        {
            string? value;
            try
            {
                value = await source.GetAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The source's message may quote what it read; keep the type, drop the text.
                throw new InvalidOperationException(
                    $"The value source of header '{name}' for MCP plugin '{pluginName}' failed ({ex.GetType().Name}).");
            }

            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException($"The value source of header '{name}' for MCP plugin '{pluginName}' returned no value.");
            }

            request.Headers.Remove(name);
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                throw new InvalidOperationException($"Header '{name}' of MCP plugin '{pluginName}' cannot be set on a request.");
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
