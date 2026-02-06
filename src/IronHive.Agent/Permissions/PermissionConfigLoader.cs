using System.Text.Json;
using System.Text.Json.Serialization;

namespace IronHive.Agent.Permissions;

/// <summary>
/// Loads permission configuration from YAML or JSON files.
/// </summary>
public static class PermissionConfigLoader
{
    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true
    };

    /// <summary>
    /// Loads permission configuration from a JSON file.
    /// </summary>
    /// <param name="filePath">Path to the JSON file.</param>
    /// <returns>Loaded configuration or default if file doesn't exist.</returns>
    public static PermissionConfig LoadFromJson(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return PermissionConfig.CreateDefault();
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var wrapper = JsonSerializer.Deserialize<PermissionConfigWrapper>(json, JsonReadOptions);
            return wrapper?.Permissions ?? PermissionConfig.CreateDefault();
        }
        catch
        {
            return PermissionConfig.CreateDefault();
        }
    }

    /// <summary>
    /// Loads permission configuration from a YAML file.
    /// Note: Requires NetEscapades.Configuration.Yaml for full YAML support.
    /// This is a simplified YAML parser for the permission format.
    /// </summary>
    /// <param name="filePath">Path to the YAML file.</param>
    /// <returns>Loaded configuration or default if file doesn't exist.</returns>
    public static PermissionConfig LoadFromYaml(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return PermissionConfig.CreateDefault();
        }

        try
        {
            var yaml = File.ReadAllText(filePath);
            return ParseYaml(yaml);
        }
        catch
        {
            return PermissionConfig.CreateDefault();
        }
    }

    /// <summary>
    /// Loads permission configuration from any supported file format.
    /// Determines format from file extension.
    /// </summary>
    /// <param name="filePath">Path to the configuration file.</param>
    /// <returns>Loaded configuration or default if file doesn't exist.</returns>
    public static PermissionConfig Load(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".json" => LoadFromJson(filePath),
            ".yaml" or ".yml" => LoadFromYaml(filePath),
            _ => PermissionConfig.CreateDefault()
        };
    }

    /// <summary>
    /// Loads permission configuration from the default locations.
    /// Searches in order: .ironhive/permissions.yaml, .ironhive/permissions.json
    /// </summary>
    /// <param name="workingDirectory">Working directory to search from.</param>
    /// <returns>Loaded configuration or default if no file found.</returns>
    public static PermissionConfig LoadFromDefaultLocations(string workingDirectory)
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

    /// <summary>
    /// Simple YAML parser for permission configuration.
    /// Handles the specific format used for permissions.
    /// Uses relative indentation levels instead of absolute values for flexibility.
    /// </summary>
    private static PermissionConfig ParseYaml(string yaml)
    {
        var config = new PermissionConfig();
        var lines = yaml.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        int baseIndent = -1;  // Will be set when we find 'permissions:'
        int sectionIndent = -1;  // Indent level for sections (read:, edit:, etc.)
        int ruleStartIndent = -1;  // Indent level for rule start (- pattern:)
        int rulePropertyIndent = -1;  // Indent level for rule properties (action:, priority:)

        List<PermissionRule>? currentRules = null;
        PermissionRule? currentRule = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var indent = line.Length - line.TrimStart().Length;
            var trimmed = line.Trim();

            // Top-level: permissions:
            if (trimmed == "permissions:")
            {
                baseIndent = indent;
                continue;
            }

            // Detect section level dynamically
            if (baseIndent >= 0 && sectionIndent < 0 && indent > baseIndent && trimmed.EndsWith(':'))
            {
                sectionIndent = indent;
            }

            // Section level (read:, edit:, bash:, etc.)
            if (sectionIndent >= 0 && indent == sectionIndent && trimmed.EndsWith(':'))
            {
                // Save current rule before switching sections
                if (currentRule != null && currentRules != null)
                {
                    currentRules.Add(currentRule);
                    currentRule = null;
                }

                var section = trimmed.TrimEnd(':');
                currentRules = section switch
                {
                    "read" => config.Read,
                    "edit" => config.Edit,
                    "bash" => config.Bash,
                    "external_directory" => config.ExternalDirectory,
                    "mcp_tools" => config.McpTools,
                    _ => null
                };
                continue;
            }

            // Default action at section level
            if (sectionIndent >= 0 && indent == sectionIndent && trimmed.StartsWith("default_action:", StringComparison.Ordinal))
            {
                var value = trimmed["default_action:".Length..].Trim();
                config.DefaultAction = ParseAction(value);
                continue;
            }

            // Detect rule start indent dynamically
            if (sectionIndent >= 0 && ruleStartIndent < 0 && indent > sectionIndent && trimmed.StartsWith("- pattern:", StringComparison.Ordinal))
            {
                ruleStartIndent = indent;
            }

            // Rule start (- pattern:)
            if (ruleStartIndent >= 0 && indent == ruleStartIndent && trimmed.StartsWith("- pattern:", StringComparison.Ordinal) && currentRules != null)
            {
                // Save previous rule
                if (currentRule != null)
                {
                    currentRules.Add(currentRule);
                }

                var pattern = ExtractValue(trimmed, "- pattern:");
                currentRule = new PermissionRule { Pattern = pattern };
                rulePropertyIndent = -1;  // Reset for next rule's properties
                continue;
            }

            // Detect rule property indent dynamically
            if (ruleStartIndent >= 0 && rulePropertyIndent < 0 && indent > ruleStartIndent && currentRule != null)
            {
                rulePropertyIndent = indent;
            }

            // Rule properties
            if (rulePropertyIndent >= 0 && indent == rulePropertyIndent && currentRule != null)
            {
                if (trimmed.StartsWith("action:", StringComparison.Ordinal))
                {
                    var action = ParseAction(ExtractValue(trimmed, "action:"));
                    currentRule = currentRule with { Action = action };
                }
                else if (trimmed.StartsWith("priority:", StringComparison.Ordinal))
                {
                    if (int.TryParse(ExtractValue(trimmed, "priority:"), out var priority))
                    {
                        currentRule = currentRule with { Priority = priority };
                    }
                }
                else if (trimmed.StartsWith("reason:", StringComparison.Ordinal))
                {
                    currentRule = currentRule with { Reason = ExtractValue(trimmed, "reason:") };
                }
            }
        }

        // Add last rule if exists
        if (currentRule != null && currentRules != null)
        {
            currentRules.Add(currentRule);
        }

        return config;
    }

    private static string ExtractValue(string line, string prefix)
    {
        var value = line[prefix.Length..].Trim();
        // Remove quotes if present
        if ((value.StartsWith('"') && value.EndsWith('"')) ||
            (value.StartsWith('\'') && value.EndsWith('\'')))
        {
            value = value[1..^1];
        }
        return value;
    }

    private static PermissionAction ParseAction(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "allow" => PermissionAction.Allow,
            "deny" => PermissionAction.Deny,
            "ask" => PermissionAction.Ask,
            _ => PermissionAction.Ask
        };
    }

    private sealed class PermissionConfigWrapper
    {
        public PermissionConfig? Permissions { get; set; }
    }
}
