using System.Reflection;
using IronHive.Agent.Context;
using Microsoft.Extensions.AI;
using Xunit;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// <see cref="CompactionConfig.ObservationMaskingMinimumClearTokens"/> — masking waits until the results past the boundary
/// are worth a prompt-cache miss — and <see cref="CompactionConfig.WithProtect"/>, the copy the compactors are built from.
/// </summary>
public class MaskingClearThresholdTests
{
    private const string UnknownModel = "unknown-model-for-masking-tests";

    private static List<ChatMessage> PastTheBoundary(int resultChars) =>
    [
        new(ChatRole.User, "Read the file"),
        new(ChatRole.Assistant, [new FunctionCallContent("c1", "read_file", new Dictionary<string, object?> { ["path"] = "a.txt" })]),
        new(ChatRole.Tool, [new FunctionResultContent("c1", new string('x', resultChars))]),
        new(ChatRole.User, "Now the next step"),
    ];

    private static bool IsMasked(IReadOnlyList<ChatMessage> history)
        => history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result!.ToString()!
            .StartsWith("[Masked:", StringComparison.Ordinal);

    [Fact]
    public void ByDefault_ASmallResultPastTheBoundary_WaitsUnmasked()
    {
        var manager = ContextManager.ForModel(UnknownModel, new CompactionConfig { ObservationMaskingProtectedTurns = 1 });

        Assert.False(IsMasked(manager.ReduceToolResults(PastTheBoundary(1_000))));
    }

    [Fact]
    public void ByDefault_ResultsPastTheBoundaryWorthTheMiss_AreMasked()
    {
        var manager = ContextManager.ForModel(UnknownModel, new CompactionConfig { ObservationMaskingProtectedTurns = 1 });

        Assert.True(IsMasked(manager.ReduceToolResults(PastTheBoundary(40_000))));
    }

    [Fact]
    public void AZeroThreshold_MasksAtEveryMove()
    {
        var manager = ContextManager.ForModel(UnknownModel, new CompactionConfig
        {
            ObservationMaskingProtectedTurns = 1,
            ObservationMaskingMinimumClearTokens = 0,
            EnableToolResultCompaction = false,
        });

        Assert.True(IsMasked(manager.ReduceToolResults(PastTheBoundary(1_000))));
    }

    [Fact]
    public void WithProtect_CarriesEverySettingButTheTwoItReplaces()
    {
        var source = new CompactionConfig();
        foreach (var property in typeof(CompactionConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            object? changed = property.PropertyType switch
            {
                var t when t == typeof(int) || t == typeof(int?) => 12_345,
                var t when t == typeof(bool) => !(bool)property.GetValue(source)!,
                var t when t == typeof(float) => 0.42f,
                var t when t == typeof(double) => 0.42,
                _ => null,
            };
            if (changed is not null)
            {
                property.SetValue(source, changed);
            }
        }

        var copy = source.WithProtect(111, 222);

        Assert.Equal(111, copy.ProtectRecentTokens);
        Assert.Equal(222, copy.MinimumPruneTokens);
        foreach (var property in typeof(CompactionConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.Name is not nameof(CompactionConfig.ProtectRecentTokens) and not nameof(CompactionConfig.MinimumPruneTokens)))
        {
            Assert.Equal(property.GetValue(source), property.GetValue(copy));
        }
    }
}
