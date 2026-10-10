using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace IronHive.Agent.Mcp;

/// <summary>
/// Reads MCP plugin documents into <see cref="McpPluginsConfig"/>.
/// </summary>
/// <remarks>
/// <para>
/// One reader serves two shapes. IronHive's own file puts entries under <c>plugins</c>; the snippet an MCP
/// server's documentation gives (Claude Desktop, Cursor and others) puts them under <c>mcpServers</c>, and
/// VS Code's <c>mcp.json</c> under <c>servers</c>. All three may appear in one document. Every entry is read
/// by the same rules:
/// </para>
/// <list type="bullet">
/// <item>The entry's key is its name. A name already read is refused.</item>
/// <item>Keys are case-insensitive and have one spelling per shape: <c>type</c>/<c>transport</c>,
/// <c>command</c>, <c>args</c>/<c>arguments</c>, <c>env</c>/<c>environment</c>,
/// <c>cwd</c>/<c>workingDirectory</c>, <c>url</c>, <c>headers</c>, <c>autoReconnect</c>, <c>timeoutMs</c>,
/// and <c>disabled</c> (or <c>enabled: false</c>), which keeps the entry but adds it to
/// <see cref="McpPluginsConfig.ExcludePlugins"/>.</item>
/// <item>Transport: an explicit <c>type</c> wins — <c>stdio</c>, or <c>http</c> / <c>sse</c> /
/// <c>streamable-http</c> (all read as <see cref="McpTransportType.Http"/>; the client detects the HTTP
/// flavour). Without one, <c>command</c> means stdio and <c>url</c> means HTTP. An entry with both and no
/// <c>type</c>, with neither, with an unknown <c>type</c>, or whose <c>type</c> lacks its <c>command</c> /
/// absolute http(s) <c>url</c> is refused.</item>
/// <item>A value of the wrong JSON type refuses the entry. An unknown key is ignored and reported.</item>
/// <item>An entry without <c>timeoutMs</c> takes the document's <c>defaultTimeoutMs</c>.</item>
/// </list>
/// <para>
/// Problems are collected per entry in <see cref="McpPluginsConfig.Issues"/> instead of stopping at the
/// first; only a document that is not a JSON object throws.
/// </para>
/// </remarks>
public static class McpPluginsConfigLoader
{
    private const int FallbackTimeoutMs = 30000;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly string[] EntrySections = ["plugins", "mcpServers", "servers"];

    /// <summary>
    /// Default configuration file paths (searched in order).
    /// </summary>
    public static readonly string[] DefaultPaths =
    [
        ".ironhive/plugins.yaml",
        ".ironhive/plugins.yml",
        ".ironhive/plugins.json",
        "plugins.yaml",
        "plugins.yml",
        "plugins.json"
    ];

    /// <summary>
    /// Loads MCP plugins configuration from the default locations.
    /// </summary>
    /// <param name="baseDirectory">Base directory to search for config files</param>
    /// <returns>Loaded configuration or default if no file found</returns>
    public static McpPluginsConfig LoadFromDefault(string? baseDirectory = null)
    {
        baseDirectory ??= Directory.GetCurrentDirectory();

        foreach (var relativePath in DefaultPaths)
        {
            var fullPath = Path.Combine(baseDirectory, relativePath);
            if (File.Exists(fullPath))
            {
                return LoadFromFile(fullPath);
            }
        }

        return new McpPluginsConfig();
    }

