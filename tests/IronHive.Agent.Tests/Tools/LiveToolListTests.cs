using AwesomeAssertions;
using IronHive.Agent.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace IronHive.Agent.Tests.Tools;

/// <summary>
/// A loop reads its tool list on every turn while an MCP server that connected late adds its tools to it. A
/// <see cref="List{T}"/> throws when it changes under an enumeration; this list hands each reader one snapshot.
/// </summary>
public class LiveToolListTests
{
    private static AIFunction Tool(string name) => AIFunctionFactory.Create(() => name, name);

    [Fact]
    public void AnEnumerationInFlight_DoesNotSeeATool_AddedBehindIt()
    {
        var list = new LiveToolList([Tool("a"), Tool("b")]);

        using var enumerator = list.GetEnumerator();
        enumerator.MoveNext().Should().BeTrue();
        list.Add(Tool("c"));
        var rest = new List<string> { enumerator.Current.Name };
        while (enumerator.MoveNext())
        {
            rest.Add(enumerator.Current.Name);
        }

        rest.Should().Equal("a", "b");
        list.Select(tool => tool.Name).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Replace_SwapsAPluginsTools_AsOneChange_KeepingTheOthersInPlace()
    {
        var builtIn = Tool("read");
        var oldA = Tool("mcp-a");
        var oldB = Tool("mcp-b");
        var list = new LiveToolList([builtIn, oldA, oldB]);

        list.Replace([oldA], [Tool("mcp-a2"), Tool("mcp-a3")]);

        list.Select(tool => tool.Name).Should().Equal("read", "mcp-b", "mcp-a2", "mcp-a3");
        list.Contains(oldA).Should().BeFalse();
    }

    [Fact]
    public void Replace_IgnoresAToolThatIsNotThere_AndMatchesByReference()
    {
        var present = Tool("x");
        var list = new LiveToolList([present]);

        list.Replace([Tool("x")], []);

        list.Should().ContainSingle().Which.Should().BeSameAs(present, "a different tool with the same name is not the same tool");
    }

    [Fact]
    public void ItBehavesAsAnIList()
    {
        var a = Tool("a");
        var b = Tool("b");
        var list = new LiveToolList();

        list.Add(a);
        list.Insert(0, b);
        list[1] = Tool("a2");

        list.Count.Should().Be(2);
        list.IndexOf(b).Should().Be(0);
        list.Remove(b).Should().BeTrue();
        list.Remove(b).Should().BeFalse();
        list.RemoveAt(0);
        list.Count.Should().Be(0);
        list.Add(a);
        list.Clear();
        list.Should().BeEmpty();
        list.IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public async Task ConcurrentAdds_AndEnumerations_NeverThrow_AndLoseNothing()
    {
        var list = new LiveToolList();
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                list.Add(Tool($"w{w}-{i}"));
            }
        }, TestContext.Current.CancellationToken));
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 400; i++)
            {
                _ = list.Select(tool => tool.Name).ToList();
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(writers.Append(reader));

        list.Count.Should().Be(800);
    }
}
