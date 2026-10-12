using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed partial class ControllerCoordinatorTests
{
    private static readonly PipePeerIdentity ProfilePeer = new("S-1-5-21-111-222-333-1001", "Settings");
    private static ControlProfileSnapshot Profile(bool lighting = true, byte percent = 60) =>
        new(Guid.NewGuid().ToString("D"), "测试组合方案", ControlMode.Manual, percent, FanCurve.RemoteHostSafe,
            lighting ? new LightingSettings(true, 1, 2, 220, 3) : null);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProfileApply_WaitsForTenNormalSamplesAndConfirmsOptionalLighting(bool withLighting)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.ReachSettledControlAsync();
        var profile = Profile(withLighting);
        var pending = fixture.Coordinator.ApplyProfileAsync(new(profile), Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None);
        await WaitForProfileSavedAsync(fixture);
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, fixture.Device.LightingWrites);
        Assert.Equal(60, (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).ManualSpeedPercent);
        for (var sample = 0; sample < 9; sample++) await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, fixture.Device.LightingWrites);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ProfileApplyOutcome.Completed, result.Application.Outcome);
        Assert.Equal(ProfileConfigurationState.Saved, result.Application.Configuration);
        Assert.Equal(ProfileFanState.Confirmed, result.Application.Fan);
        Assert.Equal(withLighting ? ProfileLightingState.Confirmed : ProfileLightingState.NotRequested, result.Application.Lighting);
        Assert.Equal(withLighting ? 1 : 0, fixture.Device.LightingWrites);
        Assert.Equal((byte)60, fixture.Device.CurrentPercent);
        Assert.True(result.Status.Software?.ProfileApplySupported);
        Assert.Equal(result.Application, result.Status.ProfileApplication);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(withLighting ? 1 : 0, fixture.Device.LightingWrites);
    }

    [Fact]
    public async Task ProfileApply_LightingReadbackFailurePreservesSavedFanAndReportsCoolingRecovery()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.ReachSettledControlAsync();
        fixture.Device.IgnoreLightingWrites = true;
        var pending = fixture.Coordinator.ApplyProfileAsync(new(Profile()), Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None);
        await WaitForProfileSavedAsync(fixture);
        for (var sample = 0; sample < 10; sample++) await fixture.Coordinator.TickAsync(CancellationToken.None);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ProfileApplyOutcome.Partial, result.Application.Outcome);
        Assert.Equal(ProfileConfigurationState.Saved, result.Application.Configuration);
        Assert.Equal(ProfileLightingState.Unknown, result.Application.Lighting);
        Assert.Equal(ProfileFanState.SafetyOverride, result.Application.Fan);
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        Assert.Equal((byte)60, (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).ManualSpeedPercent);
        Assert.Equal(1, fixture.Device.LightingWrites);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Device.LightingWrites);
    }

    [Theory]
    [InlineData("speed")]
    [InlineData("mode")]
    [InlineData("curve")]
    [InlineData("power")]
    public async Task ProfileApply_LaterExplicitControlSupersedesPendingLighting(string operation)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.ReachSettledControlAsync();
        var pending = fixture.Coordinator.ApplyProfileAsync(new(Profile()), Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None);
        await WaitForProfileSavedAsync(fixture);
        switch (operation)
        {
            case "speed": await fixture.Coordinator.SetManualSpeedAsync(71, ProfilePeer, CancellationToken.None); break;
            case "mode": await fixture.Coordinator.SetModeAsync(ControlMode.Auto, ProfilePeer, CancellationToken.None); break;
            case "curve": await fixture.Coordinator.SetCurveAsync(FanCurve.RemoteHostSafe, ProfilePeer, CancellationToken.None); break;
            case "power": await fixture.Coordinator.SetManualPowerAsync(true, true, ProfilePeer, CancellationToken.None); break;
        }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ProfileApplyOutcome.Superseded, result.Application.Outcome);
        Assert.Equal(ProfileLightingState.NotAttempted, result.Application.Lighting);
        for (var sample = 0; sample < 11; sample++) await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(0, fixture.Device.LightingWrites);
    }

    [Fact]
    public async Task ProfileApply_SecondProfilePreventsOldLightingFromBeingReplayed()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.ReachSettledControlAsync();
        var first = fixture.Coordinator.ApplyProfileAsync(new(Profile()), Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None);
        await WaitForProfileSavedAsync(fixture);
        var secondProfile = Profile(false, 73);
        var second = fixture.Coordinator.ApplyProfileAsync(new(secondProfile), Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None);
        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ProfileApplyOutcome.Superseded, firstResult.Application.Outcome);
        await WaitForProfileSavedAsync(fixture, secondProfile.Id);
        for (var sample = 0; sample < 10; sample++) await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(ProfileApplyOutcome.Completed, (await second.WaitAsync(TimeSpan.FromSeconds(3))).Application.Outcome);
        Assert.Equal(0, fixture.Device.LightingWrites);
        Assert.Equal((byte)73, fixture.Device.CurrentPercent);
    }

    [Fact]
    public async Task ProfileApply_CancellationKeepsConfigurationAndNeverReplaysLighting()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.ReachSettledControlAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Coordinator.ApplyProfileAsync(new(Profile()), Guid.NewGuid().ToString("D"), ProfilePeer, cancellation.Token);
        await WaitForProfileSavedAsync(fixture);
        await cancellation.CancelAsync();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ProfileApplyOutcome.Interrupted, result.Application.Outcome);
        Assert.Equal(ProfileConfigurationState.Saved, result.Application.Configuration);
        Assert.Equal(ProfileLightingState.NotAttempted, result.Application.Lighting);
        for (var sample = 0; sample < 11; sample++) await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(0, fixture.Device.LightingWrites);
        Assert.Equal((byte)60, fixture.Device.CurrentPercent);
    }

    [Fact]
    public async Task ProfileApply_DeadlineStopsTheSecondPhaseWithoutRetry()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        await using var fixture = await CoordinatorFixture.CreateAsync(true, suppliedTimeProvider: time);
        await ReachSettledControlAsync(fixture, time);
        var pending = fixture.Coordinator.ApplyProfileAsync(new(Profile()), Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None);
        await WaitForProfileSavedAsync(fixture);
        time.Advance(TimeSpan.FromSeconds(21));
        fixture.Thermal.CapturedAt = time.GetUtcNow();
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(ProfileApplyOutcome.Interrupted, (await pending.WaitAsync(TimeSpan.FromSeconds(3))).Application.Outcome);
        Assert.Equal(0, fixture.Device.LightingWrites);
    }

    [Theory]
    [InlineData(ControlMode.PureManual)]
    [InlineData(ControlMode.Released)]
    public async Task ProfileApply_PassiveModesRequireExplicitConfirmationThenUseNormalWarmup(ControlMode mode)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ControllerConfiguration.CreateDefault() with { Commissioned = true, Mode = mode });
        var profile = Profile();
        var counts = fixture.CaptureHardwareCounts();
        var failure = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.ApplyProfileAsync(new(profile), Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.InvalidArgument, failure.Code);
        Assert.Equal(counts, fixture.CaptureHardwareCounts());
        var pending = fixture.Coordinator.ApplyProfileAsync(new(profile, true), Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None);
        await WaitForProfileSavedAsync(fixture);
        Assert.Equal(counts, fixture.CaptureHardwareCounts());
        for (var sample = 0; sample < 10; sample++) await fixture.Coordinator.TickAsync(CancellationToken.None);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ProfileApplyOutcome.Completed, result.Application.Outcome);
        Assert.Equal(ControlMode.Manual, result.Status.Fan.Mode);
        Assert.Equal(1, fixture.Device.LightingWrites);
        Assert.Equal(0, fixture.TaskManager.DisableCalls);
    }

    [Fact]
    public async Task ProfileApply_ValidationAndUncommissionedStateNeverWriteConfigurationOrHardware()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(false);
        var original = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        var counts = fixture.CaptureHardwareCounts();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Coordinator.ApplyProfileAsync(
            new(Profile() with { Lighting = new LightingSettings(true, 99, 0, 100, 0) }), Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None));
        await Assert.ThrowsAsync<ControllerOperationException>(() => fixture.Coordinator.ApplyProfileAsync(
            new(Profile(), true), Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None));
        Assert.Equal(counts, fixture.CaptureHardwareCounts());
        Assert.Equal(original.Mode, (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).Mode);
        Assert.Null(fixture.Coordinator.GetStatus().ProfileApplication);
    }

    [Fact]
    public async Task ProfileApply_UnknownConfigurationReadbackRetainsBarrierAndSkipsHardware()
    {
        var store = new FaultInjectingConfigurationStore();
        await using var fixture = await CoordinatorFixture.CreateAsync(true, suppliedStore: store);
        await fixture.ReachSettledControlAsync();
        var counts = fixture.CaptureHardwareCounts();
        store.FailLoads = true;
        var result = await fixture.Coordinator.ApplyProfileAsync(new(Profile()), Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None);
        Assert.Equal(ProfileApplyOutcome.Partial, result.Application.Outcome);
        Assert.Equal(ProfileConfigurationState.Unknown, result.Application.Configuration);
        Assert.Equal(ProfileLightingState.NotAttempted, result.Application.Lighting);
        Assert.True(result.Status.AuditMutation?.Blocked);
        Assert.Equal(counts, fixture.CaptureHardwareCounts());
        store.FailLoads = false;
    }

    private static async Task WaitForProfileSavedAsync(CoordinatorFixture fixture, string? profileId = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (fixture.Coordinator.GetStatus().ProfileApplication is not
               { Configuration: ProfileConfigurationState.Saved, Outcome: ProfileApplyOutcome.Applying } application ||
               profileId is not null && application.ProfileId != profileId)
            await Task.Delay(5, deadline.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfileApply_SaveThrowsUsesExactReadbackRatherThanTheException(bool publishBeforeThrow)
    {
        var store = new ProfileSaveFailureStore();
        await using var fixture = await CoordinatorFixture.CreateAsync(true, suppliedStore: store);
        await fixture.ReachSettledControlAsync();
        store.FailSave = true;
        store.PublishBeforeThrow = publishBeforeThrow;
        var pending = fixture.Coordinator.ApplyProfileAsync(new(Profile(false)), Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None);
        if (publishBeforeThrow)
        {
            await WaitForProfileSavedAsync(fixture);
            for (var sample = 0; sample < 10; sample++) await fixture.Coordinator.TickAsync(CancellationToken.None);
        }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(publishBeforeThrow ? ProfileApplyOutcome.Completed : ProfileApplyOutcome.Rejected, result.Application.Outcome);
        Assert.Equal(publishBeforeThrow ? ProfileConfigurationState.Saved : ProfileConfigurationState.Unchanged, result.Application.Configuration);
        Assert.Equal(publishBeforeThrow ? (byte)60 : (byte)100, store.Current.ManualSpeedPercent);
        Assert.False(result.Status.AuditMutation?.Blocked);
        Assert.Equal(0, fixture.Device.LightingWrites);
    }

    [Fact]
    public async Task ProfileApply_TemperatureProtectionEndsOptionalLightingWithoutReplay()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.ReachSettledControlAsync();
        var pending = fixture.Coordinator.ApplyProfileAsync(new(Profile()), Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None);
        await WaitForProfileSavedAsync(fixture);
        fixture.Thermal.CpuTemperature = 95;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ProfileApplyOutcome.Partial, result.Application.Outcome);
        Assert.Equal(ProfileFanState.SafetyOverride, result.Application.Fan);
        Assert.Equal(ProfileLightingState.NotAttempted, result.Application.Lighting);
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        Assert.Equal(0, fixture.Device.LightingWrites);
    }

    private sealed class ProfileSaveFailureStore : IConfigurationStore
    {
        public ControllerConfiguration Current { get; private set; } = ControllerConfiguration.CreateDefault();
        public bool FailSave { get; set; }
        public bool PublishBeforeThrow { get; set; }
        public Task<ControllerConfiguration> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Current);
        public Task SaveAsync(ControllerConfiguration configuration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FailSave || PublishBeforeThrow) Current = configuration;
            return FailSave ? Task.FromException(new IOException("injected save failure")) : Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ProfileApply_ServiceExitInterruptsPendingLightingAndRestartDoesNotReplayIt()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.ReachSettledControlAsync();
        var pending = fixture.Coordinator.ApplyProfileAsync(new(Profile()), Guid.NewGuid().ToString("D"), ProfilePeer, CancellationToken.None);
        await WaitForProfileSavedAsync(fixture);
        var saved = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        await fixture.Coordinator.DisposeAsync();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ProfileApplyOutcome.Interrupted, result.Application.Outcome);
        Assert.Equal(0, fixture.Device.LightingWrites);
        await using var restarted = await CoordinatorFixture.CreateAsync(true, initialConfiguration: saved);
        await restarted.ReachSettledControlAsync();
        Assert.Null(restarted.Coordinator.GetStatus().ProfileApplication);
        Assert.Equal(0, restarted.Device.LightingWrites);
        Assert.Equal((byte)60, restarted.Device.CurrentPercent);
    }
}
