using IronHive.Agent.Tools;

namespace IronHive.Agent.Tests.Tools;

/// <summary>
/// Command output keeps its beginning and its end within the budget — the end is where a build or a test run
/// prints the error — and says how much of the middle it dropped.
/// </summary>
public class HeadTailCaptureTests
{
    [Fact]
    public void OutputWithinTheBudget_IsKeptWhole()
    {
        var capture = new HeadTailCapture(headChars: 20, tailChars: 20);
        capture.AppendLine("first");
        capture.AppendLine("second");

        Assert.Equal("first\nsecond\n", capture.ToString());
    }

    [Fact]
    public void OutputOverTheBudget_KeepsFirstAndLastLines_AndMarksTheGap()
    {
        var capture = new HeadTailCapture(headChars: 14, tailChars: 14);
        for (var i = 0; i < 100; i++)
        {
            capture.AppendLine($"line{i:D2}");
        }

        var text = capture.ToString();

        Assert.StartsWith("line00\nline01\n", text);
        Assert.EndsWith("line98\nline99\n", text);
        Assert.Contains("characters omitted", text);
        Assert.DoesNotContain("line50", text);
    }

    [Fact]
    public void ASingleHugeLine_KeepsItsStartAndItsEnd()
    {
        var capture = new HeadTailCapture(headChars: 10, tailChars: 10);
        capture.AppendLine("START" + new string('x', 1000) + "END");

        var text = capture.ToString();

        Assert.StartsWith("STARTxxxxx", text);
        Assert.EndsWith("END\n", text);
        Assert.True(text.Length < 100, text);
    }

    [Fact]
    public void ConcurrentAppends_FromTwoStreams_AreAllAccountedFor()
    {
        var capture = new HeadTailCapture(headChars: 1_000_000, tailChars: 1_000_000);

        Parallel.For(0, 2_000, i => capture.AppendLine($"l{i}"));

        Assert.Equal(2_000, capture.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task ExecuteCommand_OnLongOutput_ReturnsItsLastLine()
    {
        var dir = Directory.CreateTempSubdirectory("headtail").FullName;
        try
        {
            var tools = new ToolProvider(dir);
            // ~120k characters of output, the last line being the one a model needs.
            var command = OperatingSystem.IsWindows()
                ? "for /L %i in (1,1,12000) do @echo line-%i-padding & echo FINAL-SUMMARY"
                : "for i in $(seq 1 12000); do echo line-$i-padding; done; echo FINAL-SUMMARY";

            var result = await tools.ExecuteCommand(command, timeoutMs: 60_000);

            Assert.Contains("FINAL-SUMMARY", result);
            Assert.Contains("line-1-padding", result);
            Assert.Contains("characters omitted", result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
