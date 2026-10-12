using V12UltraController.Core.Status;

namespace V12UltraController.Tests.Core;

public sealed class FanDisplayTests
{
    [Theory]
    [InlineData(100, 2800)]
    [InlineData(50, 1550)]
    [InlineData(10, 550)]
    [InlineData(14, 650)]
    [InlineData(24, 900)]
    [InlineData(12, 600)]
    [InlineData(19, 775)]
    [InlineData(61, 1825)]
    public void SpeedIsAlwaysLabelledAsEstimate(byte percent, int rpm)
    {
        Assert.Contains($"{rpm} RPM", FanDisplay.Estimate(percent));
        Assert.Contains("估算", FanDisplay.Estimate(percent));
    }

    [Fact]
    public void OffUsesZeroRegardlessOfRetainedSpeed() =>
        Assert.Equal("设备回读：0% · 0 RPM（已关闭）", FanDisplay.Readback(70, false, true));

    [Fact]
    public void CalibrationIsMonotonicAcrossAllAllowedPercentages()
    {
        var values = Enumerable.Range(10, 91).Select(p => FanDisplay.EstimateRpm((byte)p)).ToArray();
        Assert.Equal(values.Order(), values);
        Assert.All(values, value => Assert.InRange(value, 550, 2800));
    }

    [Fact]
    public void StaleOffDoesNotClaimCurrentlyStopped() =>
        Assert.Contains("未知", FanDisplay.Readback(70, false, false));

    [Theory]
    [InlineData(null, true)]
    [InlineData(70, null)]
    [InlineData(0, true)]
    [InlineData(101, true)]
    public void UnknownDataNeverFabricatesSpeed(int? percent, bool? poweredOn) =>
        Assert.Contains("未知", FanDisplay.Readback((byte?)percent, poweredOn, true));
}
