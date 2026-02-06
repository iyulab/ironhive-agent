using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace IronHive.Agent.Mcp;

/// <summary>
/// Loads MCP plugins configuration from files.
/// </summary>
public static class McpPluginsConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

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
    /// Loads MCP plugins configuration from a specific file.
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
            ".json" => LoadFromJson(filePath),
            ".yaml" or ".yml" => LoadFromYaml(filePath),
            _ => throw new NotSupportedException($"Unsupported configuration file format: {extension}")
        };
    }

    /// <summary>
    /// Loads configuration from a JSON file.
    /// </summary>
    private static McpPluginsConfig LoadFromJson(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var rawConfig = JsonSerializer.Deserialize<RawPluginsConfig>(json, JsonOptions);
        return ConvertToConfig(rawConfig);
    }

    /// <summary>
    /// Loads configuration from a YAML file using Microsoft.Extensions.Configuration.
    /// </summary>
    private static McpPluginsConfig LoadFromYaml(string filePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddYamlFile(filePath, optional: false)
            .Build();

        return BindFromConfiguration(configuration);
    }

    /// <summary>
    /// Binds configuration from IConfiguration.
    /// </summary>
    public static McpPluginsConfig BindFromConfiguration(IConfiguration configuration)
    {
        var plugins = new Dictionary<string, McpPluginConfig>();

        var pluginsSection = configuration.GetSection("plugins");
        foreach (var pluginSection in pluginsSection.GetChildren())
        {
            var name = pluginSection.Key;
            var pluginConfig = new McpPluginConfig
            {
                Transport = Enum.TryParse<McpTransportType>(
                    pluginSection["transport"], true, out var transport)
                    ? transport
                    : McpTransportType.Stdio,
                Command = pluginSection["command"],
                Arguments = pluginSection.GetSection("arguments")
                    .GetChildren()
                    .Select(c => c.Value!)
                    .Where(v => v != null)
                    .ToList(),
                Environment = pluginSection.GetSection("environment")
                    .GetChildren()
                    .Where(c => c.Value != null)
                    .ToDictionary(c => c.Key, c => c.Value!),
                WorkingDirectory = pluginSection["workingDirectory"],
                Url = pluginSection["url"],
                AutoReconnect = bool.TryParse(pluginSection["autoReconnect"], out var autoReconnect)
                    ? autoReconnect
                    : true,
                TimeoutMs = int.TryParse(pluginSection["timeoutMs"], out var timeout)
                    ? timeout
                    : 30000
            };

            plugins[name] = pluginConfig;
        }

        return new McpPluginsConfig
        {
            Plugins = plugins,
            DefaultTimeoutMs = int.TryParse(configuration["defaultTimeoutMs"], out var defTimeout)
                ? defTimeout
                : 30000,
            AutoConnect = bool.TryParse(configuration["autoConnect"], out var autoConnect)
                ? autoConnect
                : true,
            ExcludePlugins = configuration.GetSection("excludePlugins")
                .GetChildren()
                .Select(c => c.Value!)
                .Where(v => v != null)
                .ToList()
        };
    }

    private static McpPluginsConfig ConvertToConfig(RawPluginsConfig? raw)
    {
        if (raw == null)
        {
            return new McpPluginsConfig();
        }

        var plugins = new Dictionary<string, McpPluginConfig>();

        if (raw.Plugins != null)
        {
            foreach (var (name, rawPlugin) in raw.Plugins)
            {
                plugins[name] = new McpPluginConfig
                {
                    Transport = Enum.TryParse<McpTransportType>(rawPlugin.Transport, true, out var t)
                        ? t
                        : McpTransportType.Stdio,
                    Command = rawPlugin.Command,
                    Arguments = rawPlugin.Arguments,
                    Environment = rawPlugin.Environment,
                    WorkingDirectory = rawPlugin.WorkingDirectory,
                    Url = rawPlugin.Url,
                    AutoReconnect = rawPlugin.AutoReconnect ?? true,
                    TimeoutMs = rawPlugin.TimeoutMs ?? 30000
                };
            }
        }

        return new McpPluginsConfig
        {
            Plugins = plugins,
            DefaultTimeoutMs = raw.DefaultTimeoutMs ?? 30000,
            AutoConnect = raw.AutoConnect ?? true,
            ExcludePlugins = raw.ExcludePlugins ?? []
        };
    }

    // Raw config for JSON deserialization
    private sealed class RawPluginsConfig
    {
        public Dictionary<string, RawPluginConfig>? Plugins { get; set; }
        public int? DefaultTimeoutMs { get; set; }
        public bool? AutoConnect { get; set; }
        public List<string>? ExcludePlugins { get; set; }
    }

    private sealed class RawPluginConfig
    {
        public string? Transport { get; set; }
        public string? Command { get; set; }
        public List<string>? Arguments { get; set; }
        public Dictionary<string, string>? Environment { get; set; }
        public string? WorkingDirectory { get; set; }
        public string? Url { get; set; }
        public bool? AutoReconnect { get; set; }
        public int? TimeoutMs { get; set; }
    }
}
