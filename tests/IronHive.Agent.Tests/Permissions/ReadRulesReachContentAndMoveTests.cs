using AwesomeAssertions;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using IronHive.Agent.Tools;

namespace IronHive.Agent.Tests.Permissions;

/// <summary>
/// What the read rules protect is a file's content. A search must not return lines of a file that <c>ReadFile</c> would ask
/// about, and a move or an edit must not open a way to content the rules refuse.
/// </summary>
public sealed class ReadRulesReachContentAndMoveTests : IDisposable
{
    private const string Needle = "needle-in-the-haystack";
    private const string Secret = "value-only-the-env-file-holds";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"read-content-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"read-content-outside-{Guid.NewGuid():N}");

    public ReadRulesReachContentAndMoveTests()
    {
        Write(_root, ".env", $"{Needle}={Secret}");
        Write(_root, "src/ok.txt", $"public {Needle}");
        Write(_root, "secrets/key.txt", $"key {Needle}");
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _root, _outside })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private PermissionConfig Default()
    {
        var config = PermissionConfig.CreateDefault();
        config.WorkingDirectory = _root;
        return config;
    }

    private PermissionConfig EditsAllowed()
    {
        var config = Default();
        config.Edit.Add(new PermissionRule { Pattern = "**/*", Action = PermissionAction.Allow, Priority = 1 });
        config.AskBeforeDelete = false;
        return config;
    }

    private ToolProvider ToolsUnder(PermissionConfig config) =>
        new(_root, new FileToolOptions { ReadRules = new PermissionEvaluator(config).ToReadRules() });

    private static Task<string> Search(ToolProvider tools, string path = ".") =>
        tools.GrepFiles(Needle, "**/*", path, TestContext.Current.CancellationToken);

    private static RiskAssessment Judge(PermissionConfig config, string tool, Dictionary<string, object?> arguments) =>
        new ToolCallPolicy(new PermissionEvaluator(config)).Evaluate(tool, arguments);

    private static Dictionary<string, object?> Move(string source, string destination) =>
        new() { ["source"] = source, ["destination"] = destination };

    private static Dictionary<string, object?> At(string path) => new() { ["path"] = path };

    [Fact]
    public async Task A_search_from_the_root_does_not_return_lines_of_a_file_the_rules_ask_about()
    {
        var found = await Search(ToolsUnder(Default()));

        found.Should().Contain("ok.txt", "what the rules allow is still searched");
        found.Should().NotContain(Secret);
        found.Should().Contain("not searched").And.Contain(".env").And.Contain("ReadFile");
    }

    [Fact]
    public async Task A_search_that_matches_nothing_still_names_what_it_did_not_read()
    {
        var found = await ToolsUnder(Default()).GrepFiles(Secret, "**/*", ".", TestContext.Current.CancellationToken);

        found.Should().StartWith("No matches found").And.Contain("not searched").And.Contain(".env");
    }

    [Fact]
    public async Task Under_the_restrictive_rules_a_search_reads_only_what_ReadFile_would_not_ask_about()
    {
        Write(_root, "notes/other.txt", $"unlisted {Needle}");
        var config = PermissionConfig.CreateRestrictive();
        config.WorkingDirectory = _root;

        var found = await Search(ToolsUnder(config));

        found.Should().Contain($"public {Needle}");
        found.Should().NotContain($"unlisted {Needle}", "no rule allows notes/, so ReadFile would ask");
        found.Should().Contain("notes/other.txt", "the file is named, so that it can be asked for");
    }

    [Fact]
    public async Task Under_rules_that_only_deny_every_other_file_is_searched_as_before()
    {
        var config = new PermissionConfig
        {
            WorkingDirectory = _root,
            Read = [new PermissionRule { Pattern = "secrets/**", Action = PermissionAction.Deny }],
            DefaultAction = PermissionAction.Allow,
        };

        var found = await Search(ToolsUnder(config));

        found.Should().Contain(Secret).And.Contain($"public {Needle}");
        found.Should().NotContain($"key {Needle}").And.NotContain("not searched");
    }

    [Fact]
    public async Task A_search_outside_the_working_directory_that_was_asked_about_returns_what_it_was_asked_for()
    {
        Write(_outside, "report.txt", $"outside {Needle}");

        // No external-directory rule: ReadFile of the file and the search of the folder ask for the same reason
        var found = await Search(ToolsUnder(Default()), _outside);

        found.Should().Contain($"outside {Needle}");
    }

    [Fact]
    public async Task An_approved_outside_search_does_not_cover_a_file_another_rule_asks_about()
    {
        Write(_outside, "report.txt", $"outside {Needle}");
        Write(_outside, "private.key", $"{Needle} {Secret}");
        var config = Default();
        config.ExternalDirectory =
        [
            new PermissionRule { Pattern = "**", Action = PermissionAction.Ask },
            new PermissionRule { Pattern = "**/*.key", Action = PermissionAction.Ask, Priority = 10 },
        ];

        var found = await Search(ToolsUnder(config), _outside);

        found.Should().Contain($"outside {Needle}", "the rule that asked about the folder covers it");
        found.Should().NotContain(Secret).And.Contain("private.key");
    }

    [Fact]
    public async Task A_search_nobody_was_asked_about_covers_no_file_of_a_host_s_own_evaluator()
    {
        Write(_outside, "report.txt", $"outside {Needle}");
        // A host's own evaluator may let the search of a folder through and still ask file by file, with no rule to name
        var evaluator = new AllowsFoldersAsksFiles();
        var tools = new ToolProvider(_root, new FileToolOptions { ReadRules = evaluator.ToReadRules() });

        var found = await Search(tools, _outside);

        found.Should().NotContain($"outside {Needle}").And.Contain("report.txt");
    }

    [Fact]
    public async Task An_approved_search_of_a_parent_folder_does_not_cover_the_working_directory_s_own_files()
    {
        var work = Path.Combine(_outside, "work");
        Write(work, "notes/other.txt", $"unlisted {Needle}");
        Write(_outside, "report.txt", $"outside {Needle}");
        var config = PermissionConfig.CreateRestrictive();
        config.WorkingDirectory = work;
        var tools = new ToolProvider(work, new FileToolOptions { ReadRules = new PermissionEvaluator(config).ToReadRules() });

        var found = await Search(tools, "..");

        found.Should().Contain($"outside {Needle}", "the folder outside was asked about");
        found.Should().NotContain($"unlisted {Needle}", "inside the working directory ReadFile asks its own question");
    }

    [Fact]
    public async Task A_search_of_a_folder_a_rule_asked_about_returns_the_files_the_same_rule_asks_about()
    {
        Write(_root, "drafts/plan.txt", $"draft {Needle}");
        Write(_root, "drafts/token.key", $"{Needle} {Secret}");
        var config = Default();
        config.Read.Add(new PermissionRule { Pattern = "drafts**", Action = PermissionAction.Ask, Priority = 5 });
        config.Read.Add(new PermissionRule { Pattern = "**/*.key", Action = PermissionAction.Ask, Priority = 10 });

        var approved = await Search(ToolsUnder(config), "drafts");
        var fromAbove = await Search(ToolsUnder(config));

        approved.Should().Contain($"draft {Needle}").And.NotContain(Secret);
        fromAbove.Should().NotContain($"draft {Needle}", "a search from the root was never asked about drafts/");
    }

    [Fact]
    public void A_move_whose_source_the_read_rules_deny_is_refused()
    {
        var moved = Judge(EditsAllowed(), "MoveFile", Move("secrets/key.txt", "src/key.txt"));

        moved.Verdict.Should().Be(PermissionAction.Deny, "moved to src/ the file could be read there");
    }

    [Fact]
    public void A_move_whose_source_the_read_rules_ask_about_asks_and_says_why()
    {
        var moved = Judge(EditsAllowed(), "MoveFile", Move(".env", "src/env.txt"));

        moved.Verdict.Should().Be(PermissionAction.Ask);
        moved.Reason.Should().Be("May contain secrets");
    }

    [Theory]
    [InlineData("secrets/key.txt", PermissionAction.Deny)]
    [InlineData(".env", PermissionAction.Ask)]
    [InlineData("src/ok.txt", PermissionAction.Allow)]
    public void An_edit_answers_to_the_read_rules_of_the_file_it_changes(string path, PermissionAction expected)
    {
        var edited = Judge(EditsAllowed(), "EditFile", At(path));

        edited.Verdict.Should().Be(expected, "what an edit answers tells what the file holds");
    }

    [Fact]
    public void A_write_does_not_answer_to_the_read_rules()
    {
        var written = Judge(EditsAllowed(), "WriteFile", At(".env"));

        written.Verdict.Should().Be(PermissionAction.Allow, "writing a whole file returns nothing of what it held");
    }

    [Fact]
    public void An_edit_deny_on_a_folder_refuses_a_move_out_of_it()
    {
        var config = EditsAllowed();
        config.Edit.Add(new PermissionRule { Pattern = "secrets/**", Action = PermissionAction.Deny, Priority = 30 });
        config.Read.RemoveAll(rule => rule.Action == PermissionAction.Deny);

        var moved = Judge(config, "MoveFile", Move("secrets/key.txt", "src/key.txt"));

        moved.Verdict.Should().Be(PermissionAction.Deny);
    }

    // Outside the working directory with no rule: folders are allowed, files are asked about.
    private sealed class AllowsFoldersAsksFiles : IPermissionEvaluator
    {
        public PermissionResult EvaluateRead(string filePath) => new()
        {
            Action = Directory.Exists(filePath) ? PermissionAction.Allow : PermissionAction.Ask,
            OutsideWorkingDirectory = true,
        };

        public PermissionResult EvaluateEdit(string filePath) => throw new NotSupportedException();

        public PermissionResult EvaluateDelete(string filePath) => throw new NotSupportedException();

        public PermissionResult EvaluateBash(string command) => throw new NotSupportedException();

        public PermissionResult EvaluateExternalDirectory(string directoryPath) => throw new NotSupportedException();

        public PermissionResult EvaluateMcpTool(string toolName) => throw new NotSupportedException();

        public PermissionResult EvaluateTool(string toolName) => throw new NotSupportedException();

        public bool IsReadOnlyTool(string toolName) => throw new NotSupportedException();

        public PermissionResult Evaluate(string permissionType, string target) => throw new NotSupportedException();
    }
}
