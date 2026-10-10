using System.Text.Json;
using IronHive.Agent.Mcp;
using Microsoft.Extensions.Configuration;

namespace IronHive.Agent.Tests.Mcp;

public class McpPluginsConfigLoaderTests
{
    [Fact]
    public void A_pasted_mcpServers_snippet_reads_as_it_is()
    {
        const string snippet = """
            { "mcpServers": {
                "files":  { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:/docs"], "env": { "KEY": "v" } },
                "remote": { "url": "https://example.test/mcp", "headers": { "Authorization": "Bearer t" } } } }
            """;

        var config = McpPluginsConfigLoader.Parse(snippet);

        Assert.Empty(config.Issues);
        var files = config.Plugins["files"];
        Assert.Equal(McpTransportType.Stdio, files.Transport);
        Assert.Equal("npx", files.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "C:/docs"], files.Arguments);
        Assert.Equal("v", files.Environment!["KEY"]);
        var remote = config.Plugins["remote"];
        Assert.Equal(McpTransportType.Http, remote.Transport);
        Assert.Equal("https://example.test/mcp", remote.Url);
        Assert.Equal("Bearer t", remote.Headers!["Authorization"]);
        Assert.Null(remote.Command);
    }

    [Fact]
    public void VS_Code_servers_with_explicit_types_read_too()
    {
        const string document = """
            { "inputs": [], "servers": {
                "a": { "type": "stdio", "command": "node", "args": ["a.js"], "cwd": "/srv" },
                "b": { "type": "sse", "url": "http://localhost:3000/sse" },
                "c": { "type": "streamable-http", "url": "https://example.test/mcp" } } }
            """;

        var config = McpPluginsConfigLoader.Parse(document);

        Assert.Equal(McpTransportType.Stdio, config.Plugins["a"].Transport);
        Assert.Equal("/srv", config.Plugins["a"].WorkingDirectory);
        Assert.Equal(McpTransportType.Http, config.Plugins["b"].Transport);
        Assert.Equal(McpTransportType.Http, config.Plugins["c"].Transport);
        var issue = Assert.Single(config.Issues);
        Assert.Equal(McpConfigIssueKind.Ignored, issue.Kind);
        Assert.Null(issue.Entry);
        Assert.Contains("'inputs'", issue.Message);
    }

    [Fact]
    public void A_url_only_entry_in_the_plugins_format_is_http_not_a_commandless_stdio_plugin()
    {
        var config = McpPluginsConfigLoader.Parse("""{ "plugins": { "remote": { "url": "https://example.test/mcp" } } }""");

        Assert.Equal(McpTransportType.Http, config.Plugins["remote"].Transport);
        Assert.Empty(config.Issues);
    }

    [Theory]
    [InlineData("""{ "command": "npx", "url": "https://example.test/mcp" }""", "both")]
    [InlineData("""{ "headers": { "a": "b" } }""", "neither")]
    [InlineData("""{ "type": "websocket", "url": "https://example.test/mcp" }""", "Unknown type 'websocket'")]
    [InlineData("""{ "type": "stdio", "url": "https://example.test/mcp" }""", "needs a 'command'")]
    [InlineData("""{ "type": "http", "command": "npx" }""", "needs a 'url'")]
    [InlineData("""{ "url": "example.test/mcp" }""", "absolute http or https URL")]
    [InlineData("""{ "url": "file:///tmp/x" }""", "absolute http or https URL")]
    [InlineData("""{ "command": 42 }""", "'command' must be a string")]
    [InlineData("""{ "command": "npx", "args": ["-y", 1] }""", "'args' must be an array of strings")]
    [InlineData("""{ "command": "npx", "env": { "PORT": 80 } }""", "'env' must be an object whose values are strings")]
    [InlineData("""{ "command": "npx", "timeoutMs": 0 }""", "'timeoutMs' must be a positive integer")]
    [InlineData(""" "npx" """, "must be an object")]
    public void A_bad_entry_is_refused_with_its_reason_and_the_others_still_load(string entry, string reason)
    {
        var config = McpPluginsConfigLoader.Parse($$"""{ "mcpServers": { "bad": {{entry}}, "good": { "command": "npx" } } }""");

        Assert.False(config.Plugins.ContainsKey("bad"));
        Assert.True(config.Plugins.ContainsKey("good"));
        var issue = Assert.Single(config.Issues, i => i.Kind == McpConfigIssueKind.Refused);
        Assert.Equal("bad", issue.Entry);
        Assert.Contains(reason, issue.Message);
    }

    [Fact]
    public void An_explicit_type_wins_over_the_other_transport_s_key_and_says_so()
    {
        var config = McpPluginsConfigLoader.Parse("""{ "mcpServers": { "x": { "type": "http", "url": "https://example.test/mcp", "command": "npx" } } }""");

        Assert.Equal(McpTransportType.Http, config.Plugins["x"].Transport);
        Assert.Null(config.Plugins["x"].Command);
        var issue = Assert.Single(config.Issues);
        Assert.Equal((McpConfigIssueKind.Ignored, "x"), (issue.Kind, issue.Entry));
    }

    [Fact]
    public void An_unknown_key_is_ignored_and_named()
    {
        var config = McpPluginsConfigLoader.Parse("""{ "mcpServers": { "x": { "command": "npx", "envFile": ".env" } } }""");

        Assert.True(config.Plugins.ContainsKey("x"));
        var issue = Assert.Single(config.Issues);
        Assert.Equal((McpConfigIssueKind.Ignored, "x"), (issue.Kind, issue.Entry));
        Assert.Contains("'envFile'", issue.Message);
    }

    [Fact]
    public void A_name_read_twice_keeps_the_first_entry()
    {
        var config = McpPluginsConfigLoader.Parse("""
            { "plugins": { "x": { "command": "first" } }, "mcpServers": { "x": { "command": "second" } } }
            """);

        Assert.Equal("first", config.Plugins["x"].Command);
        var issue = Assert.Single(config.Issues);
        Assert.Equal((McpConfigIssueKind.Refused, "x"), (issue.Kind, issue.Entry));
    }

    [Theory]
    [InlineData("""{ "command": "npx", "disabled": true }""", true)]
    [InlineData("""{ "command": "npx", "enabled": false }""", true)]
    [InlineData("""{ "command": "npx", "disabled": false }""", false)]
    [InlineData("""{ "command": "npx" }""", false)]
    public void A_disabled_entry_is_kept_but_not_auto_connected(string entry, bool excluded)
    {
        var config = McpPluginsConfigLoader.Parse($$"""{ "mcpServers": { "x": {{entry}} } }""");

        Assert.True(config.Plugins.ContainsKey("x"));
        Assert.Equal(excluded, config.ExcludePlugins.Contains("x"));
    }

    [Fact]
    public void An_entry_without_its_own_timeout_takes_the_document_default_wherever_the_default_is_written()
    {
        var config = McpPluginsConfigLoader.Parse("""
            { "plugins": { "a": { "command": "npx" }, "b": { "command": "npx", "timeoutMs": 5000 } }, "defaultTimeoutMs": 60000 }
            """);

        Assert.Equal(60000, config.Plugins["a"].TimeoutMs);
        Assert.Equal(5000, config.Plugins["b"].TimeoutMs);
    }

    [Fact]
    public void The_plugins_format_reads_its_own_keys()
    {
        var config = McpPluginsConfigLoader.Parse("""
            {
              // comments and trailing commas are allowed
              "plugins": {
                "fs": { "transport": "stdio", "command": "npx", "arguments": ["a"], "environment": { "K": "V" },
                        "workingDirectory": "/w", "autoReconnect": false, "timeoutMs": 1000, },
              },
              "autoConnect": false,
              "excludePlugins": ["other"],
            }
            """);

        Assert.Empty(config.Issues);
        Assert.Equal(
            new McpPluginConfig
            {
                Transport = McpTransportType.Stdio, Command = "npx", WorkingDirectory = "/w", AutoReconnect = false, TimeoutMs = 1000,
                Arguments = config.Plugins["fs"].Arguments, Environment = config.Plugins["fs"].Environment
            },
            config.Plugins["fs"]);
        Assert.Equal(["a"], config.Plugins["fs"].Arguments);
        Assert.Equal("V", config.Plugins["fs"].Environment!["K"]);
        Assert.False(config.AutoConnect);
        Assert.Equal(["other"], config.ExcludePlugins);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"mcpServers\"")]
    public void A_document_that_is_not_an_object_throws(string json)
    {
        Assert.Throws<JsonException>(() => McpPluginsConfigLoader.Parse(json));
    }

    [Fact]
    public void Configuration_input_follows_the_same_rules()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["mcpServers:files:command"] = "npx",
            ["mcpServers:files:args:0"] = "-y",
            ["mcpServers:files:args:1"] = "server",
            ["mcpServers:remote:url"] = "https://example.test/mcp",
            ["mcpServers:both:command"] = "npx",
            ["mcpServers:both:url"] = "https://example.test/mcp",
            ["plugins:slow:command"] = "npx",
            ["defaultTimeoutMs"] = "45000"
        }).Build();

        var config = McpPluginsConfigLoader.BindFromConfiguration(configuration);

        Assert.Equal(["-y", "server"], config.Plugins["files"].Arguments);
        Assert.Equal(McpTransportType.Http, config.Plugins["remote"].Transport);
        Assert.Equal(45000, config.Plugins["slow"].TimeoutMs);
        var issue = Assert.Single(config.Issues);
        Assert.Equal((McpConfigIssueKind.Refused, "both"), (issue.Kind, issue.Entry));
    }

    [Fact]
    public void A_json_file_with_an_mcpServers_root_loads_from_the_default_location()
    {
        var dir = Directory.CreateTempSubdirectory("mcp-loader-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, ".ironhive"));
            File.WriteAllText(Path.Combine(dir.FullName, ".ironhive", "plugins.json"),
                """{ "mcpServers": { "files": { "command": "npx" } } }""");

            var config = McpPluginsConfigLoader.LoadFromDefault(dir.FullName);

            Assert.Equal("npx", config.Plugins["files"].Command);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
