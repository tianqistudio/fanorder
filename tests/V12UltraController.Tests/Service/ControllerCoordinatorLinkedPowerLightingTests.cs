using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed partial class ControllerCoordinatorTests
{
    private static readonly LightingSettings LinkedPowerLighting = new(true, 1, 2, 220, 3);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedPowerOffConfirmsLightsOutBeforeStoppingFanAndOnRestoresPreset(bool disabled)
    {
        await using var fixture = await CreateLinkedPowerFixtureAsync(disabled);
        var original = fixture.Device.LightingState;
        var start = fixture.Device.Operations.Count;
        var writes = fixture.Device.Writes.Count;

        await fixture.Coordinator.SetManualPowerAsync(false, true, ProfilePeer, CancellationToken.None);

        Assert.False(fixture.Device.PoweredOn);
        Assert.Equal(LinkedPowerLighting with { Enabled = false }, await ReadFakeLightingAsync(fixture));
        Assert.Equal(1, fixture.Device.LightingWrites);
        Assert.Equal(1, fixture.Device.ShutdownOffWrites);
        Assert.Equal(writes, fixture.Device.Writes.Count);
        var offOperations = fixture.Device.Operations.Skip(start).ToArray();
        var rgb = Array.IndexOf(offOperations, "RGB:OFF");
        var off = Array.IndexOf(offOperations, "OFF:START");
        Assert.True(rgb >= 0 && off > rgb, string.Join(", ", offOperations));
        Assert.Contains("READ", offOperations[(rgb + 1)..off]);
        Assert.DoesNotContain(offOperations, operation => operation.StartsWith("ON:", StringComparison.Ordinal));

        await fixture.Coordinator.SetManualPowerAsync(true, true, ProfilePeer, CancellationToken.None);

        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal(original, fixture.Device.LightingState);
        Assert.Equal(2, fixture.Device.LightingWrites);
        Assert.Equal(disabled, fixture.Coordinator.GetStatus().SafetyProtection!.Disabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedPowerManualSpeedRestoresPresetAfterConfirmedFanStart(bool disabled)
    {
        await using var fixture = await CreateLinkedPowerFixtureAsync(disabled);
        await fixture.Coordinator.SetManualPowerAsync(false, true, ProfilePeer, CancellationToken.None);
        var start = fixture.Device.Operations.Count;

        await fixture.Coordinator.SetManualSpeedAsync(55, ProfilePeer, CancellationToken.None);
        if (!disabled) await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal((byte)55, fixture.Device.CurrentPercent);
        Assert.Equal(LinkedPowerLighting, await ReadFakeLightingAsync(fixture));
        var operations = fixture.Device.Operations.Skip(start).ToArray();
        var fan = Array.IndexOf(operations, "ON:55");
        var rgb = Array.IndexOf(operations, "RGB:ON");
        Assert.True(fan >= 0 && rgb > fan, string.Join(", ", operations));
        Assert.Contains("READ", operations[(fan + 1)..rgb]);
        Assert.DoesNotContain("ON:100", operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedPowerOriginallyUnlitPresetStaysUnlitAcrossOffAndOn(bool disabled)
    {
        await using var fixture = await CreateLinkedPowerFixtureAsync(disabled, lit: false);
        var original = fixture.Device.LightingState;

        await fixture.Coordinator.SetManualPowerAsync(false, true, ProfilePeer, CancellationToken.None);
        await fixture.Coordinator.SetManualPowerAsync(true, true, ProfilePeer, CancellationToken.None);

        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal(original, fixture.Device.LightingState);
        Assert.Equal(0, fixture.Device.LightingWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedPowerRepeatedOffDoesNotReplaceOriginalLightingWithMutedPreset(bool disabled)
    {
        await using var fixture = await CreateLinkedPowerFixtureAsync(disabled);
        await fixture.Coordinator.SetManualPowerAsync(false, true, ProfilePeer, CancellationToken.None);
        var writes = fixture.Device.Writes.Count;
        await fixture.Coordinator.SetManualPowerAsync(false, true, ProfilePeer, CancellationToken.None);

        Assert.False(fixture.Device.PoweredOn);
        Assert.Equal(writes, fixture.Device.Writes.Count);
        Assert.Equal(1, fixture.Device.LightingWrites);
        await fixture.Coordinator.SetManualPowerAsync(true, true, ProfilePeer, CancellationToken.None);
        Assert.Equal(LinkedPowerLighting, await ReadFakeLightingAsync(fixture));
        Assert.Equal(2, fixture.Device.LightingWrites);
    }

    [Fact]
    public async Task LinkedPowerUnconfirmedOffDoesNotTouchLightsOrFan()
    {
        await using var fixture = await CreateLinkedPowerFixtureAsync(disabled: true);
        var counts = fixture.CaptureHardwareCounts();
        var original = fixture.Device.LightingState;

        await Assert.ThrowsAsync<ControllerOperationException>(() => fixture.Coordinator.SetManualPowerAsync(
            false, false, ProfilePeer, CancellationToken.None));

        Assert.Equal(counts, fixture.CaptureHardwareCounts());
        Assert.Equal(original, fixture.Device.LightingState);
        Assert.Equal(0, fixture.Device.LightingWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedPowerFailedLightsOutStopsBeforeOffAndDoesNotRetryInBackground(bool writeThrows)
    {
        await using var fixture = await CreateLinkedPowerFixtureAsync(disabled: true);
        fixture.Device.IgnoreLightingWrites = !writeThrows;
        if (writeThrows) fixture.Device.LightingWriteFailure = new IOException("Injected RGB write failure.");
        var writes = fixture.Device.Writes.Count;

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Coordinator.SetManualPowerAsync(
            false, true, ProfilePeer, CancellationToken.None));

        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.Equal(writes, fixture.Device.Writes.Count);
        Assert.Equal(1, fixture.Device.LightingWrites);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Device.LightingWrites);
        Assert.Equal(writes, fixture.Device.Writes.Count);
        Assert.True(fixture.Coordinator.GetStatus().SafetyProtection!.Disabled);
    }

    [Fact]
    public async Task LinkedPowerLightsOutReadFailureNeverSendsOff()
    {
        await using var fixture = await CreateLinkedPowerFixtureAsync(disabled: true);
        fixture.Device.BeforeRead = () =>
        {
            if (fixture.Device.LightingWrites > 0) throw new V12ProtocolException("Injected mute readback failure.");
        };
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => fixture.Coordinator.SetManualPowerAsync(
                false, true, ProfilePeer, CancellationToken.None));
            Assert.True(fixture.Device.PoweredOn);
            Assert.Equal(1, fixture.Device.LightingWrites);
            Assert.Equal(0, fixture.Device.ShutdownOffWrites);
            Assert.Empty(fixture.Device.Writes);
        }
        finally { fixture.Device.BeforeRead = null; }
    }

    [Fact]
    public async Task LinkedPowerAlreadyOffDoesNotRestartFanToMuteLights()
    {
        await using var fixture = await CreateLinkedPowerFixtureAsync(disabled: true);
        fixture.Device.SetState(100, poweredOn: false);

        var error = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.SetManualPowerAsync(false, true, ProfilePeer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InvalidArgument, error.Code);
        Assert.False(fixture.Device.PoweredOn);
        Assert.Equal(0, fixture.Device.LightingWrites);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task LinkedPowerPhysicalLightingChangeWhileOffSupersedesRememberedPreset()
    {
        await using var fixture = await CreateLinkedPowerFixtureAsync(disabled: true);
        await fixture.Coordinator.SetManualPowerAsync(false, true, ProfilePeer, CancellationToken.None);
        var physicalLighting = new LightingSettings(false, 2, 4, 99, 1);
        fixture.Device.SetLightingState(physicalLighting);

        await fixture.Coordinator.SetManualPowerAsync(true, true, ProfilePeer, CancellationToken.None);

        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal(physicalLighting, await ReadFakeLightingAsync(fixture));
        Assert.Equal(1, fixture.Device.LightingWrites);
    }

    [Fact]
    public async Task LinkedPowerRememberedLightingIsRuntimeOnlyAndNotReplayedAfterRestart()
    {
        var marker = ExplicitLightingMarker(true);
        ControllerConfiguration saved;
        await using (var original = await CreateLinkedPowerFixtureAsync(disabled: true))
        {
            await original.Coordinator.SetManualPowerAsync(false, true, ProfilePeer, CancellationToken.None);
            saved = await original.ConfigurationStore.LoadAsync(CancellationToken.None);
            Assert.DoesNotContain("lighting", await File.ReadAllTextAsync(original.Paths.ConfigurationPath), StringComparison.OrdinalIgnoreCase);
        }
        await using var restarted = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: saved, suppliedSafetyProtectionStore: marker);
        restarted.Device.SetLightingState(LinkedPowerLighting with { Enabled = false });
        restarted.Device.SetState(100, poweredOn: false);

        await restarted.Coordinator.SetManualPowerAsync(true, true, ProfilePeer, CancellationToken.None);

        Assert.True(restarted.Device.PoweredOn);
        Assert.Equal(LinkedPowerLighting with { Enabled = false }, await ReadFakeLightingAsync(restarted));
        Assert.Equal(0, restarted.Device.LightingWrites);
    }

    [Fact]
    public async Task LinkedPowerNewManualRequestAfterMutePreventsOlderOffWrite()
    {
        await using var fixture = await CreateLinkedPowerFixtureAsync(disabled: true);
        Task? newer = null;
        fixture.Device.BeforeRead = () =>
        {
            if (fixture.Device.LightingWrites == 1 && newer is null)
                newer = fixture.Coordinator.SetManualSpeedAsync(64, ProfilePeer, CancellationToken.None);
        };

        var older = await Assert.ThrowsAsync<ControllerOperationException>(() => fixture.Coordinator.SetManualPowerAsync(
            false, true, ProfilePeer, CancellationToken.None));
        fixture.Device.BeforeRead = null;
        Assert.Equal(PipeErrorCodes.DeviceStateChanged, older.Code);
        Assert.NotNull(newer);
        await newer;

        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal((byte)64, fixture.Device.CurrentPercent);
        Assert.DoesNotContain((byte)100, fixture.Device.Writes);
    }

    [Fact]
    public async Task LinkedPowerPhysicalOffDuringRestoreReadbackDoesNotIssueRgbOrRestartAgain()
    {
        await using var fixture = await CreateLinkedPowerFixtureAsync(disabled: true);
        await fixture.Coordinator.SetManualPowerAsync(false, true, ProfilePeer, CancellationToken.None);
        fixture.Device.BeforeRead = () =>
        {
            if (fixture.Device.ManualSoftwareControlSelections > 0)
                fixture.Device.SetState(100, poweredOn: false);
        };
        try
        {
            await Assert.ThrowsAsync<ControllerOperationException>(() => fixture.Coordinator.SetManualSpeedAsync(
                55, ProfilePeer, CancellationToken.None));
            Assert.False(fixture.Device.PoweredOn);
            Assert.Equal(1, fixture.Device.LightingWrites);
            Assert.Equal(new byte[] { 55 }, fixture.Device.Writes);
        }
        finally { fixture.Device.BeforeRead = null; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedPowerRestoreFailureKeepsConfirmedFanSpeedWithoutBackgroundRgbRetry(bool writeThrows)
    {
        await using var fixture = await CreateLinkedPowerFixtureAsync(disabled: true);
        await fixture.Coordinator.SetManualPowerAsync(false, true, ProfilePeer, CancellationToken.None);
        fixture.Device.IgnoreLightingWrites = !writeThrows;
        if (writeThrows) fixture.Device.LightingWriteFailure = new IOException("Injected RGB restore failure.");

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Coordinator.SetManualSpeedAsync(
            55, ProfilePeer, CancellationToken.None));

        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal((byte)55, fixture.Device.CurrentPercent);
        Assert.Equal(new byte[] { 55 }, fixture.Device.Writes);
        Assert.Equal(2, fixture.Device.LightingWrites);
        var saved = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal((byte)55, saved.ManualSpeedPercent);
        Assert.Equal(ControlMode.Manual, saved.Mode);
        fixture.Device.LightingWriteFailure = null;
        fixture.Device.IgnoreLightingWrites = false;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(2, fixture.Device.LightingWrites);
        Assert.Equal(new byte[] { 55 }, fixture.Device.Writes);
        Assert.True(fixture.Coordinator.GetStatus().SafetyProtection!.Disabled);
    }

    [Fact]
    public async Task LinkedPowerExplicitLightingChoiceSupersedesRememberedPreset()
    {
        await using var fixture = await CreateLinkedPowerFixtureAsync(disabled: true);
        await fixture.Coordinator.SetManualPowerAsync(false, true, ProfilePeer, CancellationToken.None);
        fixture.Device.SetState(55, poweredOn: true);
        var userChoice = LinkedPowerLighting with { Enabled = false };

        await fixture.Coordinator.SetLightingAsync(userChoice, ProfilePeer, CancellationToken.None);
        await fixture.Coordinator.SetManualPowerAsync(true, true, ProfilePeer, CancellationToken.None);

        Assert.Equal(userChoice, await ReadFakeLightingAsync(fixture));
        Assert.Equal(2, fixture.Device.LightingWrites);
    }

    [Fact]
    public async Task LinkedPowerNewDeviceHandleDoesNotReceiveOldRememberedPreset()
    {
        await using var fixture = await CreateLinkedPowerFixtureAsync(disabled: true);
        await fixture.Coordinator.SetManualPowerAsync(false, true, ProfilePeer, CancellationToken.None);
        await fixture.Coordinator.SetModeAsync(ControlMode.Released, ProfilePeer, CancellationToken.None);
        await fixture.Coordinator.SetModeAsync(ControlMode.PureManual, ProfilePeer, CancellationToken.None);
        fixture.Locator.ReturnEphemeralDeviceOnOpen = true;

        await fixture.Coordinator.SetManualPowerAsync(true, true, ProfilePeer, CancellationToken.None);

        Assert.NotNull(fixture.Locator.LastEphemeralDevice);
        Assert.True(fixture.Locator.LastEphemeralDevice.TryGetTarget(out var reopened));
        var replacement = Assert.IsType<FakeDevice>(reopened);
        Assert.True(replacement.PoweredOn);
        Assert.Equal(0, replacement.LightingWrites);
        Assert.Equal(1, fixture.Device.LightingWrites);
    }

    [Fact]
    public async Task LinkedPowerPhysicalFanChangeDuringMuteReadbackStopsBeforeOffWrite()
    {
        await using var fixture = await CreateLinkedPowerFixtureAsync(disabled: true);
        fixture.Device.BeforeRead = () =>
        {
            if (fixture.Device.LightingWrites > 0)
                fixture.Device.SetState(67, poweredOn: true);
        };
        try
        {
            await Assert.ThrowsAsync<ControllerOperationException>(() => fixture.Coordinator.SetManualPowerAsync(
                false, true, ProfilePeer, CancellationToken.None));
            Assert.Equal((byte)67, fixture.Device.CurrentPercent);
            Assert.True(fixture.Device.PoweredOn);
            Assert.Equal(0, fixture.Device.ShutdownOffWrites);
            Assert.Equal(1, fixture.Device.LightingWrites);
            Assert.Empty(fixture.Device.Writes);
        }
        finally { fixture.Device.BeforeRead = null; }
    }

    [Fact]
    public async Task LinkedPowerPureManualMutesAndRestoresWithoutThermalAutomaticWrites()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ExplicitLightingConfiguration(ControlMode.PureManual));
        fixture.Device.SetLightingState(LinkedPowerLighting);
        fixture.Thermal.CpuTemperature = 100;

        await fixture.Coordinator.SetManualPowerAsync(false, true, ProfilePeer, CancellationToken.None);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.False(fixture.Device.PoweredOn);
        Assert.Equal(LinkedPowerLighting with { Enabled = false }, await ReadFakeLightingAsync(fixture));
        Assert.Empty(fixture.Device.Writes);

        await fixture.Coordinator.SetManualSpeedAsync(55, ProfilePeer, CancellationToken.None);
        Assert.Equal(LinkedPowerLighting, await ReadFakeLightingAsync(fixture));
        Assert.Equal(new byte[] { 55 }, fixture.Device.Writes);
        Assert.Equal(ControlMode.PureManual, fixture.Coordinator.GetStatus().Fan.Mode);
        Assert.False(fixture.Coordinator.GetStatus().SafetyProtection!.Disabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedPowerProtectedPreflightRefusalNeverReopensFanOrIssuesReplacementSpeed(bool unknownLighting)
    {
        await using var fixture = await CreateLinkedPowerFixtureAsync(disabled: false);
        if (unknownLighting)
        {
            var report = (await fixture.Device.ReadStateAsync(CancellationToken.None)).ExportFeatureReport();
            report[4] = 0x7F;
            report[^1] = V12FeatureReport.CalculateChecksum(report.AsSpan(1, 7));
            await fixture.Device.InitializeTrustedStateAsync(V12FeatureReport.ParseState(report), CancellationToken.None);
        }
        else fixture.Device.SetState(100, poweredOn: false);
        var writes = fixture.Device.Writes.Count;
        var poweredOn = fixture.Device.PoweredOn;
        var actualPercent = fixture.Device.CurrentPercent;

        var error = await Assert.ThrowsAsync<ControllerOperationException>(() => fixture.Coordinator.SetManualPowerAsync(
            false, true, ProfilePeer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InvalidArgument, error.Code);
        Assert.Equal(poweredOn, fixture.Device.PoweredOn);
        Assert.Equal(actualPercent, fixture.Device.CurrentPercent);
        Assert.Equal(writes, fixture.Device.Writes.Count);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.Equal(0, fixture.Device.LightingWrites);
        Assert.False(fixture.Coordinator.GetStatus().SafetyOperationAudit!.Blocked);
    }

    [Fact]
    public async Task LinkedPowerPhysicalLightingChangeDuringFanStartReadbackIsNeverRepairedToOldPreset()
    {
        await using var fixture = await CreateLinkedPowerFixtureAsync(disabled: true);
        await fixture.Coordinator.SetManualPowerAsync(false, true, ProfilePeer, CancellationToken.None);
        var physicalLighting = new LightingSettings(true, 2, 4, 99, 1);
        fixture.Device.BeforeRead = () =>
        {
            if (fixture.Device.ManualSoftwareControlSelections > 0)
                fixture.Device.SetLightingState(physicalLighting);
        };
        try
        {
            await Assert.ThrowsAsync<ControllerOperationException>(() => fixture.Coordinator.SetManualSpeedAsync(
                55, ProfilePeer, CancellationToken.None));
            Assert.Equal(physicalLighting, await ReadFakeLightingAsync(fixture));
            Assert.Equal(1, fixture.Device.LightingWrites);
            Assert.Equal(new byte[] { 55 }, fixture.Device.Writes);
            Assert.True(fixture.Device.PoweredOn);
            Assert.Equal((byte)55, fixture.Device.CurrentPercent);
        }
        finally { fixture.Device.BeforeRead = null; }
    }

    [Fact]
    public async Task LinkedPowerProtectedOffAcceptsDarkOffReadbackWithRetainedManualPercent()
    {
        await using var fixture = await CreateLinkedPowerFixtureAsync(disabled: false);
        var writes = fixture.Device.Writes.Count;
        fixture.Device.BeforeRead = () =>
        {
            if (fixture.Device.ShutdownOffWrites > 0)
                fixture.Device.SetState(47, poweredOn: false);
        };
        try
        {
            await fixture.Coordinator.SetManualPowerAsync(false, true, ProfilePeer, CancellationToken.None);
            Assert.False(fixture.Device.PoweredOn);
            Assert.Equal((byte)47, fixture.Device.CurrentPercent);
            Assert.Equal(LinkedPowerLighting with { Enabled = false }, await ReadFakeLightingAsync(fixture));
            Assert.Equal(writes, fixture.Device.Writes.Count);
            Assert.True(fixture.Coordinator.GetStatus().Fan.OffRequested);
            Assert.Equal((byte)47, fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
            Assert.False(fixture.Coordinator.GetStatus().SafetyOperationAudit!.Blocked);
        }
        finally { fixture.Device.BeforeRead = null; }
    }

    [Fact]
    public async Task LinkedPowerProtectedOnPreservesNewPhysicalLightingWithoutAdditionalFullSpeedWrite()
    {
        await using var fixture = await CreateLinkedPowerFixtureAsync(disabled: false);
        await fixture.Coordinator.SetManualPowerAsync(false, true, ProfilePeer, CancellationToken.None);
        var writes = fixture.Device.Writes.Count;
        var physicalLighting = new LightingSettings(true, 2, 4, 99, 1);
        fixture.Device.BeforeRead = () =>
        {
            if (fixture.Device.Writes.Count > writes)
                fixture.Device.SetLightingState(physicalLighting);
        };
        try
        {
            var error = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                fixture.Coordinator.SetManualPowerAsync(true, true, ProfilePeer, CancellationToken.None));
            Assert.Equal(PipeErrorCodes.DeviceStateChanged, error.Code);
            Assert.True(fixture.Device.PoweredOn);
            Assert.Equal((byte)100, fixture.Device.CurrentPercent);
            Assert.Equal(physicalLighting, await ReadFakeLightingAsync(fixture));
            Assert.Equal(new byte[] { 100 }, fixture.Device.Writes.Skip(writes));
            Assert.Equal(1, fixture.Device.LightingWrites);
            Assert.False(fixture.Coordinator.GetStatus().SafetyOperationAudit!.Blocked);
        }
        finally { fixture.Device.BeforeRead = null; }
    }

    private static async Task<CoordinatorFixture> CreateLinkedPowerFixtureAsync(bool disabled, bool lit = true)
    {
        var fixture = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ExplicitLightingConfiguration(ControlMode.Manual),
            suppliedSafetyProtectionStore: ExplicitLightingMarker(disabled));
        if (!disabled)
        {
            fixture.Thermal.CpuTemperature = 40;
            await fixture.ReachSettledControlAsync();
        }
        fixture.Device.SetLightingState(LinkedPowerLighting with { Enabled = lit });
        return fixture;
    }

    private static async Task<LightingSettings> ReadFakeLightingAsync(CoordinatorFixture fixture) =>
        LightingSettings.FromState(await fixture.Device.ReadStateAsync(CancellationToken.None))!;
}
