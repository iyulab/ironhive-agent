using IronHive.Agent.Context;

namespace IronHive.Agent.Tests.Context;

/// <summary>The observation-masking defaults a <see cref="CompactionConfig"/> starts with.</summary>
public class ObservationMaskingConfigTests
{
    #region Integration with CompactionConfig

    [Fact]
    public void CompactionConfig_DefaultsObservationMaskingEnabled()
    {
        var config = new CompactionConfig();

        Assert.True(config.EnableObservationMasking);
        Assert.Equal(3, config.ObservationMaskingProtectedTurns);
        Assert.Equal(200, config.ObservationMaskingMinResultLength);
    }

    #endregion
}
