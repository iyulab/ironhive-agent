using IronHive.Agent.Mcp;

namespace IronHive.Agent.Tests.Agent.Mcp;

/// <summary>
/// A header whose value is read from a source before every request: a credential that expires and is refreshed by something
/// else keeps a plugin authenticated for longer than one credential's life.
/// </summary>
public sealed class McpHeaderSourceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Directory.CreateTempSubdirectory("hdr").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Capture : HttpMessageHandler
    {
        public List<string?> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request.Headers.TryGetValues("Authorization", out var values) ? string.Join(",", values) : null);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class Counting(Func<int, string?> valueAt) : IMcpHeaderValueSource
    {
        private int _n;

        public ValueTask<string?> GetAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(valueAt(++_n));
    }

    private sealed class Throwing(string secret) : IMcpHeaderValueSource
    {
        public ValueTask<string?> GetAsync(CancellationToken cancellationToken = default) =>
            throw new IOException("cannot read " + secret);
    }

    private static HttpClient ClientWith(Capture inner, string header, IMcpHeaderValueSource source) =>
        new(new McpHeaderSourceHandler("remote", new Dictionary<string, IMcpHeaderValueSource> { [header] = source }, inner));

    [Fact]
    public async Task EachRequestCarriesTheValueTheSourceHoldsThen()
    {
        var capture = new Capture();
        using var client = ClientWith(capture, "Authorization", new Counting(n => $"Bearer token-{n}"));

        await client.GetAsync("https://example.test/mcp", Ct);
        await client.GetAsync("https://example.test/mcp", Ct);

        Assert.Equal(["Bearer token-1", "Bearer token-2"], capture.Seen);
    }

    [Fact]
    public async Task AFileSource_IsReReadWhenTheFileChanges_AndNotBefore()
    {
        var path = Path.Combine(_dir, "token");
        await File.WriteAllTextAsync(path, "Bearer one\n", Ct);
        var capture = new Capture();
        using var client = ClientWith(capture, "Authorization", McpHeaderSources.FromFile(path));

        await client.GetAsync("https://example.test/mcp", Ct);
        await client.GetAsync("https://example.test/mcp", Ct);
        await File.WriteAllTextAsync(path, "Bearer two-longer", Ct);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        await client.GetAsync("https://example.test/mcp", Ct);

        Assert.Equal(["Bearer one", "Bearer one", "Bearer two-longer"], capture.Seen);
    }

    [Fact]
    public async Task AnEnvironmentSource_ReadsTheVariableOnEveryRequest()
    {
        var name = "MCP_TEST_TOKEN_" + Guid.NewGuid().ToString("N");
        var capture = new Capture();
        using var client = ClientWith(capture, "Authorization", McpHeaderSources.FromEnvironment(name));
        try
        {
            Environment.SetEnvironmentVariable(name, "Bearer a");
            await client.GetAsync("https://example.test/mcp", Ct);
            Environment.SetEnvironmentVariable(name, "Bearer b");
            await client.GetAsync("https://example.test/mcp", Ct);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        Assert.Equal(["Bearer a", "Bearer b"], capture.Seen);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ASourceWithNoValue_FailsTheRequest_NamingTheHeader_AndSendsNothing(string? value)
    {
        var capture = new Capture();
        using var client = ClientWith(capture, "Authorization", new Counting(_ => value));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://example.test/mcp", Ct));

        Assert.Contains("'Authorization'", ex.Message);
        Assert.Empty(capture.Seen);
    }

    [Fact]
    public async Task ASourceThatThrows_FailsTheRequest_WithoutQuotingWhatItSaid()
    {
        using var client = ClientWith(new Capture(), "Authorization", new Throwing("s3cret-token"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://example.test/mcp", Ct));

        Assert.Contains("'Authorization'", ex.Message);
        Assert.DoesNotContain("s3cret-token", ex.Message);
    }

    [Fact]
    public void AHeaderInBothMaps_IsRejectedWhenTheTransportIsBuilt()
    {
        var config = new McpPluginConfig
        {
            Transport = McpTransportType.Http,
            Url = "https://example.test/mcp",
            Headers = new Dictionary<string, string> { ["authorization"] = "Bearer fixed" },
            HeaderSources = new Dictionary<string, IMcpHeaderValueSource> { ["Authorization"] = McpHeaderSources.FromEnvironment("X") },
        };

        var ex = Assert.Throws<ArgumentException>(() => McpPluginManager.CreateHttpTransport("remote", config));

        Assert.Contains("Authorization", ex.Message);
    }

    [Fact]
    public void WithoutSources_TheTransportIsBuiltAsBefore()
    {
        var config = new McpPluginConfig
        {
            Transport = McpTransportType.Http,
            Url = "https://example.test/mcp",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer fixed" },
        };

        Assert.NotNull(McpPluginManager.CreateHttpTransport("remote", config));
    }

    [Fact]
    public void ThePluginFileNamesSourcesInHeaderSources()
    {
        var config = McpPluginsConfigLoader.Parse("""
            { "mcpServers": { "remote": { "url": "https://example.test/mcp",
                "headers": { "X-Fixed": "1" },
                "headerSources": { "Authorization": "file:/run/token", "X-Key": "env:MCP_KEY" } } } }
            """);

        Assert.Empty(config.Issues);
        var sources = config.Plugins["remote"].HeaderSources!;
        Assert.Equal(McpHeaderSources.FromFile("/run/token"), sources["Authorization"]);
        Assert.Equal(McpHeaderSources.FromEnvironment("MCP_KEY"), sources["X-Key"]);
        Assert.Equal("1", config.Plugins["remote"].Headers!["X-Fixed"]);
    }

    [Theory]
    [InlineData("""{ "url": "https://example.test/mcp", "headers": { "Authorization": "x" }, "headerSources": { "authorization": "env:K" } }""", "both")]
    [InlineData("""{ "url": "https://example.test/mcp", "headerSources": { "Authorization": "Bearer s3cret" } }""", "must be 'env:NAME' or 'file:PATH'")]
    [InlineData("""{ "url": "https://example.test/mcp", "headerSources": { "Authorization": 5 } }""", "'headerSources' must be an object whose values are strings")]
    public void ABadHeaderSources_IsRefused_WithoutEchoingTheText(string entry, string reason)
    {
        var config = McpPluginsConfigLoader.Parse($$"""{ "mcpServers": { "bad": {{entry}}, "good": { "command": "npx" } } }""");

        Assert.False(config.Plugins.ContainsKey("bad"));
        var issue = Assert.Single(config.Issues, i => i.Kind == McpConfigIssueKind.Refused);
        Assert.Contains(reason, issue.Message);
        Assert.DoesNotContain("s3cret", issue.Message);
    }

    [Fact]
    public void EditingAHeaderOrItsSourceInThePluginFile_IsAChangedConfiguration()
    {
        var baseline = new McpPluginConfig { Transport = McpTransportType.Http, Url = "https://example.test/mcp" };
        McpPluginConfig WithSource(string path) => baseline with
        {
            HeaderSources = new Dictionary<string, IMcpHeaderValueSource> { ["A"] = McpHeaderSources.FromFile(path) },
        };

        Assert.True(McpPluginHotReloader.ConfigEquals(WithSource("/t"), WithSource("/t")), "the same source named again is not a change");
        Assert.False(McpPluginHotReloader.ConfigEquals(WithSource("/t"), WithSource("/u")));
        Assert.False(McpPluginHotReloader.ConfigEquals(baseline, baseline with { Headers = new Dictionary<string, string> { ["X"] = "2" } }));
        Assert.False(McpPluginHotReloader.ConfigEquals(baseline, WithSource("/t")));
        Assert.True(McpPluginHotReloader.ConfigEquals(baseline, baseline with { HeaderSources = new Dictionary<string, IMcpHeaderValueSource>() }));
    }
}
