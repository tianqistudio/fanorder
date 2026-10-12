using V12UltraController.Core.Configuration;

namespace V12UltraController.Tests.Configuration;

public sealed class FanCurveTests
{
    [Theory]
    [InlineData(-10, 36)]
    [InlineData(0, 36)]
    [InlineData(30, 36)]
    [InlineData(35, 39.5)]
    [InlineData(75, 73.5)]
    [InlineData(87.5, 95)]
    [InlineData(100, 100)]
    [InlineData(110, 100)]
    public void RemoteHostSafe_InterpolatesAndClampsAtEndpoints(double temperature, double expected)
    {
        Assert.Equal(expected, FanCurve.RemoteHostSafe.Interpolate(temperature), precision: 5);
    }

    [Fact]
    public void Validate_AcceptsTwoToTwentyMonotonicPoints()
    {
        new FanCurve([new(0, 10), new(100, 100)]).Validate();
        new FanCurve(
            Enumerable.Range(0, 20)
                .Select(static index => new FanCurvePoint(index * 5, (byte)(10 + index)))
                .ToArray())
            .Validate();
    }

    [Fact]
    public void Validate_RejectsCountTemperatureFanAndMonotonicityViolations()
    {
        AssertInvalid(new FanCurve([new(0, 10)]));
        AssertInvalid(new FanCurve(Enumerable.Range(0, 21).Select(static index => new FanCurvePoint(index, 10)).ToArray()));
        AssertInvalid(new FanCurve([new(-1, 10), new(100, 100)]));
        AssertInvalid(new FanCurve([new(0, 10), new(101, 100)]));
        AssertInvalid(new FanCurve([new(0, 9), new(100, 100)]));
        AssertInvalid(new FanCurve([new(0, 10), new(100, 101)]));
        AssertInvalid(new FanCurve([new(0, 10), new(0, 20)]));
        AssertInvalid(new FanCurve([new(0, 20), new(100, 10)]));
        AssertInvalid(new FanCurve([new(double.NaN, 10), new(100, 100)]));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Interpolate_RejectsNonFiniteInput(double value)
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => FanCurve.RemoteHostSafe.Interpolate(value));
    }

    private static void AssertInvalid(FanCurve curve) =>
        _ = Assert.Throws<ConfigurationValidationException>(curve.Validate);
}
