using V12UltraController.Core.Configuration;
using V12UltraController.Core.Status;

namespace V12UltraController.Tests.Core;

public sealed class CurveAdvisorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);
    private static ThermalObservation[] Samples(int count, Func<int, double> temperature, byte speed = 70,
        bool automatic = true, bool protect = false, int minutes = 1) => Enumerable.Range(0, count)
        .Select(i => new ThermalObservation(Now.AddMinutes(-(count - i) * minutes), temperature(i), speed,
            automatic, protect, CurveAdvisor.Identity(FanCurve.RemoteHostSafe))).ToArray();

    [Fact]
    public void EmptyHistoryDoesNotInventOptimization()
    {
        var a = CurveAdvisor.Analyze([], FanCurve.RemoteHostSafe, Now);
        Assert.Equal(0, a.EligibleSamples);
        Assert.All(a.Rows, r => Assert.Equal(r.Before, r.After));
    }

    [Fact]
    public void RepeatedRisingTemperatureSuggestsBoundedIncrease()
    {
        var a = CurveAdvisor.Analyze(Samples(70, i => 79 + i % 5 * 0.6), FanCurve.RemoteHostSafe, Now);
        var row = a.Rows.Single(r => r.TemperatureCelsius == 80);
        Assert.Equal(84, row.After);
        Assert.Contains("+5", row.Reason);
        a.Suggested.Validate();
        Assert.All(a.Rows.Where(r => r.TemperatureCelsius >= 90), r => Assert.Equal(100, r.After));
    }

    [Fact]
    public void LongStableCoolWindowPermitsSmallTrialDecrease()
    {
        var a = CurveAdvisor.Analyze(Samples(130, _ => 50), FanCurve.RemoteHostSafe, Now);
        Assert.Equal(47, a.Rows.Single(r => r.TemperatureCelsius == 50).After);
        a.Suggested.Validate();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ManualOrProtectedSamplesNeverJustifyDecrease(bool automatic, bool protect)
    {
        var a = CurveAdvisor.Analyze(Samples(130, _ => 50, automatic: automatic, protect: protect), FanCurve.RemoteHostSafe, Now);
        Assert.Equal(0, a.EligibleSamples);
        Assert.Equal(50, a.Rows.Single(r => r.TemperatureCelsius == 50).After);
    }

    [Fact]
    public void GapsAndDuplicateSamplesCannotInventContinuousEvidence()
    {
        var gaps = CurveAdvisor.Analyze(Samples(130, _ => 50, minutes: 5), FanCurve.RemoteHostSafe, Now);
        Assert.Equal(50, gaps.Rows.Single(r => r.TemperatureCelsius == 50).After);
        var duplicate = Samples(1, _ => 50)[0];
        var a = CurveAdvisor.Analyze(Enumerable.Repeat(duplicate, 200), FanCurve.RemoteHostSafe, Now);
        Assert.Equal(1, a.RecordedSamples);
        Assert.Equal(50, a.Rows.Single(r => r.TemperatureCelsius == 50).After);
    }

    [Fact]
    public void DifferentCurveAndNonFiniteTelemetryAreExcluded()
    {
        var history = Samples(130, _ => 50).Select((s, i) => i % 2 == 0 ? s with { CurveId = "old" } : s with { Temperature = double.NaN });
        var a = CurveAdvisor.Analyze(history, FanCurve.RemoteHostSafe, Now);
        Assert.Equal(0, a.EligibleSamples);
    }

    [Fact]
    public void OldAndFutureObservationsAreExcluded()
    {
        var history = Samples(130, _ => 50).Select(s => s with { Time = Now.AddDays(-8) })
            .Append(Samples(1, _ => 50)[0] with { Time = Now.AddDays(1) });
        Assert.Equal(0, CurveAdvisor.Analyze(history, FanCurve.RemoteHostSafe, Now).RecordedSamples);
    }
}
