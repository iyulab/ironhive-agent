using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IronHive.Agent.Tests;

/// <summary>
/// Compiles every <c>```csharp</c> block in the README against the current assemblies. A block that names a member the
/// library does not have, passes the wrong arguments, or needs a namespace the text does not mention fails here instead
/// of in a reader's editor.
/// </summary>
/// <remarks>
/// A block is compiled as a top-level program. Its <c>using</c> lines are hoisted, and the usings below are added. The
/// stand-ins below are declared when the block uses a name without declaring it: these are values a reader already has
/// from the surrounding text (a chat client, a service collection), not part of what the block shows. A block that is
/// deliberately not a program is listed in <see cref="Fragments"/> under its heading, with the reason.
/// </remarks>
public class ReadmeSnippetCompileTests
{
    // Shrink this, never grow it silently.
    private static readonly Dictionary<string, string> Fragments = new(StringComparer.Ordinal);

    private const string CommonUsings = """
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Linq;
        using System.Net.Http;
        using System.Threading;
        using System.Threading.Tasks;
        using System.ComponentModel;
        using Microsoft.Extensions.AI;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Logging;
        """;

    private static readonly (string Name, string Declaration)[] StandIns =
    [
        ("chatClient", "IChatClient chatClient = null!;"),
        ("baseChatClient", "IChatClient baseChatClient = null!;"),
        ("innerClient", "IChatClient innerClient = null!;"),
        ("inner", "IChatClient inner = null!;"),
        ("services", "IServiceCollection services = new ServiceCollection();"),
        ("serviceProvider", "IServiceProvider serviceProvider = null!;"),
        ("sp", "IServiceProvider sp = null!;"),
        ("logger", "ILogger logger = null!;"),
        ("log", "ILogger log = null!;"),
        ("loggerFactory", "ILoggerFactory loggerFactory = null!;"),
        ("cancellationToken", "CancellationToken cancellationToken = default;"),
        ("ct", "CancellationToken ct = default;"),
        ("tools", "IList<AITool> tools = [];"),
        ("allTools", "IList<AITool> allTools = [];"),
        ("options", "AgentOptions options = new();"),
        ("loop", "IAgentLoop loop = null!;"),
        ("prompt", "string prompt = \"\";"),
        ("schema", "System.Text.Json.JsonElement schema = default;"),
        ("sessionId", "string sessionId = \"\";"),
        ("config", "McpPluginConfig config = new();"),
        ("manager", "McpPluginManager manager = null!;"),
        ("modeToolFilter", "IModeToolFilter modeToolFilter = null!;"),
        ("policy", "IToolCallPolicy policy = null!;"),
        ("modeManager", "IModeManager modeManager = null!;"),
        ("approvalService", "IHumanApprovalService approvalService = null!;"),
        ("guard", "IToolResultGuard guard = null!;"),
        ("availableToolsContext", "IAvailableToolsContext availableToolsContext = null!;"),
        ("mySandboxRunner", "ISandboxRunner mySandboxRunner = null!;"),
        ("host", "IHostToolRunner host = null!;"),
        ("RestoreFileVersion", "static string RestoreFileVersion(string path, int version) => \"\";"),
        ("ShowStarted", "static void ShowStarted(string? id, string? name) { }"),
        ("ShowFinished", "static void ShowFinished(string? id, bool? success) { }"),
    ];

    // Types a block uses as the reader's own (a sandbox, a middleware step), declared after the program.
    private static readonly (string Name, string Declaration)[] StandInTypes =
    [
        ("ISandboxRunner", "public interface ISandboxRunner { Task<string> RunAsync(string command); }"),
        ("IHostToolRunner", "public interface IHostToolRunner { Task<string> RunAsync(ToolCallResult call, CancellationToken ct); }"),
        ("AuditMiddleware", "sealed class AuditMiddleware : IToolInvocationMiddleware { public ValueTask<object?> InvokeAsync(FunctionInvocationContext context, ToolInvocationNext next, CancellationToken cancellationToken) => next(context, cancellationToken); }"),
    ];

    private static readonly string[] AssembliesToLoad =
    [
        "IronHive.Agent", "IronHive.Agent.Memory", "IronHive.Agent.FluxGuard", "IronHive.Agent.Ironbees",
        "Microsoft.Extensions.AI", "Microsoft.Extensions.AI.Abstractions",
    ];

