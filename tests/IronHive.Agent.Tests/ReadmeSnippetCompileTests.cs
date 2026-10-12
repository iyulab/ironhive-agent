using System.Reflection;
using Iyu.Conventions.Testing.Snippets;

namespace IronHive.Agent.Tests;

/// <summary>
/// Compiles every C# block in the README against the current assemblies. A block that names a member the library does
/// not have, passes the wrong arguments, or needs a namespace the text does not mention fails here instead of in a
/// reader's editor.
/// </summary>
/// <remarks>
/// Each block is compiled as a top-level program by <see cref="SnippetCompiler"/>: its own usings, the usings below, and
/// the stand-ins below when the block uses a name without declaring it — values a reader already has from the surrounding
/// text (a chat client, a service collection) and the reader's own types (a sandbox, a middleware step), not part of what
/// the block shows. A block that is deliberately not a program is listed in <see cref="Fragments"/> under its heading,
/// with the reason.
/// </remarks>
public class ReadmeSnippetCompileTests
{
    // A block that is deliberately not a program (a signature sketch, pseudocode), by the heading it sits under, with the
    // reason. Shrink this, never grow it silently.
    private static readonly SnippetRoster Fragments = new(new Dictionary<string, string>(StringComparer.Ordinal));

    private static readonly string[] CommonUsings =
    [
        "System",
        "System.Collections.Generic",
        "System.IO",
        "System.Linq",
        "System.Net.Http",
        "System.Threading",
        "System.Threading.Tasks",
        "System.ComponentModel",
        "Microsoft.Extensions.AI",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Logging",
    ];

    private static readonly Dictionary<string, string> StandIns = new(StringComparer.Ordinal)
    {
        ["chatClient"] = "IChatClient chatClient = null!;",
        ["baseChatClient"] = "IChatClient baseChatClient = null!;",
        ["innerClient"] = "IChatClient innerClient = null!;",
        ["inner"] = "IChatClient inner = null!;",
        ["services"] = "IServiceCollection services = new ServiceCollection();",
        ["serviceProvider"] = "IServiceProvider serviceProvider = null!;",
        ["sp"] = "IServiceProvider sp = null!;",
        ["logger"] = "ILogger logger = null!;",
        ["log"] = "ILogger log = null!;",
        ["loggerFactory"] = "ILoggerFactory loggerFactory = null!;",
        ["cancellationToken"] = "CancellationToken cancellationToken = default;",
        ["ct"] = "CancellationToken ct = default;",
        ["tools"] = "IList<AITool> tools = [];",
        ["allTools"] = "IList<AITool> allTools = [];",
        ["options"] = "AgentOptions options = new();",
        ["loop"] = "IAgentLoop loop = null!;",
        ["prompt"] = "string prompt = \"\";",
        ["schema"] = "System.Text.Json.JsonElement schema = default;",
        ["sessionId"] = "string sessionId = \"\";",
        ["pastedJson"] = "string pastedJson = \"{}\";",
        ["config"] = "McpPluginConfig config = new();",
        ["manager"] = "McpPluginManager manager = null!;",
        ["modeToolFilter"] = "IModeToolFilter modeToolFilter = null!;",
        ["policy"] = "IToolCallPolicy policy = null!;",
        ["modeManager"] = "IModeManager modeManager = null!;",
        ["approvalService"] = "IHumanApprovalService approvalService = null!;",
        ["guard"] = "IToolResultGuard guard = null!;",
        ["availableToolsContext"] = "IAvailableToolsContext availableToolsContext = null!;",
        ["mySandboxRunner"] = "ISandboxRunner mySandboxRunner = null!;",
        // The reader's own host-side runner, as a value: the kit adds a type stand-in only when the example itself names
        // the type, and this block names only the value.
        // TODO(upstream: SnippetCompiler type stand-ins referenced from a value stand-in are not added) - return to an
        // IHostToolRunner type stand-in once the kit chains them.
        ["host"] = "var host = new { RunAsync = (Func<ToolCallResult, CancellationToken, Task<string>>)((_, _) => Task.FromResult(\"\")) };",
        ["RestoreFileVersion"] = "static string RestoreFileVersion(string path, int version) => \"\";",
        ["ShowStarted"] = "static void ShowStarted(string? id, string? name) { }",
        ["ShowProgress"] = "static void ShowProgress(string? id, string? fragment) { }",
        ["ShowFinished"] = "static void ShowFinished(string? id, bool? success) { }",

        // Types a block uses as the reader's own (a sandbox, a middleware step), declared after the program.
        ["ISandboxRunner"] = "public interface ISandboxRunner { Task<string> RunAsync(string command); }",
        ["AuditMiddleware"] = "sealed class AuditMiddleware : IToolInvocationMiddleware { public ValueTask<object?> InvokeAsync(FunctionInvocationContext context, ToolInvocationNext next, CancellationToken cancellationToken) => next(context, cancellationToken); }",
    };

    private static readonly string[] AssembliesToLoad =
    [
        "IronHive.Agent", "IronHive.Agent.Memory", "IronHive.Agent.FluxGuard", "IronHive.Agent.Ironbees",
        "Microsoft.Extensions.AI", "Microsoft.Extensions.AI.Abstractions",
    ];

    private static readonly SnippetCompiler Compiler = new(SnippetCompiler.ReferencesFor([.. AssembliesToLoad.Select(Assembly.Load)]))
    {
        Usings = [.. CommonUsings, .. LibraryNamespaces()],
        StandIns = StandIns,
    };

    private static readonly IReadOnlyList<DocSnippet> Snippets = DocSnippets.Read(Path.Combine(RepoRoot(), "README.md"), "README.md");

    public static TheoryData<string> Blocks() => new(Snippets.Select(s => s.Key));

    [Theory]
    [MemberData(nameof(Blocks))]
    public void ReadmeBlock_Compiles(string key)
    {
        var snippet = Snippets.Single(s => s.Key == key);
        if (!Fragments.IsFragment(snippet))
        {
            Compiler.ShouldCompile(snippet);
        }
    }

    [Fact]
    public void EveryReadmeBlock_IsFoundAndFragmentsNameRealHeadings() => Fragments.ShouldMatch([.. Snippets], atLeast: 15);

    /// <summary>Positive control: the compiler rejects a call the library does not have.</summary>
    [Fact]
    public void Compile_RejectsAMethodTheLibraryDoesNotHave()
    {
        var errors = Compiler.Compile("""
            var retriever = new IronHive.Agent.Context.KeywordToolRetriever();
            retriever.RetrieveEverythingAsync();
            """);

        Assert.NotEmpty(errors);
    }

    // The namespaces of the agent packages' public types: the README reads as one guide across them.
    private static IEnumerable<string> LibraryNamespaces() =>
        AssembliesToLoad.Where(a => a.StartsWith("IronHive.", StringComparison.Ordinal))
            .SelectMany(a => Assembly.Load(a).GetExportedTypes())
            .Select(t => t.Namespace).OfType<string>().Distinct().Order(StringComparer.Ordinal);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IronHive.Agent.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("IronHive.Agent.slnx not found above the test output directory");
    }
}