    /// <summary>
    /// Loads MCP plugins configuration from a specific file (<c>.json</c>, <c>.yaml</c> or <c>.yml</c>).
    /// </summary>
    /// <param name="filePath">Path to the configuration file</param>
    /// <returns>Loaded configuration</returns>
    public static McpPluginsConfig LoadFromFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath, nameof(filePath));

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Configuration file not found: {filePath}", filePath);
        }

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".json" => Parse(File.ReadAllText(filePath)),
            ".yaml" or ".yml" => BindFromConfiguration(new ConfigurationBuilder()
                .AddYamlFile(Path.GetFullPath(filePath), optional: false)
                .Build()),
            _ => throw new NotSupportedException($"Unsupported configuration file format: {extension}")
        };
    }

    /// <summary>
    /// Reads a plugins document from JSON text — IronHive's own file, or a pasted <c>mcpServers</c> /
    /// <c>servers</c> snippet. Comments and trailing commas are allowed.
    /// </summary>
    /// <param name="json">The document text.</param>
    /// <returns>The entries read, with every refused or ignored item in <see cref="McpPluginsConfig.Issues"/>.</returns>
    /// <exception cref="JsonException">The text is not JSON, or its root is not an object.</exception>
    public static McpPluginsConfig Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json, nameof(json));
        using var document = JsonDocument.Parse(json, DocumentOptions);
        return Parse(document.RootElement);
    }

    /// <summary>
    /// Reads a plugins document from a parsed JSON element. See <see cref="Parse(string)"/>.
    /// </summary>
    /// <param name="document">The document's root object.</param>
    /// <returns>The entries read, with every refused or ignored item in <see cref="McpPluginsConfig.Issues"/>.</returns>
    /// <exception cref="JsonException">The element is not an object.</exception>
    public static McpPluginsConfig Parse(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"An MCP plugins document must be a JSON object; got {document.ValueKind}.");
        }

        var reader = new DocumentReader();
        foreach (var property in document.EnumerateObject())
        {
            var key = property.Name;
            if (IsEntrySection(key))
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    reader.Refuse(null, $"'{key}' must be an object of named entries; got {property.Value.ValueKind}.");
                    continue;
                }

                foreach (var entry in property.Value.EnumerateObject())
                {
                    reader.AddEntry(entry.Name, ReadJsonEntry(entry.Name, entry.Value, reader));
                }
            }
            else if (Is(key, "defaultTimeoutMs"))
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var timeout) && timeout > 0)
                {
                    reader.DefaultTimeoutMs = timeout;
                }
                else
                {
                    reader.Ignore(null, "'defaultTimeoutMs' must be a positive integer; the default is kept.");
                }
            }
            else if (Is(key, "autoConnect"))
            {
                if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    reader.AutoConnect = property.Value.GetBoolean();
                }
                else
                {
                    reader.Ignore(null, "'autoConnect' must be true or false; the default is kept.");
                }
            }
            else if (Is(key, "excludePlugins"))
            {
                if (TryReadStrings(property.Value, out var excluded))
                {
                    reader.Exclude.AddRange(excluded);
                }
                else
                {
                    reader.Ignore(null, "'excludePlugins' must be an array of strings; it was ignored.");
                }
            }
            else
            {
                reader.Ignore(null, $"Unknown key '{key}' was ignored.");
            }
        }

        return reader.Build();
    }

    /// <summary>
    /// Reads a plugins document from configuration (for example a YAML file), by the same rules as
    /// <see cref="Parse(string)"/>. Values arrive as strings and are converted.
    /// </summary>
    public static McpPluginsConfig BindFromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration, nameof(configuration));

        var reader = new DocumentReader();
        foreach (var section in configuration.GetChildren())
        {
            var key = section.Key;
            if (IsEntrySection(key))
            {
                foreach (var entry in section.GetChildren())
                {
                    reader.AddEntry(entry.Key, ReadConfigurationEntry(entry.Key, entry, reader));
                }
            }
            else if (Is(key, "defaultTimeoutMs"))
            {
                if (int.TryParse(section.Value, out var timeout) && timeout > 0)
                {
                    reader.DefaultTimeoutMs = timeout;
                }
                else
                {
                    reader.Ignore(null, "'defaultTimeoutMs' must be a positive integer; the default is kept.");
                }
            }
            else if (Is(key, "autoConnect"))
            {
                if (bool.TryParse(section.Value, out var autoConnect))
                {
                    reader.AutoConnect = autoConnect;
                }
                else
                {
                    reader.Ignore(null, "'autoConnect' must be true or false; the default is kept.");
                }
            }
            else if (Is(key, "excludePlugins"))
            {
                reader.Exclude.AddRange(section.GetChildren().Select(c => c.Value).OfType<string>());
            }
            else
            {
                reader.Ignore(null, $"Unknown key '{key}' was ignored.");
            }
        }

        return reader.Build();
    }

    // ── Entry readers: each shape's values into EntryFields; null = refused (the reason is already recorded) ──

    private static EntryFields? ReadJsonEntry(string name, JsonElement value, DocumentReader reader)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            reader.Refuse(name, $"The entry must be an object; got {value.ValueKind}.");
            return null;
        }

        var fields = new EntryFields();
        foreach (var property in value.EnumerateObject())
        {
            var key = property.Name;
            var v = property.Value;
            var field = FieldOf(key);
            switch (field)
            {
                case Field.Type:
                case Field.Command:
                case Field.WorkingDirectory:
                case Field.Url:
                    if (v.ValueKind != JsonValueKind.String)
                    {
                        reader.Refuse(name, $"'{key}' must be a string; got {v.ValueKind}.");
                        return null;
                    }
                    fields.Set(field, v.GetString());
                    break;

                case Field.Arguments:
                    if (!TryReadStrings(v, out var arguments))
                    {
                        reader.Refuse(name, $"'{key}' must be an array of strings.");
                        return null;
                    }
                    fields.Arguments = arguments;
                    break;

                case Field.Environment:
                case Field.Headers:
                    if (!TryReadStringMap(v, out var map))
                    {
                        reader.Refuse(name, $"'{key}' must be an object whose values are strings.");
                        return null;
                    }
                    if (field == Field.Environment)
                    {
                        fields.Environment = map;
                    }
                    else
                    {
                        fields.Headers = map;
                    }
                    break;

                case Field.AutoReconnect:
                case Field.Disabled:
                case Field.Enabled:
                    if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        reader.Refuse(name, $"'{key}' must be true or false; got {v.ValueKind}.");
                        return null;
                    }
                    fields.SetFlag(field, v.GetBoolean());
                    break;

                case Field.TimeoutMs:
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var timeout) || timeout <= 0)
                    {
                        reader.Refuse(name, $"'{key}' must be a positive integer.");
                        return null;
                    }
                    fields.TimeoutMs = timeout;
                    break;

                default:
                    reader.Ignore(name, $"Unknown key '{key}' was ignored.");
                    break;
            }
        }

        return fields;
    }

    private static EntryFields? ReadConfigurationEntry(string name, IConfigurationSection entry, DocumentReader reader)
    {
        var fields = new EntryFields();
        foreach (var child in entry.GetChildren())
        {
            var key = child.Key;
            var field = FieldOf(key);
            switch (field)
            {
                case Field.Type:
                case Field.Command:
                case Field.WorkingDirectory:
                case Field.Url:
                    fields.Set(field, child.Value);
                    break;

                case Field.Arguments:
                    fields.Arguments = child.GetChildren().Select(c => c.Value).OfType<string>().ToList();
                    break;

                case Field.Environment:
                case Field.Headers:
                    var map = child.GetChildren().Where(c => c.Value is not null).ToDictionary(c => c.Key, c => c.Value!);
                    if (field == Field.Environment)
                    {
                        fields.Environment = map;
                    }
                    else
                    {
                        fields.Headers = map;
                    }
                    break;

                case Field.AutoReconnect:
                case Field.Disabled:
                case Field.Enabled:
                    if (!bool.TryParse(child.Value, out var flag))
                    {
                        reader.Refuse(name, $"'{key}' must be true or false; got '{child.Value}'.");
                        return null;
                    }
                    fields.SetFlag(field, flag);
                    break;

                case Field.TimeoutMs:
                    if (!int.TryParse(child.Value, out var timeout) || timeout <= 0)
                    {
                        reader.Refuse(name, $"'{key}' must be a positive integer; got '{child.Value}'.");
                        return null;
                    }
                    fields.TimeoutMs = timeout;
                    break;

                default:
                    reader.Ignore(name, $"Unknown key '{key}' was ignored.");
                    break;
            }
        }

        return fields;
    }

    // ── Shared rules ──

    private static McpPluginConfig? Resolve(string name, EntryFields fields, int defaultTimeoutMs, DocumentReader reader)
    {
        var hasCommand = !string.IsNullOrWhiteSpace(fields.Command);
        var hasUrl = !string.IsNullOrWhiteSpace(fields.Url);

        McpTransportType transport;
        if (!string.IsNullOrWhiteSpace(fields.Type))
        {
            switch (fields.Type.Trim().ToLowerInvariant())
            {
                case "stdio":
                    transport = McpTransportType.Stdio;
                    break;
                case "http":
                case "sse":
                case "streamable-http":
                case "streamablehttp":
                    transport = McpTransportType.Http;
                    break;
                default:
                    reader.Refuse(name, $"Unknown type '{fields.Type}'; expected stdio, http, sse or streamable-http.");
                    return null;
            }

            if (transport == McpTransportType.Stdio && !hasCommand)
            {
                reader.Refuse(name, "Type 'stdio' needs a 'command'.");
                return null;
            }

            if (transport == McpTransportType.Http && !hasUrl)
            {
                reader.Refuse(name, $"Type '{fields.Type}' needs a 'url'.");
                return null;
            }

            if (hasCommand && hasUrl)
            {
                reader.Ignore(name, transport == McpTransportType.Stdio
                    ? "'url' was ignored: the type is stdio."
                    : "'command' was ignored: the type is HTTP.");
            }
        }
        else if (hasCommand && hasUrl)
        {
            reader.Refuse(name, "The entry has both 'command' and 'url'; set 'type' to say which transport to use.");
            return null;
        }
        else if (hasCommand)
        {
            transport = McpTransportType.Stdio;
        }
        else if (hasUrl)
        {
            transport = McpTransportType.Http;
        }
        else
        {
            reader.Refuse(name, "The entry has neither 'command' (stdio) nor 'url' (HTTP).");
            return null;
        }

        if (transport == McpTransportType.Http
            && (!Uri.TryCreate(fields.Url, UriKind.Absolute, out var endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)))
        {
            // The value is not echoed: a URL can carry a token in its query.
            reader.Refuse(name, "'url' must be an absolute http or https URL.");
            return null;
        }

        return transport == McpTransportType.Stdio
            ? new McpPluginConfig
            {
                Transport = transport,
                Command = fields.Command,
                Arguments = fields.Arguments,
                Environment = fields.Environment,
                WorkingDirectory = fields.WorkingDirectory,
                AutoReconnect = fields.AutoReconnect ?? true,
                TimeoutMs = fields.TimeoutMs ?? defaultTimeoutMs
            }
            : new McpPluginConfig
            {
                Transport = transport,
                Url = fields.Url,
                Headers = fields.Headers,
                AutoReconnect = fields.AutoReconnect ?? true,
                TimeoutMs = fields.TimeoutMs ?? defaultTimeoutMs
            };
    }

    private static bool IsEntrySection(string key) => EntrySections.Any(s => Is(key, s));

    private static bool Is(string key, string name) => string.Equals(key, name, StringComparison.OrdinalIgnoreCase);

    private static Field FieldOf(string key) => key.ToLowerInvariant() switch
    {
        "type" or "transport" => Field.Type,
        "command" => Field.Command,
        "args" or "arguments" => Field.Arguments,
        "env" or "environment" => Field.Environment,
        "cwd" or "workingdirectory" => Field.WorkingDirectory,
        "url" => Field.Url,
        "headers" => Field.Headers,
        "autoreconnect" => Field.AutoReconnect,
        "timeoutms" => Field.TimeoutMs,
        "disabled" => Field.Disabled,
        "enabled" => Field.Enabled,
        _ => Field.Unknown
    };

    private static bool TryReadStrings(JsonElement value, out List<string> strings)
    {
        strings = [];
        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            strings.Add(item.GetString()!);
        }
        return true;
    }

    private static bool TryReadStringMap(JsonElement value, out Dictionary<string, string> map)
    {
        map = [];
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            map[property.Name] = property.Value.GetString()!;
        }
        return true;
    }

    private enum Field
    {
        Unknown,
        Type,
        Command,
        Arguments,
        Environment,
        WorkingDirectory,
        Url,
        Headers,
        AutoReconnect,
        TimeoutMs,
        Disabled,
        Enabled
    }

    private sealed class EntryFields
    {
        public string? Type { get; private set; }
        public string? Command { get; private set; }
        public string? WorkingDirectory { get; private set; }
        public string? Url { get; private set; }
        public List<string>? Arguments { get; set; }
        public Dictionary<string, string>? Environment { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
        public bool? AutoReconnect { get; private set; }
        public bool Disabled { get; private set; }
        public int? TimeoutMs { get; set; }

        public void Set(Field field, string? value)
        {
            switch (field)
            {
                case Field.Type: Type = value; break;
                case Field.Command: Command = value; break;
                case Field.WorkingDirectory: WorkingDirectory = value; break;
                case Field.Url: Url = value; break;
            }
        }

        public void SetFlag(Field field, bool value)
        {
            switch (field)
            {
                case Field.AutoReconnect: AutoReconnect = value; break;
                case Field.Disabled: Disabled |= value; break;
                case Field.Enabled: Disabled |= !value; break;
            }
        }
    }

    // Collects entries and issues for one document; entries resolve at Build, once defaultTimeoutMs is known
    // (it may follow the entries in the document).
    private sealed class DocumentReader
    {
        private readonly List<(string Name, EntryFields Fields)> _entries = [];
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);
        private readonly List<McpConfigIssue> _issues = [];

        public int DefaultTimeoutMs { get; set; } = FallbackTimeoutMs;
        public bool AutoConnect { get; set; } = true;
        public List<string> Exclude { get; } = [];

        public void Refuse(string? entry, string message) => _issues.Add(new McpConfigIssue(entry, McpConfigIssueKind.Refused, message));

        public void Ignore(string? entry, string message) => _issues.Add(new McpConfigIssue(entry, McpConfigIssueKind.Ignored, message));

        public void AddEntry(string name, EntryFields? fields)
        {
            if (fields is null)
            {
                return;
            }
            if (!_names.Add(name))
            {
                Refuse(name, "An entry with this name was already read; this one was left out.");
                return;
            }
            _entries.Add((name, fields));
        }

        public McpPluginsConfig Build()
        {
            var plugins = new Dictionary<string, McpPluginConfig>(StringComparer.Ordinal);
            var exclude = new List<string>(Exclude);
            foreach (var (name, fields) in _entries)
            {
                if (Resolve(name, fields, DefaultTimeoutMs, this) is not { } config)
                {
                    continue;
                }
                plugins[name] = config;
                if (fields.Disabled && !exclude.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    exclude.Add(name);
                }
            }

            return new McpPluginsConfig
            {
                Plugins = plugins,
                DefaultTimeoutMs = DefaultTimeoutMs,
                AutoConnect = AutoConnect,
                ExcludePlugins = exclude,
                Issues = _issues
            };
        }
    }
}
