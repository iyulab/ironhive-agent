using System.Net;
using System.Text;
using System.Text.Json;
using IronHive.Agent.Mcp;

namespace IronHive.Agent.Tests.Agent.Mcp;

/// <summary>
/// The header sources against a real HTTP MCP endpoint: a server that accepts only the credential it currently holds sees the
/// value the source holds at each request, so a rotated token keeps the plugin authenticated and a stale one is refused.
/// The server is the smallest streamable-HTTP responder that completes the handshake (JSON responses, no event stream).
/// </summary>
public sealed class McpHeaderSourceServerTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Directory.CreateTempSubdirectory("hdrsrv").FullName;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly string _url;

    // What the server accepts right now; a request carrying anything else is answered 401.
    private volatile string _accepted = "Bearer token-1";
    private int _refused;
    private readonly List<string?> _seen = [];

    public McpHeaderSourceServerTests()
    {
        // A free port: bind a listener on port 0 is not possible with HttpListener, so probe one.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _url = $"http://localhost:{port}/mcp";
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // the listener was closed under the loop
        }

        Directory.Delete(_dir, recursive: true);
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var authorization = context.Request.Headers["Authorization"];
            lock (_seen)
            {
                _seen.Add(authorization);
            }

            if (authorization != _accepted)
            {
                Interlocked.Increment(ref _refused);
                response.StatusCode = 401;
                return;
            }

            if (context.Request.HttpMethod != "POST")
            {
                response.StatusCode = 405; // no standalone event stream
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            using var body = JsonDocument.Parse(text);
            var root = body.RootElement;
            if (!root.TryGetProperty("id", out var id))
            {
                response.StatusCode = 202; // a notification
                return;
            }

            var method = root.GetProperty("method").GetString();
            var result = method switch
            {
                "server/discover" => "{\"supportedVersions\":[\"2026-07-28\"],\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1.0\"}}",
                "tools/list" => """{"tools":[{"name":"ping","description":"p","inputSchema":{"type":"object"}}]}""",
                _ => "{}",
            };
            var payload = $$"""{"jsonrpc":"2.0","id":{{id.GetRawText()}},"result":{{result}}}""";
            var bytes = Encoding.UTF8.GetBytes(payload);
            response.ContentType = "application/json";
            await response.OutputStream.WriteAsync(bytes);
        }
        finally
        {
            response.Close();
        }
    }

    private McpPluginConfig WithFileToken(string path) => new()
    {
        Transport = McpTransportType.Http,
        Url = _url,
        AutoReconnect = false,
        TimeoutMs = 10000,
        HeaderSources = new Dictionary<string, IMcpHeaderValueSource> { ["Authorization"] = McpHeaderSources.FromFile(path) },
    };

    [Fact]
    public async Task ARotatedTokenFile_KeepsThePluginAuthenticated_AndAStaleValueIsRefused()
    {
        var path = Path.Combine(_dir, "token");
        await File.WriteAllTextAsync(path, "Bearer token-1", Ct);
        await using var manager = new McpPluginManager();
        await manager.ConnectAsync("remote", WithFileToken(path), Ct);

        var first = await manager.GetToolsAsync("remote", Ct);
        Assert.Contains(first, t => t.Name == "ping");

        // The server rotates; a sidecar rewrites the token file. The next request carries the new value.
        _accepted = "Bearer token-2";
        await File.WriteAllTextAsync(path, "Bearer token-2-rotated-longer", Ct);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        _accepted = "Bearer token-2-rotated-longer";

        var second = await manager.GetToolsAsync("remote", Ct);
        Assert.Contains(second, t => t.Name == "ping");

        // The file is not rewritten while the server moves on: the old value is refused, and the call fails with it.
        _accepted = "Bearer token-3";
        await Assert.ThrowsAnyAsync<Exception>(() => manager.GetToolsAsync("remote", Ct));
        Assert.True(Volatile.Read(ref _refused) >= 1, "the server refused the stale value");

        lock (_seen)
        {
            Assert.Contains("Bearer token-1", _seen);
            Assert.Contains("Bearer token-2-rotated-longer", _seen);
        }
    }
}
