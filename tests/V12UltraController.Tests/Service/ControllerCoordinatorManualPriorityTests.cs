using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed partial class ControllerCoordinatorTests
{
    [Fact]
    public async Task DisabledAutoSpeedRequestSelectsManualAndPersistsConfirmedTargetWithoutChangingProtection()
    {
        var protection = ManualPriorityProtection();
        var savedProtection = protection.Current;
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ManualPriorityConfiguration(), suppliedSafetyProtectionStore: protection);

        await fixture.Coordinator.SetManualSpeedAsync(61, ProfilePeer, CancellationToken.None);

        var status = fixture.Coordinator.GetStatus();
        var saved = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal(ControlMode.Manual, status.Fan.Mode);
        Assert.Equal(ControlMode.Manual, saved.Mode);
        Assert.Equal((byte)61, saved.ManualSpeedPercent);
        Assert.Equal((byte)61, status.Fan.ConfiguredManualPercent);
        Assert.Equal((byte)61, status.Fan.ReportedControlPercent);
        Assert.True(status.SafetyProtection?.Disabled);
        Assert.Equal(savedProtection, protection.Current);
        Assert.Equal(new byte[] { 61 }, fixture.Device.Writes);

        fixture.Thermal.CpuTemperature = 100;
        for (var tick = 0; tick < 4; tick++) await fixture.Coordinator.TickAsync(CancellationToken.None);
        fixture.Thermal.CpuTemperature = null;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        await fixture.Coordinator.BestEffortWatchdogFailSafeAsync();
        Assert.Equal(new byte[] { 61 }, fixture.Device.Writes);
        AssertNoManualPriorityAuxiliaryWrites(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedManualReadbackRejectsSuccessWithoutRetryOrOverwritingSavedTarget(bool ignoreSpeedWrite)
    {
        var protection = ManualPriorityProtection();
        var savedProtection = protection.Current;
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ManualPriorityConfiguration(), suppliedSafetyProtectionStore: protection);
        fixture.Device.SetState(44, poweredOn: true);
        if (ignoreSpeedWrite)
        {
            fixture.Device.IgnoreNormalSpeedWrites = true;
            fixture.Device.IgnoreManualSoftwareControl = true;
        }
        else
        {
            var reads = 0;
            fixture.Device.BeforeRead = () =>
            {
                if (++reads == 2) fixture.Device.SetState(44, poweredOn: true);
            };
        }

        var error = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.SetManualSpeedAsync(61, ProfilePeer, CancellationToken.None));

        var status = fixture.Coordinator.GetStatus();
        var saved = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal(PipeErrorCodes.DeviceStateChanged, error.Code);
        Assert.Equal(ControlMode.Auto, status.Fan.Mode);
        Assert.Equal(ControlMode.Auto, saved.Mode);
        Assert.Equal((byte)59, saved.ManualSpeedPercent);
        Assert.Equal((byte)59, status.Fan.ConfiguredManualPercent);
        Assert.Equal((byte)44, status.Fan.ReportedControlPercent);
        Assert.NotNull(status.LastSuccessfulRead);
        Assert.Equal(savedProtection, protection.Current);
        Assert.Equal(new byte[] { 61 }, fixture.Device.Writes);
        Assert.Equal(new byte[] { 61 }, fixture.Device.ManualSoftwareControlTargets);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(new byte[] { 61 }, fixture.Device.Writes);
        AssertNoManualPriorityAuxiliaryWrites(fixture);
    }

    [Fact]
    public async Task LaterManualTargetSupersedesAnOlderRequestBeforeEitherCanWrite()
    {
        var protection = ManualPriorityProtection();
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ManualPriorityConfiguration(), suppliedSafetyProtectionStore: protection);
        using var readGate = new ManualResetEventSlim(false);
        fixture.Device.ReadGate = readGate;
        fixture.Device.IgnoreReadCancellation = true;
        var tick = fixture.Coordinator.TickAsync(CancellationToken.None);
        Task? older = null, newer = null;
        try
        {
            await fixture.Device.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            older = fixture.Coordinator.SetManualSpeedAsync(61, ProfilePeer, CancellationToken.None);
            newer = fixture.Coordinator.SetManualSpeedAsync(72, ProfilePeer, CancellationToken.None);
            Assert.False(older.IsCompleted);
            Assert.False(newer.IsCompleted);
        }
        finally { readGate.Set(); }
        await tick;
        var olderError = await Record.ExceptionAsync(() => older!);
        Assert.True(olderError is null or ControllerOperationException or OperationCanceledException,
            $"Unexpected superseded request failure: {olderError}");
        await newer!;

        var saved = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal(ControlMode.Manual, saved.Mode);
        Assert.Equal((byte)72, saved.ManualSpeedPercent);
        Assert.Equal((byte)72, fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
        Assert.Equal(new byte[] { 72 }, fixture.Device.Writes);
        AssertNoManualPriorityAuxiliaryWrites(fixture);
        Assert.True(fixture.Coordinator.GetStatus().SafetyProtection?.Disabled);
    }

    [Fact]
    public async Task LaterManualTargetPreventsOlderSetAfterItsInitialDeviceRead()
    {
        var protection = ManualPriorityProtection();
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ManualPriorityConfiguration(), suppliedSafetyProtectionStore: protection);
        using var readGate = new ManualResetEventSlim(false);
        fixture.Device.ReadGate = readGate;
        fixture.Device.IgnoreReadCancellation = true;
        var older = fixture.Coordinator.SetManualSpeedAsync(61, ProfilePeer, CancellationToken.None);
        Task? newer = null;
        try
        {
            await fixture.Device.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Empty(fixture.Device.Writes);
            newer = fixture.Coordinator.SetManualSpeedAsync(72, ProfilePeer, CancellationToken.None);
            Assert.False(older.IsCompleted);
            Assert.False(newer.IsCompleted);
        }
        finally { readGate.Set(); }
        var olderError = await Record.ExceptionAsync(() => older);
        Assert.True(olderError is null or ControllerOperationException or OperationCanceledException,
            $"Unexpected superseded command failure: {olderError}");
        await newer!;

        Assert.Equal(new byte[] { 72 }, fixture.Device.Writes);
        var saved = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal(ControlMode.Manual, saved.Mode);
        Assert.Equal((byte)72, saved.ManualSpeedPercent);
        Assert.Equal((byte)72, fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
        AssertNoManualPriorityAuxiliaryWrites(fixture);
        Assert.False(fixture.Coordinator.IsHardwarePoisoned);
    }

    [Fact]
    public async Task NewerSpeedSupersedesPowerDuringInitialReadWithoutRecoveryOrPendingAudit()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        fixture.Thermal.CpuTemperature = 40;
        await fixture.ReachSettledControlAsync();
        var writes = fixture.Device.Writes.Count;
        using var readGate = new ManualResetEventSlim(false);
        var readStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Device.BeforeRead = () => readStarted.TrySetResult(true);
        fixture.Device.ReadGate = readGate;
        fixture.Device.IgnoreReadCancellation = true;
        var older = fixture.Coordinator.SetManualPowerAsync(false, true, ProfilePeer, CancellationToken.None);
        Task? newer = null;
        try
        {
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            newer = fixture.Coordinator.SetManualSpeedAsync(72, ProfilePeer, CancellationToken.None);
            Assert.False(older.IsCompleted);
            Assert.False(newer.IsCompleted);
        }
        finally { readGate.Set(); }
        var error = await Assert.ThrowsAsync<ControllerOperationException>(() => older);
        Assert.Equal(PipeErrorCodes.DeviceStateChanged, error.Code);
        await newer!;

        Assert.Equal(writes, fixture.Device.Writes.Count);
        AssertNoManualPriorityAuxiliaryWrites(fixture);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(new byte[] { 72 }, fixture.Device.Writes.Skip(writes));
        AssertNoManualPriorityAuxiliaryWrites(fixture);
        Assert.False(fixture.Coordinator.GetStatus().SafetyOperationAudit?.Blocked);
        Assert.False(File.Exists(fixture.Paths.SafetyOperationAuditPendingPath));
        Assert.False(fixture.Coordinator.GetStatus().Fan.OffRequested);
        Assert.Equal(ControlMode.Manual, fixture.Coordinator.GetStatus().Fan.Mode);
        Assert.Equal((byte)72, (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).ManualSpeedPercent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PassiveObservationUsesFirstReadAsBaselineThenPersistsPhysicalInputWithoutControlWrites(bool powerChanged)
    {
        var protection = ManualPriorityProtection();
        var savedProtection = protection.Current;
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ManualPriorityConfiguration(), suppliedSafetyProtectionStore: protection);
        fixture.Device.SetState(44, poweredOn: true);

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(1, fixture.Locator.OpenCalls);
        Assert.Equal(1, fixture.Device.ReadCalls);
        Assert.Equal(ControlMode.Auto, fixture.Coordinator.GetStatus().Fan.Mode);
        Assert.Equal((byte)59, (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).ManualSpeedPercent);
        Assert.Empty(fixture.Device.Writes);

        var observedPercent = powerChanged ? (byte)44 : (byte)47;
        fixture.Device.SetState(observedPercent, poweredOn: !powerChanged);
        await fixture.Coordinator.TickAsync(CancellationToken.None);

        var status = fixture.Coordinator.GetStatus();
        var saved = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal(ControlMode.Manual, status.Fan.Mode);
        Assert.Equal(ControlMode.Manual, saved.Mode);
        var savedOnPercent = powerChanged ? (byte)59 : observedPercent;
        Assert.Equal(savedOnPercent, saved.ManualSpeedPercent);
        Assert.Equal(savedOnPercent, status.Fan.ConfiguredManualPercent);
        Assert.Equal(observedPercent, status.Fan.ReportedControlPercent);
        Assert.Equal(!powerChanged, status.Fan.PoweredOn);
        Assert.Equal(powerChanged, status.Fan.OffRequested);
        Assert.Equal(savedProtection, protection.Current);
        fixture.Thermal.CpuTemperature = 100;
        for (var tick = 0; tick < 4; tick++) await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Empty(fixture.Device.Writes);
        AssertNoManualPriorityAuxiliaryWrites(fixture);
    }

    [Fact]
    public async Task ObservedManualTargetSurvivesRestartWithoutAutomaticallyReplayingIt()
    {
        var protection = ManualPriorityProtection();
        var savedProtection = protection.Current;
        ControllerConfiguration saved;
        await using (var original = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ManualPriorityConfiguration(), suppliedSafetyProtectionStore: protection))
        {
            original.Device.SetState(44, poweredOn: true);
            await original.Coordinator.TickAsync(CancellationToken.None);
            original.Device.SetState(47, poweredOn: true);
            await original.Coordinator.TickAsync(CancellationToken.None);
            saved = await original.ConfigurationStore.LoadAsync(CancellationToken.None);
            Assert.Equal(ControlMode.Manual, saved.Mode);
            Assert.Equal((byte)47, saved.ManualSpeedPercent);
            Assert.Empty(original.Device.Writes);
            AssertNoManualPriorityAuxiliaryWrites(original);
        }

        await using var restarted = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: saved, suppliedSafetyProtectionStore: protection);
        restarted.Thermal.CpuTemperature = null;
        await restarted.Coordinator.TickAsync(CancellationToken.None);
        await restarted.Coordinator.BestEffortWatchdogFailSafeAsync();
        await restarted.Coordinator.FinalizeHostStopAsync(ServiceStopReason.ServiceStop);
        Assert.Equal(ControlMode.Manual, restarted.Coordinator.GetStatus().Fan.Mode);
        Assert.Equal((byte)47, restarted.Coordinator.GetStatus().Fan.ConfiguredManualPercent);
        Assert.Equal(savedProtection, protection.Current);
        Assert.Empty(restarted.Device.Writes);
        AssertNoManualPriorityAuxiliaryWrites(restarted);
    }

    [Fact]
    public async Task PassiveObservationRejectsBadPacketWithoutAdoptingTargetOrRecoveringAtFullSpeed()
    {
        var protection = ManualPriorityProtection();
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ManualPriorityConfiguration(), suppliedSafetyProtectionStore: protection);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        fixture.Device.ReadFailures.Enqueue(new V12ProtocolException("synthetic invalid observation checksum"));

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Null(fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
        var saved = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal(ControlMode.Auto, saved.Mode);
        Assert.Equal((byte)59, saved.ManualSpeedPercent);
        Assert.Empty(fixture.Device.Writes);
        AssertNoManualPriorityAuxiliaryWrites(fixture);
        Assert.True(fixture.Coordinator.GetStatus().SafetyProtection?.Disabled);
    }

    [Fact]
    public async Task PassiveObservationTimeoutKeepsOwnershipBarrierAndNeverWritesRecovery()
    {
        var protection = ManualPriorityProtection();
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ManualPriorityConfiguration(), suppliedSafetyProtectionStore: protection);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        using var readGate = new ManualResetEventSlim(false);
        fixture.Device.ReadGate = readGate;
        fixture.Device.IgnoreReadCancellation = true;
        try
        {
            await fixture.Coordinator.TickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(fixture.Coordinator.IsHardwarePoisoned);
            Assert.True(fixture.WriterLease.IsHeld);
            Assert.Null(fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
            Assert.Equal(ControlMode.Auto, (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).Mode);
            Assert.Empty(fixture.Device.Writes);
            AssertNoManualPriorityAuxiliaryWrites(fixture);
        }
        finally { readGate.Set(); }
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        await fixture.Coordinator.BestEffortWatchdogFailSafeAsync();
        Assert.Empty(fixture.Device.Writes);
        AssertNoManualPriorityAuxiliaryWrites(fixture);
    }

    [Fact]
    public async Task ObservedManualTargetRemainsHeldWhenItsSaveIsConfirmedNotApplied()
    {
        var store = new ManualObservationConfigurationStore();
        await using var fixture = await CoordinatorFixture.CreateAsync(true, suppliedStore: store,
            initialConfiguration: ManualPriorityConfiguration());
        await fixture.ReachSettledControlAsync();
        var automaticTarget = fixture.Device.CurrentPercent;
        Assert.NotEqual((byte)47, automaticTarget);
        var writes = fixture.Device.Writes.Count;
        fixture.Device.SetState(47, poweredOn: true);
        store.FailSaves = true;

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(ControlMode.Auto, store.Current.Mode);
        Assert.Equal((byte)59, store.Current.ManualSpeedPercent);
        Assert.False(fixture.Coordinator.GetStatus().AuditMutation?.Blocked);
        Assert.Equal(ControlMode.Manual, fixture.Coordinator.GetStatus().Fan.Mode);
        Assert.Equal((byte)47, fixture.Coordinator.GetStatus().Fan.RequestedPercent);
        Assert.Equal((byte)59, fixture.Coordinator.GetStatus().Fan.ConfiguredManualPercent);
        for (var sample = 0; sample < 4; sample++)
            await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(writes, fixture.Device.Writes.Count);
        Assert.Equal((byte)47, fixture.Device.CurrentPercent);
        Assert.Equal((byte)47, fixture.Coordinator.GetStatus().Fan.RequestedPercent);
        AssertNoManualPriorityAuxiliaryWrites(fixture);

        store.FailSaves = false;
        await fixture.Coordinator.SetModeAsync(ControlMode.Auto, ProfilePeer, CancellationToken.None);
        for (var sample = 0; sample < 10; sample++)
            await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(automaticTarget, fixture.Device.CurrentPercent);
        Assert.Equal(ControlMode.Auto, fixture.Coordinator.GetStatus().Fan.Mode);
    }

    [Fact]
    public async Task CancelledNewerManualRequestDoesNotConsumePendingPhysicalObservation()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true,
            initialConfiguration: ManualPriorityConfiguration(),
            suppliedSafetyProtectionStore: ManualPriorityProtection());
        fixture.Device.SetState(68, poweredOn: true);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        fixture.Device.SetState(44, poweredOn: true);
        using var readGate = new ManualResetEventSlim(false);
        using var requestCancellation = new CancellationTokenSource();
        var readStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Device.BeforeRead = () => readStarted.TrySetResult(true);
        fixture.Device.ReadGate = readGate;
        fixture.Device.IgnoreReadCancellation = true;
        var tick = fixture.Coordinator.TickAsync(CancellationToken.None);
        try
        {
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var manual = fixture.Coordinator.SetManualSpeedAsync(72, ProfilePeer, requestCancellation.Token);
            requestCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manual);
        }
        finally { readGate.Set(); }
        await tick;
        Assert.Equal((byte)44, fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        var saved = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal(ControlMode.Manual, saved.Mode);
        Assert.Equal((byte)44, saved.ManualSpeedPercent);
        Assert.Equal((byte)44, fixture.Coordinator.GetStatus().Fan.RequestedPercent);
        Assert.Empty(fixture.Device.Writes);
        AssertNoManualPriorityAuxiliaryWrites(fixture);
    }

    private static ControllerConfiguration ManualPriorityConfiguration() => ControllerConfiguration.CreateDefault() with
        { Commissioned = true, Mode = ControlMode.Auto, ManualSpeedPercent = 59 };

    private static FakeSafetyProtectionStore ManualPriorityProtection() => new()
        { Current = new SafetyProtectionIntent(1, true, DateTimeOffset.UtcNow, "S-1-5-21-111-222-333-1001") };

    private static void AssertNoManualPriorityAuxiliaryWrites(CoordinatorFixture fixture)
    {
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.Equal(0, fixture.Device.LightingWrites);
        Assert.Equal(0, fixture.Device.SoftwareControlSelections);
    }

    private sealed class ManualObservationConfigurationStore : IConfigurationStore
    {
        public ControllerConfiguration Current { get; private set; } = ControllerConfiguration.CreateDefault();
        public bool FailSaves { get; set; }

        public Task<ControllerConfiguration> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current);
        }

        public Task SaveAsync(ControllerConfiguration configuration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailSaves) throw new IOException("Injected manual observation save failure before publication.");
            Current = configuration;
            return Task.CompletedTask;
        }
    }
}
