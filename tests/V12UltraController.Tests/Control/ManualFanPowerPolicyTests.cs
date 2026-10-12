using V12UltraController.Core.Control;

namespace V12UltraController.Tests.Control;

public sealed class ManualFanPowerPolicyTests
{
    private static ManualFanPowerInput Healthy(double? temperature = 40) =>
        new(true, true, false, true, temperature, TimeSpan.Zero);

    [Fact]
    public void OffRequiresConfirmationAndDoesNotRepresentPhysicalReadback()
    {
        var policy = new ManualFanPowerPolicy();
        Assert.False(policy.RequestOff(Healthy(), false).Accepted);
        Assert.False(policy.OffRequested);
        Assert.True(policy.RequestOff(Healthy(), true).KeepOff);
        Assert.True(policy.RequestOn().Accepted);
        Assert.False(policy.OffRequested);
    }

    [Theory]
    [InlineData(59.99, true)]
    [InlineData(60, false)]
    [InlineData(90, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(-1, false)]
    [InlineData(121, false)]
    public void EntryTemperatureIsValidated(double temperature, bool accepted)
    {
        var policy = new ManualFanPowerPolicy();
        Assert.Equal(accepted, policy.RequestOff(Healthy(temperature), true).Accepted);
        Assert.Equal(accepted, policy.OffRequested);
    }

    [Fact]
    public void TemperatureRiseResumesAndNeverAutomaticallyStopsAgain()
    {
        var policy = new ManualFanPowerPolicy();
        policy.RequestOff(Healthy(), true);
        Assert.True(policy.Evaluate(Healthy(84.99)).KeepOff);
        Assert.False(policy.Evaluate(Healthy(85)).KeepOff);
        Assert.False(policy.Evaluate(Healthy()).KeepOff);
    }

    public static IEnumerable<object[]> UnsafeInputs()
    {
        yield return [Healthy() with { OwnershipVerified = false }];
        yield return [Healthy() with { CommunicationHealthy = false }];
        yield return [Healthy() with { FailSafeActive = true }];
        yield return [Healthy() with { CpuValid = false }];
        yield return [Healthy(null)];
        yield return [Healthy(double.NaN)];
        yield return [Healthy() with { SampleAge = TimeSpan.FromSeconds(10) }];
        yield return [Healthy() with { SampleAge = TimeSpan.FromSeconds(-1) }];
    }

    [Theory]
    [MemberData(nameof(UnsafeInputs))]
    public void UnsafeInputsRejectOffAndClearExistingIntent(ManualFanPowerInput input)
    {
        var policy = new ManualFanPowerPolicy();
        Assert.False(policy.RequestOff(input, true).Accepted);
        policy.RequestOff(Healthy(), true);
        Assert.False(policy.Evaluate(input).KeepOff);
        Assert.False(policy.OffRequested);
        Assert.False(policy.Evaluate(Healthy()).KeepOff);
    }

    [Fact]
    public void NewServiceLifetimeNeverInheritsOffIntent()
    {
        var oldPolicy = new ManualFanPowerPolicy();
        oldPolicy.RequestOff(Healthy(), true);
        Assert.False(new ManualFanPowerPolicy().Evaluate(Healthy()).KeepOff);
    }
}
