using V12UltraController.Core.Control;

namespace V12UltraController.Tests.Core;

public sealed class TrayWheelAccumulatorTests
{
    [Theory]
    [InlineData(120, 60, 61)]
    [InlineData(-120, 60, 59)]
    [InlineData(600, 60, 65)]
    [InlineData(-600, 12, 10)]
    [InlineData(600, 98, 100)]
    [InlineData(int.MaxValue, 60, 100)]
    [InlineData(int.MinValue, 60, 10)]
    public void DetentsAreBounded(int delta, byte baseline, byte expected) =>
        Assert.Equal(expected, new TrayWheelAccumulator().Apply(delta, baseline));

    [Fact]
    public void HighResolutionInputAccumulatesAndCanReset()
    {
        var wheel = new TrayWheelAccumulator();
        Assert.Equal(60, wheel.Apply(60, 60));
        Assert.Equal(61, wheel.Apply(60, 60));
        Assert.Equal(61, wheel.Apply(-60, 61));
        Assert.Equal(60, wheel.Apply(-60, 61));
        wheel.Apply(60, 60);
        wheel.Reset();
        Assert.Equal(60, wheel.Apply(60, 60));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void UnknownOrInvalidBaselineIsRejected(byte baseline) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrayWheelAccumulator().Apply(120, baseline));
}
