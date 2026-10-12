using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Ipc;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed partial class ControllerCoordinatorTests
{
    [Theory]
    [InlineData(true, (int)ServiceStopReason.Unknown)]
    [InlineData(true, (int)ServiceStopReason.ServiceStop)]
    [InlineData(true, (int)ServiceStopReason.SystemShutdown)]
    [InlineData(false, (int)ServiceStopReason.Unknown)]
    [InlineData(false, (int)ServiceStopReason.ServiceStop)]
    [InlineData(false, (int)ServiceStopReason.SystemShutdown)]
    public async Task DisableProtectionWhileThermalFailSafeActivePreservesTargetAndStopsAllLaterAutomaticWrites(
        bool overheating, int stopReason)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true, initialConfiguration:
            ControllerConfiguration.CreateDefault() with { Commissioned = true, Mode = ControlMode.Manual, ManualSpeedPercent = 59 });
        await fixture.ReachSettledControlAsync();
        Assert.Equal((byte)59, fixture.Device.CurrentPercent);
        fixture.Thermal.CpuTemperature = overheating ? 95 : null;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.True(fixture.Coordinator.GetStatus().FailSafe.Active);
        Assert.Equal(overheating ? FailSafeReason.EmergencyTemperature : FailSafeReason.TelemetryStale,
            fixture.Coordinator.GetStatus().FailSafe.Reason);
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);

        var countsBeforeDisable = fixture.CaptureHardwareCounts();
        await fixture.Coordinator.SetModeAsync(ControlMode.PureManual, ProfilePeer, CancellationToken.None);
        Assert.Equal(countsBeforeDisable, fixture.CaptureHardwareCounts());
        Assert.Equal(ControlMode.PureManual, fixture.Coordinator.GetStatus().Fan.Mode);
        Assert.False(fixture.Coordinator.GetStatus().FailSafe.Active);
        Assert.Equal((byte)59, (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).ManualSpeedPercent);
        // Disabling never invents a new speed, including lowering the already-written 100% value.
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);

        for (var sample = 0; sample < 12; sample++)
        {
            fixture.Thermal.CpuTemperature = sample % 2 == 0 ? 100 : null;
            await fixture.Coordinator.TickAsync(CancellationToken.None);
        }
        await fixture.Coordinator.BestEffortWatchdogFailSafeAsync();
        await fixture.Coordinator.FinalizeHostStopAsync((ServiceStopReason)stopReason);
        await fixture.Coordinator.DisposeAsync();
        Assert.Equal(countsBeforeDisable.Read + 12, fixture.Device.ReadCalls);
        Assert.Equal(countsBeforeDisable.Write, fixture.Device.Writes.Count);
        Assert.Equal(countsBeforeDisable.Off, fixture.Device.ShutdownOffWrites);
        Assert.Equal(0, fixture.Device.LightingWrites);
        Assert.Equal(0, fixture.Device.SoftwareControlSelections);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisabledProtectionSurvivesRestartWithoutReplayingSavedSpeedEvenWithBadTemperature(bool overheating)
    {
        await using var original = await CoordinatorFixture.CreateAsync(true, initialConfiguration:
            ControllerConfiguration.CreateDefault() with { Commissioned = true, Mode = ControlMode.Manual, ManualSpeedPercent = 59 });
        await original.ReachSettledControlAsync();
        await original.Coordinator.SetModeAsync(ControlMode.PureManual, ProfilePeer, CancellationToken.None);
        var saved = await original.ConfigurationStore.LoadAsync(CancellationToken.None);
        await original.Coordinator.DisposeAsync();

        await using var restarted = await CoordinatorFixture.CreateAsync(true, initialConfiguration: saved);
        restarted.Thermal.CpuTemperature = overheating ? 100 : null;
        await restarted.Coordinator.TickAsync(CancellationToken.None);
        await restarted.Coordinator.BestEffortWatchdogFailSafeAsync();
        await restarted.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);
        await restarted.Coordinator.DisposeAsync();
        Assert.Equal(ControlMode.PureManual, restarted.Coordinator.GetStatus().Fan.Mode);
        Assert.Equal((byte)59, saved.ManualSpeedPercent);
        Assert.Equal(1, restarted.Locator.OpenCalls);
        Assert.Equal(1, restarted.Device.ReadCalls);
        Assert.Empty(restarted.Device.Writes);
        Assert.Equal(0, restarted.Device.ShutdownOffWrites);
        Assert.Equal(0, restarted.Device.LightingWrites);
        Assert.Equal(0, restarted.Device.SoftwareControlSelections);
    }

    [Fact]
    public async Task DisableProtectionDoesNotPretendToPersistAcrossAnUnresolvedAuditBarrier()
    {
        var store = new FaultInjectingConfigurationStore();
        await using var fixture = await CoordinatorFixture.CreateAsync(true, suppliedStore: store);
        await fixture.ReachSettledControlAsync();
        store.FailLoads = true;
        await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.SetManualSpeedAsync(59, ProfilePeer, CancellationToken.None));
        Assert.True(fixture.Coordinator.GetStatus().AuditMutation?.Blocked);
        var error = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.SetModeAsync(ControlMode.PureManual, ProfilePeer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.InternalError, error.Code);
        Assert.NotEqual(ControlMode.PureManual, fixture.Coordinator.GetStatus().Fan.Mode);
        store.FailLoads = false;
    }
}
