using System.Text.Json;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed partial class ControllerCoordinatorTests
{
    private static readonly LightingSettings ExplicitLightingPreset = new(true, 1, 2, 220, 3);

    [Theory]
    [InlineData(ControlMode.PureManual, false, true)]
    [InlineData(ControlMode.PureManual, false, false)]
    [InlineData(ControlMode.Auto, true, true)]
    [InlineData(ControlMode.Auto, true, false)]
    [InlineData(ControlMode.Manual, true, true)]
    [InlineData(ControlMode.Manual, true, false)]
    public async Task ExplicitLightingReopensForOneVerifiedCommandWithoutChangingFanOrSavedIntent(
        ControlMode mode, bool disabled, bool hot)
    {
        var marker = ExplicitLightingMarker(disabled);
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            suppliedSafetyProtectionStore: marker, initialConfiguration: ExplicitLightingConfiguration(mode));
        fixture.Device.SetState(44, poweredOn: true);
        fixture.Device.IgnoreNormalSpeedWrites = true; // Physical-mode status is opaque, not an RGB exclusion.
        fixture.Thermal.CpuTemperature = hot ? 100 : null;
        var configBytes = await File.ReadAllBytesAsync(fixture.Paths.ConfigurationPath, TestContext.Current.CancellationToken);
        var intent = marker.Current;
        var reads = fixture.Device.ReadCalls;
        Assert.Equal(0, fixture.Locator.OpenCalls);

        var result = await fixture.Coordinator.SetLightingAsync(ExplicitLightingPreset, ProfilePeer,
            TestContext.Current.CancellationToken);

        Assert.True(JsonSerializer.SerializeToElement(result).GetProperty("readbackConfirmed").GetBoolean());
        Assert.Equal(1, fixture.Locator.OpenCalls);
        Assert.Equal(reads + 2, fixture.Device.ReadCalls);
        Assert.Equal(1, fixture.Device.LightingWrites);
        Assert.Equal(((byte)1, (byte)2, (byte)0, (byte)220), fixture.Device.LightingState);
        Assert.Equal((byte)44, fixture.Device.CurrentPercent);
        Assert.True(fixture.Device.PoweredOn);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.Equal(0, fixture.Device.SoftwareControlSelections);
        Assert.Equal(mode, fixture.Coordinator.GetStatus().Fan.Mode);
        Assert.Equal((byte)59, fixture.Coordinator.GetStatus().Fan.ConfiguredManualPercent);
        Assert.Equal(disabled, fixture.Coordinator.GetStatus().SafetyProtection!.Disabled);
        Assert.Equal(intent, marker.Current);
        Assert.Equal(configBytes, await File.ReadAllBytesAsync(fixture.Paths.ConfigurationPath, TestContext.Current.CancellationToken));
        Assert.True(fixture.WriterLease.IsHeld);

        await fixture.Coordinator.TickAsync(TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal(1, fixture.Device.LightingWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitLightingReleasedModeRejectsWithoutOpeningDevice(bool disabled)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            suppliedSafetyProtectionStore: ExplicitLightingMarker(disabled),
            initialConfiguration: ExplicitLightingConfiguration(ControlMode.Released));
        var counts = fixture.CaptureHardwareCounts();
        await Assert.ThrowsAsync<ControllerOperationException>(() => fixture.Coordinator.SetLightingAsync(
            ExplicitLightingPreset, ProfilePeer, TestContext.Current.CancellationToken));
        Assert.Equal(counts, fixture.CaptureHardwareCounts());
        Assert.Equal(0, fixture.Device.LightingWrites);
    }

    [Theory]
    [InlineData(ControlMode.PureManual, false)]
    [InlineData(ControlMode.Manual, true)]
    public async Task ExplicitLightingOffReadbackDoesNotTurnFanOnOrIssueLighting(ControlMode mode, bool disabled)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            suppliedSafetyProtectionStore: ExplicitLightingMarker(disabled),
            initialConfiguration: ExplicitLightingConfiguration(mode));
        fixture.Device.SetState(100, poweredOn: false);
        await Assert.ThrowsAsync<ControllerOperationException>(() => fixture.Coordinator.SetLightingAsync(
            ExplicitLightingPreset, ProfilePeer, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Device.ReadCalls);
        Assert.Equal(0, fixture.Device.LightingWrites);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.False(fixture.Device.PoweredOn);
        Assert.False(fixture.Coordinator.IsHardwarePoisoned);
    }

    [Fact]
    public async Task ExplicitLightingMismatchReportsFailureWithoutFanCompensationOrRetry()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            suppliedSafetyProtectionStore: ExplicitLightingMarker(true),
            initialConfiguration: ExplicitLightingConfiguration(ControlMode.Manual));
        fixture.Device.SetState(44, poweredOn: true);
        fixture.Device.IgnoreLightingWrites = true;
        var failure = await Assert.ThrowsAsync<ControllerOperationException>(() => fixture.Coordinator.SetLightingAsync(
            ExplicitLightingPreset, ProfilePeer, TestContext.Current.CancellationToken));
        Assert.Contains(failure.Code, new[] { PipeErrorCodes.CommandReadbackMismatch, PipeErrorCodes.DeviceStateChanged });
        Assert.Equal(2, fixture.Device.ReadCalls);
        Assert.Equal(1, fixture.Device.LightingWrites);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal((byte)44, fixture.Device.CurrentPercent);
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal(0, fixture.Device.SoftwareControlSelections);
        Assert.False(fixture.Coordinator.IsHardwarePoisoned);
        Assert.True(fixture.Coordinator.GetStatus().SafetyProtection!.Disabled);
    }

    [Fact]
    public async Task ExplicitLightingPostWriteReadFailureNeverRetriesOrCompensatesFan()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            suppliedSafetyProtectionStore: ExplicitLightingMarker(true),
            initialConfiguration: ExplicitLightingConfiguration(ControlMode.Auto));
        fixture.Device.SetState(44, poweredOn: true);
        fixture.Device.BeforeRead = () =>
        {
            if (fixture.Device.LightingWrites > 0) throw new V12ProtocolException("simulated RGB readback checksum failure");
        };
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => fixture.Coordinator.SetLightingAsync(
                ExplicitLightingPreset, ProfilePeer, TestContext.Current.CancellationToken));
            Assert.Equal(1, fixture.Device.LightingWrites);
            Assert.Empty(fixture.Device.Writes);
            Assert.Equal(0, fixture.Device.ShutdownOffWrites);
            Assert.Equal((byte)44, fixture.Device.CurrentPercent);
            Assert.True(fixture.Device.PoweredOn);
            Assert.Equal(0, fixture.Device.SoftwareControlSelections);
            Assert.True(fixture.Coordinator.GetStatus().SafetyProtection!.Disabled);
            Assert.Null(fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
        }
        finally { fixture.Device.BeforeRead = null; }
    }

    [Fact]
    public async Task ExplicitLightingExistingAuditBarrierRemainsAuthoritativeAndUnchanged()
    {
        const string existingJournal = "unreadable pending safety operation retained verbatim";
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            suppliedSafetyProtectionStore: ExplicitLightingMarker(true),
            initialConfiguration: ExplicitLightingConfiguration(ControlMode.Manual),
            rawSafetyOperationAuditJournalJson: existingJournal);
        var counts = fixture.CaptureHardwareCounts();
        await Assert.ThrowsAsync<ControllerOperationException>(() => fixture.Coordinator.SetLightingAsync(
            ExplicitLightingPreset, ProfilePeer, TestContext.Current.CancellationToken));
        Assert.Equal(counts, fixture.CaptureHardwareCounts());
        Assert.Equal(0, fixture.Device.LightingWrites);
        Assert.True(fixture.Coordinator.GetStatus().SafetyOperationAudit!.Blocked);
        Assert.Equal(existingJournal, await File.ReadAllTextAsync(fixture.Paths.SafetyOperationAuditPendingPath,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExplicitLightingTimeoutRetainsPoisonBarrierAndLeaseForLaterRequests()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            suppliedSafetyProtectionStore: ExplicitLightingMarker(true),
            initialConfiguration: ExplicitLightingConfiguration(ControlMode.Manual));
        using var nativeRead = new ManualResetEventSlim(false);
        fixture.Device.ReadGate = nativeRead;
        fixture.Device.IgnoreReadCancellation = true;
        var command = fixture.Coordinator.SetLightingAsync(ExplicitLightingPreset, ProfilePeer, CancellationToken.None);
        try
        {
            await fixture.Device.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<Exception>(() => command.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.True(fixture.Coordinator.IsHardwarePoisoned);
            Assert.True(fixture.WriterLease.IsHeld);
            var counts = fixture.CaptureHardwareCounts();
            await Assert.ThrowsAsync<ControllerOperationException>(() => fixture.Coordinator.SetLightingAsync(
                ExplicitLightingPreset, ProfilePeer, TestContext.Current.CancellationToken));
            Assert.Equal(counts, fixture.CaptureHardwareCounts());
            Assert.Equal(0, fixture.Device.LightingWrites);
            Assert.Empty(fixture.Device.Writes);
        }
        finally { nativeRead.Set(); }
    }

    private static ControllerConfiguration ExplicitLightingConfiguration(ControlMode mode) =>
        ControllerConfiguration.CreateDefault() with { Commissioned = true, Mode = mode, ManualSpeedPercent = 59 };

    private static FakeSafetyProtectionStore ExplicitLightingMarker(bool disabled) => new()
    {
        Current = disabled ? new(1, true, DateTimeOffset.UtcNow, ProfilePeer.CallerSid) : null,
    };
}
