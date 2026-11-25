using AwesomeAssertions;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using IronHive.Agent.Tools;

namespace IronHive.Agent.Tests.Permissions;

/// <summary>
/// A rule speaks about a file, however the file is spelled. Windows opens one file by many names; a rule that judged the
/// name as written was passed by writing another one.
/// </summary>
public sealed class PathSpellingTests : IDisposable
{
    private const string Secret = "value-only-the-secret-file-holds";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"path-spelling-{Guid.NewGuid():N}");

    public PathSpellingTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "secrets"));
        File.WriteAllText(Path.Combine(_root, "secrets", "key.txt"), Secret);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    // Only a deny, and everything else allowed: the configuration in which a spelling that escapes the deny opens the file.
    private PermissionConfig OnlyDenies() => new()
    {
        WorkingDirectory = _root,
        Read = [new PermissionRule { Pattern = "**/*secrets/**", Action = PermissionAction.Deny }, new PermissionRule { Pattern = "**/*", Action = PermissionAction.Allow }],
        DefaultAction = PermissionAction.Allow,
    };

    private string Spell(string spelling) => spelling
        .Replace("<root>", _root, StringComparison.Ordinal)
        .Replace("<root/>", _root.Replace('\\', '/'), StringComparison.Ordinal);

    [Theory]
    [InlineData(@"secrets::$INDEX_ALLOCATION\key.txt")]
    [InlineData(@"secrets:$I30:$INDEX_ALLOCATION\key.txt")]
    [InlineData(@"secrets\key.txt::$DATA")]
    [InlineData(@"secrets.\key.txt")]
    [InlineData(@"<root>\secrets::$INDEX_ALLOCATION\key.txt")]
    [InlineData(@"\\.\.\<root>\secrets\key.txt")]
    [InlineData(@"\\.\\<root>\secrets\key.txt")]
    [InlineData(@"//?/./<root/>/secrets/key.txt")]
    [InlineData(@"\\.\Z:\..\<root>\secrets\key.txt")]
    [InlineData(@"\??\<root>\secrets\key.txt")]
    public void A_deny_covers_the_file_however_it_is_spelled(string spelling)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows path syntax");

        var verdict = new PermissionEvaluator(OnlyDenies()).EvaluateRead(Spell(spelling));

        verdict.Action.Should().Be(PermissionAction.Deny);
    }

    // The short (8.3) name of a folder, alone and with stream syntax. Skipped where the volume gives no short names; the
    // spelling is checked to open the file before the rules are asked about it.
    [Theory]
    [InlineData(@"CONFIG~1\key.txt")]
    [InlineData(@"CONFIG~1::$INDEX_ALLOCATION\key.txt")]
    public void A_short_name_of_a_denied_folder_is_the_folder(string spelling)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "8.3 names");
        Directory.CreateDirectory(Path.Combine(_root, ".config-folder"));
        File.WriteAllText(Path.Combine(_root, ".config-folder", "key.txt"), Secret);
        Assert.SkipUnless(File.Exists(Path.Combine(_root, spelling)), "this volume does not open the folder by that name");
        var config = OnlyDenies();
        config.Read.Add(new PermissionRule { Pattern = ".config-folder/**", Action = PermissionAction.Deny, Priority = 5 });

        var verdict = new PermissionEvaluator(config).EvaluateRead(spelling);

        verdict.Action.Should().Be(PermissionAction.Deny);
    }

    // A name with no drive letter behind it cannot be mapped to the file a rule speaks about, so a default of Allow does
    // not stretch to it: a person is asked.
    [Theory]
    [InlineData(@"\\localhost\C$\somewhere\secrets\key.txt")]
    [InlineData(@"\\127.0.0.1\C$\somewhere\secrets\key.txt")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\secrets\key.txt")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\secrets\key.txt")]
    [InlineData(@"\\?\UNC\localhost\C$\somewhere\secrets\key.txt")]
    public void A_name_that_cannot_be_mapped_is_not_allowed_by_default_alone(string spelling)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows path syntax");

        var verdict = new PermissionEvaluator(OnlyDenies()).EvaluateRead(spelling);

        verdict.Action.Should().Be(PermissionAction.Ask);
    }

    [Fact]
    public void A_rule_that_speaks_about_such_a_name_still_decides()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows path syntax");
        var config = OnlyDenies();
        config.ExternalDirectory = [new PermissionRule { Pattern = "//build-server/share/**", Action = PermissionAction.Allow }];

        var verdict = new PermissionEvaluator(config).EvaluateRead(@"\\build-server\share\log.txt");

        verdict.Action.Should().Be(PermissionAction.Allow);
    }

    [Fact]
    public async Task The_gate_and_the_search_refuse_the_stream_spelling_of_a_denied_folder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "NTFS stream syntax");
        var config = PermissionConfig.CreateDefault();
        config.WorkingDirectory = _root;
        var evaluator = new PermissionEvaluator(config);
        var tools = new ToolProvider(_root, new FileToolOptions { ReadRules = evaluator.ToReadRules() });

        var read = new ToolCallPolicy(evaluator).Evaluate("ReadFile", new Dictionary<string, object?> { ["path"] = "secrets::$INDEX_ALLOCATION/key.txt" });
        var searched = await tools.GrepFiles(Secret, "**/*", "secrets::$INDEX_ALLOCATION", TestContext.Current.CancellationToken);

        read.Verdict.Should().Be(PermissionAction.Deny);
        searched.Should().Contain("not available").And.NotContain("key.txt:");
    }
}
