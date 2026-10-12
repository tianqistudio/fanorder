using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed partial class ControllerCoordinatorTests
{
    [Theory]
    [InlineData(ControlMode.PureManual)]
    [InlineData(ControlMode.Released)]
    public async Task ManualPowerQueuedBeforePassiveModeCommitCannotSendOrdinaryFullSpeedRecovery(ControlMode mode)
    {
        var store = new PassiveModeGateStore();
        await using var fixture = await CoordinatorFixture.CreateAsync(true, suppliedStore: store);
        await fixture.ReachSettledControlAsync();
        store.BlockPassiveSave = true;
        var changeMode = fixture.Coordinator.SetModeAsync(mode, ProfilePeer, CancellationToken.None);
        await store.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var count = fixture.CaptureHardwareCounts();
        var power = fixture.Coordinator.SetManualPowerAsync(true, true, ProfilePeer, CancellationToken.None);
        Assert.False(power.IsCompleted);
        store.ReleaseSave.TrySetResult();
        await changeMode;
        var error = await Assert.ThrowsAsync<ControllerOperationException>(() => power);
        Assert.Equal(PipeErrorCodes.InvalidArgument, error.Code);
        Assert.Contains("mode changed", error.Message, StringComparison.Ordinal);
        Assert.Equal(count.Read, fixture.Device.ReadCalls);
        Assert.Equal(count.Write, fixture.Device.Writes.Count);
        Assert.Equal(count.Off, fixture.Device.ShutdownOffWrites);
        Assert.Equal(mode, fixture.Coordinator.GetStatus().Fan.Mode);
    }

    [Fact]
    public async Task PureManualBadPacketRemainsProtocolFailureWithoutRetryOrAutomaticFullSpeed()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.ReachSettledControlAsync();
        await fixture.Coordinator.SetModeAsync(ControlMode.PureManual, ProfilePeer, CancellationToken.None);
        var writes = fixture.Device.Writes.Count;
        fixture.Device.ReadFailures.Enqueue(new V12ProtocolException("Injected invalid checksum in device readback."));
        await Assert.ThrowsAsync<V12ProtocolException>(() =>
            fixture.Coordinator.SetManualSpeedAsync(61, ProfilePeer, CancellationToken.None));
        Assert.Equal(writes, fixture.Device.Writes.Count);
        Assert.Null(fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(writes, fixture.Device.Writes.Count);
        Assert.Equal(0, fixture.Device.LightingWrites);
    }

    [Fact]
    public async Task PureManualLightingDifferenceKeepsRealReadbackWithoutTryingCorrection()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.ReachSettledControlAsync();
        await fixture.Coordinator.SetModeAsync(ControlMode.PureManual, ProfilePeer, CancellationToken.None);
        fixture.Device.ChangeLightingOnNextSpeedWrite = true;
        fixture.Device.IgnoreLightingWrites = true;
        var writes = fixture.Device.Writes.Count;
        var error = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.SetManualSpeedAsync(61, ProfilePeer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.CommandReadbackMismatch, error.Code);
        Assert.Equal(0, fixture.Device.LightingWrites);
        Assert.Equal(new byte[] { 61 }, fixture.Device.Writes.Skip(writes));
        Assert.Equal((byte)61, fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(0, fixture.Device.LightingWrites);
        Assert.Equal(new byte[] { 61 }, fixture.Device.Writes.Skip(writes));
    }

    private sealed class PassiveModeGateStore : IConfigurationStore
    {
        private ControllerConfiguration current = ControllerConfiguration.CreateDefault();
        public bool BlockPassiveSave { get; set; }
        public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ControllerConfiguration> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(current);
        public async Task SaveAsync(ControllerConfiguration configuration, CancellationToken cancellationToken)
        {
            if (BlockPassiveSave && configuration.Mode is ControlMode.PureManual or ControlMode.Released)
            {
                SaveEntered.TrySetResult();
                await ReleaseSave.Task.WaitAsync(cancellationToken);
            }
            current = configuration;
        }
    }
}
