using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Ipc;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed partial class ControllerCoordinatorTests
{
    [Fact]
    public async Task SafetyDisabledStartup_TurnsAnOffFanOnOnceAndConfirmsReadback()
    {
        var marker = new FakeSafetyProtectionStore
        {
            Current = new SafetyProtectionIntent(1, true, DateTimeOffset.UtcNow, "S-1-5-21-test"),
        };
        var configuration = ControllerConfiguration.CreateDefault() with
        {
            Commissioned = true,
            Mode = ControlMode.Manual,
            ManualSpeedPercent = 41,
        };
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            initialConfiguration: configuration,
            suppliedSafetyProtectionStore: marker);
        fixture.Device.SetState(41, poweredOn: false);

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal([ (byte)41 ], fixture.Device.ManualSoftwareControlTargets);
        Assert.True(fixture.Coordinator.GetStatus().SafetyProtection?.Disabled);
        Assert.Equal("SafetyProtectionDisabled", fixture.Coordinator.GetStatus().ServiceState);

        var writes = fixture.Device.Writes.Count;
        var selections = fixture.Device.ManualSoftwareControlSelections;
        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(writes, fixture.Device.Writes.Count);
        Assert.Equal(selections, fixture.Device.ManualSoftwareControlSelections);
    }

    [Theory]
    [InlineData(true, (int)ServiceStopReason.ServiceStop)]
    [InlineData(false, (int)ServiceStopReason.SystemShutdown)]
    public async Task SafetyLatchStopsHotOrMissingTelemetryWithoutChangingTargetAndSurvivesRestart(bool hot, int stop)
    {
        var marker = new FakeSafetyProtectionStore();
        var config = ControllerConfiguration.CreateDefault() with
            { Commissioned = true, Mode = ControlMode.Manual, ManualSpeedPercent = 59 };
        await using (var fixture = await CoordinatorFixture.CreateAsync(true, initialConfiguration: config,
            suppliedSafetyProtectionStore: marker))
        {
            await fixture.ReachSettledControlAsync();
            fixture.Thermal.CpuTemperature = hot ? 100 : null;
            await fixture.Coordinator.TickAsync(CancellationToken.None);
            Assert.Equal((byte)100, fixture.Device.CurrentPercent);
            var counts = fixture.CaptureHardwareCounts();
            var result = await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None);
            Assert.True(result.SafetyProtection?.Disabled);
            Assert.True(result.SafetyProtection?.Persisted);
            Assert.True(result.SafetyProtection?.AutomaticOutputStopped);
            Assert.Equal(counts, fixture.CaptureHardwareCounts());
            Assert.Equal(ControlMode.Manual, result.Fan.Mode);
            Assert.Equal((byte)59, result.Fan.ConfiguredManualPercent);
            await fixture.Coordinator.TickAsync(CancellationToken.None);
            await fixture.Coordinator.BestEffortWatchdogFailSafeAsync();
            await fixture.Coordinator.FinalizeHostStopAsync((ServiceStopReason)stop);
            Assert.Equal(counts.Write, fixture.Device.Writes.Count);
            Assert.Equal(counts.Off, fixture.Device.ShutdownOffWrites);
            Assert.Equal(0, fixture.Device.LightingWrites);
            Assert.Equal(0, fixture.Device.SoftwareControlSelections);
        }
        await using var restarted = await CoordinatorFixture.CreateAsync(true, initialConfiguration: config,
            suppliedSafetyProtectionStore: marker);
        restarted.Thermal.CpuTemperature = null;
        await restarted.Coordinator.TickAsync(CancellationToken.None);
        await restarted.Coordinator.BestEffortWatchdogFailSafeAsync();
        await restarted.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);
        Assert.True(restarted.Coordinator.GetStatus().SafetyProtection?.Disabled);
        Assert.Equal(1, restarted.Locator.OpenCalls);
        Assert.Equal(1, restarted.Device.ReadCalls);
        Assert.Empty(restarted.Device.Writes);
        Assert.Equal(0, restarted.Device.ShutdownOffWrites);
        Assert.Equal(0, restarted.Device.LightingWrites);
        Assert.Equal(0, restarted.Device.SoftwareControlSelections);
    }

    [Fact]
    public async Task SafetyLatchBypassesConfigAuditBarrierAndResumeNeverDeletesTheOriginalBarrier()
    {
        var store = new FaultInjectingConfigurationStore();
        await using var fixture = await CoordinatorFixture.CreateAsync(true, suppliedStore: store);
        await fixture.ReachSettledControlAsync();
        store.FailLoads = true;
        await Assert.ThrowsAsync<ControllerOperationException>(() => fixture.Coordinator.SetManualSpeedAsync(59, ProfilePeer, CancellationToken.None));
        Assert.True(fixture.Coordinator.GetStatus().AuditMutation?.Blocked);
        var counts = fixture.CaptureHardwareCounts();
        var disabled = await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None);
        Assert.True(disabled.SafetyProtection?.Disabled);
        Assert.True(disabled.SafetyProtection?.Persisted);
        await Assert.ThrowsAsync<ControllerOperationException>(() => fixture.Coordinator.SetManualSpeedAsync(59, ProfilePeer, CancellationToken.None));
        var resumed = await fixture.Coordinator.ResumeSafetyProtectionAsync(true, ProfilePeer, CancellationToken.None);
        Assert.False(resumed.SafetyProtection?.Disabled);
        Assert.True(resumed.AuditMutation?.Blocked);
        await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        await fixture.Coordinator.BestEffortWatchdogFailSafeAsync();
        Assert.Equal(counts, fixture.CaptureHardwareCounts());
        store.FailLoads = false;
    }

    [Theory]
    [InlineData((int)ControlMode.Auto)]
    [InlineData((int)ControlMode.Manual)]
    public async Task SafetyLatchAllowsOneExplicitSpeedAtHighTemperatureAndModeChangesNeverClearIt(int mode)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true, initialConfiguration:
            ControllerConfiguration.CreateDefault() with { Commissioned = true, Mode = (ControlMode)mode });
        await fixture.ReachSettledControlAsync();
        await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None);
        fixture.Thermal.CpuTemperature = 100;
        var count = fixture.Device.Writes.Count;
        await fixture.Coordinator.SetManualSpeedAsync(59, ProfilePeer, CancellationToken.None);
        Assert.Equal(count + 1, fixture.Device.Writes.Count);
        Assert.Equal((byte)59, fixture.Device.CurrentPercent);
        Assert.Equal(ControlMode.Manual, fixture.Coordinator.GetStatus().Fan.Mode);
        Assert.True(fixture.Coordinator.GetStatus().SafetyProtection?.Disabled);
        await fixture.Coordinator.SetModeAsync(ControlMode.Auto, ProfilePeer, CancellationToken.None);
        for (var i = 0; i < 12; i++) await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(count + 1, fixture.Device.Writes.Count);
        Assert.Equal((byte)59, fixture.Device.CurrentPercent);
        Assert.True(fixture.Coordinator.GetStatus().SafetyProtection?.Disabled);
    }

    [Fact]
    public async Task SafetyLatchWinsWhileTickHasBlockedReadAndDoesNotWaitForCoordinatorGate()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.ReachSettledControlAsync();
        using var readGate = new ManualResetEventSlim(false);
        fixture.Device.ReadGate = readGate;
        fixture.Device.IgnoreReadCancellation = true;
        fixture.Thermal.CpuTemperature = 100;
        var writes = fixture.Device.Writes.Count;
        var tick = fixture.Coordinator.TickAsync(CancellationToken.None);
        try
        {
            await fixture.Device.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var result = await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(result.SafetyProtection?.AutomaticOutputStopped);
            Assert.True(result.SafetyProtection?.HasInFlightOperation);
        }
        finally { readGate.Set(); }
        await tick;
        Assert.Equal(writes, fixture.Device.Writes.Count);
        Assert.False(fixture.Coordinator.IsHardwarePoisoned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SafetyLatchReportsDurabilityByReadbackEvenWhenSaveThrows(bool published)
    {
        var marker = new FakeSafetyProtectionStore { FailWrites = true, PublishBeforeFailure = published };
        await using var fixture = await CoordinatorFixture.CreateAsync(true, suppliedSafetyProtectionStore: marker);
        var result = await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, new CancellationToken(true));
        Assert.True(result.SafetyProtection?.Disabled);
        Assert.Equal(published, result.SafetyProtection?.Persisted);
        Assert.Equal(published, result.SafetyProtection?.PersistenceError is null);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task SafetyLatchResumeRequiresConfirmationAndVerifiedSaveThenRestartsNormalWarmup()
    {
        var marker = new FakeSafetyProtectionStore();
        await using var fixture = await CoordinatorFixture.CreateAsync(true, initialConfiguration:
            ControllerConfiguration.CreateDefault() with { Commissioned = true, Mode = ControlMode.Manual, ManualSpeedPercent = 59 },
            suppliedSafetyProtectionStore: marker);
        await fixture.ReachSettledControlAsync();
        await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None);
        await Assert.ThrowsAsync<ControllerOperationException>(() => fixture.Coordinator.ResumeSafetyProtectionAsync(false, ProfilePeer, CancellationToken.None));
        marker.FailReadback = true;
        var failed = await fixture.Coordinator.ResumeSafetyProtectionAsync(true, ProfilePeer, CancellationToken.None);
        Assert.True(failed.SafetyProtection?.Disabled);
        Assert.False(failed.SafetyProtection?.Persisted);
        Assert.NotNull(failed.SafetyProtection?.PersistenceError);
        marker.FailReadback = false;
        var result = await fixture.Coordinator.ResumeSafetyProtectionAsync(true, ProfilePeer, CancellationToken.None);
        Assert.False(result.SafetyProtection?.Disabled);
        Assert.True(result.SafetyProtection?.Persisted);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        Assert.True(fixture.Coordinator.GetStatus().FailSafe.Active);
    }

    [Fact]
    public async Task SafetyLatchUnreadableMarkerStopsStartupAndPreservesPendingSafetyJournal()
    {
        var marker = new FakeSafetyProtectionStore { FailReadback = true };
        await using var fixture = await CoordinatorFixture.CreateAsync(true, suppliedSafetyProtectionStore: marker,
            rawSafetyOperationAuditJournalJson: "invalid journal must be preserved");
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.True(fixture.Coordinator.GetStatus().SafetyProtection?.Disabled);
        Assert.False(fixture.Coordinator.GetStatus().SafetyProtection?.Persisted);
        Assert.True(fixture.Coordinator.GetStatus().SafetyOperationAudit?.Blocked);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal("invalid journal must be preserved", await File.ReadAllTextAsync(fixture.Paths.SafetyOperationAuditPendingPath));
    }

    [Fact]
    public async Task SafetyLatchNewDisableSupersedesAnOlderResumeWaitingForTheControllerGate()
    {
        var marker = new FakeSafetyProtectionStore();
        await using var fixture = await CoordinatorFixture.CreateAsync(true, suppliedSafetyProtectionStore: marker);
        await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None);
        using var thermalGate = new ManualResetEventSlim(false);
        fixture.Thermal.ReadGate = thermalGate;
        var tick = fixture.Coordinator.TickAsync(CancellationToken.None);
        try
        {
            await fixture.Thermal.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var resume = fixture.Coordinator.ResumeSafetyProtectionAsync(true, ProfilePeer, CancellationToken.None);
            await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None);
            thermalGate.Set();
            await tick;
            await Assert.ThrowsAsync<ControllerOperationException>(() => resume);
            Assert.True(fixture.Coordinator.GetStatus().SafetyProtection?.Disabled);
            Assert.True(marker.Current?.Disabled);
            Assert.Empty(fixture.Device.Writes);
        }
        finally { thermalGate.Set(); }
    }

    [Fact]
    public async Task SafetyLatchCancelsAQueuedOpenWithoutPoisoningOrSendingAnyCommand()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        var blockerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockerRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? blocker = null;
        fixture.Locator.BeforeEnumerate = () => blocker = fixture.HidDispatcher.InvokeAsync(async token =>
        {
            blockerStarted.TrySetResult(true);
            return await blockerRelease.Task.WaitAsync(token);
        }, TimeSpan.FromSeconds(2), CancellationToken.None);
        var tick = fixture.Coordinator.TickAsync(CancellationToken.None);
        try
        {
            await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await Task.Delay(20);
            await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None);
            blockerRelease.TrySetResult(true);
            await tick;
            Assert.False(fixture.Coordinator.IsHardwarePoisoned);
            Assert.Equal(0, fixture.Locator.OpenCalls);
            Assert.Empty(fixture.Device.Writes);
        }
        finally
        {
            blockerRelease.TrySetResult(true);
            if (blocker is not null) await blocker;
        }
    }

    [Fact]
    public async Task SafetyLatchDisableThenResumeCannotRevivePendingProfileLightingBetweenTicks()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.ReachSettledControlAsync();
        var pending = fixture.Coordinator.ApplyProfileAsync(new(Profile()), Guid.NewGuid().ToString("D"),
            ProfilePeer, CancellationToken.None);
        await WaitForProfileSavedAsync(fixture);
        await fixture.Coordinator.DisableSafetyProtectionAsync(ProfilePeer, CancellationToken.None);
        await fixture.Coordinator.ResumeSafetyProtectionAsync(true, ProfilePeer, CancellationToken.None);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(V12UltraController.Core.Status.ProfileApplyOutcome.Interrupted, result.Application.Outcome);
        for (var i = 0; i < 12; i++) await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(0, fixture.Device.LightingWrites);
    }

    private sealed class FakeSafetyProtectionStore : ISafetyProtectionStore
    {
        public SafetyProtectionIntent? Current { get; set; }
        public bool FailWrites { get; set; }
        public bool PublishBeforeFailure { get; set; }
        public bool FailReadback { get; set; }
        public Task<SafetyProtectionIntent?> LoadAsync(CancellationToken cancellationToken) =>
            FailReadback ? Task.FromException<SafetyProtectionIntent?>(new IOException("simulated read failure")) : Task.FromResult(Current);
        public Task SaveAsync(SafetyProtectionIntent intent, CancellationToken cancellationToken)
        {
            if (!FailWrites || PublishBeforeFailure) Current = intent;
            return FailWrites ? Task.FromException(new IOException("simulated write failure")) : Task.CompletedTask;
        }
    }
}
