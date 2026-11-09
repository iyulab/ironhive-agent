using System.Text.Json;
﻿using System.Collections.Concurrent;
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
        // The asker of a question the model puts to the person: a server of this name would be told apart from it by nothing.
        if (string.Equals(name, Tools.AskUserTool.AskerName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{name}' is reserved for questions the model asks the person (AskUserTool.AskerName); name the plugin differently.", nameof(name));
        }

        // A handler that releases waiting calls needs the server's state, which the SDK keeps to itself: read it on the way.
        var tap = _elicitation?.DeferAfter is { } after && after > TimeSpan.Zero ? new McpInputRequiredTap(transport) : null;
        var client = await McpClient.CreateAsync(tap ?? transport, ClientOptions(name, tap), cancellationToken: cancellationToken);

        var wrapper = new McpClientWrapper(name, client, config) { Tap = tap };
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
                wrapper.Record(tools);
                allTools.AddRange(tools.Select(tool => WithCallTracking(WithDeclaredHints(tool), wrapper.Name, tool.Name)));
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
        wrapper.Record(tools);
        return tools.Select(tool => WithCallTracking(WithDeclaredHints(tool), pluginName, tool.Name)).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public bool IsMcpTool(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        return _clients.Values.Any(wrapper => wrapper.ToolNames.Contains(toolName));
    }

    /// <inheritdoc />
    public bool IsTrustedReadOnly(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        // A name two plugins both list is read-only only when each of them is trusted and says so: the policy sees a name, not a plugin.
        var owners = _clients.Values.Where(wrapper => wrapper.ToolNames.Contains(toolName)).ToList();
        return owners.Count > 0
            && owners.All(wrapper => wrapper.Config.TrustReadOnlyHints && wrapper.ReadOnlyToolNames.Contains(toolName));
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

        using var inFlight = await TrackCurrentCallAsync(pluginName, toolName, cancellationToken);

        // Convert IDictionary to IReadOnlyDictionary
        IReadOnlyDictionary<string, object?>? args = arguments != null
            ? new Dictionary<string, object?>(arguments)
            : null;

        return await DispatchAsync(
            pluginName, toolName, args, FunctionInvokingChatClient.CurrentContext?.CallContent?.CallId,
            () => wrapper.Client.CallToolAsync(toolName, args, progress: null, cancellationToken: cancellationToken).AsTask(),
            cancellationToken);
    }

    // One path for a call and for the re-issue of a released one: the guard checks the request before it is sent and the result
    // before it is returned, and a call released to wait for a person ends as awaiting.
    private async Task<McpToolResult> DispatchAsync(
        string pluginName,
        string toolName,
        IReadOnlyDictionary<string, object?>? args,
        string? callId,
        Func<Task<CallToolResult>> send,
        CancellationToken cancellationToken)
    {
        McpToolCallInspection? inspection = null;
        if (_guard != null)
        {
            inspection = new McpToolCallInspection(pluginName, toolName, args);

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
        }

        try
        {
            var result = await send();
            var content = ExtractTextContent(result);
            string? replacement = null;
            if (inspection is not null)
            {
                ToolResultVerdict resultVerdict;
                try
                {
                    resultVerdict = await _guard!.CheckResultAsync(inspection, content, cancellationToken);
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

                replacement = resultVerdict.Replacement;
            }

            return new McpToolResult
            {
                Content = replacement ?? content,
                // A replacement stands for the whole result; the images it was judged with do not survive it.
                Images = replacement is null ? ExtractImages(result) : [],
                IsError = result.IsError ?? false,
                StructuredContent = result.StructuredContent
            };
        }
        catch (McpInputDeferredException deferred)
        {
            return AwaitingResult(pluginName, toolName, args, deferred, callId);
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

    // What the model proposed for the call being run, for the handler to show beside a plugin's question about it. Read only when
    // a handler exists (nobody else could show it) and with the same rule the approval gate uses; a failure to read is a proposal
    // not made, never a failed call.
    private async ValueTask<Mode.ModelAuthoredProposal?> ReadProposalAsync(FunctionInvocationContext? invocation, CancellationToken cancellationToken)
    {
        if (_elicitation is null || invocation is null)
        {
            return null;
        }

        try
        {
            return await Mode.ToolCallProposalSource.Default.GetProposalAsync(invocation, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
#pragma warning disable CA1848 // Use LoggerMessage delegates for performance-critical paths
            _logger?.LogWarning(ex, "Reading the model's approval proposal for a call to '{Tool}' failed - asking without one", invocation.Function.Name);
#pragma warning restore CA1848
            return null;
        }
    }

    // The plugin may ask a person about a call while it runs (elicitation); the request arrives on the client's own loop, not
    // in the call's flow, so the call is registered for the handler to find - with the model's call id and proposal when it
    // is made from a function-invocation turn.
    private async ValueTask<IDisposable> TrackCurrentCallAsync(string pluginName, string toolName, CancellationToken cancellationToken)
    {
        var invocation = FunctionInvokingChatClient.CurrentContext;
        return TrackCall(pluginName, new McpCallInfo(toolName, invocation?.CallContent?.CallId)
        {
            ModelProposal = await ReadProposalAsync(invocation, cancellationToken),
            TapMark = TapMark(pluginName)
        });
    }

    // A tool the manager lists is what the model calls, and an MCP client tool invokes its server directly - without this a
    // call the model makes never reaches CallToolAsync, so the handler is asked about a request tied to no call. Only a
    // manager with a handler tracks: nobody else reads the registration.
    private AITool WithCallTracking(AITool tool, string pluginName, string toolName) =>
        _elicitation is not null && tool is AIFunction function
            ? new CallTrackingFunction(function, this, pluginName, toolName)
            : tool;

    private sealed class CallTrackingFunction(AIFunction inner, McpPluginManager owner, string pluginName, string toolName)
        : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            using var inFlight = await owner.TrackCurrentCallAsync(pluginName, toolName, cancellationToken);
            try
            {
                return await base.InvokeCoreAsync(arguments, cancellationToken);
            }
            catch (McpInputDeferredException deferred)
            {
                // The model's own call was released: it reads that the call waits for the person, and the host gets the entry.
                var args = arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                return owner.AwaitingResult(pluginName, toolName, args, deferred, FunctionInvokingChatClient.CurrentContext?.CallContent?.CallId).Content;
            }
        }
    }

    private readonly Lock _inFlightLock = new();
    private readonly Dictionary<string, List<McpCallInfo>> _inFlight = new(StringComparer.Ordinal);

    // Where the plugin's tap stood when a call started: only results captured after that can be this call's question.
    private long TapMark(string pluginName) => _clients.TryGetValue(pluginName, out var wrapper) ? wrapper.Tap?.Sequence ?? 0 : 0;

    private CallScope TrackCall(string pluginName, McpCallInfo call)
    {
        lock (_inFlightLock)
        {
            if (!_inFlight.TryGetValue(pluginName, out var calls))
            {
                _inFlight[pluginName] = calls = [];
            }

            calls.Add(call);
        }

        return new CallScope(this, pluginName, call);
    }

    // The one call the plugin is running, or null when it is running none or several: with several, an elicitation cannot be
    // told from the others, and naming one would put a person's answer on the wrong call.
    private McpCallInfo? SoleCallInFlight(string pluginName)
    {
        lock (_inFlightLock)
        {
            return _inFlight.TryGetValue(pluginName, out var calls) && calls.Count == 1 ? calls[0] : null;
        }
    }

    private sealed class CallScope(McpPluginManager owner, string pluginName, McpCallInfo call) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._inFlightLock)
            {
                if (owner._inFlight.TryGetValue(pluginName, out var calls))
                {
                    calls.Remove(call);
                }
            }
        }
    }

    /// <summary>
    /// With an <see cref="IMcpElicitationHandler"/>: URL-mode elicitation advertised and routed to it. Without: the SDK's
    /// defaults (no elicitation capability).
    /// </summary>
    private McpClientOptions? ClientOptions(string name, McpInputRequiredTap? tap)
    {
        if (_elicitation is not { } elicitation)
        {
            return null;
        }

        // Per client: a server's completion notification names an elicitation only that server issued.
        var waits = new ElicitationWaits();
        return new McpClientOptions
        {
            Capabilities = new ClientCapabilities
            {
                Elicitation = new ElicitationCapability
                {
                    Url = new UrlElicitationCapability(),
                    Form = elicitation.SupportsForms ? new FormElicitationCapability() : null,
                }
            },
            Handlers = new McpClientHandlers
            {
                // A form is not a URL step a server can report complete elsewhere: it does not go through the completion wait.
                ElicitationHandler = (request, ct) => request is null
                    ? ValueTask.FromResult(new ElicitResult { Action = "cancel" })
                    : IsForm(request)
                        ? AskAsync(elicitation, name, request, tap, ct)
                        : waits.RunAsync(request, token => AskAsync(elicitation, name, request, tap, token), ct),
                NotificationHandlers =
                [
                    new KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>(
                        NotificationMethods.ElicitationCompleteNotification,
                        (notification, _) =>
                        {
                            waits.Complete(notification);
                            return ValueTask.CompletedTask;
                        })
                ]
            }
        };
    }

    // A request is a form unless it names URL mode or carries a URL (the mode defaults to form when omitted).
    private static bool IsForm(ElicitRequestParams request) =>
        !string.Equals(request.Mode, "url", StringComparison.Ordinal) && request.Url is null;

    private async ValueTask<ElicitResult> AskAsync(
        IMcpElicitationHandler elicitation, string name, ElicitRequestParams request, McpInputRequiredTap? tap, CancellationToken cancellationToken)
    {
        var form = IsForm(request);
        if (form && (!elicitation.SupportsForms || request.RequestedSchema is null))
        {
            // A server that did not read the capabilities (the handler never opted in), or a form with nothing to fill in:
            // nobody is asked.
            return new ElicitResult { Action = "decline" };
        }

        var result = await AskOrDeferAsync(elicitation, name, request, tap, cancellationToken);
        if (!form || result.Action != "accept")
        {
            return result;
        }

        // The handler is the last place a wrong value can come from; an answer that breaks the schema is not forwarded as one.
        if (ElicitationFormContent.Validate(request, result.Content) is { } problem)
        {
#pragma warning disable CA1848 // Use LoggerMessage delegates for performance-critical paths
            _logger?.LogWarning("The answer to a form request from plugin '{PluginName}' does not satisfy its schema ({Problem}) - the server reads cancel", name, problem);
#pragma warning restore CA1848
            return new ElicitResult { Action = "cancel" };
        }

        return result;
    }

    // The longest DeferAfter taken at face value (Task.Delay cannot wait longer than about 49 days), and how long a handler that
    // ignored its cancelled token gets to hand in an answer before the call is released anyway.
    private static readonly TimeSpan MaxDeferAfter = TimeSpan.FromDays(30);
    private static readonly TimeSpan ReleaseGrace = TimeSpan.FromSeconds(2);

    // Asks the handler; with DeferAfter, a request that came with a tools/call and is the one call the plugin runs is released
    // when the wait gets longer than that - the SDK's retry loop ends with McpInputDeferredException and the caller of the
    // call builds the awaiting entry. A request no call waits on (server-initiated: no result of this call's own asked it), one
    // that cannot be told from other calls' (several in flight: the state could be put on the wrong call), or one the entry
    // cannot represent (not an elicitation) keeps waiting as it always did.
    private async ValueTask<ElicitResult> AskOrDeferAsync(
        IMcpElicitationHandler elicitation, string name, ElicitRequestParams request, McpInputRequiredTap? tap, CancellationToken cancellationToken)
    {
        var call = SoleCallInFlight(name);
        if (tap is null || elicitation.DeferAfter is not { } after || after <= TimeSpan.Zero)
        {
            return await elicitation.ElicitAsync(name, call, request, cancellationToken);
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ask = elicitation.ElicitAsync(name, call, request, wait.Token).AsTask();
        // Not linked to the caller's token: a cancelled caller is not a deadline.
        using var timer = new CancellationTokenSource();
        var timeout = Task.Delay(after > MaxDeferAfter ? MaxDeferAfter : after, _time, timer.Token);
        var first = await Task.WhenAny(ask, timeout);
        if (first == ask || cancellationToken.IsCancellationRequested)
        {
            await timer.CancelAsync();
            return await ask;
        }

        // Another call may have started while the person was thinking: the state then cannot be told apart, so keep waiting.
        if (SoleCallInFlight(name) is not { } sole
            || tap.Find(request, sole.TapMark) is not { } captured
            || captured.InputRequests.Values.Any(input => input.ElicitationParams is null))
        {
            return await ask;
        }

        await wait.CancelAsync();
        // An answer that comes in the last moment wins; a handler that ignores its token gets a short grace, then is let go.
        var finished = await Task.WhenAny(ask, Task.Delay(ReleaseGrace, _time, CancellationToken.None));
        if (finished == ask && ask.IsCompletedSuccessfully)
        {
            return ask.Result;
        }

        _ = ask.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        throw new McpInputDeferredException(request, captured);
    }

    /// <inheritdoc />
    public async Task<McpToolResult> ReissueAsync(
        McpAwaitingInput awaiting, IReadOnlyDictionary<string, ElicitResult> answers, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(awaiting);
        ArgumentNullException.ThrowIfNull(answers);

        if (!_clients.TryGetValue(awaiting.PluginName, out var wrapper))
        {
            return McpToolResult.Error($"Plugin '{awaiting.PluginName}' is not connected.");
        }

        var inputResponses = new Dictionary<string, InputResponse>(StringComparer.Ordinal);
        foreach (var (key, asked) in awaiting.Requests)
        {
            var answer = answers.TryGetValue(key, out var given) ? given : new ElicitResult { Action = "cancel" };
            if (answer.Action == "accept" && ElicitationFormContent.Validate(asked, answer.Content) is { } problem)
            {
                // The same rule as for a live answer: one that breaks the request's schema is not forwarded as an answer.
#pragma warning disable CA1848 // Use LoggerMessage delegates for performance-critical paths
                _logger?.LogWarning("The answer to a re-issued request from plugin '{PluginName}' does not satisfy its schema ({Problem}) - the server reads cancel", awaiting.PluginName, problem);
#pragma warning restore CA1848
                answer = new ElicitResult { Action = "cancel" };
            }

            inputResponses[key] = InputResponse.FromElicitResult(answer);
        }

        var arguments = awaiting.Arguments?.ToDictionary(
            pair => pair.Key,
            pair => JsonSerializer.SerializeToElement(pair.Value, ModelContextProtocol.McpJsonUtilities.DefaultOptions),
            StringComparer.Ordinal);
        using var inFlight = TrackCall(awaiting.PluginName, new McpCallInfo(awaiting.ToolName, awaiting.CallId) { TapMark = TapMark(awaiting.PluginName) });
        return await DispatchAsync(
            awaiting.PluginName, awaiting.ToolName, awaiting.Arguments, awaiting.CallId,
            () => wrapper.Client.CallToolAsync(
                new CallToolRequestParams
                {
                    Name = awaiting.ToolName,
                    Arguments = arguments,
                    RequestState = awaiting.RequestState,
                    InputResponses = inputResponses,
                },
                cancellationToken).AsTask(),
            cancellationToken);
    }

    /// <summary>Raised when a call is released to wait for a person's answer (see <see cref="IMcpElicitationHandler.DeferAfter"/>).</summary>
    public event EventHandler<McpAwaitingInputEventArgs>? CallAwaitingInput;

    // The awaiting entry of a released call: its identity from the caller, the server's state from the result that asked.
    private McpToolResult AwaitingResult(
        string pluginName, string toolName, IReadOnlyDictionary<string, object?>? arguments, McpInputDeferredException deferred, string? callId)
    {
        var requests = deferred.Captured.InputRequests
            .Where(pair => pair.Value.ElicitationParams is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value.ElicitationParams!, StringComparer.Ordinal);
        var awaiting = new McpAwaitingInput(
            pluginName, toolName, arguments is null ? null : new Dictionary<string, object?>(arguments, StringComparer.Ordinal),
            deferred.Captured.RequestState, requests, callId);
        try
        {
            CallAwaitingInput?.Invoke(this, new McpAwaitingInputEventArgs(awaiting));
        }
        catch (Exception ex)
        {
            // A subscriber's fault must not lose the entry: the result below still carries it.
#pragma warning disable CA1848 // Use LoggerMessage delegates for performance-critical paths
            _logger?.LogWarning(ex, "A CallAwaitingInput subscriber threw for '{Plugin}.{Tool}'", pluginName, toolName);
#pragma warning restore CA1848
        }

        return McpToolResult.Awaiting(awaiting);
    }

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

    internal static HttpClientTransport CreateHttpTransport(string name, McpPluginConfig config)
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

        if (config.HeaderSources is not { Count: > 0 } sources)
        {
            return new HttpClientTransport(options);
        }

        // No silent precedence: a header is a fixed value or a source, not both.
        foreach (var header in sources.Keys)
        {
            if (config.Headers is not null && config.Headers.Keys.Any(k => string.Equals(k, header, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"Header '{header}' is in both Headers and HeaderSources for plugin '{name}'; use one.");
            }
        }

        var client = new HttpClient(new McpHeaderSourceHandler(name, sources, new HttpClientHandler()));
        return new HttpClientTransport(options, client, loggerFactory: null, ownsHttpClient: true);
    }

    private sealed record PendingPlugin(McpPluginConfig Config, string LastError, int Attempts, DateTimeOffset NextAttempt)
    {
        public McpPluginPending State => new(LastError, Attempts, NextAttempt);
    }

    private sealed record McpClientWrapper(string Name, McpClient Client, McpPluginConfig Config)
    {
        // Reads the state of the input_required results a server answers with; set only when the handler releases waiting calls.
        public McpInputRequiredTap? Tap { get; init; }

        // The names this plugin's last listing returned; replaced whole on each listing, read by IsMcpTool.
        public volatile HashSet<string> ToolNames = new(StringComparer.Ordinal);

        // Of those, the ones the server declared read-only (annotations.readOnlyHint == true); read by IsTrustedReadOnly.
        public volatile HashSet<string> ReadOnlyToolNames = new(StringComparer.Ordinal);

        public void Record(IEnumerable<McpClientTool> tools)
        {
            var listed = tools.ToList();
            ReadOnlyToolNames = listed
                .Where(tool => tool.ProtocolTool.Annotations?.ReadOnlyHint == true)
                .Select(tool => tool.Name)
                .ToHashSet(StringComparer.Ordinal);
            ToolNames = listed.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        }
    }
}
