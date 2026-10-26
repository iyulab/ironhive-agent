using System.Collections.Concurrent;
using System.Text;
using IronHive.Agent.Mode;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using IronHive.Agent.Context;
using IronHive.Agent.Invocation;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace IronHive.Agent.Mcp;

/// <summary>
/// Default implementation of MCP plugin manager.
/// </summary>
public class McpPluginManager : IMcpPluginManager, IDisposable
{
    private readonly ConcurrentDictionary<string, McpClientWrapper> _clients = new();
    private readonly ConcurrentDictionary<string, PendingPlugin> _pending = new();
    private readonly TimeProvider _time;
    private readonly ILogger<McpPluginManager>? _logger;
    private readonly IMcpToolCallGuard? _guard;
    private readonly IMcpElicitationHandler? _elicitation;
    private bool _disposed;

    /// <param name="logger">Optional logger.</param>
    /// <param name="guard">
    /// Optional MCP tool-call guard (<see cref="IMcpToolCallGuard"/>; a FluxGuard-backed one is in the
    /// <c>IronHive.Agent.FluxGuard</c> package). Opt-in: when null (the default), tool calls dispatch unguarded.
    /// When provided, every <see cref="CallToolAsync"/> checks the request before dispatch and the result before
    /// returning it — see that method's remarks for the fail-closed policy on a guard-side exception.
    /// </param>
    /// <param name="timeProvider">Clock for the reconnect schedule (<see cref="PendingPlugins"/>); the system clock by default.</param>
    public McpPluginManager(ILogger<McpPluginManager>? logger = null, IMcpToolCallGuard? guard = null, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _guard = guard;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// A manager whose servers can ask a person for input — chosen by dependency injection when an
    /// <see cref="IMcpElicitationHandler"/> is registered.
    /// </summary>
    /// <param name="elicitation">
    /// Answers a server's request for a person (URL-mode elicitation, including a <c>tools/call</c> answered with
    /// <c>input_required</c>) — see <see cref="IMcpElicitationHandler"/>. Without one (the other constructor) no elicitation
    /// capability is advertised, and such a call fails.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="guard">Optional MCP tool-call guard — see the other constructor.</param>
    /// <param name="timeProvider">Clock for the reconnect schedule — see the other constructor.</param>
    public McpPluginManager(
        IMcpElicitationHandler elicitation, ILogger<McpPluginManager>? logger = null, IMcpToolCallGuard? guard = null,
        TimeProvider? timeProvider = null)
        : this(logger, guard, timeProvider)
    {
        _elicitation = elicitation ?? throw new ArgumentNullException(nameof(elicitation));
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> ConnectedPlugins => [.. _clients.Keys];

    /// <inheritdoc />
    public IReadOnlyDictionary<string, McpPluginPending> PendingPlugins =>
        _pending.ToDictionary(entry => entry.Key, entry => entry.Value.State, StringComparer.Ordinal);

    /// <summary>The transport a configuration connects over; a test replaces it with an in-process one.</summary>
    internal Func<string, McpPluginConfig, IClientTransport> TransportFactory { get; set; } = CreateTransport;

    /// <inheritdoc />
    public event EventHandler<McpPluginEventArgs>? PluginConnected;

    /// <inheritdoc />
    public event EventHandler<McpPluginEventArgs>? PluginDisconnected;

    /// <inheritdoc />
    public event EventHandler<McpPluginEventArgs>? PluginConnectionFailed;

    /// <inheritdoc />
    public event EventHandler<McpPluginEventArgs>? ToolsChanged;

    /// <summary>
    /// Raises the ToolsChanged event for a plugin.
    /// </summary>
    protected void OnToolsChanged(string pluginName)
    {
        ToolsChanged?.Invoke(this, new McpPluginEventArgs { PluginName = pluginName });
    }

    /// <inheritdoc />
    public async Task ConnectAsync(string name, McpPluginConfig config, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        ArgumentNullException.ThrowIfNull(config, nameof(config));

        if (_clients.ContainsKey(name))
        {
            throw new InvalidOperationException($"Plugin '{name}' is already connected.");
        }

        // A configuration the transport cannot be built from is the caller's mistake, not a server that is not ready:
        // it throws here and is not retried.
        var transport = TransportFactory(name, config);
        try
        {
            await ConnectCoreAsync(name, transport, config, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            NoteConnectionFailed(name, config, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ReconnectPendingAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pending.IsEmpty)
        {
            return [];
        }

        var now = _time.GetUtcNow();
        var connected = new List<string>();
        foreach (var (name, entry) in _pending.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (entry.NextAttempt > now)
            {
                continue;
            }

            if (await TryConnectAsync(name, entry.Config, cancellationToken))
            {
                connected.Add(name);
            }
        }

        return connected;
    }

    /// <inheritdoc />
    public async Task<bool> ReconnectAsync(string name, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));

        McpPluginConfig config;
        if (_clients.TryGetValue(name, out var current))
        {
            config = current.Config;
            if (!config.AutoReconnect)
            {
                return false;
            }

            if (_clients.TryRemove(name, out var removed))
            {
                try
                {
                    await removed.Client.DisposeAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // Ignore disposal errors: the connection is being replaced because it is already bad.
                }

                PluginDisconnected?.Invoke(this, new McpPluginEventArgs { PluginName = name });
            }
        }
        else if (_pending.TryGetValue(name, out var pending))
        {
            config = pending.Config;
        }
        else
        {
            return false;
        }

        return await TryConnectAsync(name, config, cancellationToken);
    }

    /// <summary>
    /// One attempt for a plugin that is not connected, bounded by the configuration's connect timeout. A failure is recorded
    /// (<see cref="PendingPlugins"/>, <see cref="PluginConnectionFailed"/>) and answered with false; only the caller's own
    /// cancellation propagates.
    /// </summary>
    private async Task<bool> TryConnectAsync(string name, McpPluginConfig config, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromMilliseconds(config.TimeoutMs > 0 ? config.TimeoutMs : 30000));
        try
        {
            await ConnectCoreAsync(name, TransportFactory(name, config), config, attempt.Token);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            NoteConnectionFailed(name, config, ex);
            return false;
        }
    }

    /// <summary>
    /// Records a failed attempt: the plugin is pending again, to be tried after a wait that doubles from 5 seconds to
    /// 5 minutes, unless its configuration turned <see cref="McpPluginConfig.AutoReconnect"/> off.
    /// </summary>
    private void NoteConnectionFailed(string name, McpPluginConfig config, Exception error)
    {
        if (config.AutoReconnect)
        {
            _pending.AddOrUpdate(
                name,
                _ => new PendingPlugin(config, error.Message, 1, _time.GetUtcNow() + Backoff(1)),
                (_, previous) => new PendingPlugin(config, error.Message, previous.Attempts + 1, _time.GetUtcNow() + Backoff(previous.Attempts + 1)));
        }

#pragma warning disable CA1848 // Use LoggerMessage delegates for performance-critical paths
        _logger?.LogWarning(error, "MCP plugin '{PluginName}' could not be connected (will retry: {WillRetry})", name, config.AutoReconnect);
#pragma warning restore CA1848
        PluginConnectionFailed?.Invoke(this, new McpPluginEventArgs { PluginName = name, ErrorMessage = error.Message });
    }

    private static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(attempts - 1, 6))));

    /// <summary>Connects over a given transport — the seam an in-process server test uses.</summary>
    internal async Task ConnectCoreAsync(string name, IClientTransport transport, McpPluginConfig config, CancellationToken cancellationToken)
    {
        var client = await McpClient.CreateAsync(transport, ClientOptions(name), cancellationToken: cancellationToken);

        var wrapper = new McpClientWrapper(name, client, config);
        if (!_clients.TryAdd(name, wrapper))
        {
            await client.DisposeAsync();
            throw new InvalidOperationException($"Plugin '{name}' connection failed.");
        }

        _pending.TryRemove(name, out _);
        PluginConnected?.Invoke(this, new McpPluginEventArgs { PluginName = name });
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(string name, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _pending.TryRemove(name, out _);
        if (_clients.TryRemove(name, out var wrapper))
        {
            await wrapper.Client.DisposeAsync();
            PluginDisconnected?.Invoke(this, new McpPluginEventArgs { PluginName = name });
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAllAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var names = _clients.Keys.ToList();
        foreach (var name in names)
        {
            await DisconnectAsync(name, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var allTools = new List<AITool>();

        // By plugin name, not the dictionary's enumeration order: string hashing is randomised per process, so with two
        // or more plugins the catalog would come back in a different order on every start.
        foreach (var (_, wrapper) in _clients.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            try
            {
                // McpClientTool inherits from AIFunction which inherits from AITool
                var tools = await wrapper.Client.ListToolsAsync(cancellationToken: cancellationToken);
                wrapper.ToolNames = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
                allTools.AddRange(tools.Select(WithDeclaredHints));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Log but don't fail - other plugins may still work
#pragma warning disable CA1848 // Use LoggerMessage delegates for performance-critical paths
                _logger?.LogWarning(ex, "Failed to list tools from plugin '{PluginName}'", wrapper.Name);
#pragma warning restore CA1848
            }
        }

        return allTools.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AITool>> GetToolsAsync(string pluginName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_clients.TryGetValue(pluginName, out var wrapper))
        {
            throw new InvalidOperationException($"Plugin '{pluginName}' is not connected.");
        }

        var tools = await wrapper.Client.ListToolsAsync(cancellationToken: cancellationToken);
        wrapper.ToolNames = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        return tools.Select(WithDeclaredHints).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public bool IsMcpTool(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        return _clients.Values.Any(wrapper => wrapper.ToolNames.Contains(toolName));
    }

    /// <summary>
    /// Carries the hints an MCP server declares in a tool's <c>_meta</c> into the tool's
    /// <see cref="AITool.AdditionalProperties"/>, each as a string array or one comma-separated string:
    /// <list type="bullet">
    /// <item>retrieval hints under <see cref="ToolRetrievalHints.AliasesKey"/> and <see cref="ToolRetrievalHints.CompanionsKey"/>,
    /// read by the tool retrievers;</item>
    /// <item>target arguments under <see cref="ToolInvocationHints.TargetArgumentsKey"/>, read by the loop guards. A name
    /// the tool's input schema does not declare is dropped (it would make every call the same target).</item>
    /// </list>
    /// A tool without them is returned unchanged. Public for a host that lists tools through its own
    /// <see cref="McpClientTool"/> clients rather than this manager.
    /// </summary>
    public static AITool WithDeclaredHints(McpClientTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var meta = tool.ProtocolTool.Meta;
        if (meta is null)
        {
            return tool;
        }

        var aliases = ReadHint(meta, ToolRetrievalHints.AliasesKey);
        var companions = ReadHint(meta, ToolRetrievalHints.CompanionsKey);
        var targets = ReadHint(meta, ToolInvocationHints.TargetArgumentsKey) is { } declared
            ? ToolInvocationHints.KnownTargets(tool, declared)
            : [];

        AITool hinted = tool;
        if (aliases is not null || companions is not null)
        {
            hinted = hinted.WithRetrievalHints(aliases, companions);
        }

        if (targets.Length > 0)
        {
            hinted = hinted.WithTargetArguments(targets);
        }

        return hinted;
    }

    private static string[]? ReadHint(System.Text.Json.Nodes.JsonObject meta, string key)
    {
        if (!meta.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        var values = ToolRetrievalHints.ParseValue(System.Text.Json.JsonElement.Parse(node.ToJsonString()));
        return values.Length == 0 ? null : values;
    }

    /// <summary>
    /// Calls a tool on a connected MCP plugin.
    /// </summary>
    /// <remarks>
    /// When a guard was supplied to the constructor, both the request (before dispatch) and
    /// the result (before it is returned) are checked. A guard-reported block is reported as an
    /// error result — the underlying tool call is never dispatched in the request case, and its
    /// result is never surfaced to the caller in the result case. An exception raised BY the
    /// guard itself (as opposed to a request/result it checks and blocks) is treated as
    /// fail-closed: the call is blocked rather than silently dispatched unguarded. Registering an
    /// <see cref="IMcpToolCallGuard"/> is an explicit per-consumer opt-in, not a broadly-applied
    /// default guard — a consumer who wired one up wants it enforced, so a guard malfunction should
    /// not silently disable the protection they asked for.
    /// </remarks>
    /// <inheritdoc />
    public async Task<McpToolResult> CallToolAsync(
        string pluginName,
        string toolName,
        IDictionary<string, object?>? arguments,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_clients.TryGetValue(pluginName, out var wrapper))
        {
            return McpToolResult.Error($"Plugin '{pluginName}' is not connected.");
        }

        // Convert IDictionary to IReadOnlyDictionary
        IReadOnlyDictionary<string, object?>? args = arguments != null
            ? new Dictionary<string, object?>(arguments)
            : null;

        if (_guard != null)
        {
            var inspection = new McpToolCallInspection(pluginName, toolName, args);

            McpToolCallVerdict requestVerdict;
            try
            {
                requestVerdict = await _guard.CheckCallAsync(inspection, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
#pragma warning disable CA1848 // Use LoggerMessage delegates for performance-critical paths
                _logger?.LogWarning(ex, "MCP guardrail threw while validating a tool call to '{Plugin}.{Tool}' — blocking (fail-closed)", pluginName, toolName);
#pragma warning restore CA1848
                return McpToolResult.Error($"Tool call blocked: guardrail error ({ex.Message})");
            }

            if (requestVerdict.Blocked)
            {
                return McpToolResult.Error($"Tool call blocked by guardrail: {requestVerdict.Reason ?? "policy violation"}");
            }

            try
            {
                var result = await wrapper.Client.CallToolAsync(
                    toolName,
                    args,
                    progress: null,
                    cancellationToken: cancellationToken);

                var content = ExtractTextContent(result);

                ToolResultVerdict resultVerdict;
                try
                {
                    resultVerdict = await _guard.CheckResultAsync(inspection, content, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
#pragma warning disable CA1848 // Use LoggerMessage delegates for performance-critical paths
                    _logger?.LogWarning(ex, "MCP guardrail threw while validating a tool result from '{Plugin}.{Tool}' — blocking (fail-closed)", pluginName, toolName);
#pragma warning restore CA1848
                    return McpToolResult.Error($"Tool result blocked: guardrail error ({ex.Message})");
                }

                if (resultVerdict.Withheld)
                {
                    return McpToolResult.Error($"Tool result blocked by guardrail: {resultVerdict.Reason ?? "policy violation"}");
                }

                return new McpToolResult
                {
                    Content = resultVerdict.Replacement ?? content,
                    // A replacement stands for the whole result; the images it was judged with do not survive it.
                    Images = resultVerdict.Replacement is null ? ExtractImages(result) : [],
                    IsError = result.IsError ?? false,
                    StructuredContent = result.StructuredContent
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return McpToolResult.Error($"Tool call failed: {ex.Message}");
            }
        }

        try
        {
            var result = await wrapper.Client.CallToolAsync(
                toolName,
                args,
                progress: null,
                cancellationToken: cancellationToken);

            return new McpToolResult
            {
                Content = ExtractTextContent(result),
                Images = ExtractImages(result),
                IsError = result.IsError ?? false,
                StructuredContent = result.StructuredContent
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return McpToolResult.Error($"Tool call failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The result's image blocks, decoded, in order. A block whose data is not base64 or that names no MIME type is
    /// skipped — <see cref="McpToolResult.Content"/> keeps the text either way.
    /// </summary>
    private static List<DataContent> ExtractImages(CallToolResult result)
    {
        var images = new List<DataContent>();
        foreach (var image in result.Content.OfType<ImageContentBlock>())
        {
            if (string.IsNullOrWhiteSpace(image.MimeType))
            {
                continue;
            }

            try
            {
                images.Add(new DataContent(image.DecodedData, image.MimeType));
            }
            catch (FormatException)
            {
                // Not base64: nothing to carry.
            }
        }

        return images;
    }

    private static string ExtractTextContent(ModelContextProtocol.Protocol.CallToolResult result)
    {
        var contentBuilder = new StringBuilder();
        foreach (var content in result.Content)
        {
            if (content is TextContentBlock textBlock && textBlock.Text != null)
            {
                if (contentBuilder.Length > 0)
                {
                    contentBuilder.AppendLine();
                }
                contentBuilder.Append(textBlock.Text);
            }
        }

        return contentBuilder.ToString();
    }

    /// <inheritdoc />
    public async Task<bool> IsHealthyAsync(string pluginName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_clients.TryGetValue(pluginName, out var wrapper))
        {
            return false;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await wrapper.Client.ListToolsAsync(cancellationToken: cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Disposes the connected clients synchronously — for a container disposed with <c>Dispose()</c>, which throws on a
    /// service that implements only <see cref="IAsyncDisposable"/> (<c>AddIronHiveAgent</c> registers this type).
    /// </summary>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var wrapper in _clients.Values)
        {
            try
            {
                await wrapper.Client.DisposeAsync();
            }
            catch
            {
                // Ignore disposal errors
            }
        }

        _clients.Clear();
        _pending.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// With an <see cref="IMcpElicitationHandler"/>: URL-mode elicitation advertised and routed to it. Without: the SDK's
    /// defaults (no elicitation capability).
    /// </summary>
    private McpClientOptions? ClientOptions(string name) => _elicitation is not { } elicitation
        ? null
        : new McpClientOptions
        {
            Capabilities = new ClientCapabilities { Elicitation = new ElicitationCapability { Url = new UrlElicitationCapability() } },
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = (request, ct) => request is null
                    ? ValueTask.FromResult(new ElicitResult { Action = "cancel" })
                    : elicitation.ElicitAsync(name, request, ct)
            }
        };

    private static IClientTransport CreateTransport(string name, McpPluginConfig config)
    {
        return config.Transport switch
        {
            McpTransportType.Stdio => CreateStdioTransport(name, config),
            McpTransportType.Http => CreateHttpTransport(name, config),
            _ => throw new ArgumentException($"Unknown transport type: {config.Transport}")
        };
    }

    private static StdioClientTransport CreateStdioTransport(string name, McpPluginConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Command))
        {
            throw new ArgumentException("Command is required for stdio transport.");
        }

        var options = new StdioClientTransportOptions
        {
            Name = name,
            Command = config.Command,
            Arguments = config.Arguments?.ToList() ?? []
        };

        if (config.Environment != null)
        {
            options.EnvironmentVariables = config.Environment.ToDictionary(
                kvp => kvp.Key,
                kvp => (string?)kvp.Value);
        }

        if (!string.IsNullOrWhiteSpace(config.WorkingDirectory))
        {
            options.WorkingDirectory = config.WorkingDirectory;
        }

        return new StdioClientTransport(options);
    }

    private static HttpClientTransport CreateHttpTransport(string name, McpPluginConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Url))
        {
            throw new ArgumentException("Url is required for HTTP transport.");
        }

        var options = new HttpClientTransportOptions
        {
            Name = name,
            Endpoint = new Uri(config.Url)
        };

        if (config.Headers != null)
        {
            options.AdditionalHeaders = new Dictionary<string, string>(config.Headers);
        }

        return new HttpClientTransport(options);
    }

    private sealed record PendingPlugin(McpPluginConfig Config, string LastError, int Attempts, DateTimeOffset NextAttempt)
    {
        public McpPluginPending State => new(LastError, Attempts, NextAttempt);
    }

    private sealed record McpClientWrapper(string Name, McpClient Client, McpPluginConfig Config)
    {
        // The names this plugin's last listing returned; replaced whole on each listing, read by IsMcpTool.
        public volatile HashSet<string> ToolNames = new(StringComparer.Ordinal);
    }
}
