using V12UltraController.Core.Control;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed partial class ControllerCoordinatorTests
{
    [Theory]
    [InlineData("Settings")]
    [InlineData("Tray")]
    public async Task ManualTargetSelectionAppliesRequestedSpeedFromPhysicalModeWithoutFullSpeed(string source)
    {
        var protection = ManualPriorityProtection();
        var originalProtection = protection.Current;
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ManualPriorityConfiguration(), suppliedSafetyProtectionStore: protection);
        fixture.Device.SetState(59, poweredOn: true);
        fixture.Device.IgnoreNormalSpeedWrites = true;
        var lighting = fixture.Device.LightingState;

        await fixture.Coordinator.SetManualSpeedAsync(55,
            new PipePeerIdentity(ProfilePeer.CallerSid, source), CancellationToken.None);

        Assert.Equal(new byte[] { 55 }, fixture.Device.ManualSoftwareControlTargets);
        Assert.Equal(new byte[] { 55 }, fixture.Device.Writes);
        Assert.Equal((byte)55, fixture.Device.CurrentPercent);
        Assert.Equal((byte)55, fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
        var saved = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal(ControlMode.Manual, saved.Mode);
        Assert.Equal((byte)55, saved.ManualSpeedPercent);
        Assert.Equal(originalProtection, protection.Current);
        Assert.Equal(lighting, fixture.Device.LightingState);
        AssertNoManualPriorityAuxiliaryWrites(fixture);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(new byte[] { 55 }, fixture.Device.ManualSoftwareControlTargets);
        Assert.Equal(new byte[] { 55 }, fixture.Device.Writes);
    }

    [Fact]
    public async Task IgnoredManualTargetSelectionRejectsUnchangedReadbackWithoutSavingOldActualAsSuccess()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ManualPriorityConfiguration() with { ManualSpeedPercent = 62 },
            suppliedSafetyProtectionStore: ManualPriorityProtection());
        fixture.Device.SetState(59, poweredOn: true);
        fixture.Device.IgnoreNormalSpeedWrites = true;
        fixture.Device.IgnoreManualSoftwareControl = true;
        var lastSuccessfulWrite = fixture.Coordinator.GetStatus().LastSuccessfulWrite;

        var error = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.SetManualSpeedAsync(55, ProfilePeer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.DeviceStateChanged, error.Code);
        var saved = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal((byte)62, saved.ManualSpeedPercent);
        Assert.Equal(ControlMode.Auto, saved.Mode);
        Assert.Equal((byte)59, fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
        Assert.Equal(lastSuccessfulWrite, fixture.Coordinator.GetStatus().LastSuccessfulWrite);
        Assert.Equal(new byte[] { 55 }, fixture.Device.ManualSoftwareControlTargets);
        Assert.Equal(new byte[] { 55 }, fixture.Device.Writes);
        AssertNoManualPriorityAuxiliaryWrites(fixture);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(new byte[] { 55 }, fixture.Device.Writes);
        Assert.Equal((byte)62, (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).ManualSpeedPercent);
    }

    [Fact]
    public async Task PhysicalInputAfterManualTargetSelectionIsAdoptedWithoutRepeatingSet()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ManualPriorityConfiguration(),
            suppliedSafetyProtectionStore: ManualPriorityProtection());
        fixture.Device.SetState(59, poweredOn: true);
        fixture.Device.IgnoreNormalSpeedWrites = true;
        var reads = 0;
        fixture.Device.BeforeRead = () =>
        {
            if (++reads == 2) fixture.Device.SetState(64, poweredOn: true);
        };

        await fixture.Coordinator.SetManualSpeedAsync(55, ProfilePeer, CancellationToken.None);

        Assert.Equal(new byte[] { 55 }, fixture.Device.ManualSoftwareControlTargets);
        Assert.Equal(new byte[] { 55 }, fixture.Device.Writes);
        Assert.Equal((byte)64, fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
        var saved = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal(ControlMode.Manual, saved.Mode);
        Assert.Equal((byte)64, saved.ManualSpeedPercent);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(new byte[] { 55 }, fixture.Device.Writes);
        AssertNoManualPriorityAuxiliaryWrites(fixture);
    }

    [Fact]
    public async Task NewerManualRequestSkipsOlderUnsentSoftwareTargetSelection()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ManualPriorityConfiguration(),
            suppliedSafetyProtectionStore: ManualPriorityProtection());
        fixture.Device.SetState(59, poweredOn: true);
        fixture.Device.IgnoreNormalSpeedWrites = true;
        using var readGate = new ManualResetEventSlim(false);
        fixture.Device.ReadGate = readGate;
        fixture.Device.IgnoreReadCancellation = true;
        var older = fixture.Coordinator.SetManualSpeedAsync(55, ProfilePeer, CancellationToken.None);
        Task? newer = null;
        try
        {
            await fixture.Device.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            newer = fixture.Coordinator.SetManualSpeedAsync(64, ProfilePeer, CancellationToken.None);
        }
        finally { readGate.Set(); }
        var error = await Assert.ThrowsAsync<ControllerOperationException>(() => older);
        Assert.Equal(PipeErrorCodes.DeviceStateChanged, error.Code);
        await newer!;

        Assert.Equal(new byte[] { 64 }, fixture.Device.ManualSoftwareControlTargets);
        Assert.Equal(new byte[] { 64 }, fixture.Device.Writes);
        Assert.Equal((byte)64, (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).ManualSpeedPercent);
        AssertNoManualPriorityAuxiliaryWrites(fixture);
    }

    [Fact]
    public async Task ProtectedManualRequestSelectsUserTargetAfterEarlierPhysicalModeChange()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ManualPriorityConfiguration());
        await fixture.ReachSettledControlAsync();
        fixture.Device.SetState(59, poweredOn: true);
        fixture.Device.IgnoreNormalSpeedWrites = true;
        var writes = fixture.Device.Writes.Count;

        await fixture.Coordinator.SetManualSpeedAsync(55, ProfilePeer, CancellationToken.None);
        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(new byte[] { 55 }, fixture.Device.ManualSoftwareControlTargets);
        Assert.Equal(new byte[] { 55 }, fixture.Device.Writes.Skip(writes));
        Assert.Equal((byte)55, fixture.Device.CurrentPercent);
        Assert.Equal((byte)55, fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
        Assert.Equal(ControlMode.Manual, fixture.Coordinator.GetStatus().Fan.Mode);
        Assert.Equal((byte)55, (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).ManualSpeedPercent);
        AssertNoManualPriorityAuxiliaryWrites(fixture);
    }

    [Fact]
    public async Task ProtectedIgnoredManualTargetKeepsSavedIntentAndReportsUnconfirmedWithoutRetry()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ManualPriorityConfiguration());
        await fixture.ReachSettledControlAsync();
        fixture.Device.SetState(59, poweredOn: true);
        fixture.Device.IgnoreNormalSpeedWrites = true;
        fixture.Device.IgnoreManualSoftwareControl = true;
        var writes = fixture.Device.Writes.Count;

        await fixture.Coordinator.SetManualSpeedAsync(55, ProfilePeer, CancellationToken.None);
        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal("ManualCommandUnconfirmed", fixture.Coordinator.GetStatus().ServiceState);
        Assert.Equal((byte)59, fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
        Assert.Equal((byte)55, (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).ManualSpeedPercent);
        for (var sample = 0; sample < 4; sample++)
            await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal("ManualCommandUnconfirmed", fixture.Coordinator.GetStatus().ServiceState);
        Assert.Equal(new byte[] { 55 }, fixture.Device.ManualSoftwareControlTargets);
        Assert.Equal(new byte[] { 55 }, fixture.Device.Writes.Skip(writes));
        Assert.Equal((byte)55, (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).ManualSpeedPercent);
        AssertNoManualPriorityAuxiliaryWrites(fixture);
    }

    [Fact]
    public async Task ProtectedManualRequestTurnsEarlierPhysicalOffStateOnAtRequestedTargetWithoutFullSpeed()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ManualPriorityConfiguration());
        await fixture.ReachSettledControlAsync();
        fixture.Device.SetState(100, poweredOn: false);
        fixture.Device.IgnoreNormalSpeedWrites = true;
        var writes = fixture.Device.Writes.Count;

        await fixture.Coordinator.SetManualSpeedAsync(55, ProfilePeer, CancellationToken.None);
        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(new byte[] { 55 }, fixture.Device.ManualSoftwareControlTargets);
        Assert.Equal(new byte[] { 55 }, fixture.Device.Writes.Skip(writes));
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal((byte)55, fixture.Device.CurrentPercent);
        Assert.False(fixture.Coordinator.GetStatus().Fan.OffRequested);
        Assert.Equal((byte)55, fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
        Assert.Equal((byte)55, (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).ManualSpeedPercent);
        AssertNoManualPriorityAuxiliaryWrites(fixture);
    }

    [Fact]
    public async Task NewManualProfileCanCompleteAfterPreviousManualTargetWasUnconfirmed()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ManualPriorityConfiguration());
        await fixture.ReachSettledControlAsync();
        fixture.Device.SetState(59, poweredOn: true);
        fixture.Device.IgnoreManualSoftwareControl = true;
        await fixture.Coordinator.SetManualSpeedAsync(55, ProfilePeer, CancellationToken.None);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal("ManualCommandUnconfirmed", fixture.Coordinator.GetStatus().ServiceState);
        Assert.Equal((byte)59, fixture.Device.CurrentPercent);

        fixture.Device.IgnoreManualSoftwareControl = false;
        var pending = fixture.Coordinator.ApplyProfileAsync(new(Profile(lighting: false, percent: 62)),
            Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None);
        await WaitForProfileSavedAsync(fixture);
        for (var sample = 0; sample < 10; sample++)
            await fixture.Coordinator.TickAsync(CancellationToken.None);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(ProfileApplyOutcome.Completed, result.Application.Outcome);
        Assert.Equal(ProfileFanState.Confirmed, result.Application.Fan);
        Assert.Equal(ProfileLightingState.NotRequested, result.Application.Lighting);
        Assert.Equal((byte)62, fixture.Device.CurrentPercent);
        Assert.Equal((byte)62, result.Status.Fan.ReportedControlPercent);
        Assert.Equal((byte)62, (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).ManualSpeedPercent);
        Assert.Equal(new byte[] { 55, 62 }, fixture.Device.ManualSoftwareControlTargets);
        Assert.NotEqual("ManualCommandUnconfirmed", result.Status.ServiceState);
        AssertNoManualPriorityAuxiliaryWrites(fixture);
    }
}
