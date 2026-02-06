using IronHive.Agent.Permissions;

namespace IronHive.Agent.Tests.Permissions;

/// <summary>
/// Cycle 4: 권한 시스템 - YAML/JSON 설정 로딩
/// </summary>
public class PermissionConfigLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public PermissionConfigLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ironhive-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    #region YAML Loading

    [Fact]
    public void LoadFromYaml_ValidFile_ParsesCorrectly()
    {
        // Arrange
        var yamlContent = """
            permissions:
              read:
                - pattern: "**/*"
                  action: allow
                - pattern: "*.env"
                  action: ask
                  priority: 10
                  reason: "환경 변수 파일"
              bash:
                - pattern: "git *"
                  action: allow
                - pattern: "rm -rf *"
                  action: deny
                  priority: 100
              default_action: ask
            """;
        var filePath = Path.Combine(_tempDir, "permissions.yaml");
        File.WriteAllText(filePath, yamlContent);

        // Act
        var config = PermissionConfigLoader.LoadFromYaml(filePath);

        // Assert
        Assert.Equal(2, config.Read.Count);
        Assert.Equal("**/*", config.Read[0].Pattern);
        Assert.Equal(PermissionAction.Allow, config.Read[0].Action);
        Assert.Equal("*.env", config.Read[1].Pattern);
        Assert.Equal(PermissionAction.Ask, config.Read[1].Action);
        Assert.Equal(10, config.Read[1].Priority);
        Assert.Equal("환경 변수 파일", config.Read[1].Reason);

        Assert.Equal(2, config.Bash.Count);
        Assert.Equal(PermissionAction.Deny, config.Bash[1].Action);
        Assert.Equal(100, config.Bash[1].Priority);

        Assert.Equal(PermissionAction.Ask, config.DefaultAction);
    }

    [Fact]
    public void LoadFromYaml_WithQuotes_ParsesCorrectly()
    {
        // Arrange - patterns with quotes
        var yamlContent = """
            permissions:
              read:
                - pattern: "*.env"
                  action: "allow"
                - pattern: '**/*.cs'
                  action: 'ask'
            """;
        var filePath = Path.Combine(_tempDir, "permissions.yaml");
        File.WriteAllText(filePath, yamlContent);

        // Act
        var config = PermissionConfigLoader.LoadFromYaml(filePath);

        // Assert
        Assert.Equal(2, config.Read.Count);
        Assert.Equal("*.env", config.Read[0].Pattern);
        Assert.Equal(PermissionAction.Allow, config.Read[0].Action);
        Assert.Equal("**/*.cs", config.Read[1].Pattern);
        Assert.Equal(PermissionAction.Ask, config.Read[1].Action);
    }

    [Fact]
    public void LoadFromYaml_WithComments_IgnoresComments()
    {
        // Arrange
        var yamlContent = """
            # Permission configuration
            permissions:
              # Read rules
              read:
                - pattern: "*.md"
                  action: allow  # Always allow markdown files
            """;
        var filePath = Path.Combine(_tempDir, "permissions.yaml");
        File.WriteAllText(filePath, yamlContent);

        // Act
        var config = PermissionConfigLoader.LoadFromYaml(filePath);

        // Assert
        Assert.Single(config.Read);
        Assert.Equal("*.md", config.Read[0].Pattern);
    }

    [Fact]
    public void LoadFromYaml_FileNotFound_ReturnsDefault()
    {
        // Act
        var config = PermissionConfigLoader.LoadFromYaml(Path.Combine(_tempDir, "nonexistent.yaml"));

        // Assert
        Assert.NotNull(config);
        Assert.NotEmpty(config.Read); // Default has rules
    }

    [Fact]
    public void LoadFromYaml_InvalidYaml_ReturnsDefault()
    {
        // Arrange - completely invalid YAML
        var yamlContent = "this is not: : : valid yaml {{}}";
        var filePath = Path.Combine(_tempDir, "invalid.yaml");
        File.WriteAllText(filePath, yamlContent);

        // Act
        var config = PermissionConfigLoader.LoadFromYaml(filePath);

        // Assert - should return default config, not throw
        Assert.NotNull(config);
    }

    #endregion

    #region JSON Loading

    [Fact]
    public void LoadFromJson_ValidFile_ParsesCorrectly()
    {
        // Arrange
        var jsonContent = """
            {
              "permissions": {
                "read": [
                  { "pattern": "**/*", "action": "allow" },
                  { "pattern": "*.env", "action": "ask", "priority": 10 }
                ],
                "bash": [
                  { "pattern": "git *", "action": "allow" }
                ],
                "defaultAction": "ask"
              }
            }
            """;
        var filePath = Path.Combine(_tempDir, "permissions.json");
        File.WriteAllText(filePath, jsonContent);

        // Act
        var config = PermissionConfigLoader.LoadFromJson(filePath);

        // Assert
        Assert.Equal(2, config.Read.Count);
        Assert.Equal("**/*", config.Read[0].Pattern);
        Assert.Single(config.Bash);
    }

    [Fact]
    public void LoadFromJson_WithComments_ParsesCorrectly()
    {
        // Arrange - JSON with comments (JsonCommentHandling.Skip)
        var jsonContent = """
            {
              // This is a comment
              "permissions": {
                "read": [
                  { "pattern": "*.md", "action": "allow" }
                ]
              }
            }
            """;
        var filePath = Path.Combine(_tempDir, "permissions.json");
        File.WriteAllText(filePath, jsonContent);

        // Act
        var config = PermissionConfigLoader.LoadFromJson(filePath);

        // Assert
        Assert.Single(config.Read);
    }

    [Fact]
    public void LoadFromJson_FileNotFound_ReturnsDefault()
    {
        // Act
        var config = PermissionConfigLoader.LoadFromJson(Path.Combine(_tempDir, "nonexistent.json"));

        // Assert
        Assert.NotNull(config);
        Assert.NotEmpty(config.Read);
    }

    #endregion

    #region Load (auto-detect format)

    [Fact]
    public void Load_YamlExtension_UsesYamlLoader()
    {
        // Arrange
        var yamlContent = """
            permissions:
              read:
                - pattern: "test.yaml"
                  action: allow
            """;
        var filePath = Path.Combine(_tempDir, "permissions.yaml");
        File.WriteAllText(filePath, yamlContent);

        // Act
        var config = PermissionConfigLoader.Load(filePath);

        // Assert
        Assert.Single(config.Read);
        Assert.Equal("test.yaml", config.Read[0].Pattern);
    }

    [Fact]
    public void Load_YmlExtension_UsesYamlLoader()
    {
        // Arrange
        var yamlContent = """
            permissions:
              read:
                - pattern: "test.yml"
                  action: allow
            """;
        var filePath = Path.Combine(_tempDir, "permissions.yml");
        File.WriteAllText(filePath, yamlContent);

        // Act
        var config = PermissionConfigLoader.Load(filePath);

        // Assert
        Assert.Single(config.Read);
        Assert.Equal("test.yml", config.Read[0].Pattern);
    }

    [Fact]
    public void Load_JsonExtension_UsesJsonLoader()
    {
        // Arrange
        var jsonContent = """
            {
              "permissions": {
                "read": [
                  { "pattern": "test.json", "action": "allow" }
                ]
              }
            }
            """;
        var filePath = Path.Combine(_tempDir, "permissions.json");
        File.WriteAllText(filePath, jsonContent);

        // Act
        var config = PermissionConfigLoader.Load(filePath);

        // Assert
        Assert.Single(config.Read);
        Assert.Equal("test.json", config.Read[0].Pattern);
    }

    [Fact]
    public void Load_UnknownExtension_ReturnsDefault()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "permissions.txt");
        File.WriteAllText(filePath, "some content");

        // Act
        var config = PermissionConfigLoader.Load(filePath);

        // Assert
        Assert.NotNull(config);
        Assert.NotEmpty(config.Read); // Default config
    }

    #endregion

    #region LoadFromDefaultLocations

    [Fact]
    public void LoadFromDefaultLocations_YamlExists_LoadsYaml()
    {
        // Arrange
        var ironhiveDir = Path.Combine(_tempDir, ".ironhive");
        Directory.CreateDirectory(ironhiveDir);
        var yamlContent = """
            permissions:
              read:
                - pattern: "from-default-yaml"
                  action: allow
            """;
        File.WriteAllText(Path.Combine(ironhiveDir, "permissions.yaml"), yamlContent);

        // Act
        var config = PermissionConfigLoader.LoadFromDefaultLocations(_tempDir);

        // Assert
        Assert.Single(config.Read);
        Assert.Equal("from-default-yaml", config.Read[0].Pattern);
    }

    [Fact]
    public void LoadFromDefaultLocations_JsonExists_LoadsJson()
    {
        // Arrange
        var ironhiveDir = Path.Combine(_tempDir, ".ironhive");
        Directory.CreateDirectory(ironhiveDir);
        var jsonContent = """
            {
              "permissions": {
                "read": [
                  { "pattern": "from-default-json", "action": "allow" }
                ]
              }
            }
            """;
        File.WriteAllText(Path.Combine(ironhiveDir, "permissions.json"), jsonContent);

        // Act
        var config = PermissionConfigLoader.LoadFromDefaultLocations(_tempDir);

        // Assert
        Assert.Single(config.Read);
        Assert.Equal("from-default-json", config.Read[0].Pattern);
    }

    [Fact]
    public void LoadFromDefaultLocations_BothExist_PrefersYaml()
    {
        // Arrange
        var ironhiveDir = Path.Combine(_tempDir, ".ironhive");
        Directory.CreateDirectory(ironhiveDir);

        var yamlContent = """
            permissions:
              read:
                - pattern: "from-yaml"
                  action: allow
            """;
        File.WriteAllText(Path.Combine(ironhiveDir, "permissions.yaml"), yamlContent);

        var jsonContent = """
            {
              "permissions": {
                "read": [
                  { "pattern": "from-json", "action": "allow" }
                ]
              }
            }
            """;
        File.WriteAllText(Path.Combine(ironhiveDir, "permissions.json"), jsonContent);

        // Act
        var config = PermissionConfigLoader.LoadFromDefaultLocations(_tempDir);

        // Assert - YAML has priority
        Assert.Single(config.Read);
        Assert.Equal("from-yaml", config.Read[0].Pattern);
    }

    [Fact]
    public void LoadFromDefaultLocations_NoneExist_ReturnsDefault()
    {
        // Act
        var config = PermissionConfigLoader.LoadFromDefaultLocations(_tempDir);

        // Assert
        Assert.NotNull(config);
        Assert.NotEmpty(config.Read); // Default config
    }

    #endregion

    #region SaveToJson

    [Fact]
    public void SaveToJson_CreatesDirectoryAndFile()
    {
        // Arrange
        var config = new PermissionConfig
        {
            Read = [new() { Pattern = "*.md", Action = PermissionAction.Allow }],
            DefaultAction = PermissionAction.Deny
        };
        var filePath = Path.Combine(_tempDir, "subdir", "permissions.json");

        // Act
        PermissionConfigLoader.SaveToJson(config, filePath);

        // Assert
        Assert.True(File.Exists(filePath));

        // Reload and verify
        var loaded = PermissionConfigLoader.LoadFromJson(filePath);
        Assert.Single(loaded.Read);
        Assert.Equal("*.md", loaded.Read[0].Pattern);
    }

    [Fact]
    public void SaveToJson_RoundTrip_PreservesData()
    {
        // Arrange
        var original = new PermissionConfig
        {
            Read =
            [
                new() { Pattern = "**/*", Action = PermissionAction.Allow, Priority = 0 },
                new() { Pattern = "*.env", Action = PermissionAction.Ask, Priority = 10, Reason = "Secret file" }
            ],
            Bash =
            [
                new() { Pattern = "git *", Action = PermissionAction.Allow },
                new() { Pattern = "rm -rf *", Action = PermissionAction.Deny, Priority = 100 }
            ],
            DefaultAction = PermissionAction.Ask
        };
        var filePath = Path.Combine(_tempDir, "roundtrip.json");

        // Act
        PermissionConfigLoader.SaveToJson(original, filePath);
        var loaded = PermissionConfigLoader.LoadFromJson(filePath);

        // Assert
        Assert.Equal(original.Read.Count, loaded.Read.Count);
        Assert.Equal(original.Read[0].Pattern, loaded.Read[0].Pattern);
        Assert.Equal(original.Read[0].Action, loaded.Read[0].Action);
        Assert.Equal(original.Read[1].Priority, loaded.Read[1].Priority);
        Assert.Equal(original.Read[1].Reason, loaded.Read[1].Reason);
        Assert.Equal(original.Bash.Count, loaded.Bash.Count);
        Assert.Equal(original.DefaultAction, loaded.DefaultAction);
    }

    #endregion

    #region All Sections

    [Fact]
    public void LoadFromYaml_AllSections_ParsesAllCorrectly()
    {
        // Arrange
        var yamlContent = """
            permissions:
              read:
                - pattern: "*.cs"
                  action: allow
              edit:
                - pattern: "src/**/*"
                  action: allow
              bash:
                - pattern: "dotnet *"
                  action: allow
              external_directory:
                - pattern: "/tmp/**"
                  action: ask
              mcp_tools:
                - pattern: "memory_*"
                  action: allow
              default_action: deny
            """;
        var filePath = Path.Combine(_tempDir, "all-sections.yaml");
        File.WriteAllText(filePath, yamlContent);

        // Act
        var config = PermissionConfigLoader.LoadFromYaml(filePath);

        // Assert
        Assert.Single(config.Read);
        Assert.Single(config.Edit);
        Assert.Single(config.Bash);
        Assert.Single(config.ExternalDirectory);
        Assert.Single(config.McpTools);
        Assert.Equal(PermissionAction.Deny, config.DefaultAction);
    }

    #endregion
}
