using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed partial class ControllerCoordinatorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitProfileAppliesManualFanAndOptionalRgbOnceFromColdDisabledStartup(bool lighting)
    {
        var marker = new FakeSafetyProtectionStore();
        await using var fixture = await CoordinatorFixture.CreateAsync(true, suppliedSafetyProtectionStore: marker);
        await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None);
        var savedIntent = marker.Current;
        fixture.Thermal.CpuTemperature = null;
        var profile = Profile(lighting, 59);
        var result = await fixture.Coordinator.ApplyProfileAsync(new(profile), Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None);
        Assert.Equal(ProfileApplyOutcome.Completed, result.Application.Outcome);
        Assert.Equal(ProfileConfigurationState.Saved, result.Application.Configuration);
        Assert.Equal(ProfileFanState.Confirmed, result.Application.Fan);
        Assert.Equal(lighting ? ProfileLightingState.Confirmed : ProfileLightingState.NotRequested, result.Application.Lighting);
        Assert.Equal(new byte[] { 59 }, fixture.Device.Writes);
        Assert.Equal(lighting ? 1 : 0, fixture.Device.LightingWrites);
        Assert.Equal(1, fixture.Locator.OpenCalls);
        Assert.Equal(ControlMode.Manual, result.Status.Fan.Mode);
        Assert.True(result.Status.Software?.ManualActionsWhileProtectionDisabledSupported);
        Assert.True(result.Status.SafetyProtection?.Disabled);
        Assert.Equal(savedIntent, marker.Current);
        var configuration = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal((byte)59, configuration.ManualSpeedPercent);
        for (var i = 0; i < 12; i++) await fixture.Coordinator.TickAsync(CancellationToken.None);
        await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.ServiceStop);
        Assert.Equal(new byte[] { 59 }, fixture.Device.Writes);
        Assert.Equal(lighting ? 1 : 0, fixture.Device.LightingWrites);
        Assert.False(File.Exists(fixture.Paths.SafetyOperationAuditPendingPath));
    }

    [Fact]
    public async Task ExplicitProfileLightingMismatchIsPartialAndNeverAddsOneHundredRecovery()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None);
        fixture.Device.IgnoreLightingWrites = true;
        var result = await fixture.Coordinator.ApplyProfileAsync(new(Profile(percent: 59)), Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None);
        Assert.Equal(ProfileApplyOutcome.Partial, result.Application.Outcome);
        Assert.Equal(ProfileConfigurationState.Saved, result.Application.Configuration);
        Assert.Equal(ProfileFanState.Confirmed, result.Application.Fan);
        Assert.Equal(ProfileLightingState.Unknown, result.Application.Lighting);
        Assert.Equal(new byte[] { 59 }, fixture.Device.Writes);
        Assert.Equal(1, fixture.Device.LightingWrites);
        Assert.True(result.Status.SafetyProtection?.Disabled);
        Assert.False(fixture.Coordinator.IsHardwarePoisoned);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Device.LightingWrites);
    }

    [Fact]
    public async Task ExplicitProfilePreservesLaterPhysicalSpeedAndReportsPartialInsteadOfComplete()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None);
        // Fan uses its first two GETs; the RGB transaction's fresh third GET sees the knob.
        fixture.Device.BeforeRead = () => { if (fixture.Device.ReadCalls == 2) fixture.Device.SetState(44, poweredOn: true); };
        var result = await fixture.Coordinator.ApplyProfileAsync(new(Profile(percent: 59)), Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None);
        Assert.Equal(ProfileApplyOutcome.Partial, result.Application.Outcome);
        Assert.Equal(ProfileFanState.Unknown, result.Application.Fan);
        Assert.Equal(ProfileLightingState.Confirmed, result.Application.Lighting);
        Assert.Equal((byte)44, result.Status.Fan.ReportedControlPercent);
        Assert.Equal((byte)59, result.Status.Fan.ConfiguredManualPercent);
        Assert.Equal(new byte[] { 59 }, fixture.Device.Writes);
        Assert.Equal(1, fixture.Device.LightingWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitProfilePhysicalInputDuringFanReadbackIsPartialAndNeverStartsOptionalLighting(bool lighting)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            suppliedSafetyProtectionStore: ManualPriorityProtection());
        fixture.Device.SetState(50, poweredOn: true);
        fixture.Device.BeforeRead = () =>
        {
            if (fixture.Device.ReadCalls == 1) fixture.Device.SetState(47, poweredOn: true);
        };

        var result = await fixture.Coordinator.ApplyProfileAsync(new(Profile(lighting, 59)),
            Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None);

        Assert.Equal(ProfileApplyOutcome.Partial, result.Application.Outcome);
        Assert.Equal(ProfileConfigurationState.Saved, result.Application.Configuration);
        Assert.Equal(ProfileFanState.Unknown, result.Application.Fan);
        Assert.Equal(lighting ? ProfileLightingState.NotAttempted : ProfileLightingState.NotRequested,
            result.Application.Lighting);
        Assert.Equal((byte)47, result.Status.Fan.ReportedControlPercent);
        Assert.Equal((byte)59, result.Status.Fan.ConfiguredManualPercent);
        Assert.Equal((byte)59, (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).ManualSpeedPercent);
        Assert.Equal(new byte[] { 59 }, fixture.Device.ManualSoftwareControlTargets);
        Assert.Equal(new byte[] { 59 }, fixture.Device.Writes);
        AssertNoManualPriorityAuxiliaryWrites(fixture);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(new byte[] { 59 }, fixture.Device.Writes);
        Assert.Equal(0, fixture.Device.LightingWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitProfileNewDisableOrClientCancellationStopsLaterLightingAndNewRequestsStillWork(bool clientCancellation)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None);
        using var readGate = new ManualResetEventSlim(false);
        using var lifetime = new CancellationTokenSource();
        fixture.Device.BeforeRead = () => { if (fixture.Device.ReadCalls == 2) fixture.Device.ReadGate = readGate; };
        var pending = fixture.Coordinator.ApplyProfileAsync(new(Profile(percent: 59)), Guid.NewGuid().ToString("D"), ProfilePeer, lifetime.Token);
        try
        {
            await fixture.Device.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            if (clientCancellation) await lifetime.CancelAsync();
            else await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None);
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(ProfileApplyOutcome.Interrupted, result.Application.Outcome);
            Assert.Equal(ProfileConfigurationState.Saved, result.Application.Configuration);
            Assert.Equal(ProfileLightingState.NotAttempted, result.Application.Lighting);
            Assert.Equal(new byte[] { 59 }, fixture.Device.Writes);
            Assert.Equal(0, fixture.Device.LightingWrites);
            Assert.False(fixture.Coordinator.IsHardwarePoisoned);
            Assert.True(result.Status.SafetyProtection?.Disabled);
            var saved = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
            Assert.Equal(saved.Mode, result.Status.Fan.Mode);
            Assert.Equal(saved.ManualSpeedPercent, result.Status.Fan.ConfiguredManualPercent);
        }
        finally { readGate.Set(); fixture.Device.ReadGate = null; fixture.Device.BeforeRead = null; }
        var next = await fixture.Coordinator.ApplyProfileAsync(new(Profile(percent: 62)), Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None);
        Assert.Equal(ProfileApplyOutcome.Completed, next.Application.Outcome);
        Assert.Equal(new byte[] { 59, 62 }, fixture.Device.Writes);
        Assert.Equal(1, fixture.Device.LightingWrites);
    }

    [Fact]
    public async Task ExplicitProfileSavedThenCancelledBeforeFanImmediatelyReportsSavedConfiguration()
    {
        using var lifetime = new CancellationTokenSource();
        var store = new CancelAfterExplicitProfileSaveStore(lifetime);
        await using var fixture = await CoordinatorFixture.CreateAsync(true, suppliedStore: store);
        await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None);
        store.CancelAfterSave = true;
        var result = await fixture.Coordinator.ApplyProfileAsync(new(Profile(percent: 59)),
            Guid.NewGuid().ToString("D"), ProfilePeer, lifetime.Token);
        Assert.Equal(ProfileApplyOutcome.Interrupted, result.Application.Outcome);
        Assert.Equal(ProfileConfigurationState.Saved, result.Application.Configuration);
        Assert.Equal(ProfileLightingState.NotAttempted, result.Application.Lighting);
        Assert.Equal(ControlMode.Manual, store.Current.Mode);
        Assert.Equal(store.Current.Mode, result.Status.Fan.Mode);
        Assert.Equal((byte)59, store.Current.ManualSpeedPercent);
        Assert.Equal(store.Current.ManualSpeedPercent, result.Status.Fan.ConfiguredManualPercent);
        Assert.True(result.Status.SafetyProtection?.Disabled);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Equal(0, fixture.Device.LightingWrites);
    }

    private sealed class CancelAfterExplicitProfileSaveStore(CancellationTokenSource lifetime) : IConfigurationStore
    {
        public ControllerConfiguration Current { get; private set; } = ControllerConfiguration.CreateDefault();
        public bool CancelAfterSave { get; set; }
        public Task<ControllerConfiguration> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Current);
        public Task SaveAsync(ControllerConfiguration configuration, CancellationToken cancellationToken)
        {
            Current = configuration;
            if (CancelAfterSave) lifetime.Cancel();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ExplicitProfileCancellationWaitsForAlreadyIssuedFakeNativeBeforeReleasingItsTransaction()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None);
        using var writeGate = new ManualResetEventSlim(false);
        fixture.Device.WriteGate = writeGate;
        fixture.Device.IgnoreWriteCancellation = true;
        fixture.Device.GatedWritePercent = 59;
        using var lifetime = new CancellationTokenSource();
        var pending = fixture.Coordinator.ApplyProfileAsync(new(Profile(percent: 59)), Guid.NewGuid().ToString("D"), ProfilePeer, lifetime.Token);
        try
        {
            await fixture.Device.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await lifetime.CancelAsync();
            await Task.Delay(25);
            Assert.False(pending.IsCompleted);
            Assert.True(fixture.Coordinator.GetStatus().SafetyProtection?.HasInFlightOperation);
            writeGate.Set();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(ProfileApplyOutcome.Interrupted, result.Application.Outcome);
            Assert.Equal(0, fixture.Device.LightingWrites);
            Assert.Equal(new byte[] { 59 }, fixture.Device.Writes);
            Assert.False(fixture.Coordinator.IsHardwarePoisoned);
        }
        finally { writeGate.Set(); }
    }

    [Theory]
    [InlineData(ControlMode.Auto)]
    [InlineData(ControlMode.Released)]
    public async Task ExplicitProfileDoesNotResumeAutomaticProfileOrReleasedOwnership(ControlMode condition)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true, initialConfiguration:
            ControllerConfiguration.CreateDefault() with { Commissioned = true, Mode = condition });
        await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None);
        var profile = condition == ControlMode.Auto ? Profile() with { Mode = ControlMode.Auto } : Profile();
        await Assert.ThrowsAsync<ControllerOperationException>(() => fixture.Coordinator.ApplyProfileAsync(new(profile, true),
            Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None));
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
        Assert.True(fixture.Coordinator.GetStatus().SafetyProtection?.Disabled);
        Assert.Equal(condition, fixture.Coordinator.GetStatus().Fan.Mode);
    }
}
