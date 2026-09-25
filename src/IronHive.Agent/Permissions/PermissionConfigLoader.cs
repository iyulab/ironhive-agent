using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace IronHive.Agent.Permissions;

/// <summary>
/// Loads permission configuration from YAML or JSON files. A missing file yields
/// <see cref="PermissionConfig.CreateDefault"/>; a file that exists but is not a permission configuration throws
/// <see cref="PermissionConfigException"/> — the defaults allow more than a restrictive file would, so a typo must not
/// silently widen what the agent may do.
/// </summary>
public static class PermissionConfigLoader
{
    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // A misspelled key is an error, not a dropped rule list.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true
    };

    // Unmatched keys throw: a misspelled section (raed:) is an error, not a dropped rule list.
    private static readonly IDeserializer YamlReader = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    /// <summary>
    /// Loads permission configuration from a JSON file: <c>{ "permissions": { "read": [...], ... } }</c>.
    /// </summary>
    /// <param name="filePath">Path to the JSON file.</param>
    /// <returns>The file's configuration, or <see cref="PermissionConfig.CreateDefault"/> when the file does not exist.</returns>
    /// <exception cref="PermissionConfigException">
    /// The file exists but is not a permission configuration: malformed JSON, no <c>permissions</c> object, or a key or
    /// action this configuration does not have.
    /// </exception>
    public static PermissionConfig LoadFromJson(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return PermissionConfig.CreateDefault();
        }

        PermissionConfigWrapper? wrapper;
        try
        {
            wrapper = JsonSerializer.Deserialize<PermissionConfigWrapper>(File.ReadAllText(filePath), JsonReadOptions);
        }
        catch (JsonException ex)
        {
            throw new PermissionConfigException(filePath, ex.Message, ex);
        }

        return wrapper?.Permissions
            ?? throw new PermissionConfigException(filePath, "no 'permissions' object");
    }

    /// <summary>
    /// Loads permission configuration from a YAML file: a <c>permissions:</c> section with <c>read</c>, <c>edit</c>,
    /// <c>bash</c>, <c>external_directory</c>, <c>mcp_tools</c> and <c>tools</c> (lists of rules — <c>pattern</c>,
    /// <c>action</c>, <c>priority</c>, <c>reason</c>), <c>read_only_tools</c> (a list of tool names) and
    /// <c>default_action</c>.
    /// </summary>
    /// <param name="filePath">Path to the YAML file.</param>
    /// <returns>The file's configuration, or <see cref="PermissionConfig.CreateDefault"/> when the file does not exist.</returns>
    /// <exception cref="PermissionConfigException">
    /// The file exists but is not a permission configuration: malformed YAML, no <c>permissions</c> section, or a
    /// section, key or action this configuration does not have.
    /// </exception>
    public static PermissionConfig LoadFromYaml(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return PermissionConfig.CreateDefault();
        }

        YamlPermissionFile? file;
        try
        {
            file = YamlReader.Deserialize<YamlPermissionFile?>(File.ReadAllText(filePath));
        }
        catch (YamlException ex)
        {
            throw new PermissionConfigException(filePath, ex.InnerException?.Message ?? ex.Message, ex);
        }

        if (file?.Permissions is not { } section)
        {
            throw new PermissionConfigException(filePath, "no 'permissions' section");
        }

        return new PermissionConfig
        {
            Read = ToRules(filePath, "read", section.Read),
            Edit = ToRules(filePath, "edit", section.Edit),
            Bash = ToRules(filePath, "bash", section.Bash),
            ExternalDirectory = ToRules(filePath, "external_directory", section.ExternalDirectory),
            McpTools = ToRules(filePath, "mcp_tools", section.McpTools),
            Tools = ToRules(filePath, "tools", section.Tools),
            ReadOnlyTools = section.ReadOnlyTools ?? [],
            DefaultAction = section.DefaultAction is null
                ? PermissionAction.Ask
                : ParseAction(filePath, "default_action", section.DefaultAction)
        };
    }

    /// <summary>
    /// Loads permission configuration from a <c>.json</c>, <c>.yaml</c> or <c>.yml</c> file.
    /// </summary>
    /// <param name="filePath">Path to the configuration file.</param>
    /// <returns>The file's configuration, or <see cref="PermissionConfig.CreateDefault"/> when the file does not exist.</returns>
    /// <exception cref="PermissionConfigException">The file exists and is not a readable permission configuration.</exception>
    /// <exception cref="ArgumentException">The extension is none of the three.</exception>
    public static PermissionConfig Load(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".json" => LoadFromJson(filePath),
            ".yaml" or ".yml" => LoadFromYaml(filePath),
            _ => throw new ArgumentException(
                $"Permission configuration '{filePath}' has an unsupported extension '{extension}' (expected .json, .yaml or .yml).",
                nameof(filePath))
        };
    }

    /// <summary>
    /// Loads permission configuration from the default locations.
    /// Searches in order: .ironhive/permissions.yaml, .ironhive/permissions.yml, .ironhive/permissions.json
    /// </summary>
    /// <param name="workingDirectory">Working directory to search from.</param>
    /// <returns>
    /// Loaded configuration or default if no file found. Its <see cref="PermissionConfig.WorkingDirectory"/>
    /// is <paramref name="workingDirectory"/>: the rules were found relative to it, and they describe
    /// paths relative to it.
    /// </returns>
    /// <exception cref="PermissionConfigException">The file found is not a readable permission configuration.</exception>
    public static PermissionConfig LoadFromDefaultLocations(string workingDirectory)
    {
        var config = LoadFromDefaultLocationsCore(workingDirectory);
        config.WorkingDirectory ??= workingDirectory;
        return config;
    }

    private static PermissionConfig LoadFromDefaultLocationsCore(string workingDirectory)
    {
        var searchPaths = new[]
        {
            Path.Combine(workingDirectory, ".ironhive", "permissions.yaml"),
            Path.Combine(workingDirectory, ".ironhive", "permissions.yml"),
            Path.Combine(workingDirectory, ".ironhive", "permissions.json"),
        };

        foreach (var path in searchPaths)
        {
            if (File.Exists(path))
            {
                return Load(path);
            }
        }

        return PermissionConfig.CreateDefault();
    }

    /// <summary>
    /// Saves permission configuration to a JSON file.
    /// </summary>
    public static void SaveToJson(PermissionConfig config, string filePath)
    {
        var wrapper = new PermissionConfigWrapper { Permissions = config };
        var json = JsonSerializer.Serialize(wrapper, JsonWriteOptions);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(filePath, json);
    }

    private static List<PermissionRule> ToRules(string filePath, string section, List<YamlPermissionRule>? rules)
    {
        if (rules is null)
        {
            return [];
        }

        var result = new List<PermissionRule>(rules.Count);
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern))
            {
                throw new PermissionConfigException(filePath, $"a rule in '{section}' has no pattern");
            }

            result.Add(new PermissionRule
            {
                Pattern = rule.Pattern,
                Action = rule.Action is null ? PermissionAction.Ask : ParseAction(filePath, section, rule.Action),
                Priority = rule.Priority ?? 0,
                Reason = rule.Reason
            });
        }
        return result;
    }

    private static PermissionAction ParseAction(string filePath, string where, string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "allow" => PermissionAction.Allow,
            "deny" => PermissionAction.Deny,
            "ask" => PermissionAction.Ask,
            _ => throw new PermissionConfigException(filePath, $"'{value}' in '{where}' is not an action (allow, deny or ask)")
        };

    private sealed class PermissionConfigWrapper
    {
        public PermissionConfig? Permissions { get; set; }
    }

    private sealed class YamlPermissionFile
    {
        public YamlPermissionSection? Permissions { get; set; }
    }

    private sealed class YamlPermissionSection
    {
        public List<YamlPermissionRule>? Read { get; set; }
        public List<YamlPermissionRule>? Edit { get; set; }
        public List<YamlPermissionRule>? Bash { get; set; }
        public List<YamlPermissionRule>? ExternalDirectory { get; set; }
        public List<YamlPermissionRule>? McpTools { get; set; }
        public List<YamlPermissionRule>? Tools { get; set; }
        public List<string>? ReadOnlyTools { get; set; }
        public string? DefaultAction { get; set; }
    }

    private sealed class YamlPermissionRule
    {
        public string? Pattern { get; set; }
        public string? Action { get; set; }
        public int? Priority { get; set; }
        public string? Reason { get; set; }
    }
}