    public static TheoryData<string> Blocks()
    {
        var data = new TheoryData<string>();
        foreach (var block in ReadBlocks())
        {
            data.Add(block.Key);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Blocks))]
    public void ReadmeBlock_Compiles(string key)
    {
        var block = ReadBlocks().Single(b => b.Key == key);
        if (Fragments.ContainsKey(block.Heading))
        {
            return;
        }

        var errors = Compile(block.Code);

        Assert.True(errors.IsEmpty,
            $"README block {key} does not compile against the current API:\n" +
            string.Join("\n", errors.Select(e => e.ToString())) + "\n--- source ---\n" + Program(block.Code));
    }

    [Fact]
    public void EveryReadmeBlock_IsFoundAndFragmentsNameRealHeadings()
    {
        var blocks = ReadBlocks();
        Assert.True(blocks.Count >= 15, $"expected the README's C# blocks, found {blocks.Count}");
        Assert.All(Fragments.Keys, heading => Assert.Contains(blocks, b => b.Heading == heading));
    }

    /// <summary>Positive control: the compiler rejects a call the library does not have.</summary>
    [Fact]
    public void Compile_RejectsAMethodTheLibraryDoesNotHave()
    {
        var errors = Compile("""
            var retriever = new IronHive.Agent.Context.KeywordToolRetriever();
            retriever.RetrieveEverythingAsync();
            """);

        Assert.NotEmpty(errors);
    }

    private sealed record Block(string Key, string Heading, string Code);

    private static List<Block> ReadBlocks()
    {
        var path = Path.Combine(RepoRoot(), "README.md");
        var lines = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var blocks = new List<Block>();
        var heading = "(top)";
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith('#'))
            {
                heading = lines[i].TrimStart('#').Trim();
            }

            if (lines[i].Trim() != "```csharp")
            {
                continue;
            }

            var start = i + 1;
            var code = new StringBuilder();
            for (i++; i < lines.Length && lines[i].Trim() != "```"; i++)
            {
                code.AppendLine(lines[i]);
            }
            blocks.Add(new Block($"README.md line {start}: {heading}", heading, code.ToString()));
        }

        return blocks;
    }

    private static string Program(string code)
    {
        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        static string Code(string l) => l.Split("//", 2)[0].TrimEnd();
        bool IsUsingDirective(string l) =>
            l.StartsWith("using ", StringComparison.Ordinal) && Code(l).EndsWith(';') && !l.StartsWith("using var ", StringComparison.Ordinal);

        var body = string.Join("\n", lines.Where(l => !IsUsingDirective(l)));
        // Names are looked for in code only: a comment saying "your host's loop" does not use a variable named host.
        var codeText = string.Join("\n", body.Split('\n').Select(Code));
        var standIns = StandIns
            .Where(s => Regex.IsMatch(codeText, $@"\b{s.Name}\b")
                        && !Regex.IsMatch(codeText, $@"\b(var|[A-Z][\w<>?,\[\]\s]*)\s+{s.Name}\s*[=;,)]"))
            .Select(s => s.Declaration)
            .ToList();

        var standInTypes = StandInTypes
            .Where(s => Regex.IsMatch(codeText + "\n" + string.Join("\n", standIns), $@"\b{s.Name}\b")
                        && !Regex.IsMatch(codeText, $@"\b(class|interface|record|struct)\s+{s.Name}\b"))
            .Select(s => s.Declaration);

        return string.Join("\n", lines.Where(IsUsingDirective)) + "\n" + CommonUsings + "\n"
               + string.Join("\n", LibraryNamespaces().Select(n => $"using {n};")) + "\n"
               + string.Join("\n", standIns) + "\n" + StatementsFirst(body) + "\n"
               + string.Join("\n", standInTypes);
    }

    // A block may declare a type before the statements that use it (reading order); C# wants top-level statements first.
    private static string StatementsFirst(string body)
    {
        var root = CSharpSyntaxTree.ParseText(body, new CSharpParseOptions(LanguageVersion.Latest)).GetCompilationUnitRoot();
        var members = root.Members;
        var statements = members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax>().ToList();
        if (statements.Count == 0 || statements.Count == members.Count)
        {
            return body;
        }

        var others = members.Where(m => m is not Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax);
        return string.Join("\n", statements.Select(m => m.ToFullString()))
               + "\n" + string.Join("\n", others.Select(m => m.ToFullString()));
    }

    // The namespaces of the agent packages' public types: the README reads as one guide across them.
    private static IEnumerable<string> LibraryNamespaces() =>
        AssembliesToLoad.Where(a => a.StartsWith("IronHive.", StringComparison.Ordinal))
            .SelectMany(a => Assembly.Load(a).GetExportedTypes())
            .Select(t => t.Namespace).OfType<string>().Distinct().Order(StringComparer.Ordinal);

    private static ImmutableArray<Diagnostic> Compile(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(Program(code), new CSharpParseOptions(LanguageVersion.Latest));
        // A block of declarations only (a class the reader writes) has no entry point: compile it as a library.
        var hasStatements = tree.GetCompilationUnitRoot().Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax>().Any();
        var compilation = CSharpCompilation.Create(
            "ReadmeSnippet", [tree], References(),
            new CSharpCompilationOptions(
                hasStatements ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        return compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
    }

    private static List<MetadataReference> References()
    {
        foreach (var name in AssembliesToLoad)
        {
            Assembly.Load(name);
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted)
        {
            paths.UnionWith(trusted.Split(Path.PathSeparator).Where(p => p.Length > 0));
        }
        paths.UnionWith(AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && a.Location.Length > 0)
            .Select(a => a.Location));
        paths.UnionWith(Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"));
        return paths.Where(IsManagedAssembly).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

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
