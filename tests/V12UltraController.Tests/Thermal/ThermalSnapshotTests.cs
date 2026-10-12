using V12UltraController.Core.Thermal;

namespace V12UltraController.Tests.Thermal;

public sealed class ThermalSnapshotTests
{
    [Fact]
    public void ControlTemperature_UsesMaximumValidCpuAndGpuValue()
    {
        var snapshot = new ThermalSnapshot(DateTimeOffset.UtcNow, 65, [45, 82, double.NaN, 130]);

        Assert.True(snapshot.CpuIsValid);
        Assert.Equal(82, snapshot.ControlCelsius);
        Assert.Equal(new[] { 45d, 82d }, snapshot.ValidGpuCelsius);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1d)]
    [InlineData(0d)]
    [InlineData(121d)]
    [InlineData(double.NaN)]
    public void MissingOrInvalidCpu_MakesControlTemperatureInvalid(double? cpu)
    {
        var snapshot = new ThermalSnapshot(DateTimeOffset.UtcNow, cpu, [70]);

        Assert.False(snapshot.CpuIsValid);
        Assert.Null(snapshot.ControlCelsius);
    }

    [Fact]
    public async Task FakeThermalSource_DemonstratesInjectableSourceContract()
    {
        await using IThermalSource source = new FakeThermalSource(
            new ThermalSnapshot(DateTimeOffset.UtcNow, 60, [70]));

        var result = await source.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(70, result.ControlCelsius);
    }

    private sealed class FakeThermalSource(ThermalSnapshot snapshot) : IThermalSource
    {
        public Task<ThermalSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(snapshot);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
