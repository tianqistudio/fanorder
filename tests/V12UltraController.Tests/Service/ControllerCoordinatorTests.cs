using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Security;
using V12UltraController.Core.Thermal;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

[Collection(HardwareTimingCollection.Name)]
public sealed partial class ControllerCoordinatorTests
{
    [Fact]
    public async Task PureManualKeepsNewPhysicalLightingWithoutChangingRequestedSpeed()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.ReachSettledControlAsync();
        var peer = new PipePeerIdentity("S-1-5-21-111-222-333-1001", "Settings");
        await fixture.Coordinator.SetModeAsync(ControlMode.PureManual, peer, CancellationToken.None);
        var before = await fixture.Device.ReadStateAsync(CancellationToken.None);
        fixture.Device.ChangeLightingOnNextSpeedWrite = true;
        var writes = fixture.Device.Writes.Count;
        var lightingWrites = fixture.Device.LightingWrites;
        var error = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.SetManualSpeedAsync(61, peer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.CommandReadbackMismatch, error.Code);
        var after = await fixture.Device.ReadStateAsync(CancellationToken.None);
        Assert.Equal((byte)(before.LightPower ^ 1), after.LightPower);
        Assert.Equal(61, after.FanSpeedPercent);
        Assert.Equal(lightingWrites, fixture.Device.LightingWrites);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(new byte[] { 61 }, fixture.Device.Writes.Skip(writes));
    }

    [Fact]
    public async Task PureManualPowerMismatchIsNotSuppressedAsKnobAdjustment()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.ReachSettledControlAsync();
        var peer = new PipePeerIdentity("S-1-5-21-111-222-333-1001", "Settings");
        await fixture.Coordinator.SetModeAsync(ControlMode.PureManual, peer, CancellationToken.None);
        fixture.Device.ReturnShutdownOffMismatchedReadback = true;
        var offCount = fixture.Device.ShutdownOffWrites;
        var error = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.SetManualPowerAsync(false, true, peer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.CommandReadbackMismatch, error.Code);
        Assert.Equal(offCount + 1, fixture.Device.ShutdownOffWrites);
        Assert.True(fixture.Coordinator.GetStatus().Fan.PoweredOn);
        Assert.False(fixture.Coordinator.GetStatus().Fan.OffRequested);
        var count = fixture.Device.Writes.Count;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(count, fixture.Device.Writes.Count);
    }

    [Fact]
    public async Task PureManualTargetSelectionOverridesEarlierPhysicalModeWithoutFullSpeedRecovery()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.ReachSettledControlAsync();
        var peer = new PipePeerIdentity("S-1-5-21-111-222-333-1001", "Settings");
        await fixture.Coordinator.SetModeAsync(ControlMode.PureManual, peer, CancellationToken.None);
        fixture.Device.IgnoreNormalSpeedWrites = true;
        var count = fixture.Device.Writes.Count;
        await fixture.Coordinator.SetManualSpeedAsync(61, peer, CancellationToken.None);
        Assert.Equal((byte)61, fixture.Device.CurrentPercent);
        Assert.Equal(new byte[] { 61 }, fixture.Device.ManualSoftwareControlTargets);
        Assert.Equal(fixture.Device.CurrentPercent, fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
        Assert.Equal(fixture.Device.CurrentPercent, (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).ManualSpeedPercent);
        Assert.Equal(ControlMode.PureManual, fixture.Coordinator.GetStatus().Fan.Mode);
        Assert.NotNull(fixture.Coordinator.GetStatus().LastSuccessfulRead);
        Assert.Equal(new byte[] { 61 }, fixture.Device.Writes.Skip(count));
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        await fixture.Coordinator.DisposeAsync();
        Assert.Equal(new byte[] { 61 }, fixture.Device.Writes.Skip(count));
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.Equal(0, fixture.Device.LightingWrites);
        Assert.Equal(0, fixture.Device.SoftwareControlSelections);
    }

    [Fact]
    public async Task PureManualExplicitCommandFromFreshProcessOpensOnlyOnClick()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true, initialConfiguration:
            ControllerConfiguration.CreateDefault() with { Commissioned = true, Mode = ControlMode.PureManual });
        Assert.Empty(fixture.Device.Writes);
        var peer = new PipePeerIdentity("S-1-5-21-111-222-333-1001", "Settings");
        await fixture.Coordinator.SetManualSpeedAsync(62, peer, CancellationToken.None);
        Assert.Equal(new byte[] { 62 }, fixture.Device.Writes);
        await fixture.Coordinator.SetModeAsync(ControlMode.Released, peer, CancellationToken.None);
        var saved = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal(ControlMode.Released, saved.Mode);
    }
    [Theory]
    [InlineData(ControlMode.PureManual)]
    [InlineData(ControlMode.Released)]
    public async Task PassiveStartupOnlyQueriesOwnedPureManualDeviceIncludingAtHighTemperature(ControlMode mode)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true, initialConfiguration:
            ControllerConfiguration.CreateDefault() with { Commissioned = true, Mode = mode });
        fixture.Thermal.CpuTemperature = 100;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        fixture.Thermal.CpuTemperature = null;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal(mode == ControlMode.PureManual ? 2 : 0, fixture.Device.ReadCalls);
        Assert.Equal(mode == ControlMode.PureManual ? 1 : 0, fixture.Locator.OpenCalls);
        Assert.False(fixture.Coordinator.GetStatus().FailSafe.Active);
        await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.Equal(0, fixture.Device.LightingWrites);
        Assert.Equal(0, fixture.Device.SoftwareControlSelections);
    }

    [Fact]
    public async Task PureManualWritesOnlyExplicitCommandsAndReleaseDoesNotChangeSpeed()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.ReachSettledControlAsync();
        var peer = new PipePeerIdentity("S-1-5-21-111-222-333-1001", "Settings");
        await fixture.Coordinator.SetModeAsync(ControlMode.PureManual, peer, CancellationToken.None);
        var count = fixture.Device.Writes.Count;
        fixture.Thermal.CpuTemperature = 100;
        for (var i = 0; i < 35; i++) await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(count, fixture.Device.Writes.Count);
        await fixture.Coordinator.SetManualSpeedAsync(64, peer, CancellationToken.None);
        Assert.Equal(64, fixture.Device.CurrentPercent);
        Assert.Equal(count + 1, fixture.Device.Writes.Count);
        await fixture.Coordinator.SetModeAsync(ControlMode.Released, peer, CancellationToken.None);
        Assert.False(fixture.WriterLease.IsHeld);
        Assert.False(fixture.Coordinator.GetStatus().Device.Connected);
        await Assert.ThrowsAsync<ControllerOperationException>(() => fixture.Coordinator.SetManualSpeedAsync(50, peer, CancellationToken.None));
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);
        Assert.Equal(count + 1, fixture.Device.Writes.Count);
    }

    [Fact]
    public async Task PureManualOffHasNoThermalAutoRestart()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(true);
        await fixture.ReachSettledControlAsync();
        var peer = new PipePeerIdentity("S-1-5-21-111-222-333-1001", "Settings");
        await fixture.Coordinator.SetModeAsync(ControlMode.PureManual, peer, CancellationToken.None);
        fixture.Thermal.CpuTemperature = 100;
        await fixture.Coordinator.SetManualPowerAsync(false, true, peer, CancellationToken.None);
        Assert.False(fixture.Device.PoweredOn);
        var count = fixture.Device.Writes.Count;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);
        Assert.Equal(count, fixture.Device.Writes.Count);
        Assert.False(fixture.Device.PoweredOn);
    }
    [Fact]
    public async Task LightingChangePreservesFanAndSurvivesNormalTick()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        await fixture.ReachSettledControlAsync();
        var percent = fixture.Device.CurrentPercent;
        var mode = fixture.Coordinator.GetStatus().Fan.Mode;
        var desired = new LightingSettings(true, 1, 2, 220, 3);
        await fixture.Coordinator.SetLightingAsync(desired,
            new PipePeerIdentity("S-1-5-21-111-222-333-1001", "Settings"), CancellationToken.None);
        Assert.Equal(1, fixture.Device.LightingWrites);
        Assert.Equal(percent, fixture.Device.CurrentPercent);
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal(((byte)1,(byte)2,(byte)0,(byte)220), fixture.Device.LightingState);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(mode, fixture.Coordinator.GetStatus().Fan.Mode);
        Assert.Equal(((byte)1,(byte)2,(byte)0,(byte)220), fixture.Device.LightingState);
    }

    [Fact]
    public async Task LightingMismatchFailsAndRestoresCooling()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        await fixture.ReachSettledControlAsync();
        fixture.Device.IgnoreLightingWrites = true;
        await Assert.ThrowsAsync<V12ProtocolException>(() => fixture.Coordinator.SetLightingAsync(new(true,1,2,220,3),
            new PipePeerIdentity("S-1-5-21-111-222-333-1001", "CLI"), CancellationToken.None));
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal(100, fixture.Device.CurrentPercent);
        Assert.True(fixture.Coordinator.GetStatus().FailSafe.Active);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(90.0)]
    public async Task LightingRefusesUnhealthyTemperature(double? cpu)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        await fixture.ReachSettledControlAsync();
        fixture.Thermal.CpuTemperature = cpu;
        var error = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.SetLightingAsync(new(true,1,2,220,3),
                new PipePeerIdentity("S-1-5-21-111-222-333-1001", "CLI"), CancellationToken.None));
        Assert.Equal(PipeErrorCodes.FailSafeActive,error.Code);
        Assert.Equal(0,fixture.Device.LightingWrites);
    }

    [Fact]
    public async Task UncommissionedLightingGetIsCachedAndSetNeverTouchesDevice()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        await fixture.Coordinator.GetLightingAsync(CancellationToken.None);
        var error = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.SetLightingAsync(new(true,1,2,220,3),
                new PipePeerIdentity("S-1-5-21-111-222-333-1001", "CLI"), CancellationToken.None));
        Assert.Equal(PipeErrorCodes.NotCommissioned,error.Code);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Equal(0, fixture.Device.LightingWrites);
        Assert.Empty(fixture.Device.Writes);
    }

    [Theory]
    [InlineData(44)]
    [InlineData(100)]
    public async Task CommissionedOpenRestoresPhysicalModeEvenAtMatchingOneHundred(int physicalPercent)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Device.SetState((byte)physicalPercent, true);
        fixture.Device.IgnoreNormalSpeedWrites = true;
        var lighting = fixture.Device.LightingState;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Device.SoftwareControlSelections);
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal(lighting, fixture.Device.LightingState);
        Assert.True(fixture.Coordinator.GetStatus().FailSafe.Active);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        await fixture.Coordinator.SetManualSpeedAsync(50,
            new PipePeerIdentity("S-1-5-21-111-222-333-1001", "Settings"), CancellationToken.None);
        await fixture.ReachSettledControlAsync();
        Assert.Equal((byte)50, fixture.Device.CurrentPercent);
        Assert.Equal(1, fixture.Device.SoftwareControlSelections);
    }

    [Fact]
    public async Task CommissionedObservedSpeedChangeSelectsManualWithoutFullSpeedTakeover()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        await fixture.ReachSettledControlAsync();
        var writes = fixture.Device.Writes.Count;
        fixture.Device.SetState(44, true);
        fixture.Device.IgnoreNormalSpeedWrites = true;
        var lighting = fixture.Device.LightingState;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(0, fixture.Device.SoftwareControlSelections);
        Assert.Equal((byte)44, fixture.Device.CurrentPercent);
        Assert.Equal(writes, fixture.Device.Writes.Count);
        Assert.Equal(ControlMode.Manual, fixture.Coordinator.GetStatus().Fan.Mode);
        Assert.Equal((byte)44, (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).ManualSpeedPercent);
        Assert.Equal(lighting, fixture.Device.LightingState);
        Assert.False(fixture.Coordinator.GetStatus().FailSafe.Active);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        for (var tick = 0; tick < 4; tick++) await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.All(fixture.Device.Writes.Skip(writes), percent => Assert.Equal((byte)44, percent));
        Assert.Equal(0, fixture.Device.SoftwareControlSelections);
        Assert.Equal(lighting, fixture.Device.LightingState);
    }

    [Fact]
    public async Task UncommissionedPhysicalKnobIsNeverAutomaticallyTakenOver()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        fixture.Device.SetState(44, true);
        fixture.Device.IgnoreNormalSpeedWrites = true;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(0, fixture.Device.SoftwareControlSelections);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task FailedSoftwareModeRestorationDoesNotClaimSafeSpeedOrIssueLowerTarget()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Device.SetState(44, true);
        fixture.Device.IgnoreNormalSpeedWrites = true;
        fixture.Device.SoftwareControlSelectionFailure = new IOException("Synthetic software mode failure");
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Device.SoftwareControlSelections);
        Assert.Equal((byte)44, fixture.Device.CurrentPercent);
        Assert.True(fixture.Coordinator.GetStatus().FailSafe.Active);
        Assert.Null(fixture.Coordinator.GetStatus().LastSuccessfulWrite);
        Assert.All(fixture.Device.Writes, percent => Assert.Equal((byte)100, percent));
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
    }
    [Theory]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    public async Task AttendedAbortRecovery_RequiresPresenceAndExactCaller(bool attended, bool sameCaller, bool physicalManualMode, bool automaticAbort)
    {
        const string caller = "S-1-5-21-111-222-333-1001";
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false,
            configureSafetyOperationAudit: async (store, _) =>
            {
                var journal = store.Create(automaticAbort ? "commission.safety-abort" : PipeOperations.CommissionAbort,
                    caller, automaticAbort ? "service" : "v12ctl",
                    SafetyOperationKind.HidWithOn100Recovery, new { poweredOn = true },
                    new { fanPercent = 100 }, requiresOn100Recovery: true);
                await store.PrepareAsync(journal);
                await store.MarkEffectInProgressAsync(journal);
            });
        Assert.Equal(0, fixture.Locator.OpenCalls);
        fixture.Device.IgnoreNormalSpeedWrites = physicalManualMode;
        fixture.Device.SetState(44, true);
        var peer = new PipePeerIdentity(sameCaller ? caller : "S-1-5-21-999", "v12ctl");
        if (!attended || !sameCaller)
        {
            await Assert.ThrowsAsync<ControllerOperationException>(() =>
                fixture.Coordinator.RecoverCommissioningSafetyAsync(attended, peer, CancellationToken.None));
            Assert.Equal(0, fixture.Locator.OpenCalls);
            Assert.NotNull(await fixture.SafetyOperationAuditJournalStore.LoadAsync());
            return;
        }
        await fixture.Coordinator.RecoverCommissioningSafetyAsync(true, peer, CancellationToken.None);
        Assert.Equal(physicalManualMode ? new byte[] { 100, 100 } : new byte[] { 100 }, fixture.Device.Writes);
        Assert.Equal(physicalManualMode ? 1 : 0, fixture.Device.SoftwareControlSelections);
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.Null(await fixture.SafetyOperationAuditJournalStore.LoadAsync());
        Assert.False(fixture.Coordinator.GetStatus().Commissioned);
        Assert.False(fixture.Coordinator.GetStatus().SafetyOperationAudit?.Blocked);
    }

    [Fact]
    public async Task AttendedAbortRecovery_RejectsUnrelatedHardwareJournal()
    {
        const string caller = "S-1-5-21-111-222-333-1001";
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false,
            configureSafetyOperationAudit: async (store, _) =>
            {
                var journal = store.Create(PipeOperations.CommissionIdempotent, caller, "v12ctl",
                    SafetyOperationKind.HidWithOn100Recovery, new { poweredOn = true },
                    new { fanPercent = 100 }, requiresOn100Recovery: true);
                await store.PrepareAsync(journal);
                await store.MarkEffectInProgressAsync(journal);
            });
        await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.RecoverCommissioningSafetyAsync(true,
                new PipePeerIdentity(caller, "v12ctl"), CancellationToken.None));
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
        Assert.NotNull(await fixture.SafetyOperationAuditJournalStore.LoadAsync());
    }

    [Fact]
    public async Task AttendedAbortRecovery_AutomaticAbortRetainsBarrierWhenModeRecoveryFails()
    {
        const string caller = "S-1-5-21-111-222-333-1001";
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false,
            configureSafetyOperationAudit: async (store, _) =>
            {
                var journal = store.Create("commission.safety-abort", caller, "service",
                    SafetyOperationKind.HidWithOn100Recovery, new { poweredOn = true },
                    new { fanPercent = 100 }, requiresOn100Recovery: true);
                await store.PrepareAsync(journal);
                await store.MarkEffectInProgressAsync(journal);
            });
        fixture.Device.SetState(68, true);
        fixture.Device.IgnoreNormalSpeedWrites = true;
        fixture.Device.SoftwareControlSelectionFailure = new IOException("Synthetic recovery failure");
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Coordinator.RecoverCommissioningSafetyAsync(true,
            new PipePeerIdentity(caller, "v12ctl"), CancellationToken.None));
        Assert.NotNull(await fixture.SafetyOperationAuditJournalStore.LoadAsync());
        Assert.True(fixture.Coordinator.GetStatus().SafetyOperationAudit?.Blocked);
        Assert.False(fixture.Coordinator.GetStatus().Commissioned);
        Assert.Equal((byte)68, fixture.Device.CurrentPercent);
        Assert.All(fixture.Device.Writes, percent => Assert.Equal((byte)100, percent));
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
    }

    [Fact]
    public async Task ThermalHistoryRecorderPersistsWithoutOpeningHardware()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        using var recorder = new ThermalHistoryRecorder(fixture.Coordinator, fixture.Paths);
        await recorder.StartAsync(CancellationToken.None);
        for (var i = 0; i < 40 && recorder.Analyze(fixture.Coordinator.GetCurve()).RecordedSamples == 0; i++)
            await Task.Delay(25);
        await recorder.StopAsync(CancellationToken.None);
        Assert.Equal(1, recorder.Analyze(fixture.Coordinator.GetCurve()).RecordedSamples);
        Assert.Equal(0, recorder.Analyze(fixture.Coordinator.GetCurve()).EligibleSamples);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Paths.DataDirectory, "thermal-history-v1"), "*.jsonl"));
        using var restarted = new ThermalHistoryRecorder(fixture.Coordinator, fixture.Paths);
        await restarted.StartAsync(CancellationToken.None);
        for (var i = 0; i < 40 && restarted.Analyze(fixture.Coordinator.GetCurve()).StorageWarning is not null; i++)
            await Task.Delay(25);
        await restarted.StopAsync(CancellationToken.None);
        Assert.Equal(1, restarted.Analyze(fixture.Coordinator.GetCurve()).RecordedSamples);
    }

    [Fact]
    public async Task ThermalHistoryFailureIsAdvisoryOnly()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        await File.WriteAllTextAsync(Path.Combine(fixture.Paths.DataDirectory, "thermal-history-v1"), "synthetic path conflict");
        using var recorder = new ThermalHistoryRecorder(fixture.Coordinator, fixture.Paths);
        await recorder.StartAsync(CancellationToken.None);
        for (var i = 0; i < 40 && recorder.Analyze(fixture.Coordinator.GetCurve()).StorageWarning == "历史记录正在初始化。"; i++)
            await Task.Delay(25);
        await recorder.StopAsync(CancellationToken.None);
        Assert.Contains("不可用", recorder.Analyze(fixture.Coordinator.GetCurve()).StorageWarning);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task OversizedThermalHistoryCompletesWithoutFaultingTheHostedWorker()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var directory = Path.Combine(fixture.Paths.DataDirectory, "thermal-history-v1");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture) + ".jsonl");
        await File.WriteAllBytesAsync(path, new byte[2 * 1024 * 1024 + 1]);
        using var recorder = new ThermalHistoryRecorder(fixture.Coordinator, fixture.Paths);

        await recorder.StartAsync(CancellationToken.None);
        // An unhandled exception here would invoke the host's StopHost policy.
        await recorder.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await recorder.StopAsync(CancellationToken.None);

        var advice = recorder.Analyze(fixture.Coordinator.GetCurve());
        Assert.Contains(nameof(InvalidDataException), advice.StorageWarning);
        Assert.Equal(0, advice.RecordedSamples);
        Assert.Equal(2 * 1024 * 1024 + 1, new FileInfo(path).Length);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task CorruptThermalHistoryRemainsAdvisoryAndPreservesTheOriginalFile()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var directory = Path.Combine(fixture.Paths.DataDirectory, "thermal-history-v1");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture) + ".jsonl");
        const string corruptHistory = "{incomplete historical observation\n";
        await File.WriteAllTextAsync(path, corruptHistory);
        using var recorder = new ThermalHistoryRecorder(fixture.Coordinator, fixture.Paths);
        await recorder.StartAsync(CancellationToken.None);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (recorder.Analyze(fixture.Coordinator.GetCurve()).RecordedSamples == 0)
                await Task.Delay(10, deadline.Token);
        }
        finally { await recorder.StopAsync(CancellationToken.None); }

        await recorder.ExecuteTask!;
        Assert.Contains("损坏", recorder.Analyze(fixture.Coordinator.GetCurve()).StorageWarning);
        Assert.Equal(corruptHistory, await File.ReadAllTextAsync(path));
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task CurveAdviceApplyIsAtomicEnablesAutoAndRejectsStaleBaseline()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-111-222-333-1001", "Settings");
        await fixture.Coordinator.SetManualSpeedAsync(37, peer, CancellationToken.None);
        var baseline = fixture.Coordinator.GetCurve();
        var proposed = new FanCurve([new(0, 50), new(90, 100)]);
        await fixture.Coordinator.ApplyCurveAdviceAsync(baseline, proposed, peer, CancellationToken.None);
        var persisted = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal(ControlMode.Auto, persisted.Mode);
        Assert.Equal(proposed.Points, persisted.Curve.Points);
        await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.ApplyCurveAdviceAsync(baseline, FanCurve.RemoteHostSafe, peer, CancellationToken.None));
        Assert.Equal(proposed.Points, fixture.Coordinator.GetCurve().Points);
        Assert.Empty(fixture.Device.Writes);
        await fixture.Coordinator.ApplyCurveAdviceAsync(proposed, baseline, peer, CancellationToken.None);
        Assert.Equal(baseline.Points, fixture.Coordinator.GetCurve().Points);
    }

    [Fact]
    public async Task ManualPower_OffReadbackMismatchPreservesActualStateWithoutRepeatingOn()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Thermal.CpuTemperature = 40;
        await fixture.ReachSettledControlAsync();
        var lighting = fixture.Device.LightingState;
        var writes = fixture.Device.Writes.Count;
        fixture.Device.ReturnShutdownOffMismatchedReadback = true;
        var error = await Assert.ThrowsAsync<ControllerOperationException>(() => fixture.Coordinator.SetManualPowerAsync(false, true,
            new PipePeerIdentity("S-1-5-21-111-222-333-1001", "Settings"), CancellationToken.None));
        Assert.Equal(PipeErrorCodes.CommandReadbackMismatch, error.Code);
        Assert.Equal(writes, fixture.Device.Writes.Count);
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        Assert.Equal(lighting, fixture.Device.LightingState);
        Assert.False(fixture.Coordinator.GetStatus().Fan.OffRequested);
    }

    [Fact]
    public async Task ManualPower_OffTimeoutPoisonsAccessAndNeverClaimsOffSuccess()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Thermal.CpuTemperature = 40;
        await fixture.ReachSettledControlAsync();
        fixture.Device.ShutdownOffDelay = TimeSpan.FromSeconds(2);
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Coordinator.SetManualPowerAsync(false, true,
            new PipePeerIdentity("S-1-5-21-111-222-333-1001", "Settings"), CancellationToken.None));
        Assert.True(fixture.Coordinator.GetStatus().HardwareSafety.Poisoned);
        Assert.False(fixture.Coordinator.GetStatus().Fan.OffRequested);
        Assert.True(fixture.Coordinator.GetStatus().FailSafe.Active);
    }

    [Fact]
    public async Task ManualPower_UncommissionedRequestNeverOpensDevice()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.SetManualPowerAsync(false, true,
                new PipePeerIdentity("S-1-5-21-111-222-333-1001", "Settings"), CancellationToken.None));
        Assert.Equal(PipeErrorCodes.NotCommissioned, rejected.Code);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
    }

    [Fact]
    public async Task ManualPower_MissingCpuResumesAndDoesNotRestoreOffIntent()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Thermal.CpuTemperature = 40;
        await fixture.ReachSettledControlAsync();
        await fixture.Coordinator.SetManualPowerAsync(false, true,
            new PipePeerIdentity("S-1-5-21-111-222-333-1001", "Settings"), CancellationToken.None);
        fixture.Thermal.CpuTemperature = null;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        Assert.False(fixture.Coordinator.GetStatus().Fan.OffRequested);
    }

    [Fact]
    public async Task ManualPower_OffFailureConfirmsRecoveryAndResolvesJournal()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Thermal.CpuTemperature = 40;
        await fixture.ReachSettledControlAsync();
        fixture.Device.ShutdownOffFailure = new IOException("synthetic power failure");
        await Assert.ThrowsAsync<IOException>(() => fixture.Coordinator.SetManualPowerAsync(false, true,
            new PipePeerIdentity("S-1-5-21-111-222-333-1001", "Settings"), CancellationToken.None));
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        Assert.True(fixture.Coordinator.GetStatus().Fan.PoweredOn);
        Assert.False(fixture.Coordinator.GetStatus().Fan.OffRequested);
        Assert.False(fixture.Coordinator.GetStatus().SafetyOperationAudit?.Pending);
    }

    [Fact]
    public async Task ManualPower_CancellationBeforeOffUsesIndependentRecoveryToken()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Thermal.CpuTemperature = 40;
        await fixture.ReachSettledControlAsync();
        using var cancelled = new CancellationTokenSource();
        fixture.Device.BeforeRead = () => cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Coordinator.SetManualPowerAsync(false, true,
            new PipePeerIdentity("S-1-5-21-111-222-333-1001", "Settings"), cancelled.Token));
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        Assert.False(fixture.Coordinator.GetStatus().Fan.OffRequested);
        Assert.False(fixture.Coordinator.GetStatus().HardwareSafety.Poisoned);
    }

    [Fact]
    public async Task ManualPower_OffPersistsAcrossHealthyTicksAndOnIsVerified()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Thermal.CpuTemperature = 40;
        await fixture.ReachSettledControlAsync();
        var peer = new PipePeerIdentity("S-1-5-21-111-222-333-1001", "Settings");
        await fixture.Coordinator.SetManualPowerAsync(false, true, peer, CancellationToken.None);
        Assert.False(fixture.Coordinator.GetStatus().Fan.PoweredOn);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.False(fixture.Coordinator.GetStatus().Fan.PoweredOn);
        Assert.Equal("ManuallyOff", fixture.Coordinator.GetStatus().ServiceState);
        await fixture.Coordinator.SetManualPowerAsync(true, true, peer, CancellationToken.None);
        Assert.True(fixture.Coordinator.GetStatus().Fan.PoweredOn);
        Assert.Equal((byte)100, fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
    }

    [Theory]
    [InlineData(85)]
    [InlineData(100)]
    public async Task ManualPower_HeatResumesAtOneHundred(double temperature)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Thermal.CpuTemperature = 40;
        await fixture.ReachSettledControlAsync();
        var peer = new PipePeerIdentity("S-1-5-21-111-222-333-1001", "Settings");
        await fixture.Coordinator.SetManualPowerAsync(false, true, peer, CancellationToken.None);
        fixture.Thermal.CpuTemperature = temperature;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.True(fixture.Coordinator.GetStatus().Fan.PoweredOn);
        Assert.Equal((byte)100, fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
        fixture.Thermal.CpuTemperature = 40;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.True(fixture.Coordinator.GetStatus().Fan.PoweredOn);
    }

    [Theory]
    [InlineData(false, 40)]
    [InlineData(true, 60)]
    public async Task ManualPower_UnsafeOffDoesNotWrite(bool confirmed, double temperature)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Thermal.CpuTemperature = temperature;
        await fixture.ReachSettledControlAsync();
        await Assert.ThrowsAsync<ControllerOperationException>(() => fixture.Coordinator.SetManualPowerAsync(
            false, confirmed, new PipePeerIdentity("S-1-5-21-111-222-333-1001", "Settings"), CancellationToken.None));
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
    }

    private static readonly DeviceIdentity SupportedIdentity = new(
        DeviceAllowList.V12UltraVendorId,
        DeviceAllowList.V12UltraProductId,
        DeviceAllowList.V12UltraFirmwareVersion,
        DeviceAllowList.V12UltraUsagePage,
        DeviceAllowList.V12UltraUsage);

    [Fact]
    public async Task CommissionedStartup_ImmediatelyWritesOneHundredPercent()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Contains((byte)100, fixture.Device.Writes);
        var status = fixture.Coordinator.GetStatus();
        Assert.True(status.FailSafe.Active);
        Assert.Equal(FailSafeReason.StartupWarmup, status.FailSafe.Reason);
        Assert.Equal((byte)100, status.Fan.ConfiguredManualPercent);
        Assert.NotNull(status.Commissioning);
        Assert.False(status.Commissioning.Active);
    }

    [Fact]
    public async Task CommissionedStartup_MatchingVidPidWithUnknownUsageIsUnsupportedWithoutOpenOrWrite()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Locator.Identities =
        [
            SupportedIdentity with
            {
                UsagePage = 0xFF01,
                Usage = 0x0002,
            },
        ];

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(1, fixture.Locator.EnumerateCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal("UnsupportedDevice", fixture.Coordinator.GetStatus().ServiceState);
        Assert.Equal(
            FailSafeReason.UnsupportedDevice,
            fixture.Coordinator.GetStatus().FailSafe.Reason);
    }

    [Fact]
    public async Task Diagnostics_DeviceStateCacheFailureNeverLeaksExceptionPath()
    {
        const string secretPath = @"C:\Users\private-user\sensitive\device-state-cache.json";
        var cache = new LeakyDeviceStateCacheStore(secretPath);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            suppliedDeviceStateCacheStore: cache,
            primeCommissioningThermal: false);
        await cache.LoadAttempted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var diagnostics = string.Empty;
        Assert.True(SpinWait.SpinUntil(
            () =>
            {
                diagnostics = JsonSerializer.Serialize(fixture.Coordinator.GetDiagnostics());
                return diagnostics.Contains("CACHE_IO_ERROR", StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(1)));

        Assert.DoesNotContain(secretPath, diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-user", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CACHE_IO_ERROR", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostics_InvalidConfigurationNeverLeaksParserExceptionDetails()
    {
        const string secretMaterial =
            @"C:\Users\private-user\config.json HID#VID_374A&PID_B101 SERIAL-VERY-SECRET " +
            "00 80 00 00 00 00 00 00 7F";
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            suppliedStore: new LeakyInvalidConfigurationStore(secretMaterial),
            primeCommissioningThermal: false);

        var diagnostics = JsonSerializer.Serialize(fixture.Coordinator.GetDiagnostics());

        Assert.DoesNotContain(secretMaterial, diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-user", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SERIAL-VERY-SECRET", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("00 80 00 00 00 00 00 00 7F", diagnostics, StringComparison.Ordinal);
        Assert.Contains("CONTROLLER_CONFIGURATION_ERROR", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommissionedStartup_BlockingCacheSaveCannotDelayOnAtOneHundredOrNextTick()
    {
        var cache = new BlockingDeviceStateCacheStore { BlockSave = true };
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedDeviceStateCacheStore: cache);
        try
        {
            await fixture.Coordinator.TickAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(1));
            await cache.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Contains((byte)100, fixture.Device.Writes);
            Assert.True(fixture.Device.PoweredOn);
            Assert.Equal((byte)100, fixture.Device.CurrentPercent);

            await fixture.Coordinator.TickAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            cache.ReleaseSave.TrySetResult(true);
        }
    }

    [Fact]
    public async Task Dispose_BlockingCacheSaveCannotDelayShutdownOneHundredPercentWrite()
    {
        var cache = new BlockingDeviceStateCacheStore { BlockSave = true };
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedDeviceStateCacheStore: cache);
        try
        {
            await fixture.Coordinator.TickAsync(CancellationToken.None);
            await cache.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            fixture.Device.SetState(50, poweredOn: true);
            var writesBeforeDispose = fixture.Device.Writes.Count;

            var dispose = fixture.Coordinator.DisposeAsync().AsTask();
            Assert.True(SpinWait.SpinUntil(
                () => fixture.Device.Writes.Count > writesBeforeDispose &&
                      fixture.Device.CurrentPercent == 100,
                TimeSpan.FromSeconds(1)));
            Assert.False(cache.ReleaseSave.Task.IsCompleted);

            cache.ReleaseSave.TrySetResult(true);
            await dispose.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            cache.ReleaseSave.TrySetResult(true);
        }
    }

    [Fact]
    public async Task CommissionedStartup_BlockingCachePreloadCannotHoldGateAfterFirstReadFailure()
    {
        var cache = new BlockingDeviceStateCacheStore { BlockLoad = true };
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedDeviceStateCacheStore: cache);
        await cache.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        fixture.Device.ReadFailures.Enqueue(new IOException("Injected first-read failure."));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await fixture.Coordinator.TickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            stopwatch.Stop();

            Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromMilliseconds(450));
            Assert.Equal(1, cache.LoadCalls);
            Assert.Empty(fixture.Device.Writes);
            Assert.Equal("HidFailure", fixture.Coordinator.GetStatus().ServiceState);

            await fixture.Coordinator.TickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1, cache.LoadCalls);
            Assert.Contains((byte)100, fixture.Device.Writes);
            Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        }
        finally
        {
            cache.ReleaseLoad.TrySetResult(true);
        }
    }

    [Fact]
    public async Task Status_SeparatesConfiguredManualPercentFromFailSafeRequestedPercent()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-402", "settings");

        await fixture.Coordinator.SetManualSpeedAsync(40, peer, CancellationToken.None);
        await fixture.Coordinator.TickAsync(CancellationToken.None);

        var status = fixture.Coordinator.GetStatus();
        Assert.Equal(ControlMode.Manual, status.Fan.Mode);
        Assert.Equal((byte)40, status.Fan.ConfiguredManualPercent);
        Assert.Equal((byte)100, status.Fan.RequestedPercent);
        Assert.True(status.FailSafe.Active);
        Assert.Equal(FailSafeReason.NotCommissioned, status.FailSafe.Reason);
    }

    [Fact]
    public async Task CommissionedStartup_FirstReadFailureUsesExactValidatedCacheOnlyForOnAtOneHundred()
    {
        var cachedState = V12FeatureReport.ParseState(
            CreateControllerStateReport(57, powerFlag: 0));
        var cache = new ReadyDeviceStateCacheStore(cachedState);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedDeviceStateCacheStore: cache);
        await cache.LoadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        fixture.Device.ReadFailures.Enqueue(new IOException("Injected first-read failure."));

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(1, fixture.Device.TrustedStateInitializations);
        Assert.Equal([(byte)100], fixture.Device.Writes);
        Assert.Equal(2, fixture.Device.ReadCalls);
        Assert.Equal(
            ["READ", "CACHE:INITIALIZE", "ON:100", "READ"],
            fixture.Device.Operations.ToArray());
        Assert.True(fixture.Coordinator.GetStatus().Fan.PoweredOn);
        Assert.Equal((byte)100, fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
    }

    [Fact]
    public async Task CommissionedStartup_FirstReadFailureWithMismatchedCachePerformsNoWrite()
    {
        var cache = new ReadyDeviceStateCacheStore(state: null);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedDeviceStateCacheStore: cache);
        await cache.LoadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        fixture.Device.ReadFailures.Enqueue(new IOException("Injected first-read failure."));

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(0, fixture.Device.TrustedStateInitializations);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal("HidFailure", fixture.Coordinator.GetStatus().ServiceState);
    }

    [Fact]
    public async Task CommissionedStartup_PoweredOffDeviceGetsConfiguredStartupKickBeforeSafetyWarmup()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Device.SetState(40, poweredOn: false);

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal([(byte)50, (byte)100], fixture.Device.Writes);
        Assert.Equal(2, fixture.Device.ReadCalls);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.True(fixture.Coordinator.GetStatus().Fan.PoweredOn);
        Assert.Equal((byte)50, fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
        Assert.True(fixture.Coordinator.GetStatus().FailSafe.Active);

        await fixture.CollectBackgroundThermalSamplesAsync(9);

        Assert.False(fixture.Coordinator.GetStatus().FailSafe.Active);
        Assert.Contains((byte)68, fixture.Device.Writes);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
    }

    [Fact]
    public async Task CommissionedStartup_UsesConfiguredKickWhenFanIsStopped()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            initialConfiguration: ControllerConfiguration.CreateDefault() with
            {
                Commissioned = true,
                StartupFanPercent = 73,
            });
        fixture.Device.SetState(40, poweredOn: false);

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal((byte)73, fixture.Device.Writes[0]);
        Assert.Equal((byte)73, fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
    }

    [Fact]
    public async Task CommissionedStartup_ManualModeKeepsTheSavedManualTargetAsHighestPriority()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            initialConfiguration: ControllerConfiguration.CreateDefault() with
            {
                Commissioned = true,
                Mode = ControlMode.Manual,
                ManualSpeedPercent = 64,
                StartupFanPercent = 50,
            });
        fixture.Device.SetState(40, poweredOn: false);

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal((byte)64, fixture.Device.Writes[0]);
        Assert.Equal((byte)64, fixture.Coordinator.GetStatus().Fan.ReportedControlPercent);
    }

    [Fact]
    public async Task CommissionedStartup_SafetyReadbackMismatchRetriesOneHundredAndNeverRunsCurve()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Device.ReturnMismatchedReadback = true;

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal([(byte)100, (byte)100], fixture.Device.Writes);
        Assert.Equal("HidFailure", fixture.Coordinator.GetStatus().ServiceState);
        Assert.True(fixture.Coordinator.GetStatus().FailSafe.Active);
        Assert.DoesNotContain(fixture.Device.Writes, static percent => percent < 100);
        Assert.Equal(1, fixture.Device.DisposeCalls);
    }

    [Fact]
    public async Task Reconnect_RepeatsGetOnOneHundredGetBeforePolicyResumes()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        fixture.Device.FailReads = true;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        fixture.Device.FailReads = false;
        var readsBeforeReconnect = fixture.Device.ReadCalls;
        var writesBeforeReconnect = fixture.Device.Writes.Count;

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(readsBeforeReconnect + 2, fixture.Device.ReadCalls);
        Assert.Equal(writesBeforeReconnect + 1, fixture.Device.Writes.Count);
        Assert.Equal((byte)100, fixture.Device.Writes[^1]);
        Assert.Equal(FailSafeReason.DeviceReconnected,
            fixture.Coordinator.GetStatus().FailSafe.Reason);
        Assert.DoesNotContain(
            fixture.Device.Writes.Skip(writesBeforeReconnect),
            static percent => percent < 100);
    }

    [Fact]
    public async Task OrdinaryReadTimeout_PoisonsOwnershipRetainsLeaseAndRunsLateOnAtOneHundredRecovery()
    {
        var previousExitCode = Environment.ExitCode;
        using var readGate = new ManualResetEventSlim(initialState: false);
        try
        {
            await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
            await fixture.ReachSettledControlAsync();
            var writesBeforeTimeout = fixture.Device.Writes.Count;
            fixture.Device.ReadGate = readGate;
            fixture.Device.IgnoreReadCancellation = true;

            var tick = fixture.Coordinator.TickAsync(CancellationToken.None);
            await fixture.Device.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await tick.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(fixture.Coordinator.IsHardwarePoisoned);
            Assert.Equal("HID_OPERATION_TIMEOUT",
                fixture.Coordinator.GetStatus().HardwareSafety.Reason);
            Assert.Equal(7, Environment.ExitCode);
            Assert.Equal(0, fixture.Device.DisposeCalls);
            using (var competingLease = new SingleWriterLease(fixture.Paths.WriterLeasePath))
            {
                Assert.False(competingLease.TryAcquire());
            }

            readGate.Set();
            Assert.True(SpinWait.SpinUntil(
                () => fixture.Device.Writes.Count == writesBeforeTimeout + 1,
                TimeSpan.FromSeconds(1)));
            Assert.Equal((byte)100, fixture.Device.Writes[^1]);
            Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        }
        finally
        {
            readGate.Set();
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task OrdinaryWriteTimeout_LateLowerWriteIsFollowedByOnAtOneHundredAndOwnershipStaysPoisoned()
    {
        var previousExitCode = Environment.ExitCode;
        using var writeGate = new ManualResetEventSlim(initialState: false);
        try
        {
            await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
            await fixture.ReachSettledControlAsync();
            var writesBeforeTimeout = fixture.Device.Writes.Count;
            fixture.Thermal.CpuTemperature = 80;
            fixture.Thermal.CapturedAt = DateTimeOffset.UtcNow;
            fixture.Device.WriteGate = writeGate;
            fixture.Device.GatedWritePercent = 79;
            fixture.Device.IgnoreWriteCancellation = true;

            var tick = fixture.Coordinator.TickAsync(CancellationToken.None);
            Assert.Equal((byte)79,
                await fixture.Device.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(1)));
            await tick.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(fixture.Coordinator.IsHardwarePoisoned);
            Assert.Equal(7, Environment.ExitCode);
            Assert.Equal(0, fixture.Device.DisposeCalls);
            using (var competingLease = new SingleWriterLease(fixture.Paths.WriterLeasePath))
            {
                Assert.False(competingLease.TryAcquire());
            }

            writeGate.Set();
            Assert.True(SpinWait.SpinUntil(
                () => fixture.Device.Writes.Count == writesBeforeTimeout + 2,
                TimeSpan.FromSeconds(1)));
            Assert.Equal([(byte)79, (byte)100],
                fixture.Device.Writes.Skip(writesBeforeTimeout).ToArray());
            Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        }
        finally
        {
            writeGate.Set();
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task CallerCancelledTick_LateLowerWriteRecoversBeforeHostStopAndRetainsOwnership()
    {
        using var writeGate = new ManualResetEventSlim(initialState: false);
        using var stopping = new CancellationTokenSource();
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        await fixture.ReachSettledControlAsync();
        fixture.Thermal.CpuTemperature = 80;
        fixture.Thermal.CapturedAt = DateTimeOffset.UtcNow;
        fixture.Device.WriteGate = writeGate;
        fixture.Device.GatedWritePercent = 79;
        fixture.Device.IgnoreWriteCancellation = true;
        var operationStart = fixture.Device.Operations.Count;
        try
        {
            var tick = fixture.Coordinator.TickAsync(stopping.Token);
            Assert.Equal((byte)79,
                await fixture.Device.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(1)));

            stopping.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tick);
            Assert.Equal(0, fixture.Device.DisposeCalls);
            using (var competingLease = new SingleWriterLease(fixture.Paths.WriterLeasePath))
            {
                Assert.False(competingLease.TryAcquire());
            }

            var finalization = fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.ServiceStop);
            await Task.Yield();
            writeGate.Set();
            await finalization.WaitAsync(TimeSpan.FromSeconds(2));

            var operations = fixture.Device.Operations.Skip(operationStart).ToArray();
            Assert.True(operations.Length >= 6, string.Join(",", operations));
            Assert.Equal("READ", operations[0]);
            Assert.Equal("ON:79", operations[1]);
            Assert.Equal("ON:100", operations[2]);
            Assert.Equal("READ", operations[3]);
            Assert.Equal("ON:100", operations[4]);
            Assert.Equal("READ", operations[5]);
            Assert.Equal(HostStopDisposition.KeepAt100Confirmed,
                fixture.Coordinator.HostStopDisposition);
            Assert.Equal((byte)100, fixture.Device.CurrentPercent);
            Assert.Equal(0, fixture.Device.DisposeCalls);
            using var leaseAfterFinalization = new SingleWriterLease(fixture.Paths.WriterLeasePath);
            Assert.False(leaseAfterFinalization.TryAcquire());
        }
        finally
        {
            writeGate.Set();
        }
    }

    [Fact]
    public async Task CancellationIgnoringThermalRead_DoesNotBlockHidFailSafeWrite()
    {
        using var thermalGate = new ManualResetEventSlim(initialState: false);
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        await fixture.ReachSettledControlAsync();
        var writesBeforeTimeout = fixture.Device.Writes.Count;
        fixture.Thermal.ReadGate = thermalGate;
        fixture.Thermal.IgnoreReadCancellation = true;

        try
        {
            var tick = fixture.Coordinator.TickAsync(CancellationToken.None);
            await fixture.Thermal.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await tick.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.False(thermalGate.IsSet);
            Assert.False(fixture.Coordinator.IsHardwarePoisoned);
            Assert.Equal(writesBeforeTimeout + 1, fixture.Device.Writes.Count);
            Assert.Equal((byte)100, fixture.Device.Writes[^1]);
            Assert.Equal((byte)100, fixture.Device.CurrentPercent);
            Assert.True(fixture.Coordinator.GetStatus().FailSafe.Active);
        }
        finally
        {
            thermalGate.Set();
        }
    }

    [Fact]
    public async Task FutureThermalTimestamp_IsNeverReportedHealthy()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new AdjustableTimeProvider(now);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedTimeProvider: time);
        fixture.Thermal.CapturedAt = now.AddMinutes(1);

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.False(fixture.Coordinator.GetStatus().Thermal.Healthy);
    }

    [Fact]
    public async Task DuplicateCollections_FailClosedWithoutOpeningEitherDevice()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Locator.Identities = [SupportedIdentity, SupportedIdentity];

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
        var status = fixture.Coordinator.GetStatus();
        Assert.Equal("DuplicateDevices", status.ServiceState);
        Assert.True(status.FailSafe.Active);
        Assert.Equal(FailSafeReason.UnsupportedDevice, status.FailSafe.Reason);
    }

    [Fact]
    public async Task CommissionedTick_TypedExactOpenFailurePerformsNoFeatureIo()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Locator.OpenStatus = V12DeviceOpenStatus.HandleIdentityMismatch;

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(1, fixture.Locator.OpenCalls);
        Assert.Equal(SupportedIdentity, fixture.Locator.LastExpectedIdentity);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal("UnsupportedDevice", fixture.Coordinator.GetStatus().ServiceState);
        Assert.Equal(FailSafeReason.UnsupportedDevice,
            fixture.Coordinator.GetStatus().FailSafe.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    public async Task Probe_FreshInvalidCpuFailsBeforeAnyHidEnumeration(double? invalidCpu)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        fixture.Thermal.CpuTemperature = invalidCpu;
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-420", "testhost");

        var exception = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.TelemetryStale, exception.Code);
        Assert.Equal(0, fixture.Locator.EnumerateCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task Probe_RequiresTenConsecutiveBackgroundSamples()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            primeCommissioningThermal: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-421", "testhost");

        await fixture.CollectBackgroundThermalSamplesAsync(9);
        var tooEarly = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.TelemetryStale, tooEarly.Code);
        Assert.Equal(0, fixture.Locator.EnumerateCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Empty(fixture.Device.Writes);

        await fixture.CollectBackgroundThermalSamplesAsync(1);
        _ = await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None);

        Assert.Equal(1, fixture.Locator.EnumerateCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task ProbeReadiness_InvalidSampleResetsTheConsecutiveCount()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            primeCommissioningThermal: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-422", "testhost");

        await fixture.CollectBackgroundThermalSamplesAsync(5);
        fixture.Thermal.CpuTemperature = null;
        await fixture.CollectBackgroundThermalSamplesAsync(1);
        fixture.Thermal.CpuTemperature = 70;
        await fixture.CollectBackgroundThermalSamplesAsync(9);

        var tooEarly = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.TelemetryStale, tooEarly.Code);
        Assert.Equal(0, fixture.Locator.EnumerateCalls);

        await fixture.CollectBackgroundThermalSamplesAsync(1);
        _ = await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None);
        Assert.Equal(1, fixture.Locator.EnumerateCalls);
    }

    [Fact]
    public async Task ProbeReadiness_ThermalReadErrorResetsTheConsecutiveCount()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            primeCommissioningThermal: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-423", "testhost");

        await fixture.CollectBackgroundThermalSamplesAsync(5);
        fixture.Thermal.FailReads = true;
        await fixture.CollectBackgroundThermalSamplesAsync(1);
        fixture.Thermal.FailReads = false;
        await fixture.CollectBackgroundThermalSamplesAsync(9);

        var tooEarly = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.TelemetryStale, tooEarly.Code);
        Assert.Equal(0, fixture.Locator.EnumerateCalls);

        await fixture.CollectBackgroundThermalSamplesAsync(1);
        _ = await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None);
        Assert.Equal(1, fixture.Locator.EnumerateCalls);
    }

    [Fact]
    public async Task ProbeReadiness_NewSamplesRemainConsecutiveAcrossUtcRollback()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            primeCommissioningThermal: false,
            suppliedTimeProvider: time);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-4231", "testhost");

        for (var sample = 0; sample < 5; sample++)
        {
            fixture.Thermal.CapturedAt = time.GetUtcNow();
            await fixture.Coordinator.TickAsync(CancellationToken.None);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        time.JumpUtc(TimeSpan.FromDays(-1));
        for (var sample = 0; sample < 5; sample++)
        {
            fixture.Thermal.CapturedAt = time.GetUtcNow();
            await fixture.Coordinator.TickAsync(CancellationToken.None);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        fixture.Thermal.CapturedAt = time.GetUtcNow();
        _ = await fixture.Coordinator.ProbeCommissioningAsync(
            true,
            peer,
            CancellationToken.None);

        Assert.Equal(1, fixture.Locator.EnumerateCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task Probe_IsEnumerationOnlyAndIdempotentPerformsFirstDeviceIo()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-424", "testhost");

        var probe = await fixture.Coordinator.ProbeCommissioningAsync(
            true, peer, CancellationToken.None);

        Assert.Equal(1, fixture.Locator.EnumerateCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Empty(fixture.Device.Writes);
        var probedStatus = fixture.Coordinator.GetStatus();
        Assert.NotNull(probedStatus.Commissioning);
        Assert.True(probedStatus.Commissioning.Active);
        Assert.Equal(ReadSessionId(probe).ToString("D"), probedStatus.Commissioning.SessionId);
        Assert.Equal("Probed", probedStatus.Commissioning.Stage);
        Assert.NotNull(probedStatus.Commissioning.ExpiresAt);

        await fixture.Coordinator.VerifyIdempotentAsync(
            ReadSessionId(probe), true, peer, CancellationToken.None);

        Assert.Equal(2, fixture.Locator.EnumerateCalls);
        Assert.Equal(1, fixture.Locator.OpenCalls);
        Assert.Equal(2, fixture.Device.ReadCalls);
        Assert.Equal([(byte)50], fixture.Device.Writes);
        Assert.Equal("IdempotentVerified", fixture.Coordinator.GetStatus().Commissioning?.Stage);
    }

    [Fact]
    public async Task Idempotent_IdentityDriftFailsBeforeOpenOrFeatureIo()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-425", "testhost");
        var probe = await fixture.Coordinator.ProbeCommissioningAsync(
            true, peer, CancellationToken.None);
        fixture.Locator.Identities =
        [
            SupportedIdentity with { Version = (ushort)(SupportedIdentity.Version + 1) },
        ];

        var exception = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.VerifyIdempotentAsync(
                ReadSessionId(probe), true, peer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.UnsupportedDevice, exception.Code);
        Assert.Equal(2, fixture.Locator.EnumerateCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal("CommissioningAbortedIdentityDrift",
            fixture.Coordinator.GetStatus().ServiceState);
    }

    [Fact]
    public async Task Idempotent_TypedExactOpenFailureInvalidatesSessionWithoutFeatureIo()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-433", "testhost");
        var sessionId = ReadSessionId(
            await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));
        fixture.Locator.OpenStatus = V12DeviceOpenStatus.HandleIdentityMismatch;

        var failure = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.VerifyIdempotentAsync(
                sessionId, true, peer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.UnsupportedDevice, failure.Code);
        Assert.Equal(1, fixture.Locator.OpenCalls);
        Assert.Equal(SupportedIdentity, fixture.Locator.LastExpectedIdentity);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal(0, fixture.Device.DisposeCalls);

        var retry = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.VerifyIdempotentAsync(
                sessionId, true, peer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.InvalidArgument, retry.Code);
    }

    [Fact]
    public async Task Idempotent_ExpiredProbeOnlySessionIsInvalidatedWithoutHidIo()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            suppliedTimeProvider: time);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-427", "testhost");
        var sessionId = ReadSessionId(
            await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));
        time.Advance(ServiceConstants.CommissioningSessionLifetime + TimeSpan.FromSeconds(1));

        var expired = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.VerifyIdempotentAsync(
                sessionId, true, peer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InvalidArgument, expired.Code);
        Assert.Equal("CommissioningExpired", fixture.Coordinator.GetStatus().ServiceState);
        var expiredCommissioning = fixture.Coordinator.GetStatus().Commissioning;
        Assert.NotNull(expiredCommissioning);
        Assert.False(expiredCommissioning.Active);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal(0, fixture.Device.DisposeCalls);

        var retry = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.VerifyIdempotentAsync(
                sessionId, true, peer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.InvalidArgument, retry.Code);
    }

    [Fact]
    public async Task Test_WrongSidCannotExpireAttachedSessionAndOwnerThenCleansItUp()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            suppliedTimeProvider: time);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-431", "testhost");
        var probe = await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None);
        var sessionId = ReadSessionId(probe);
        await fixture.Coordinator.VerifyIdempotentAsync(
            sessionId, true, peer, CancellationToken.None);
        time.Advance(ServiceConstants.CommissioningSessionLifetime + TimeSpan.FromSeconds(1));
        var writesBeforeUnauthorizedCall = fixture.Device.Writes.ToArray();
        var readsBeforeUnauthorizedCall = fixture.Device.ReadCalls;
        var other = new PipePeerIdentity("S-1-5-21-100-200-300-432", "other");

        var unauthorized = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.TestCommissioningAsync(
                sessionId, true, other, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InvalidArgument, unauthorized.Code);
        Assert.Equal(writesBeforeUnauthorizedCall, fixture.Device.Writes);
        Assert.Equal(readsBeforeUnauthorizedCall, fixture.Device.ReadCalls);
        Assert.Equal(0, fixture.Device.DisposeCalls);
        using (var blockedLease = new SingleWriterLease(fixture.Paths.WriterLeasePath))
        {
            Assert.False(blockedLease.TryAcquire());
        }

        var expired = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.TestCommissioningAsync(
                sessionId, true, peer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InvalidArgument, expired.Code);
        Assert.Equal((byte)100, fixture.Device.Writes[^1]);
        Assert.Equal(1, fixture.Device.DisposeCalls);
        Assert.Equal("CommissioningExpired", fixture.Coordinator.GetStatus().ServiceState);
        using var replacementLease = new SingleWriterLease(fixture.Paths.WriterLeasePath);
        Assert.True(replacementLease.TryAcquire());
    }

    [Fact]
    public async Task CommissioningStateMismatch_BlocksHidWithoutARecoveryJournal()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            runtimeCommissioned: true);

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.True(fixture.Coordinator.GetStatus().Commissioned);
        Assert.Equal("ConfigurationInvalid", fixture.Coordinator.GetStatus().ServiceState);
        Assert.Equal(FailSafeReason.ConfigurationInvalid, fixture.Coordinator.GetStatus().FailSafe.Reason);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task InvalidConfigurationWithKnownRuntimeOwner_UsesOneHundredPercentOnly()
    {
        var store = new InvalidLoadConfigurationStore();
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedStore: store,
            runtimeCommissioned: true);

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(1, fixture.Locator.OpenCalls);
        Assert.Equal((byte)100, fixture.Device.Writes[^1]);
        Assert.Equal("ConfigurationInvalid", fixture.Coordinator.GetStatus().ServiceState);
        Assert.Equal(FailSafeReason.ConfigurationInvalid,
            fixture.Coordinator.GetStatus().FailSafe.Reason);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task InvalidConfigurationWithPendingJournalBlocksEveryTaskAndHardwarePath(
        bool runtimeCommissioned,
        bool legacySchema)
    {
        var taskManager = new FakeScheduledTaskManager();
        var transition = CreateTransition(
            TakeoverTransitionOperation.Commit,
            legacySchema
                ? TakeoverTransitionStage.Prepared
                : TakeoverTransitionStage.TaskDisableInProgress,
            taskManager.Backup) with
        {
            SchemaVersion = legacySchema ? 1 : TakeoverTransitionJournal.CurrentSchemaVersion,
        };
        var rawTransition = legacySchema
            ? JsonSerializer.Serialize(
                transition,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
            : null;
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: runtimeCommissioned,
            suppliedStore: new InvalidLoadConfigurationStore(),
            runtimeCommissioned: runtimeCommissioned,
            transition: legacySchema ? null : transition,
            suppliedTaskManager: taskManager,
            rawTransitionJournalJson: rawTransition);
        var journalBeforeCalls = await File.ReadAllTextAsync(
            fixture.Paths.TransitionJournalPath,
            CancellationToken.None);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-486", "settings");

        await fixture.Coordinator.TickAsync(CancellationToken.None);
        var probe = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));
        var idempotent = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.VerifyIdempotentAsync(
                Guid.NewGuid(), true, peer, CancellationToken.None));
        var rollback = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.RollbackAsync(
                startMythCool: false,
                installer: false,
                peer,
                CancellationToken.None));
        var recovery = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.RecoverTaskTransitionAsync(true, peer, CancellationToken.None));
        var mutation = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.SetModeAsync(ControlMode.Manual, peer, CancellationToken.None));

        Assert.All(
            new[] { probe, idempotent, rollback, recovery, mutation },
            exception => Assert.Equal(PipeErrorCodes.InternalError, exception.Code));
        Assert.Equal("ConfigurationInvalid", fixture.Coordinator.GetStatus().ServiceState);
        Assert.Equal(
            journalBeforeCalls,
            await File.ReadAllTextAsync(fixture.Paths.TransitionJournalPath, CancellationToken.None));
        Assert.Equal(0, taskManager.BackupCalls);
        Assert.Equal(0, taskManager.GetEnabledCalls);
        Assert.Equal(0, taskManager.DisableCalls);
        Assert.Equal(0, taskManager.RestoreCalls);
        Assert.Equal(0, taskManager.VerifiedBackupReadCalls);
        Assert.Equal(0, fixture.Locator.EnumerateCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task SuccessfulCommissioning_DisablesTaskOnlyAfterSafeHardwareStages()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        fixture.Thermal.CpuTemperature = 50;
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-400", "testhost");

        var probe = await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None);
        var sessionId = ReadSessionId(probe);
        await fixture.Coordinator.VerifyIdempotentAsync(sessionId, true, peer, CancellationToken.None);
        await fixture.Coordinator.TestCommissioningAsync(sessionId, true, peer, CancellationToken.None);
        Assert.Equal("TestVerified", fixture.Coordinator.GetStatus().Commissioning?.Stage);
        await fixture.Coordinator.CommitCommissioningAsync(sessionId, true, peer, CancellationToken.None);

        Assert.Equal(1, fixture.TaskManager.BackupCalls);
        Assert.Equal(1, fixture.TaskManager.DisableCalls);
        Assert.Equal(0, fixture.TaskManager.RestoreCalls);
        Assert.True(fixture.Coordinator.GetStatus().Commissioned);
        Assert.False(fixture.Coordinator.GetStatus().Commissioning?.Active ?? true);
        Assert.Equal(100, fixture.Device.Writes[^1]);
        Assert.Equal(0, fixture.Coordinator.RetainedAbandonedOpenDeviceCount);
        var persisted = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.True(persisted.Commissioned);
        Assert.Null(await fixture.TransitionJournalStore.LoadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(89)]
    public async Task CommissioningTest_WarmNoLoweringPathQualifiesAfterConfirmedOneHundred(double temperature)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-498", "testhost");
        var probe = await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None);
        var sessionId = ReadSessionId(probe);
        await fixture.Coordinator.VerifyIdempotentAsync(sessionId, true, peer, CancellationToken.None);
        fixture.Thermal.CpuTemperature = temperature;
        var writesBeforeHighTemperatureTest = fixture.Device.Writes.Count;

        var result = JsonSerializer.SerializeToElement(await fixture.Coordinator.TestCommissioningAsync(
            sessionId, true, peer, CancellationToken.None));
        Assert.True(result.GetProperty("verified").GetBoolean());
        Assert.True(result.GetProperty("lowerSpeedStagesSkipped").GetBoolean());
        Assert.Equal([(byte)100], fixture.Device.Writes.Skip(writesBeforeHighTemperatureTest));
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        var waiting = fixture.Coordinator.GetStatus();
        Assert.Equal("CommissioningTestVerified", waiting.ServiceState);
        Assert.True(waiting.Commissioning?.Active);
        Assert.Equal(sessionId.ToString("D"), waiting.Commissioning?.SessionId);
        Assert.Equal("TestVerified", waiting.Commissioning?.Stage);
        Assert.Equal(0, fixture.TaskManager.BackupCalls);
        Assert.Equal(0, fixture.TaskManager.DisableCalls);
        await fixture.Coordinator.CommitCommissioningAsync(sessionId, true, peer, CancellationToken.None);
        Assert.True(fixture.Coordinator.GetStatus().Commissioned);
        Assert.Equal(1, fixture.TaskManager.DisableCalls);
        Assert.All(fixture.Device.Writes.Skip(writesBeforeHighTemperatureTest), value => Assert.Equal((byte)100, value));
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
    }

    [Theory]
    [InlineData(90.0)]
    [InlineData(null)]
    public async Task WarmCommissioningCannotCommitAfterEmergencyOrMissingTemperature(double? temperature)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-498", "testhost");
        var sessionId = ReadSessionId(await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));
        await fixture.Coordinator.VerifyIdempotentAsync(sessionId, true, peer, CancellationToken.None);
        fixture.Thermal.CpuTemperature = 80;
        await fixture.Coordinator.TestCommissioningAsync(sessionId, true, peer, CancellationToken.None);
        fixture.Thermal.CpuTemperature = temperature;
        await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.CommitCommissioningAsync(sessionId, true, peer, CancellationToken.None));
        Assert.False(fixture.Coordinator.GetStatus().Commissioned);
        Assert.Equal(0, fixture.TaskManager.DisableCalls);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(85)]
    public async Task NearFullTest_RestoresOneHundredAndNeverQualifiesForCommit(double temperature)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-498", "testhost");
        var sessionId = ReadSessionId(await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));
        await fixture.Coordinator.VerifyIdempotentAsync(sessionId, true, peer, CancellationToken.None);
        Assert.NotNull(fixture.Coordinator.GetStatus().LastSuccessfulWrite);
        fixture.Thermal.CpuTemperature = temperature;
        var before = fixture.Device.Writes.Count;
        var result = JsonSerializer.SerializeToElement(await fixture.Coordinator.TestCommissioningAsync(
            sessionId, true, peer, CancellationToken.None, nearFullOnly: true));
        Assert.Equal([(byte)99, (byte)100], fixture.Device.Writes.Skip(before));
        Assert.True(result.GetProperty("nearFullTestPassed").GetBoolean());
        Assert.False(result.GetProperty("verified").GetBoolean());
        Assert.Equal(100, result.GetProperty("retainedPercent").GetInt32());
        Assert.Equal("IdempotentVerified", fixture.Coordinator.GetStatus().Commissioning?.Stage);
        var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.CommitCommissioningAsync(sessionId, true, peer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.InvalidArgument, rejected.Code);
        Assert.Equal(0, fixture.TaskManager.DisableCalls);
        Assert.NotNull(fixture.Coordinator.GetStatus().LastSuccessfulWrite);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(100)]
    public async Task NearFullTest_StillRejectsEmergencyTemperature(double temperature)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-498", "testhost");
        var sessionId = ReadSessionId(await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));
        await fixture.Coordinator.VerifyIdempotentAsync(sessionId, true, peer, CancellationToken.None);
        fixture.Thermal.CpuTemperature = temperature;
        var before = fixture.Device.Writes.Count;
        var error = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.TestCommissioningAsync(sessionId, true, peer, CancellationToken.None, nearFullOnly: true));
        Assert.Equal(PipeErrorCodes.FailSafeActive, error.Code);
        Assert.DoesNotContain((byte)99, fixture.Device.Writes.Skip(before));
        Assert.Equal((byte)100, fixture.Device.Writes[^1]);
    }

    [Fact]
    public async Task NearFullTest_CancellationAfter99Restores100BeforeReturning()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-498", "testhost");
        var sessionId = ReadSessionId(await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));
        await fixture.Coordinator.VerifyIdempotentAsync(sessionId, true, peer, CancellationToken.None);
        fixture.Thermal.CpuTemperature = 85;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Coordinator.TestCommissioningAsync(sessionId, true, peer, cancellation.Token, nearFullOnly: true));
        Assert.Contains((byte)99, fixture.Device.Writes);
        Assert.Equal((byte)100, fixture.Device.Writes[^1]);
        Assert.False(fixture.Coordinator.GetStatus().Commissioning!.Active);
    }

    [Fact]
    public async Task CommitRecovery_PreparedBeforeDisable_AbandonsSafeOriginalStateWithoutHid()
    {
        var taskManager = new FakeScheduledTaskManager { Enabled = true };
        var transition = CreateTransition(
            TakeoverTransitionOperation.Commit,
            TakeoverTransitionStage.Prepared,
            taskManager.Backup);

        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            transition: transition,
            suppliedTaskManager: taskManager);

        Assert.False(fixture.Coordinator.GetStatus().Commissioned);
        Assert.Null(await fixture.TransitionJournalStore.LoadAsync());
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal(0, taskManager.RestoreCalls);
    }

    [Theory]
    [InlineData(1, false, false)] // TaskDisabled.
    [InlineData(2, false, true)]  // RuntimeStatePersisted.
    [InlineData(3, true, true)]   // ConfigurationPersisted.
    public async Task CommitRecovery_AfterDisable_CompletesBothStatesAndDeletesJournalWithoutHid(
        int stageValue,
        bool configurationCommissioned,
        bool runtimeCommissioned)
    {
        var stage = (TakeoverTransitionStage)stageValue;
        var taskManager = new FakeScheduledTaskManager { Enabled = false };
        var transition = CreateTransition(TakeoverTransitionOperation.Commit, stage, taskManager.Backup);

        await using var fixture = await CoordinatorFixture.CreateAsync(
            configurationCommissioned,
            runtimeCommissioned: runtimeCommissioned,
            transition: transition,
            suppliedTaskManager: taskManager);

        Assert.True((await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).Commissioned);
        Assert.True((await fixture.RuntimeStateStore.LoadAsync(CancellationToken.None)).Commissioned);
        Assert.True(fixture.Coordinator.GetStatus().Commissioned);
        Assert.Null(await fixture.TransitionJournalStore.LoadAsync());
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task CommitRecovery_TaskDisabledStageCompletesWhenTaskWasOriginallyDisabled()
    {
        var taskManager = new FakeScheduledTaskManager
        {
            Enabled = false,
            Backup = new ScheduledTaskBackupMetadata(
                ServiceConstants.MythCoolTaskName,
                OriginallyEnabled: false,
                new string('B', 64),
                DateTimeOffset.UtcNow),
        };
        var transition = CreateTransition(
            TakeoverTransitionOperation.Commit,
            TakeoverTransitionStage.TaskDisabled,
            taskManager.Backup);

        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            transition: transition,
            suppliedTaskManager: taskManager);

        Assert.True(fixture.Coordinator.GetStatus().Commissioned);
        Assert.Null(await fixture.TransitionJournalStore.LoadAsync());
        Assert.Equal(0, fixture.Locator.OpenCalls);
    }

    [Theory]
    [InlineData(0, 6)] // Commit / TaskDisableInProgress.
    [InlineData(1, 7)] // Rollback / TaskRestoreInProgress.
    public async Task Recovery_InProgressTaskMutationHardBlocksWithoutTaskOrHidAccess(
        int operationValue,
        int stageValue)
    {
        var taskManager = new FakeScheduledTaskManager();
        var transition = CreateTransition(
            (TakeoverTransitionOperation)operationValue,
            (TakeoverTransitionStage)stageValue,
            taskManager.Backup);

        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            transition: transition,
            suppliedTaskManager: taskManager);
        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal("ConfigurationInvalid", fixture.Coordinator.GetStatus().ServiceState);
        Assert.Equal(0, taskManager.GetEnabledCalls);
        Assert.Equal(0, taskManager.DisableCalls);
        Assert.Equal(0, taskManager.RestoreCalls);
        Assert.Equal(0, taskManager.VerifiedBackupReadCalls);
        Assert.Equal(0, fixture.Locator.EnumerateCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal((TakeoverTransitionStage)stageValue,
            (await fixture.TransitionJournalStore.LoadAsync())!.Stage);
    }

    [Fact]
    public async Task AttendedRecovery_UncertainDisableWithExactOriginalTaskAbandonsCommit()
    {
        var taskManager = new FakeScheduledTaskManager { Enabled = true };
        var transition = CreateTransition(
            TakeoverTransitionOperation.Commit,
            TakeoverTransitionStage.TaskDisableInProgress,
            taskManager.Backup);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            transition: transition,
            suppliedTaskManager: taskManager);

        var result = JsonSerializer.SerializeToElement(
            await fixture.Coordinator.RecoverTaskTransitionAsync(
                attended: true,
                new PipePeerIdentity("S-1-5-21-100-200-300-501", "settings"),
                CancellationToken.None));

        Assert.Equal("commitAbandonedOriginalState", result.GetProperty("action").GetString());
        Assert.False(result.GetProperty("commissioned").GetBoolean());
        Assert.Null(await fixture.TransitionJournalStore.LoadAsync());
        Assert.Equal(3, taskManager.RecoveryInspectionCalls);
        Assert.Equal(0, taskManager.DisableCalls);
        Assert.Equal(0, taskManager.RestoreCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
        var auditLine = Assert.Single(await File.ReadAllLinesAsync(
            fixture.Paths.AuditLogPath,
            CancellationToken.None));
        using var audit = JsonDocument.Parse(auditLine);
        Assert.Equal("commission.commit",
            audit.RootElement.GetProperty("eventName").GetString());
        Assert.Equal("not-applied", audit.RootElement.GetProperty("result").GetString());
        var details = audit.RootElement.GetProperty("details");
        Assert.Equal("CommitAbandonedOriginalState", details.GetProperty("action").GetString());
        var current = details.GetProperty("current");
        Assert.Equal(taskManager.Backup.XmlSha256,
            current.GetProperty("currentTaskXmlSha256").GetString());
        Assert.True(current.GetProperty("exactBackupXmlMatch").GetBoolean());
        Assert.Equal("S-1-5-21-100-200-300-501",
            details.GetProperty("resolvedByCallerSid").GetString());
    }

    [Theory]
    [InlineData(false, "commitCompleted", true)]
    [InlineData(true, "rollbackCompleted", false)]
    public async Task AttendedRecovery_ConfirmedMutationCompletesOnlyLocalJournalAndState(
        bool restoring,
        string expectedAction,
        bool expectedCommissioned)
    {
        var taskManager = new FakeScheduledTaskManager { Enabled = restoring };
        var transition = CreateTransition(
            restoring ? TakeoverTransitionOperation.Rollback : TakeoverTransitionOperation.Commit,
            restoring
                ? TakeoverTransitionStage.TaskRestoreInProgress
                : TakeoverTransitionStage.TaskDisableInProgress,
            taskManager.Backup,
            startMythCool: restoring);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            runtimeCommissioned: false,
            transition: transition,
            suppliedTaskManager: taskManager);

        var result = JsonSerializer.SerializeToElement(
            await fixture.Coordinator.RecoverTaskTransitionAsync(
                attended: true,
                new PipePeerIdentity("S-1-5-21-100-200-300-502", "v12ctl"),
                CancellationToken.None));

        Assert.Equal(expectedAction, result.GetProperty("action").GetString());
        Assert.Equal(expectedCommissioned, result.GetProperty("commissioned").GetBoolean());
        Assert.False(result.GetProperty("startedMythCool").GetBoolean());
        Assert.Equal(expectedCommissioned,
            (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).Commissioned);
        Assert.Equal(expectedCommissioned,
            (await fixture.RuntimeStateStore.LoadAsync(CancellationToken.None)).Commissioned);
        Assert.Null(await fixture.TransitionJournalStore.LoadAsync());
        Assert.Equal(4, taskManager.RecoveryInspectionCalls);
        Assert.Equal(0, taskManager.DisableCalls);
        Assert.Equal(0, taskManager.RestoreCalls);
        Assert.Empty(taskManager.RestoreStartRequests);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task AttendedRecovery_UncertainRestoreStillDisabledCompensatesToControllerOwnership()
    {
        var taskManager = new FakeScheduledTaskManager { Enabled = false };
        var transition = CreateTransition(
            TakeoverTransitionOperation.Rollback,
            TakeoverTransitionStage.TaskRestoreInProgress,
            taskManager.Backup,
            startMythCool: true);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            transition: transition,
            suppliedTaskManager: taskManager);

        var result = JsonSerializer.SerializeToElement(
            await fixture.Coordinator.RecoverTaskTransitionAsync(
                attended: true,
                new PipePeerIdentity("S-1-5-21-100-200-300-503", "settings"),
                CancellationToken.None));

        Assert.Equal("rollbackCompensatedToController", result.GetProperty("action").GetString());
        Assert.True(result.GetProperty("commissioned").GetBoolean());
        Assert.True((await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).Commissioned);
        Assert.True((await fixture.RuntimeStateStore.LoadAsync(CancellationToken.None)).Commissioned);
        Assert.Null(await fixture.TransitionJournalStore.LoadAsync());
        Assert.Equal(4, taskManager.RecoveryInspectionCalls);
        Assert.Equal(0, taskManager.DisableCalls);
        Assert.Equal(0, taskManager.RestoreCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task AttendedRecovery_DefinitionDriftRefusesAndPreservesJournalWithoutMutation()
    {
        var taskManager = new FakeScheduledTaskManager
        {
            Enabled = false,
            RecoveryInspection = new ScheduledTaskRecoveryInspection(
                Enabled: false,
                CurrentXmlSha256: new string('D', 64),
                ExactBackupXmlMatch: false,
                DefinitionMatchesBackup: false),
        };
        var transition = CreateTransition(
            TakeoverTransitionOperation.Commit,
            TakeoverTransitionStage.TaskDisableInProgress,
            taskManager.Backup);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            transition: transition,
            suppliedTaskManager: taskManager);

        var exception = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.RecoverTaskTransitionAsync(
                attended: true,
                new PipePeerIdentity("S-1-5-21-100-200-300-504", "v12ctl"),
                CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InternalError, exception.Code);
        Assert.Equal(TakeoverTransitionStage.TaskDisableInProgress,
            (await fixture.TransitionJournalStore.LoadAsync())!.Stage);
        Assert.Equal(1, taskManager.RecoveryInspectionCalls);
        Assert.Equal(0, taskManager.DisableCalls);
        Assert.Equal(0, taskManager.RestoreCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task AttendedRecovery_RestoreCompensationRefusesWhenVendorPresenceIsNotProvenAbsent()
    {
        var taskManager = new FakeScheduledTaskManager { Enabled = false };
        var transition = CreateTransition(
            TakeoverTransitionOperation.Rollback,
            TakeoverTransitionStage.TaskRestoreInProgress,
            taskManager.Backup);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            transition: transition,
            suppliedTaskManager: taskManager);
        fixture.Conflict.InspectionFailed = true;

        var exception = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.RecoverTaskTransitionAsync(
                attended: true,
                new PipePeerIdentity("S-1-5-21-100-200-300-505", "settings"),
                CancellationToken.None));

        Assert.Equal(PipeErrorCodes.MythCoolConflict, exception.Code);
        Assert.Equal(TakeoverTransitionStage.TaskRestoreInProgress,
            (await fixture.TransitionJournalStore.LoadAsync())!.Stage);
        Assert.Equal(1, taskManager.RecoveryInspectionCalls);
        Assert.Equal(0, taskManager.RestoreCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task AttendedRecovery_RequiresAttendedFreshProcessAndExactDecommissionedState()
    {
        var taskManager = new FakeScheduledTaskManager { Enabled = false };
        var transition = CreateTransition(
            TakeoverTransitionOperation.Commit,
            TakeoverTransitionStage.TaskDisableInProgress,
            taskManager.Backup);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            runtimeCommissioned: true,
            transition: transition,
            suppliedTaskManager: taskManager);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-506", "settings");

        var unattended = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.RecoverTaskTransitionAsync(false, peer, CancellationToken.None));
        var mismatched = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.RecoverTaskTransitionAsync(true, peer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InvalidArgument, unattended.Code);
        Assert.Equal(PipeErrorCodes.InternalError, mismatched.Code);
        Assert.Equal(0, taskManager.RecoveryInspectionCalls);
        Assert.Equal(TakeoverTransitionStage.TaskDisableInProgress,
            (await fixture.TransitionJournalStore.LoadAsync())!.Stage);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task AttendedRecovery_QueryFailureAndWrongJournalStageBothPreserveJournal()
    {
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-507", "v12ctl");
        var taskManager = new FakeScheduledTaskManager
        {
            Enabled = false,
            RecoveryInspectionFailure = new InvalidDataException("Injected missing task query."),
        };
        var inProgress = CreateTransition(
            TakeoverTransitionOperation.Commit,
            TakeoverTransitionStage.TaskDisableInProgress,
            taskManager.Backup);
        await using (var fixture = await CoordinatorFixture.CreateAsync(
                         commissioned: false,
                         transition: inProgress,
                         suppliedTaskManager: taskManager))
        {
            var exception = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                fixture.Coordinator.RecoverTaskTransitionAsync(true, peer, CancellationToken.None));

            Assert.Equal(PipeErrorCodes.InternalError, exception.Code);
            Assert.Equal(TakeoverTransitionStage.TaskDisableInProgress,
                (await fixture.TransitionJournalStore.LoadAsync())!.Stage);
            Assert.Equal(1, taskManager.RecoveryInspectionCalls);
            Assert.Equal(0, taskManager.DisableCalls);
            Assert.Equal(0, taskManager.RestoreCalls);
            Assert.Empty(fixture.Device.Writes);
        }

        var wrongStageManager = new FakeScheduledTaskManager { Enabled = false };
        var wrongStage = CreateTransition(
            TakeoverTransitionOperation.Commit,
            TakeoverTransitionStage.TaskDisabled,
            wrongStageManager.Backup);
        await using var wrongStageFixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            runtimeCommissioned: true,
            transition: wrongStage,
            suppliedTaskManager: wrongStageManager);
        // Automatic determinate recovery consumes TaskDisabled, so recreate it only to prove
        // the public attended operation cannot act as a generic journal acknowledgement.
        await wrongStageFixture.TransitionJournalStore.SaveAsync(wrongStage, CancellationToken.None);

        var wrongStageException = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            wrongStageFixture.Coordinator.RecoverTaskTransitionAsync(true, peer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InvalidArgument, wrongStageException.Code);
        Assert.Equal(TakeoverTransitionStage.TaskDisabled,
            (await wrongStageFixture.TransitionJournalStore.LoadAsync())!.Stage);
        Assert.Equal(0, wrongStageManager.DisableCalls);
        Assert.Equal(0, wrongStageManager.RestoreCalls);
    }

    [Fact]
    public async Task Recovery_LegacySchemaOneJournalHardBlocksWithoutTaskOrHidAccess()
    {
        var taskManager = new FakeScheduledTaskManager();
        var legacy = CreateTransition(
            TakeoverTransitionOperation.Commit,
            TakeoverTransitionStage.Prepared,
            taskManager.Backup) with
        {
            SchemaVersion = 1,
        };
        var raw = JsonSerializer.Serialize(
            legacy,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            suppliedTaskManager: taskManager,
            rawTransitionJournalJson: raw);
        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal("ConfigurationInvalid", fixture.Coordinator.GetStatus().ServiceState);
        Assert.True(File.Exists(fixture.Paths.TransitionJournalPath));
        Assert.Equal(0, taskManager.GetEnabledCalls);
        Assert.Equal(0, taskManager.DisableCalls);
        Assert.Equal(0, taskManager.RestoreCalls);
        Assert.Equal(0, taskManager.VerifiedBackupReadCalls);
        Assert.Equal(0, fixture.Locator.EnumerateCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Theory]
    [InlineData(0, true, true, false, 1, 2)] // Prepared plus terminal audit readback.
    [InlineData(4, false, false, false, 1, 2)] // ControllerStateDecommissioned plus audit readback.
    [InlineData(5, false, false, true, 0, 2)] // TaskRestored: verify twice, never replay.
    public async Task RollbackRecovery_CompletesAndNeverReplaysStartRequestOrTouchesHid(
        int stageValue,
        bool configurationCommissioned,
        bool runtimeCommissioned,
        bool taskEnabled,
        int expectedRestoreCalls,
        int expectedRecoveryInspectionCalls)
    {
        var stage = (TakeoverTransitionStage)stageValue;
        var taskManager = new FakeScheduledTaskManager { Enabled = taskEnabled };
        var transition = CreateTransition(
            TakeoverTransitionOperation.Rollback,
            stage,
            taskManager.Backup,
            startMythCool: true);

        await using var fixture = await CoordinatorFixture.CreateAsync(
            configurationCommissioned,
            runtimeCommissioned: runtimeCommissioned,
            transition: transition,
            suppliedTaskManager: taskManager);

        Assert.False((await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).Commissioned);
        Assert.False((await fixture.RuntimeStateStore.LoadAsync(CancellationToken.None)).Commissioned);
        Assert.False(fixture.Coordinator.GetStatus().Commissioned);
        Assert.Null(await fixture.TransitionJournalStore.LoadAsync());
        Assert.Equal(expectedRestoreCalls, taskManager.RestoreCalls);
        Assert.Equal(expectedRecoveryInspectionCalls, taskManager.RecoveryInspectionCalls);
        Assert.Equal(expectedRestoreCalls, taskManager.RestoreStartRequests.Count);
        Assert.All(taskManager.RestoreStartRequests, static startRequested => Assert.False(startRequested));
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task TaskRestoredRecovery_PersistsBothDecommissionedStatesBeforeDeletingJournal()
    {
        var taskManager = new FakeScheduledTaskManager { Enabled = true };
        var transition = CreateTransition(
            TakeoverTransitionOperation.Rollback,
            TakeoverTransitionStage.TaskRestored,
            taskManager.Backup);

        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            runtimeCommissioned: true,
            transition: transition,
            suppliedTaskManager: taskManager);

        Assert.False((await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).Commissioned);
        Assert.False((await fixture.RuntimeStateStore.LoadAsync(CancellationToken.None)).Commissioned);
        Assert.Null(await fixture.TransitionJournalStore.LoadAsync(CancellationToken.None));
        Assert.Equal(2, taskManager.RecoveryInspectionCalls);
        Assert.Equal(0, taskManager.RestoreCalls);
        Assert.Equal(0, fixture.Locator.EnumerateCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task TaskRestoredRecovery_ReadbackMismatchRetainsJournalAndHardBlocksWithoutMutationOrHid()
    {
        var taskManager = new FakeScheduledTaskManager { Enabled = false };
        var transition = CreateTransition(
            TakeoverTransitionOperation.Rollback,
            TakeoverTransitionStage.TaskRestored,
            taskManager.Backup);

        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            transition: transition,
            suppliedTaskManager: taskManager);
        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal("ConfigurationInvalid", fixture.Coordinator.GetStatus().ServiceState);
        Assert.Equal(1, taskManager.RecoveryInspectionCalls);
        Assert.Equal(0, taskManager.RestoreCalls);
        Assert.NotNull(await fixture.TransitionJournalStore.LoadAsync(CancellationToken.None));
        Assert.Equal(0, fixture.Locator.EnumerateCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task RecoveryWithoutMatchingBackup_StaysConfigurationInvalidAndDoesNotTouchHid()
    {
        var taskManager = new FakeScheduledTaskManager
        {
            Enabled = false,
            HasVerifiedBackup = false,
        };
        var transition = CreateTransition(
            TakeoverTransitionOperation.Commit,
            TakeoverTransitionStage.TaskDisabled,
            taskManager.Backup);

        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            transition: transition,
            suppliedTaskManager: taskManager);
        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal("ConfigurationInvalid", fixture.Coordinator.GetStatus().ServiceState);
        Assert.Equal(FailSafeReason.ConfigurationInvalid, fixture.Coordinator.GetStatus().FailSafe.Reason);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
        Assert.NotNull(await fixture.TransitionJournalStore.LoadAsync());
    }

    [Fact]
    public async Task FailedRollbackRecovery_CannotBeReportedAsIdempotentSuccess()
    {
        var taskManager = new FakeScheduledTaskManager
        {
            Enabled = false,
            FailRestore = true,
        };
        var transition = CreateTransition(
            TakeoverTransitionOperation.Rollback,
            TakeoverTransitionStage.ControllerStateDecommissioned,
            taskManager.Backup);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            transition: transition,
            suppliedTaskManager: taskManager);

        var exception = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.RollbackAsync(
                startMythCool: false,
                installer: true,
                new PipePeerIdentity("S-1-5-18", "installer"),
                CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InternalError, exception.Code);
        Assert.Equal(1, taskManager.RestoreCalls);
        Assert.NotNull(await fixture.TransitionJournalStore.LoadAsync());
        Assert.Equal(0, fixture.Locator.OpenCalls);
    }

    [Fact]
    public async Task RollbackOfCommissionedController_RequiresBackupBeforeChangingEitherState()
    {
        var taskManager = new FakeScheduledTaskManager { HasVerifiedBackup = false, Enabled = false };
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedTaskManager: taskManager);

        var exception = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.RollbackAsync(
                startMythCool: false,
                installer: false,
                new PipePeerIdentity("S-1-5-21-test", "testhost"),
                CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InvalidArgument, exception.Code);
        Assert.True((await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).Commissioned);
        Assert.True((await fixture.RuntimeStateStore.LoadAsync(CancellationToken.None)).Commissioned);
        Assert.Null(await fixture.TransitionJournalStore.LoadAsync());
        Assert.Equal(0, taskManager.RestoreCalls);
    }

    [Fact]
    public async Task RollbackWhenNeverCommissioned_IsIdempotentWithoutBackupOrTaskMutation()
    {
        var taskManager = new FakeScheduledTaskManager { HasVerifiedBackup = false };
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            suppliedTaskManager: taskManager);

        var result = JsonSerializer.SerializeToElement(await fixture.Coordinator.RollbackAsync(
            startMythCool: false,
            installer: true,
            new PipePeerIdentity("S-1-5-18", "installer"),
            CancellationToken.None));

        Assert.False(result.GetProperty("commissioned").GetBoolean());
        Assert.False(result.GetProperty("restoredTask").GetBoolean());
        Assert.Equal(0, taskManager.RestoreCalls);
        Assert.Equal(0, taskManager.BackupCalls);
        Assert.Null(await fixture.TransitionJournalStore.LoadAsync());
    }

    [Fact]
    public async Task CommitPersistenceFailure_RestoresTaskOnlyAfterConfigFalseIsDurable()
    {
        var store = new FailingCommitConfigurationStore(failCompensation: false);
        await using var fixture = await CoordinatorFixture.CreateAsync(false, store);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-401", "testhost");
        var sessionId = await RunToCommitAsync(fixture, peer);

        var exception = await Assert.ThrowsAsync<ControllerOperationException>(
            () => fixture.Coordinator.CommitCommissioningAsync(sessionId, true, peer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InternalError, exception.Code);
        Assert.Equal(1, fixture.TaskManager.DisableCalls);
        Assert.Equal(1, fixture.TaskManager.RestoreCalls);
        Assert.False(store.Current.Commissioned);
    }

    [Fact]
    public async Task CommitUncertainDisableRetainsJournalDeviceAndLeaseWithoutTaskCompensation()
    {
        var previousExitCode = Environment.ExitCode;
        try
        {
            var taskManager = new FakeScheduledTaskManager
            {
                DisableFailure = new ScheduledTaskCommandOwnershipUncertainException(
                    "Injected unconfirmed schtasks termination.",
                    new TimeoutException()),
            };
            await using var fixture = await CoordinatorFixture.CreateAsync(
                commissioned: false,
                suppliedTaskManager: taskManager);
            var peer = new PipePeerIdentity("S-1-5-21-100-200-300-482", "settings");
            var sessionId = await RunToCommitAsync(fixture, peer);

            var failure = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                fixture.Coordinator.CommitCommissioningAsync(
                    sessionId, true, peer, CancellationToken.None));

            Assert.Equal(PipeErrorCodes.InternalError, failure.Code);
            Assert.Equal(1, taskManager.DisableCalls);
            Assert.Equal(0, taskManager.RestoreCalls);
            var retainedTransition = await fixture.TransitionJournalStore.LoadAsync();
            Assert.NotNull(retainedTransition);
            Assert.Equal(
                TakeoverTransitionStage.TaskDisableInProgress,
                retainedTransition.Stage);
            Assert.Equal(0, fixture.Device.DisposeCalls);
            Assert.True(fixture.WriterLease.IsHeld);
            Assert.Equal((byte)100, fixture.Device.Writes[^1]);
            var status = fixture.Coordinator.GetStatus();
            Assert.True(status.HardwareSafety.Poisoned);
            Assert.Equal("TASK_COMMAND_OUTCOME_UNCERTAIN", status.HardwareSafety.Reason);
            Assert.False(status.Commissioned);
            Assert.False(status.Device.Connected);
            Assert.Equal(7, Environment.ExitCode);

            var recovery = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                fixture.Coordinator.RecoverTaskTransitionAsync(
                    attended: true,
                    peer,
                    CancellationToken.None));
            Assert.Equal(PipeErrorCodes.InternalError, recovery.Code);
            Assert.Equal(0, taskManager.RecoveryInspectionCalls);

            var counts = fixture.CaptureHardwareCounts();
            await fixture.Coordinator.TickAsync(CancellationToken.None);
            Assert.Equal(counts, fixture.CaptureHardwareCounts());
            Assert.Equal(0, taskManager.RestoreCalls);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task CommitCancellationAfterDisableStartsFinishesDurableTransactionWithoutCompensation()
    {
        var disableGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var taskManager = new FakeScheduledTaskManager
        {
            DisableGate = disableGate,
        };
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            suppliedTaskManager: taskManager);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-483", "settings");
        var sessionId = await RunToCommitAsync(fixture, peer);
        using var cancellation = new CancellationTokenSource();

        var commit = fixture.Coordinator.CommitCommissioningAsync(
            sessionId, true, peer, cancellation.Token);
        await taskManager.DisableStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        Assert.False(commit.IsCompleted);
        disableGate.TrySetResult(true);
        await commit;

        Assert.Equal(1, taskManager.DisableCalls);
        Assert.Equal(0, taskManager.RestoreCalls);
        Assert.Equal(0, fixture.Device.DisposeCalls);
        Assert.True(fixture.WriterLease.IsHeld);
        Assert.Null(await fixture.TransitionJournalStore.LoadAsync());
        Assert.False(fixture.Coordinator.IsHardwarePoisoned);
        Assert.True(fixture.Coordinator.GetStatus().Commissioned);
    }

    [Fact]
    public async Task RollbackCancellationAfterRestoreStartsFinishesDurableTransaction()
    {
        var previousExitCode = Environment.ExitCode;
        try
        {
            var restoreGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var taskManager = new FakeScheduledTaskManager
            {
                Enabled = false,
                RestoreGate = restoreGate,
            };
            using var cancellation = new CancellationTokenSource();

            await using (var fixture = await CoordinatorFixture.CreateAsync(
                             commissioned: true,
                             suppliedTaskManager: taskManager))
            {
                var rollback = fixture.Coordinator.RollbackAsync(
                    startMythCool: false,
                    installer: false,
                    new PipePeerIdentity("S-1-5-21-100-200-300-484", "settings"),
                    cancellation.Token);
                await taskManager.RestoreStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

                cancellation.Cancel();
                Assert.False(rollback.IsCompleted);
                restoreGate.TrySetResult(true);
                await rollback;

                Assert.Null(await fixture.TransitionJournalStore.LoadAsync(CancellationToken.None));
                Assert.Equal(1, taskManager.RestoreCalls);
                Assert.Equal(0, taskManager.GetEnabledCalls);
                Assert.False(fixture.Coordinator.GetStatus().Commissioned);
                Assert.Equal("NotCommissioned", fixture.Coordinator.GetStatus().ServiceState);
            }
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task RollbackCancellationBeforeMutationBarrierRetainsDecommissionedStageForSafeRestartRestore()
    {
        var previousExitCode = Environment.ExitCode;
        try
        {
            var taskManager = new FakeScheduledTaskManager { Enabled = false };
            TakeoverTransitionJournal retainedTransition;
            using var disposeGate = new ManualResetEventSlim(initialState: false);
            using var cancellation = new CancellationTokenSource();

            await using (var fixture = await CoordinatorFixture.CreateAsync(
                             commissioned: true,
                             suppliedTaskManager: taskManager))
            {
                await fixture.Coordinator.TickAsync(CancellationToken.None);
                fixture.Device.DisposeGate = disposeGate;

                var rollback = fixture.Coordinator.RollbackAsync(
                    startMythCool: false,
                    installer: false,
                    new PipePeerIdentity("S-1-5-21-100-200-300-485", "settings"),
                    cancellation.Token);
                await fixture.Device.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

                cancellation.Cancel();
                disposeGate.Set();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rollback);

                retainedTransition = Assert.IsType<TakeoverTransitionJournal>(
                    await fixture.TransitionJournalStore.LoadAsync(CancellationToken.None));
                Assert.Equal(
                    TakeoverTransitionStage.ControllerStateDecommissioned,
                    retainedTransition.Stage);
                Assert.Equal(0, taskManager.RestoreCalls);
                var diagnostics = JsonSerializer.SerializeToElement(
                    fixture.Coordinator.GetDiagnostics());
                Assert.Equal(
                    "CONTROLLER_CONFIGURATION_ERROR",
                    diagnostics.GetProperty("configurationErrorCode").GetString());
            }

            await using var recovered = await CoordinatorFixture.CreateAsync(
                commissioned: false,
                runtimeCommissioned: false,
                transition: retainedTransition,
                suppliedTaskManager: taskManager);

            Assert.Equal(1, taskManager.RestoreCalls);
            Assert.Single(taskManager.RestoreStartRequests);
            Assert.False(taskManager.RestoreStartRequests[0]);
            Assert.Null(await recovered.TransitionJournalStore.LoadAsync(CancellationToken.None));
            Assert.Equal(0, recovered.Locator.EnumerateCalls);
            Assert.Equal(0, recovered.Locator.OpenCalls);
            Assert.Equal(0, recovered.Device.ReadCalls);
            Assert.Empty(recovered.Device.Writes);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task CommitCompensationFailure_KeepsMythCoolDisabledAndDeviceAtOneHundred()
    {
        var store = new FailingCommitConfigurationStore(failCompensation: true);
        await using var fixture = await CoordinatorFixture.CreateAsync(false, store);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-402", "testhost");
        var sessionId = await RunToCommitAsync(fixture, peer);

        _ = await Assert.ThrowsAsync<ControllerOperationException>(
            () => fixture.Coordinator.CommitCommissioningAsync(sessionId, true, peer, CancellationToken.None));

        Assert.Equal(1, fixture.TaskManager.DisableCalls);
        Assert.Equal(0, fixture.TaskManager.RestoreCalls);
        Assert.Equal(100, fixture.Device.Writes[^1]);
        var status = fixture.Coordinator.GetStatus();
        Assert.Equal("CommitCompensationFailed", status.ServiceState);
        Assert.True(status.Commissioned);
        Assert.True(status.Device.Connected);
        Assert.True(fixture.WriterLease.IsHeld);
        Assert.False(fixture.TaskManager.Enabled);

        var countsBeforeBlockedTick = fixture.CaptureHardwareCounts();
        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(countsBeforeBlockedTick, fixture.CaptureHardwareCounts());
        Assert.True(fixture.Coordinator.GetStatus().Commissioned);
        Assert.True(fixture.Coordinator.GetStatus().Device.Connected);
    }

    [Fact]
    public async Task CommitRestoreFailure_NeverReassignsDisposedSessionDeviceOrReopensHardware()
    {
        var store = new FailingCommitConfigurationStore(failCompensation: false);
        var taskManager = new FakeScheduledTaskManager
        {
            FailRestore = true,
            ApplyRestoreStateBeforeFailure = true,
        };
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            suppliedStore: store,
            suppliedTaskManager: taskManager);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-403", "testhost");
        var sessionId = await RunToCommitAsync(fixture, peer);

        _ = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.CommitCommissioningAsync(
                sessionId, true, peer, CancellationToken.None));

        Assert.Equal(1, fixture.Device.DisposeCalls);
        Assert.Equal(1, taskManager.RestoreCalls);
        Assert.True(taskManager.Enabled);
        Assert.NotNull(await fixture.TransitionJournalStore.LoadAsync());
        var failedStatus = fixture.Coordinator.GetStatus();
        Assert.False(failedStatus.Commissioned);
        Assert.False(failedStatus.Device.Connected);
        Assert.False(fixture.WriterLease.IsHeld);
        var countsBeforeTick = fixture.CaptureHardwareCounts();

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(countsBeforeTick, fixture.CaptureHardwareCounts());
        Assert.Equal("ConfigurationInvalid", fixture.Coordinator.GetStatus().ServiceState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackRestoreFailure_LatchesRecoveryAndBlocksEveryInProcessHardwareReentry(
        bool applyRestoreStateBeforeFailure)
    {
        var taskManager = new FakeScheduledTaskManager
        {
            Enabled = false,
            FailRestore = true,
            ApplyRestoreStateBeforeFailure = applyRestoreStateBeforeFailure,
        };
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedTaskManager: taskManager);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-471", "settings");

        var rollback = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.RollbackAsync(
                startMythCool: false,
                installer: false,
                peer,
                CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InternalError, rollback.Code);
        Assert.Equal(1, fixture.Device.DisposeCalls);
        Assert.False(fixture.WriterLease.IsHeld);
        Assert.Equal(applyRestoreStateBeforeFailure, taskManager.Enabled);
        Assert.NotNull(await fixture.TransitionJournalStore.LoadAsync());
        var status = fixture.Coordinator.GetStatus();
        Assert.Equal("RollbackRecoveryRequired", status.ServiceState);
        Assert.False(status.Commissioned);
        Assert.False(status.Device.Connected);
        Assert.Equal(FailSafeReason.ConfigurationInvalid, status.FailSafe.Reason);
        _ = fixture.Coordinator.GetDiagnostics();
        var countsAfterFailure = fixture.CaptureHardwareCounts();

        var mode = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.SetModeAsync(ControlMode.Manual, peer, CancellationToken.None));
        var speed = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.SetManualSpeedAsync(60, peer, CancellationToken.None));
        var curve = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.SetCurveAsync(FanCurve.RemoteHostSafe, peer, CancellationToken.None));
        var probe = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));
        var idempotent = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.VerifyIdempotentAsync(
                Guid.NewGuid(), true, peer, CancellationToken.None));
        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.All(
            new[] { mode, speed, curve, probe, idempotent },
            exception => Assert.Equal(PipeErrorCodes.InternalError, exception.Code));
        Assert.Equal(countsAfterFailure, fixture.CaptureHardwareCounts());
        Assert.Equal("ConfigurationInvalid", fixture.Coordinator.GetStatus().ServiceState);
        Assert.False(fixture.Coordinator.GetStatus().Commissioned);
        Assert.False(fixture.Coordinator.GetStatus().Device.Connected);
        Assert.NotNull(await fixture.TransitionJournalStore.LoadAsync());
    }

    [Fact]
    public async Task ConfigurationAudit_RecordsCallerSourceAndCompletePreviousAndCurrentCurve()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-472", "settings");
        var curve = new FanCurve([new(0, 40), new(90, 100)]);

        await fixture.Coordinator.SetCurveAsync(curve, peer, CancellationToken.None);

        var records = await ReadAuditRecordsAsync(fixture.Paths.AuditLogPath);
        var record = records.Single(item => item.GetProperty("eventName").GetString() == "curve.set");
        Assert.Equal(peer.CallerSid, record.GetProperty("callerSid").GetString());
        Assert.Equal(peer.Source, record.GetProperty("source").GetString());
        Assert.Equal("success", record.GetProperty("result").GetString());
        var details = record.GetProperty("details");
        var previous = details.GetProperty("previous");
        var current = details.GetProperty("current");
        Assert.Equal(10, previous.GetProperty("curve").GetArrayLength());
        Assert.Equal(2, current.GetProperty("curve").GetArrayLength());
        Assert.Equal(40, current.GetProperty("curve")[0].GetProperty("fanPercent").GetByte());
        Assert.Equal(90, current.GetProperty("curve")[1].GetProperty("temperatureCelsius").GetDouble());
    }

    [Fact]
    public async Task PersistentPolicyMutations_AllUseDistinctDurableWalTransactions()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-490", "v12ctl");

        await fixture.Coordinator.SetModeAsync(ControlMode.Manual, peer, CancellationToken.None);
        await fixture.Coordinator.SetManualSpeedAsync(73, peer, CancellationToken.None);
        await fixture.Coordinator.SetCurveAsync(
            new FanCurve([new(0, 40), new(90, 100)]),
            peer,
            CancellationToken.None);

        Assert.False(File.Exists(fixture.Paths.AuditMutationPendingPath));
        var records = await ReadAuditRecordsAsync(fixture.Paths.AuditLogPath);
        var mutations = records.Where(record =>
                record.GetProperty("eventName").GetString() is
                    PipeOperations.ModeSet or PipeOperations.SpeedSet or PipeOperations.CurveSet)
            .ToArray();
        Assert.Equal(3, mutations.Length);
        Assert.Equal(3, mutations.Select(record =>
            record.GetProperty("transactionId").GetGuid()).Distinct().Count());
        Assert.All(mutations, record =>
        {
            Assert.Equal("success", record.GetProperty("result").GetString());
            var details = record.GetProperty("details");
            Assert.True(details.GetProperty("previous").TryGetProperty(
                "systemShutdownFanBehavior", out _));
            Assert.True(details.GetProperty("current").TryGetProperty(
                "shutdownOffQualification", out _));
        });
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(2, true, false)]
    [InlineData(3, true, true)]
    [InlineData(4, true, true)]
    public async Task LiveMutation_JournalWriteCutPointsRetainBarrierAndBlockHardware(
        int failWriteCall,
        bool expectPending,
        bool expectPublishedConfiguration)
    {
        var persistence = new FaultInjectingAuditMutationPersistence();
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            auditMutationStoreFactory: (_, time) =>
                new AuditMutationJournalStore(persistence, time));
        persistence.FailWriteCall = failWriteCall;
        var counts = fixture.CaptureHardwareCounts();

        var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.SetModeAsync(
                ControlMode.Manual,
                new PipePeerIdentity("S-1-5-21-100-200-300-494", "settings"),
                CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InternalError, rejected.Code);
        var status = fixture.Coordinator.GetStatus();
        Assert.True(status.AuditMutation!.Blocked);
        Assert.Equal(expectPending, status.AuditMutation.Pending);
        Assert.Equal(counts, fixture.CaptureHardwareCounts());
        Assert.Equal(expectPublishedConfiguration ? ControlMode.Manual : ControlMode.Auto,
            (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).Mode);
        if (expectPending)
        {
            Assert.NotNull(await fixture.AuditMutationJournalStore.LoadAsync());
        }
    }

    [Fact]
    public async Task LiveMutation_AuditAppendFailureRetainsStateAppliedBarrier()
    {
        FaultInjectingAuditLog? failingAudit = null;
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            auditLogFactory: (paths, time) =>
                failingAudit = new FaultInjectingAuditLog(paths, time));
        failingAudit!.FailTransactionWrite = true;

        var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.SetModeAsync(
                ControlMode.Manual,
                new PipePeerIdentity("S-1-5-21-100-200-300-495", "v12ctl"),
                CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InternalError, rejected.Code);
        var journal = Assert.IsType<AuditMutationJournal>(
            await fixture.AuditMutationJournalStore.LoadAsync());
        Assert.Equal(AuditMutationStage.StateApplied, journal.Stage);
        Assert.Equal(AuditMutationOutcome.Applied, journal.Outcome);
        Assert.True(fixture.Coordinator.GetStatus().AuditMutation!.Blocked);
        Assert.Equal(ControlMode.Manual,
            (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).Mode);
        Assert.Equal((0, 0, 0, 0, 0, 0), fixture.CaptureHardwareCounts());
    }

    [Fact]
    public async Task LiveMutation_JournalDeleteFailureRetainsAuditAppendedBarrier()
    {
        var persistence = new FaultInjectingAuditMutationPersistence();
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            auditMutationStoreFactory: (_, time) =>
                new AuditMutationJournalStore(persistence, time));
        persistence.FailDelete = true;

        var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.SetModeAsync(
                ControlMode.Manual,
                new PipePeerIdentity("S-1-5-21-100-200-300-496", "settings"),
                CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InternalError, rejected.Code);
        var journal = Assert.IsType<AuditMutationJournal>(
            await fixture.AuditMutationJournalStore.LoadAsync());
        Assert.Equal(AuditMutationStage.AuditAppended, journal.Stage);
        Assert.Equal(AuditMutationOutcome.Applied, journal.Outcome);
        Assert.True(fixture.Coordinator.GetStatus().AuditMutation!.Blocked);
        var records = await ReadAuditRecordsAsync(fixture.Paths.AuditLogPath);
        Assert.Single(records, record =>
            record.GetProperty("transactionId").GetGuid() == journal.TransactionId);
    }

    [Fact]
    public async Task LiveMutation_ConfigurationReadbackFailureRetainsInProgressBarrier()
    {
        var configurationStore = new FaultInjectingConfigurationStore();
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            suppliedStore: configurationStore);
        configurationStore.FailLoads = true;

        var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.SetModeAsync(
                ControlMode.Manual,
                new PipePeerIdentity("S-1-5-21-100-200-300-497", "settings"),
                CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InternalError, rejected.Code);
        var journal = Assert.IsType<AuditMutationJournal>(
            await fixture.AuditMutationJournalStore.LoadAsync());
        Assert.Equal(AuditMutationStage.MutationInProgress, journal.Stage);
        Assert.True(fixture.Coordinator.GetStatus().AuditMutation!.Blocked);
        Assert.Equal(ControlMode.Manual, configurationStore.Current.Mode);
        Assert.Equal((0, 0, 0, 0, 0, 0), fixture.CaptureHardwareCounts());
    }

    [Fact]
    public async Task StartupRecovery_StateAppliedWithExistingAuditDoesNotDuplicateRecord()
    {
        var previous = ControllerConfiguration.CreateDefault();
        var current = previous with { Mode = ControlMode.Manual };
        var transactionId = Guid.Empty;
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            initialConfiguration: current,
            configureAuditMutation: async (store, audit) =>
            {
                var journal = store.Create(
                    PipeOperations.ModeSet,
                    "S-1-5-21-100-200-300-498",
                    "settings",
                    AuditConfigurationSnapshot.FromConfiguration(previous),
                    AuditConfigurationSnapshot.FromConfiguration(current));
                transactionId = journal.TransactionId;
                await store.PrepareAsync(journal);
                journal = await store.AdvanceAsync(
                    journal,
                    AuditMutationStage.MutationInProgress);
                journal = await store.AdvanceAsync(journal, AuditMutationStage.StateApplied);
                await audit.WriteAsync(
                    journal.EventName,
                    "success",
                    journal.CallerSid,
                    journal.Source,
                    new { previous = journal.Previous, current = journal.Current },
                    transactionId: journal.TransactionId);
            });

        Assert.False(fixture.Coordinator.GetStatus().AuditMutation!.Blocked);
        Assert.False(File.Exists(fixture.Paths.AuditMutationPendingPath));
        var records = await ReadAuditRecordsAsync(fixture.Paths.AuditLogPath);
        Assert.Single(records, record =>
            record.GetProperty("transactionId").GetGuid() == transactionId);
    }

    [Theory]
    [InlineData((int)AuditMutationStage.Prepared, false, (int)AuditMutationOutcome.NotApplied, "not-applied")]
    [InlineData((int)AuditMutationStage.MutationInProgress, false, (int)AuditMutationOutcome.NotApplied, "not-applied")]
    [InlineData((int)AuditMutationStage.MutationInProgress, true, (int)AuditMutationOutcome.Applied, "success")]
    [InlineData((int)AuditMutationStage.StateApplied, true, (int)AuditMutationOutcome.Applied, "success")]
    [InlineData((int)AuditMutationStage.MutationNotApplied, false, (int)AuditMutationOutcome.NotApplied, "not-applied")]
    [InlineData((int)AuditMutationStage.AuditAppended, true, (int)AuditMutationOutcome.Applied, "success")]
    [InlineData((int)AuditMutationStage.AuditAppended, false, (int)AuditMutationOutcome.NotApplied, "not-applied")]
    public async Task StartupRecovery_ConvergesEveryDurableAuditMutationStage(
        int stageValue,
        bool persistedCurrent,
        int outcomeValue,
        string expectedResult)
    {
        var stage = (AuditMutationStage)stageValue;
        var outcome = (AuditMutationOutcome)outcomeValue;
        var previous = ControllerConfiguration.CreateDefault();
        var current = previous with { Mode = ControlMode.Manual, ManualSpeedPercent = 73 };
        var transactionId = Guid.Empty;
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            initialConfiguration: persistedCurrent ? current : previous,
            configureAuditMutation: async (store, audit) =>
            {
                var journal = store.Create(
                    PipeOperations.ModeSet,
                    "S-1-5-21-100-200-300-491",
                    "settings",
                    AuditConfigurationSnapshot.FromConfiguration(previous),
                    AuditConfigurationSnapshot.FromConfiguration(current));
                transactionId = journal.TransactionId;
                await store.PrepareAsync(journal);
                if (stage != AuditMutationStage.Prepared)
                {
                    journal = await store.AdvanceAsync(
                        journal,
                        AuditMutationStage.MutationInProgress);
                }

                if (stage is AuditMutationStage.StateApplied or AuditMutationStage.AuditAppended &&
                    outcome == AuditMutationOutcome.Applied)
                {
                    journal = await store.AdvanceAsync(journal, AuditMutationStage.StateApplied);
                }
                else if (stage is AuditMutationStage.MutationNotApplied or AuditMutationStage.AuditAppended)
                {
                    journal = await store.AdvanceAsync(
                        journal,
                        AuditMutationStage.MutationNotApplied);
                }

                if (stage == AuditMutationStage.AuditAppended)
                {
                    await audit.WriteAsync(
                        journal.EventName,
                        outcome == AuditMutationOutcome.Applied ? "success" : "not-applied",
                        journal.CallerSid,
                        journal.Source,
                        new { previous = journal.Previous, current = journal.Current },
                        transactionId: journal.TransactionId);
                    _ = await store.AdvanceAsync(journal, AuditMutationStage.AuditAppended);
                }
            });

        var status = fixture.Coordinator.GetStatus();
        Assert.False(status.AuditMutation!.Blocked);
        Assert.False(status.AuditMutation.Pending);
        Assert.False(File.Exists(fixture.Paths.AuditMutationPendingPath));
        Assert.Equal(persistedCurrent ? ControlMode.Manual : ControlMode.Auto,
            (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).Mode);
        Assert.Equal((0, 0, 0, 0, 0, 0), fixture.CaptureHardwareCounts());
        var records = await ReadAuditRecordsAsync(fixture.Paths.AuditLogPath);
        var recovered = Assert.Single(records, record =>
            record.TryGetProperty("transactionId", out var value) &&
            value.ValueKind == JsonValueKind.String &&
            value.GetGuid() == transactionId);
        Assert.Equal(expectedResult, recovered.GetProperty("result").GetString());
    }

    [Fact]
    public async Task StartupRecovery_AuditAppendedWithoutDiscoverableRecordBlocksBeforeHid()
    {
        var enabled = CreateQualifiedShutdownOffConfiguration();
        var disabled = enabled with
        {
            SystemShutdownFanBehavior = SystemShutdownFanBehavior.KeepAt100,
            ShutdownOffQualification = null,
        };
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            runtimeCommissioned: true,
            initialConfiguration: enabled,
            configureAuditMutation: async (store, unusedAudit) =>
            {
                _ = unusedAudit;
                var journal = store.Create(
                    PipeOperations.ShutdownEnable,
                    "S-1-5-21-100-200-300-492",
                    "settings",
                    AuditConfigurationSnapshot.FromConfiguration(disabled),
                    AuditConfigurationSnapshot.FromConfiguration(enabled));
                await store.PrepareAsync(journal);
                journal = await store.AdvanceAsync(
                    journal,
                    AuditMutationStage.MutationInProgress);
                journal = await store.AdvanceAsync(journal, AuditMutationStage.StateApplied);
                _ = await store.AdvanceAsync(journal, AuditMutationStage.AuditAppended);
            },
            primeCommissioningThermal: false);

        var status = fixture.Coordinator.GetStatus();
        Assert.Equal("ConfigurationInvalid", status.ServiceState);
        Assert.True(status.AuditMutation!.Blocked);
        Assert.True(status.AuditMutation.Pending);
        Assert.Equal(PipeOperations.ShutdownEnable, status.AuditMutation.EventName);
        Assert.Contains("AuditAppended", status.AuditMutation.Reason, StringComparison.Ordinal);
        Assert.True(File.Exists(fixture.Paths.AuditMutationPendingPath));
        Assert.Equal((0, 0, 0, 0, 0, 0), fixture.CaptureHardwareCounts());

        await fixture.Coordinator.TickAsync(CancellationToken.None);
        await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);

        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.Equal((0, 0, 0, 0, 0, 0), fixture.CaptureHardwareCounts());
    }

    [Fact]
    public async Task StartupRecovery_InProgressShutdownEnableRollsBackToKeepAt100BeforeHid()
    {
        var enabled = CreateQualifiedShutdownOffConfiguration();
        var disabled = enabled with
        {
            SystemShutdownFanBehavior = SystemShutdownFanBehavior.KeepAt100,
            ShutdownOffQualification = null,
        };
        var transactionId = Guid.Empty;
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            runtimeCommissioned: true,
            initialConfiguration: enabled,
            configureAuditMutation: async (store, unusedAudit) =>
            {
                _ = unusedAudit;
                var journal = store.Create(
                    PipeOperations.ShutdownEnable,
                    "S-1-5-21-100-200-300-493",
                    "settings",
                    AuditConfigurationSnapshot.FromConfiguration(disabled),
                    AuditConfigurationSnapshot.FromConfiguration(enabled));
                transactionId = journal.TransactionId;
                await store.PrepareAsync(journal);
                _ = await store.AdvanceAsync(
                    journal,
                    AuditMutationStage.MutationInProgress);
            },
            primeCommissioningThermal: false);

        var persisted = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal(SystemShutdownFanBehavior.KeepAt100,
            persisted.SystemShutdownFanBehavior);
        Assert.Null(persisted.ShutdownOffQualification);
        Assert.False(fixture.Coordinator.GetShutdownStatus().Enabled);
        Assert.False(fixture.Coordinator.GetStatus().AuditMutation!.Blocked);
        Assert.False(File.Exists(fixture.Paths.AuditMutationPendingPath));
        Assert.Equal((0, 0, 0, 0, 0, 0), fixture.CaptureHardwareCounts());
        var records = await ReadAuditRecordsAsync(fixture.Paths.AuditLogPath);
        var record = Assert.Single(records, item =>
            item.GetProperty("transactionId").GetGuid() == transactionId);
        Assert.Equal("not-applied", record.GetProperty("result").GetString());
        Assert.Equal("startup-shutdown-enable-rolled-back-to-keep-at-100",
            record.GetProperty("details").GetProperty("recovery").GetString());
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{}")]
    public async Task StartupRecovery_CorruptAuditMutationJournalBlocksWithoutHid(string rawJournal)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            runtimeCommissioned: true,
            primeCommissioningThermal: false,
            rawAuditMutationJournalJson: rawJournal);

        var status = fixture.Coordinator.GetStatus();
        Assert.True(status.AuditMutation!.Blocked);
        Assert.Contains("audit-mutation", status.AuditMutation.Reason!,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal((0, 0, 0, 0, 0, 0), fixture.CaptureHardwareCounts());
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal((0, 0, 0, 0, 0, 0), fixture.CaptureHardwareCounts());
    }

    [Fact]
    public async Task StartupRecovery_UnknownMutationEventIsPreservedAndBlocks()
    {
        var configuration = ControllerConfiguration.CreateDefault() with { Commissioned = true };
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            initialConfiguration: configuration,
            configureAuditMutation: async (store, unusedAudit) =>
            {
                _ = unusedAudit;
                var journal = store.Create(
                    "future.configuration.operation",
                    "S-1-5-18",
                    "service",
                    AuditConfigurationSnapshot.FromConfiguration(configuration),
                    AuditConfigurationSnapshot.FromConfiguration(configuration));
                await store.PrepareAsync(journal);
            },
            primeCommissioningThermal: false);

        var status = fixture.Coordinator.GetStatus();
        Assert.True(status.AuditMutation!.Blocked);
        Assert.Equal("future.configuration.operation", status.AuditMutation.EventName);
        Assert.True(File.Exists(fixture.Paths.AuditMutationPendingPath));
        Assert.Equal((0, 0, 0, 0, 0, 0), fixture.CaptureHardwareCounts());
    }

    [Fact]
    public async Task TakeoverAudit_RecordsOwnershipTaskAndCommissioningStateTransitions()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-473", "settings");
        var sessionId = await RunToCommitAsync(fixture, peer);

        await fixture.Coordinator.CommitCommissioningAsync(sessionId, true, peer, CancellationToken.None);
        await fixture.Coordinator.RollbackAsync(
            startMythCool: false,
            installer: false,
            peer,
            CancellationToken.None);

        var records = await ReadAuditRecordsAsync(fixture.Paths.AuditLogPath);
        var commit = records.Single(item => item.GetProperty("eventName").GetString() == "commission.commit");
        var commitDetails = commit.GetProperty("details");
        Assert.False(commitDetails.GetProperty("previous").GetProperty("runtimeCommissioned").GetBoolean());
        Assert.True(commitDetails.GetProperty("previous").GetProperty("mythCoolTaskEnabled").GetBoolean());
        Assert.True(commitDetails.GetProperty("current").GetProperty("runtimeCommissioned").GetBoolean());
        Assert.False(commitDetails.GetProperty("current").GetProperty("mythCoolTaskEnabled").GetBoolean());
        Assert.Equal(peer.CallerSid, commitDetails.GetProperty("current").GetProperty("ownerSid").GetString());

        var rollback = records.Single(item => item.GetProperty("eventName").GetString() == "commission.rollback");
        var rollbackDetails = rollback.GetProperty("details");
        Assert.True(rollbackDetails.GetProperty("previous").GetProperty("runtimeCommissioned").GetBoolean());
        Assert.False(rollbackDetails.GetProperty("previous").GetProperty("mythCoolTaskEnabled").GetBoolean());
        Assert.False(rollbackDetails.GetProperty("current").GetProperty("runtimeCommissioned").GetBoolean());
        Assert.True(rollbackDetails.GetProperty("current").GetProperty("mythCoolTaskEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null,
            rollbackDetails.GetProperty("current").GetProperty("ownerSid").ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TakeoverAudit_AppendFailureRetainsResolvedJournalWithoutCompensatingBusinessState(
        bool publishBeforeFailure)
    {
        FaultInjectingAuditLog? injectedAudit = null;
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            auditLogFactory: (paths, timeProvider) =>
                injectedAudit = new FaultInjectingAuditLog(paths, timeProvider));
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-474", "settings");
        var sessionId = await RunToCommitAsync(fixture, peer);
        injectedAudit!.FailTransactionWrite = true;
        injectedAudit.PublishBeforeFailure = publishBeforeFailure;

        var failure = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.CommitCommissioningAsync(
                sessionId,
                attended: true,
                peer,
                CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InternalError, failure.Code);
        var retained = Assert.IsType<TakeoverTransitionJournal>(
            await fixture.TransitionJournalStore.LoadAsync(CancellationToken.None));
        Assert.Equal(TakeoverTransitionStage.OutcomeResolved, retained.Stage);
        Assert.Equal(TakeoverTransitionAuditAction.CommitCompleted, retained.Audit.Action);
        Assert.True((await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).Commissioned);
        Assert.True((await fixture.RuntimeStateStore.LoadAsync(CancellationToken.None)).Commissioned);
        Assert.False(fixture.TaskManager.Enabled);
        Assert.True(fixture.WriterLease.IsHeld);
        Assert.Equal("TakeoverAuditPending", fixture.Coordinator.GetStatus().ServiceState);
        Assert.Equal(
            publishBeforeFailure,
            await injectedAudit.ContainsTransactionAsync(retained.TransitionId, CancellationToken.None));
    }

    [Theory]
    [InlineData((int)TakeoverTransitionStage.OutcomeResolved)]
    [InlineData((int)TakeoverTransitionStage.AuditAppended)]
    public async Task StartupRecovery_PublishedTakeoverAuditIsNotDuplicatedAndDeletesExactJournal(
        int stageValue)
    {
        var configuration = ControllerConfiguration.CreateDefault() with { Commissioned = false };
        var taskManager = new FakeScheduledTaskManager { Enabled = true };
        var transition = CreateTransition(
            TakeoverTransitionOperation.Commit,
            TakeoverTransitionStage.Prepared,
            taskManager.Backup);
        var current = CreateExpectedTakeoverAuditSnapshot(
            configuration,
            ServiceRuntimeState.Initial,
            taskManager.Backup,
            taskEnabled: true);
        transition = transition with
        {
            Stage = (TakeoverTransitionStage)stageValue,
            Audit = transition.Audit with
            {
                Result = TakeoverTransitionAuditResult.NotApplied,
                Action = TakeoverTransitionAuditAction.CommitAbandonedOriginalState,
                Current = current,
            },
        };

        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            initialConfiguration: configuration,
            transition: transition,
            suppliedTaskManager: taskManager,
            configureAuditMutation: async (_, audit) =>
                await audit.WriteAsync(
                    transition.Audit.EventName,
                    "not-applied",
                    transition.Audit.CallerSid,
                    transition.Audit.Source,
                    new { seeded = true },
                    CancellationToken.None,
                    transition.TransitionId));

        Assert.Null(await fixture.TransitionJournalStore.LoadAsync(CancellationToken.None));
        var records = await ReadAuditRecordsAsync(fixture.Paths.AuditLogPath);
        Assert.Single(records, record =>
            record.TryGetProperty("transactionId", out var transactionId) &&
            transactionId.GetGuid() == transition.TransitionId);
        Assert.Equal(0, taskManager.DisableCalls);
        Assert.Equal(0, taskManager.RestoreCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task ProbeOnlySession_InvalidCpuOnTick_AbortsWithoutTouchingHidAndRequiresNewWarmup()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-410", "testhost");
        var sessionId = ReadSessionId(
            await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));
        fixture.Thermal.CpuTemperature = null;

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Empty(fixture.Device.Writes);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        var status = fixture.Coordinator.GetStatus();
        Assert.Equal("CommissioningAbortedTelemetryStale", status.ServiceState);
        Assert.Equal((byte)100, status.Fan.RequestedPercent);
        Assert.Equal(FailSafeReason.TelemetryStale, status.FailSafe.Reason);
        var exception = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.VerifyIdempotentAsync(sessionId, true, peer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.InvalidArgument, exception.Code);

        fixture.Thermal.CpuTemperature = 70;
        for (var sample = 0; sample < 10; sample++)
        {
            await fixture.Coordinator.TickAsync(CancellationToken.None);
        }

        var replacement = await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None);
        Assert.NotEqual(sessionId, ReadSessionId(replacement));
    }

    [Fact]
    public async Task ProbeOnlySession_EmergencyTemperatureOnTick_AbortsWithoutTouchingHid()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-411", "testhost");
        _ = await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None);
        fixture.Thermal.CpuTemperature = 90;

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Empty(fixture.Device.Writes);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Equal("CommissioningAbortedEmergencyTemperature",
            fixture.Coordinator.GetStatus().ServiceState);
        Assert.Equal(FailSafeReason.EmergencyTemperature,
            fixture.Coordinator.GetStatus().FailSafe.Reason);
    }

    [Fact]
    public async Task CommissioningSession_MythCoolConflictOnTick_AbortsBeforeAnotherRead()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-412", "testhost");
        _ = await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None);
        var readsBeforeTick = fixture.Device.ReadCalls;
        fixture.Conflict.HasConflict = true;

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(readsBeforeTick, fixture.Device.ReadCalls);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal(FailSafeReason.MythCoolConflict, fixture.Coordinator.GetStatus().FailSafe.Reason);
    }

    [Fact]
    public async Task CommissionedTick_ProcessInspectionFailureBlocksBeforeHardwareAccess()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Conflict.InspectionFailed = true;
        var countsBeforeTick = fixture.CaptureHardwareCounts();

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(countsBeforeTick, fixture.CaptureHardwareCounts());
        var status = fixture.Coordinator.GetStatus();
        Assert.Equal("BlockedByMythCool", status.ServiceState);
        Assert.True(status.FailSafe.Active);
        Assert.Equal(FailSafeReason.MythCoolConflict, status.FailSafe.Reason);
        Assert.Equal((byte)100, status.Fan.RequestedPercent);
    }

    [Fact]
    public async Task CommissionedTick_RuntimeConflictConfirmsOneHundredBeforeReleasingOwnership()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        await fixture.ReachSettledControlAsync();
        fixture.Device.SetState(40, poweredOn: true);
        var lightingBeforeHandoff = fixture.Device.LightingState;
        var operationStart = fixture.Device.Operations.Count;
        fixture.Conflict.HasConflict = true;

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(
            ["ON:100", "READ"],
            fixture.Device.Operations.Skip(operationStart));
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        Assert.Equal(lightingBeforeHandoff, fixture.Device.LightingState);
        Assert.Equal(1, fixture.Device.DisposeCalls);
        var status = fixture.Coordinator.GetStatus();
        Assert.Equal("BlockedByMythCool", status.ServiceState);
        Assert.Equal(FailSafeReason.MythCoolConflict, status.FailSafe.Reason);
        using var replacementLease = new SingleWriterLease(fixture.Paths.WriterLeasePath);
        Assert.True(replacementLease.TryAcquire());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CommissionedTick_UnconfirmedRuntimeConflictHandoffPoisonsAndRetainsOwnership(
        bool failWrite)
    {
        var previousExitCode = Environment.ExitCode;
        try
        {
            await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
            await fixture.ReachSettledControlAsync();
            fixture.Device.SetState(40, poweredOn: true);
            if (failWrite)
            {
                fixture.Device.WriteFailures.Enqueue(
                    new IOException("Injected conflict handoff write failure."));
            }
            else
            {
                fixture.Device.ReturnMismatchedReadback = true;
            }

            fixture.Conflict.HasConflict = true;

            await fixture.Coordinator.TickAsync(CancellationToken.None);

            Assert.True(fixture.Coordinator.IsHardwarePoisoned);
            Assert.Equal(
                "CONFLICT_HANDOFF_UNCONFIRMED",
                fixture.Coordinator.GetStatus().HardwareSafety.Reason);
            Assert.Equal(7, Environment.ExitCode);
            Assert.Equal(0, fixture.Device.DisposeCalls);
            using var blockedLease = new SingleWriterLease(fixture.Paths.WriterLeasePath);
            Assert.False(blockedLease.TryAcquire());

            var terminator = new RecordingProcessTerminator();
            var fatalFailure = new FatalServiceFailureHandler(
                fixture.Coordinator,
                terminator,
                NullLogger<FatalServiceFailureHandler>.Instance);
            var watchdog = new ControllerWatchdogWorker(
                fixture.Coordinator,
                terminator,
                fatalFailure,
                TimeProvider.System,
                NullLogger<ControllerWatchdogWorker>.Instance);

            Assert.True(await watchdog.CheckForTerminationAsync());
            Assert.Equal([7], terminator.ExitCodes);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task Probe_ProcessInspectionFailureReturnsSanitizedMythCoolConflict()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        fixture.Conflict.InspectionFailed = true;

        var exception = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.ProbeCommissioningAsync(
                true,
                new PipePeerIdentity("S-1-5-21-100-200-300-499", "settings"),
                CancellationToken.None));

        Assert.Equal(PipeErrorCodes.MythCoolConflict, exception.Code);
        Assert.Equal(
            "Myth.Cool process state could not be verified; the operation was refused.",
            exception.Message);
        Assert.Equal(0, fixture.Locator.EnumerateCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Empty(fixture.Device.Writes);
    }

    [Fact]
    public async Task Test_MythCoolConflictImmediatelyInvalidatesAttachedSession()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-428", "testhost");
        var probe = await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None);
        var sessionId = ReadSessionId(probe);
        await fixture.Coordinator.VerifyIdempotentAsync(
            sessionId, true, peer, CancellationToken.None);
        fixture.Conflict.HasConflict = true;

        var conflict = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.TestCommissioningAsync(
                sessionId, true, peer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.MythCoolConflict, conflict.Code);
        Assert.Equal((byte)100, fixture.Device.Writes[^1]);
        Assert.Equal(1, fixture.Device.DisposeCalls);
        Assert.Equal("CommissioningAbortedMythCoolConflict",
            fixture.Coordinator.GetStatus().ServiceState);
        using var replacementLease = new SingleWriterLease(fixture.Paths.WriterLeasePath);
        Assert.True(replacementLease.TryAcquire());

        fixture.Conflict.HasConflict = false;
        var retry = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.TestCommissioningAsync(
                sessionId, true, peer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.InvalidArgument, retry.Code);
    }

    [Fact]
    public async Task CommissioningSession_DeviceFailureOnTick_AbortsAtOneHundred()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-415", "testhost");
        var probe = await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None);
        await fixture.Coordinator.VerifyIdempotentAsync(
            ReadSessionId(probe), true, peer, CancellationToken.None);
        fixture.Device.FailReads = true;

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal((byte)100, fixture.Device.Writes[^1]);
        var status = fixture.Coordinator.GetStatus();
        Assert.Equal("SafetyOperationAuditRecoveryRequired", status.ServiceState);
        Assert.Equal(FailSafeReason.ConfigurationInvalid, status.FailSafe.Reason);
        Assert.True(status.SafetyOperationAudit!.Blocked);
        Assert.Equal("commission.safety-abort", status.SafetyOperationAudit.EventName);
    }

    [Fact]
    public async Task Test_RuntimeFailureInvalidatesSessionWithoutCleanupOverwritingOriginalError()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-429", "testhost");
        var probe = await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None);
        var sessionId = ReadSessionId(probe);
        await fixture.Coordinator.VerifyIdempotentAsync(
            sessionId, true, peer, CancellationToken.None);
        fixture.Device.WriteFailures.Enqueue(new IOException("Primary staged-test write failure."));
        fixture.Device.WriteFailures.Enqueue(new ObjectDisposedException("cleanup-device"));

        var failure = await Assert.ThrowsAsync<IOException>(() =>
            fixture.Coordinator.TestCommissioningAsync(
                sessionId, true, peer, CancellationToken.None));

        Assert.Equal("Primary staged-test write failure.", failure.Message);
        Assert.Equal((byte)100, fixture.Device.Writes[^1]);
        Assert.Equal(1, fixture.Device.DisposeCalls);
        Assert.Equal("SafetyOperationAuditRecoveryRequired",
            fixture.Coordinator.GetStatus().ServiceState);
        using var replacementLease = new SingleWriterLease(fixture.Paths.WriterLeasePath);
        Assert.True(replacementLease.TryAcquire());

        var retry = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.TestCommissioningAsync(
                sessionId, true, peer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.InternalError, retry.Code);
    }

    [Fact]
    public async Task Commit_BackupFailureInvalidatesSessionBeforeDurableTransition()
    {
        var taskManager = new FakeScheduledTaskManager();
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            suppliedTaskManager: taskManager);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-430", "testhost");
        var sessionId = await RunToCommitAsync(fixture, peer);
        taskManager.FailBackup = true;

        var failure = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.CommitCommissioningAsync(
                sessionId, true, peer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InternalError, failure.Code);
        Assert.Equal(1, taskManager.BackupCalls);
        Assert.Equal(0, taskManager.DisableCalls);
        Assert.Equal((byte)100, fixture.Device.Writes[^1]);
        Assert.Equal(1, fixture.Device.DisposeCalls);
        Assert.Null(await fixture.TransitionJournalStore.LoadAsync(CancellationToken.None));
        using var replacementLease = new SingleWriterLease(fixture.Paths.WriterLeasePath);
        Assert.True(replacementLease.TryAcquire());

        var retry = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.CommitCommissioningAsync(
                sessionId, true, peer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.InvalidArgument, retry.Code);
        Assert.False(fixture.Coordinator.GetStatus().SafetyOperationAudit!.Blocked);
    }

    [Fact]
    public async Task ProbeWithExistingProbeOnlySession_IsRejectedUntilExplicitAbort()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-416", "testhost");
        var firstSession = ReadSessionId(
            await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));

        var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InvalidArgument, rejected.Code);
        Assert.Equal(1, fixture.Locator.EnumerateCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Empty(fixture.Device.Writes);

        _ = await fixture.Coordinator.AbortCommissioningAsync(
            firstSession, peer, CancellationToken.None);
        var replacement = await fixture.Coordinator.ProbeCommissioningAsync(
            true, peer, CancellationToken.None);
        Assert.NotEqual(firstSession, ReadSessionId(replacement));
        Assert.Equal(2, fixture.Locator.EnumerateCalls);
    }

    [Fact]
    public async Task ProbeWithExistingAttachedSession_IsRejectedWithoutChangingDeviceState()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-426", "testhost");
        var probe = await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None);
        var sessionId = ReadSessionId(probe);
        await fixture.Coordinator.VerifyIdempotentAsync(
            sessionId, true, peer, CancellationToken.None);
        var writesBeforeRejectedProbe = fixture.Device.Writes.ToArray();
        var readsBeforeRejectedProbe = fixture.Device.ReadCalls;

        var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InvalidArgument, rejected.Code);
        Assert.Equal(writesBeforeRejectedProbe, fixture.Device.Writes);
        Assert.Equal(readsBeforeRejectedProbe, fixture.Device.ReadCalls);
        Assert.Equal(1, fixture.Locator.OpenCalls);

        var aborted = JsonSerializer.SerializeToElement(
            await fixture.Coordinator.AbortCommissioningAsync(
                sessionId, peer, CancellationToken.None));
        Assert.Equal(100, aborted.GetProperty("requestedPercent").GetInt32());
        Assert.True(aborted.GetProperty("writeAttempted").GetBoolean());
        Assert.True(aborted.GetProperty("writeSucceeded").GetBoolean());
        Assert.True(aborted.GetProperty("readbackConfirmed").GetBoolean());
    }

    [Fact]
    public async Task IdempotentProtocolFailure_WritesOneHundredAndInvalidatesSession()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-417", "testhost");
        var sessionId = ReadSessionId(
            await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));
        fixture.Device.ReturnMismatchedReadback = true;

        var exception = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.VerifyIdempotentAsync(sessionId, true, peer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.ProtocolChecksum, exception.Code);
        Assert.Equal((byte)100, fixture.Device.Writes[^1]);
        var retry = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.VerifyIdempotentAsync(sessionId, true, peer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.InternalError, retry.Code);
        Assert.True(fixture.Coordinator.GetStatus().SafetyOperationAudit!.Blocked);
    }

    [Fact]
    public async Task AbortProbeOnlySession_RequiresOwningSidWithoutTouchingHid()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var owner = new PipePeerIdentity("S-1-5-21-100-200-300-413", "settings");
        var other = new PipePeerIdentity("S-1-5-21-100-200-300-414", "other");
        var sessionId = ReadSessionId(
            await fixture.Coordinator.ProbeCommissioningAsync(true, owner, CancellationToken.None));

        var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.AbortCommissioningAsync(sessionId, other, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.InvalidArgument, rejected.Code);

        var result = JsonSerializer.SerializeToElement(
            await fixture.Coordinator.AbortCommissioningAsync(sessionId, owner, CancellationToken.None));
        Assert.True(result.GetProperty("aborted").GetBoolean());
        Assert.Equal(100, result.GetProperty("requestedPercent").GetInt32());
        Assert.False(result.GetProperty("writeAttempted").GetBoolean());
        Assert.False(result.GetProperty("writeSucceeded").GetBoolean());
        Assert.False(result.GetProperty("readbackConfirmed").GetBoolean());
        Assert.True(result.GetProperty("requiresProbe").GetBoolean());
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal("CommissioningAborted", fixture.Coordinator.GetStatus().ServiceState);
    }

    [Fact]
    public async Task ShutdownQualification_RequiresSeparateEnableBeforePersistingIdentityBoundQualification()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Thermal.CpuTemperature = 50;
        await fixture.ReachSettledControlAsync();
        var operationStart = fixture.Device.Operations.Count;
        var lightingBefore = fixture.Device.LightingState;
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-470", "settings");

        var response = JsonSerializer.SerializeToElement(
            await fixture.Coordinator.QualifySystemShutdownOffAsync(
                true, peer, CancellationToken.None));

        Assert.Equal(
            ["READ", "ON:100", "READ", "OFF:START", "OFF:COMPLETE", "READ", "ON:100", "READ"],
            fixture.Device.Operations.Skip(operationStart).ToArray());
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        Assert.Equal(lightingBefore, fixture.Device.LightingState);
        var pendingShutdown = response.GetProperty("shutdown");
        Assert.False(pendingShutdown.GetProperty("Enabled").GetBoolean());
        Assert.False(pendingShutdown.GetProperty("Qualified").GetBoolean());
        Assert.True(pendingShutdown.GetProperty("Pending").GetBoolean());
        var qualificationId = response.GetProperty("qualificationId").GetGuid();
        Assert.NotEqual(Guid.Empty, qualificationId);
        Assert.True(response.GetProperty("finalState").GetProperty("PoweredOn").GetBoolean());

        var persisted = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal(SystemShutdownFanBehavior.KeepAt100, persisted.SystemShutdownFanBehavior);
        Assert.Null(persisted.ShutdownOffQualification);

        var operationsAfterQualification = fixture.Device.Operations.Count;
        var enabled = JsonSerializer.SerializeToElement(
            await fixture.Coordinator.EnableSystemShutdownOffAsync(
                qualificationId, true, peer, CancellationToken.None));
        Assert.Equal(operationsAfterQualification, fixture.Device.Operations.Count);
        Assert.True(enabled.GetProperty("shutdown").GetProperty("Enabled").GetBoolean());
        Assert.True(enabled.GetProperty("shutdown").GetProperty("MatchesCurrentHandle").GetBoolean());
        Assert.False(enabled.GetProperty("shutdown").GetProperty("Pending").GetBoolean());

        persisted = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal(SystemShutdownFanBehavior.TurnOffOnSystemShutdown,
            persisted.SystemShutdownFanBehavior);
        var qualification = Assert.IsType<SystemShutdownOffQualification>(
            persisted.ShutdownOffQualification);
        Assert.Equal(SupportedIdentity.VendorId, qualification.VendorId);
        Assert.Equal(SupportedIdentity.ProductId, qualification.ProductId);
        Assert.Equal(SupportedIdentity.Version, qualification.FirmwareVersion);
        Assert.Equal(SupportedIdentity.UsagePage, qualification.UsagePage);
        Assert.Equal(SupportedIdentity.Usage, qualification.Usage);
        Assert.Equal(SupportedIdentity.FeatureReportLength, qualification.FeatureReportLength);
        Assert.True(qualification.ShutdownOffReadbackConfirmed);
        Assert.True(qualification.StartupOnAt100ReadbackConfirmed);
        Assert.True(qualification.LightingStatePreserved);
    }

    [Fact]
    public async Task ShutdownQualification_PendingIsMemoryOnlyAndAServiceRestartLosesIt()
    {
        var store = new ShutdownQualificationConfigurationStore();
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-480", "v12ctl");
        await using (var first = await CoordinatorFixture.CreateAsync(
                         commissioned: true,
                         suppliedStore: store))
        {
            first.Thermal.CpuTemperature = 50;
            await first.ReachSettledControlAsync();

            // Ignoring the successful response models a client that disconnects before
            // issuing the separate enable request. The service may retain only ephemeral
            // pending state; it must not turn that abandoned request into durable opt-in.
            _ = await first.Coordinator.QualifySystemShutdownOffAsync(
                true, peer, CancellationToken.None);

            Assert.True(first.Coordinator.GetShutdownStatus().Pending);
            Assert.False(first.Coordinator.GetShutdownStatus().Enabled);
            Assert.Equal(SystemShutdownFanBehavior.KeepAt100,
                store.Current.SystemShutdownFanBehavior);
            Assert.Null(store.Current.ShutdownOffQualification);
        }

        await using var restarted = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedStore: store);
        var restartedStatus = restarted.Coordinator.GetShutdownStatus();
        Assert.False(restartedStatus.Pending);
        Assert.False(restartedStatus.Enabled);
        Assert.Equal(SystemShutdownFanBehavior.KeepAt100,
            store.Current.SystemShutdownFanBehavior);
    }

    [Fact]
    public async Task ShutdownEnable_RequiresTheSameCallerAndRechecksConflictBeforePersistence()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Thermal.CpuTemperature = 50;
        await fixture.ReachSettledControlAsync();
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-481", "settings");
        var other = new PipePeerIdentity("S-1-5-21-100-200-300-482", "v12ctl");
        var qualified = JsonSerializer.SerializeToElement(
            await fixture.Coordinator.QualifySystemShutdownOffAsync(
                true, peer, CancellationToken.None));
        var qualificationId = qualified.GetProperty("qualificationId").GetGuid();

        var unattended = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.EnableSystemShutdownOffAsync(
                qualificationId, false, peer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.InvalidArgument, unattended.Code);
        Assert.True(fixture.Coordinator.GetShutdownStatus().Pending);

        var wrongCaller = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.EnableSystemShutdownOffAsync(
                qualificationId, true, other, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.InvalidArgument, wrongCaller.Code);
        Assert.True(fixture.Coordinator.GetShutdownStatus().Pending);

        fixture.Conflict.HasConflict = true;
        var conflict = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.EnableSystemShutdownOffAsync(
                qualificationId, true, peer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.MythCoolConflict, conflict.Code);
        Assert.True(fixture.Coordinator.GetShutdownStatus().Pending);
        Assert.Equal(SystemShutdownFanBehavior.KeepAt100,
            (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None))
            .SystemShutdownFanBehavior);

        fixture.Conflict.HasConflict = false;
        fixture.Thermal.CpuTemperature = 60;
        var hot = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.EnableSystemShutdownOffAsync(
                qualificationId, true, peer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.FailSafeActive, hot.Code);
        Assert.True(fixture.Coordinator.GetShutdownStatus().Pending);

        fixture.Thermal.CpuTemperature = 50;
        _ = await fixture.Coordinator.EnableSystemShutdownOffAsync(
            qualificationId, true, peer, CancellationToken.None);
        Assert.True(fixture.Coordinator.GetShutdownStatus().Enabled);
        Assert.False(fixture.Coordinator.GetShutdownStatus().Pending);
    }

    [Fact]
    public async Task ShutdownEnable_IdentityDriftInvalidatesPendingQualification()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Thermal.CpuTemperature = 50;
        await fixture.ReachSettledControlAsync();
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-483", "settings");
        var qualified = JsonSerializer.SerializeToElement(
            await fixture.Coordinator.QualifySystemShutdownOffAsync(
                true, peer, CancellationToken.None));
        var qualificationId = qualified.GetProperty("qualificationId").GetGuid();
        fixture.Device.Identity = SupportedIdentity with { Version = 0x1603 };

        var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.EnableSystemShutdownOffAsync(
                qualificationId, true, peer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.UnsupportedDevice, rejected.Code);
        Assert.False(fixture.Coordinator.GetShutdownStatus().Pending);
        Assert.Equal(SystemShutdownFanBehavior.KeepAt100,
            (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None))
            .SystemShutdownFanBehavior);
    }

    [Fact]
    public async Task ShutdownQualification_PendingExpiryUsesMonotonicTimeNotUtcWallClock()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedTimeProvider: time);
        fixture.Thermal.CpuTemperature = 50;
        await fixture.ReachSettledControlAsync();
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-484", "settings");
        var qualified = JsonSerializer.SerializeToElement(
            await fixture.Coordinator.QualifySystemShutdownOffAsync(
                true, peer, CancellationToken.None));
        var qualificationId = qualified.GetProperty("qualificationId").GetGuid();

        time.JumpUtc(TimeSpan.FromDays(1));
        Assert.True(fixture.Coordinator.GetShutdownStatus().Pending);
        time.JumpUtc(TimeSpan.FromDays(-2));
        time.AdvanceMonotonic(ServiceConstants.ShutdownQualificationPendingLifetime +
                              TimeSpan.FromMilliseconds(1));
        Assert.False(fixture.Coordinator.GetShutdownStatus().Pending);

        var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.EnableSystemShutdownOffAsync(
                qualificationId, true, peer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.InvalidArgument, rejected.Code);
    }

    [Fact]
    public async Task ShutdownQualification_RejectsMissingCommissioningFailSafeHeatAndConflictBeforeOff()
    {
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-471", "v12ctl");
        await using (var uncommissioned = await CoordinatorFixture.CreateAsync(commissioned: false))
        {
            var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                uncommissioned.Coordinator.QualifySystemShutdownOffAsync(
                    true, peer, CancellationToken.None));
            Assert.Equal(PipeErrorCodes.NotCommissioned, rejected.Code);
            Assert.Equal(0, uncommissioned.Device.ShutdownOffWrites);
        }

        await using (var warming = await CoordinatorFixture.CreateAsync(commissioned: true))
        {
            await warming.Coordinator.TickAsync(CancellationToken.None);
            var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                warming.Coordinator.QualifySystemShutdownOffAsync(
                    true, peer, CancellationToken.None));
            Assert.Equal(PipeErrorCodes.FailSafeActive, rejected.Code);
            Assert.Equal(0, warming.Device.ShutdownOffWrites);
        }

        await using (var hot = await CoordinatorFixture.CreateAsync(commissioned: true))
        {
            hot.Thermal.CpuTemperature = 60;
            await hot.ReachSettledControlAsync();
            var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                hot.Coordinator.QualifySystemShutdownOffAsync(
                    true, peer, CancellationToken.None));
            Assert.Equal(PipeErrorCodes.FailSafeActive, rejected.Code);
            Assert.Equal(0, hot.Device.ShutdownOffWrites);
        }

        await using (var conflict = await CoordinatorFixture.CreateAsync(commissioned: true))
        {
            conflict.Thermal.CpuTemperature = 50;
            await conflict.ReachSettledControlAsync();
            conflict.Conflict.HasConflict = true;
            var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                conflict.Coordinator.QualifySystemShutdownOffAsync(
                    true, peer, CancellationToken.None));
            Assert.Equal(PipeErrorCodes.MythCoolConflict, rejected.Code);
            Assert.Equal(0, conflict.Device.ShutdownOffWrites);
        }

        await using (var inspectionFailure = await CoordinatorFixture.CreateAsync(commissioned: true))
        {
            inspectionFailure.Thermal.CpuTemperature = 50;
            await inspectionFailure.ReachSettledControlAsync();
            inspectionFailure.Conflict.InspectionFailed = true;
            var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                inspectionFailure.Coordinator.QualifySystemShutdownOffAsync(
                    true, peer, CancellationToken.None));
            Assert.Equal(PipeErrorCodes.MythCoolConflict, rejected.Code);
            Assert.DoesNotContain("MythCool.exe", rejected.Message, StringComparison.Ordinal);
            Assert.Equal(0, inspectionFailure.Device.ShutdownOffWrites);
        }
    }

    [Fact]
    public async Task ShutdownQualification_RejectsUnattendedStaleIdentityLeaseAndOwnershipDrift()
    {
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-476", "v12ctl");
        await using (var unattended = await CoordinatorFixture.CreateAsync(commissioned: true))
        {
            unattended.Thermal.CpuTemperature = 50;
            await unattended.ReachSettledControlAsync();
            var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                unattended.Coordinator.QualifySystemShutdownOffAsync(
                    false, peer, CancellationToken.None));
            Assert.Equal(PipeErrorCodes.InvalidArgument, rejected.Code);
            Assert.Equal(0, unattended.Device.ShutdownOffWrites);
        }

        await using (var noHandle = await CoordinatorFixture.CreateAsync(commissioned: true))
        {
            var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                noHandle.Coordinator.QualifySystemShutdownOffAsync(
                    true, peer, CancellationToken.None));
            Assert.Equal(PipeErrorCodes.DeviceNotFound, rejected.Code);
            Assert.Equal(0, noHandle.Device.ShutdownOffWrites);
        }

        await using (var stale = await CoordinatorFixture.CreateAsync(commissioned: true))
        {
            stale.Thermal.CpuTemperature = 50;
            await stale.ReachSettledControlAsync();
            stale.Thermal.CapturedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1);
            var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                stale.Coordinator.QualifySystemShutdownOffAsync(
                    true, peer, CancellationToken.None));
            Assert.Equal(PipeErrorCodes.TelemetryStale, rejected.Code);
            Assert.Equal(0, stale.Device.ShutdownOffWrites);
        }

        await using (var identity = await CoordinatorFixture.CreateAsync(commissioned: true))
        {
            identity.Thermal.CpuTemperature = 50;
            await identity.ReachSettledControlAsync();
            identity.Device.Identity = SupportedIdentity with { Version = 0x1603 };
            var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                identity.Coordinator.QualifySystemShutdownOffAsync(
                    true, peer, CancellationToken.None));
            Assert.Equal(PipeErrorCodes.UnsupportedDevice, rejected.Code);
            Assert.Equal(0, identity.Device.ShutdownOffWrites);
        }

        await using (var lease = await CoordinatorFixture.CreateAsync(commissioned: true))
        {
            lease.Thermal.CpuTemperature = 50;
            await lease.ReachSettledControlAsync();
            lease.WriterLease.Release();
            var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                lease.Coordinator.QualifySystemShutdownOffAsync(
                    true, peer, CancellationToken.None));
            Assert.Equal(PipeErrorCodes.HidTimeout, rejected.Code);
            Assert.Equal(0, lease.Device.ShutdownOffWrites);
        }

        await using (var drift = await CoordinatorFixture.CreateAsync(
                         commissioned: true,
                         runtimeCommissioned: false))
        {
            var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                drift.Coordinator.QualifySystemShutdownOffAsync(
                    true, peer, CancellationToken.None));
            Assert.Equal(PipeErrorCodes.InternalError, rejected.Code);
            Assert.Equal(0, drift.Device.ShutdownOffWrites);
        }

        await using (var missingOwner = await CoordinatorFixture.CreateAsync(
                         commissioned: true,
                         runtimeOwnerMissing: true))
        {
            var before = missingOwner.CaptureHardwareCounts();
            Assert.Equal("ConfigurationInvalid", missingOwner.Coordinator.GetStatus().ServiceState);
            var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                missingOwner.Coordinator.QualifySystemShutdownOffAsync(
                    true, peer, CancellationToken.None));
            Assert.Equal(PipeErrorCodes.InternalError, rejected.Code);
            Assert.Equal(0, missingOwner.Device.ShutdownOffWrites);
            Assert.Equal(before, missingOwner.CaptureHardwareCounts());
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task ShutdownQualification_EveryTransactionStepFailureRecoversOnSameWorkItem(
        int failingStep)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Thermal.CpuTemperature = 50;
        await fixture.ReachSettledControlAsync();
        fixture.Device.FailAtOperationCall = fixture.Device.OperationCallCount + failingStep;
        fixture.Device.ScheduledOperationFailure =
            new V12ProtocolException($"Injected qualification step {failingStep} failure.");
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-472", "v12ctl");

        var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.QualifySystemShutdownOffAsync(
                true, peer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.ProtocolChecksum, rejected.Code);
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        Assert.Equal((byte)100, fixture.Device.Writes[^1]);
        Assert.False(fixture.Coordinator.IsHardwarePoisoned);
        Assert.False(fixture.Coordinator.GetShutdownStatus().Enabled);
        Assert.False(fixture.Coordinator.GetShutdownStatus().Pending);
        var persisted = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal(SystemShutdownFanBehavior.KeepAt100, persisted.SystemShutdownFanBehavior);
        Assert.Null(persisted.ShutdownOffQualification);
    }

    [Fact]
    public async Task ShutdownQualification_CallerCancellationAfterOffWaitsForOnAtOneHundredRecovery()
    {
        using var offGate = new ManualResetEventSlim(initialState: false);
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Thermal.CpuTemperature = 50;
        await fixture.ReachSettledControlAsync();
        fixture.Device.ShutdownOffGate = offGate;
        using var cancellation = new CancellationTokenSource();
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-473", "settings");
        var qualification = fixture.Coordinator.QualifySystemShutdownOffAsync(
            true, peer, cancellation.Token);
        await fixture.Device.ShutdownOffStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        cancellation.Cancel();
        Assert.False(qualification.IsCompleted);
        offGate.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => qualification);

        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        Assert.True(fixture.Device.PostShutdownOffRecoveryWrite.Task.IsCompletedSuccessfully);
        Assert.False(fixture.Coordinator.IsHardwarePoisoned);
        Assert.False(fixture.Coordinator.GetShutdownStatus().Enabled);
        Assert.False(fixture.Coordinator.GetShutdownStatus().Pending);
        var persisted = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal(SystemShutdownFanBehavior.KeepAt100, persisted.SystemShutdownFanBehavior);
        Assert.Null(persisted.ShutdownOffQualification);
    }

    [Fact]
    public async Task ShutdownQualification_UnknownTimedOutOffPoisonsAndRetainsOwnershipWithoutQualification()
    {
        var previousExitCode = Environment.ExitCode;
        using var offGate = new ManualResetEventSlim(initialState: false);
        CoordinatorFixture? fixture = null;
        try
        {
            fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
            fixture.Thermal.CpuTemperature = 50;
            await fixture.ReachSettledControlAsync();
            fixture.Device.ShutdownOffGate = offGate;
            fixture.Device.IgnoreShutdownOffCancellation = true;
            var peer = new PipePeerIdentity("S-1-5-21-100-200-300-477", "settings");

            var qualification = fixture.Coordinator.QualifySystemShutdownOffAsync(
                true, peer, CancellationToken.None);
            await fixture.Device.ShutdownOffStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() => qualification);

            Assert.Equal(PipeErrorCodes.HidTimeout, rejected.Code);
            Assert.True(fixture.Coordinator.IsHardwarePoisoned);
            Assert.Equal("HID_OPERATION_TIMEOUT",
                fixture.Coordinator.GetStatus().HardwareSafety.Reason);
            Assert.Equal(7, Environment.ExitCode);
            Assert.True(fixture.WriterLease.IsHeld);
            Assert.False(fixture.Coordinator.GetShutdownStatus().Enabled);
            Assert.False(fixture.Coordinator.GetShutdownStatus().Pending);
            var persisted = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
            Assert.Equal(SystemShutdownFanBehavior.KeepAt100, persisted.SystemShutdownFanBehavior);
            Assert.Null(persisted.ShutdownOffQualification);
        }
        finally
        {
            offGate.Set();
            if (fixture is not null)
            {
                await fixture.Device.PostShutdownOffRecoveryWrite.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.True(fixture.Device.PoweredOn);
                Assert.Equal((byte)100, fixture.Device.CurrentPercent);
                await fixture.DisposeAsync();
            }

            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task ShutdownEnable_SaveFailureWithExactPreviousStateIsAuditedNotApplied()
    {
        var store = new ShutdownQualificationConfigurationStore();
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedStore: store);
        fixture.Thermal.CpuTemperature = 50;
        await fixture.ReachSettledControlAsync();
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-474", "settings");
        var qualification = JsonSerializer.SerializeToElement(
            await fixture.Coordinator.QualifySystemShutdownOffAsync(
                true, peer, CancellationToken.None));
        var qualificationId = qualification.GetProperty("qualificationId").GetGuid();
        store.EnabledSaveFailure = new InvalidOperationException(
            "Injected non-I/O shutdown enable save failure.");
        store.PublishEnabledBeforeFailure = false;

        var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.EnableSystemShutdownOffAsync(
                qualificationId, true, peer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InternalError, rejected.Code);
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        Assert.Equal(SystemShutdownFanBehavior.KeepAt100, store.Current.SystemShutdownFanBehavior);
        Assert.Null(store.Current.ShutdownOffQualification);
        Assert.False(fixture.Coordinator.GetShutdownStatus().Enabled);
        Assert.False(File.Exists(fixture.Paths.AuditMutationPendingPath));
        var records = await ReadAuditRecordsAsync(fixture.Paths.AuditLogPath);
        var record = Assert.Single(records, item =>
            item.GetProperty("eventName").GetString() == PipeOperations.ShutdownEnable);
        Assert.Equal("not-applied", record.GetProperty("result").GetString());
        Assert.NotEqual(Guid.Empty, record.GetProperty("transactionId").GetGuid());
    }

    [Fact]
    public async Task ShutdownEnable_SaveThrowsAfterPublishUsesExactReadbackAndCompletesWal()
    {
        var previousExitCode = Environment.ExitCode;
        try
        {
            var store = new ShutdownQualificationConfigurationStore();
            await using var fixture = await CoordinatorFixture.CreateAsync(
                commissioned: true,
                suppliedStore: store);
            fixture.Thermal.CpuTemperature = 50;
            await fixture.ReachSettledControlAsync();
            var peer = new PipePeerIdentity("S-1-5-21-100-200-300-479", "settings");
            var qualification = JsonSerializer.SerializeToElement(
                await fixture.Coordinator.QualifySystemShutdownOffAsync(
                    true, peer, CancellationToken.None));
            var qualificationId = qualification.GetProperty("qualificationId").GetGuid();
            store.EnabledSaveFailure = new InvalidOperationException(
                "Injected non-I/O published shutdown enable failure.");
            store.PublishEnabledBeforeFailure = true;
            _ = await fixture.Coordinator.EnableSystemShutdownOffAsync(
                qualificationId, true, peer, CancellationToken.None);

            Assert.True(fixture.Device.PoweredOn);
            Assert.Equal((byte)100, fixture.Device.CurrentPercent);
            Assert.False(fixture.Coordinator.IsHardwarePoisoned);
            Assert.True(fixture.WriterLease.IsHeld);
            Assert.True(fixture.Coordinator.GetShutdownStatus().Enabled);
            Assert.False(fixture.Coordinator.GetShutdownStatus().Pending);
            Assert.False(File.Exists(fixture.Paths.AuditMutationPendingPath));
            Assert.Equal(SystemShutdownFanBehavior.TurnOffOnSystemShutdown,
                store.Current.SystemShutdownFanBehavior);
            var records = await ReadAuditRecordsAsync(fixture.Paths.AuditLogPath);
            var record = Assert.Single(records, item =>
                item.GetProperty("eventName").GetString() == PipeOperations.ShutdownEnable);
            Assert.Equal("success", record.GetProperty("result").GetString());
            Assert.Equal("save-threw-but-current-confirmed",
                record.GetProperty("details").GetProperty("recovery").GetString());
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task ShutdownDisable_IsAtomicConfigurationOnlyAndIdentityDriftCannotRemainMatched()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            initialConfiguration: CreateQualifiedShutdownOffConfiguration());
        fixture.Thermal.CpuTemperature = 50;
        await fixture.ReachSettledControlAsync();
        Assert.True(fixture.Coordinator.GetShutdownStatus().MatchesCurrentHandle);
        fixture.WriterLease.Release();
        Assert.False(fixture.Coordinator.GetShutdownStatus().MatchesCurrentHandle);
        Assert.True(fixture.WriterLease.TryAcquire());
        Assert.True(fixture.Coordinator.GetShutdownStatus().MatchesCurrentHandle);
        fixture.Device.Identity = SupportedIdentity with { Version = 0x1603 };
        Assert.False(fixture.Coordinator.GetShutdownStatus().MatchesCurrentHandle);
        var counts = fixture.CaptureHardwareCounts();
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-475", "settings");

        var response = JsonSerializer.SerializeToElement(
            await fixture.Coordinator.DisableSystemShutdownOffAsync(
                peer, CancellationToken.None));

        Assert.Equal(counts, fixture.CaptureHardwareCounts());
        Assert.False(response.GetProperty("shutdown").GetProperty("Enabled").GetBoolean());
        Assert.False(response.GetProperty("shutdown").GetProperty("Qualified").GetBoolean());
        Assert.False(response.GetProperty("hidAccessed").GetBoolean());
        var persisted = await fixture.ConfigurationStore.LoadAsync(CancellationToken.None);
        Assert.Equal(SystemShutdownFanBehavior.KeepAt100, persisted.SystemShutdownFanBehavior);
        Assert.Null(persisted.ShutdownOffQualification);
    }

    [Fact]
    public async Task ShutdownDisable_ClearsPendingQualificationWithoutTouchingHid()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        fixture.Thermal.CpuTemperature = 50;
        await fixture.ReachSettledControlAsync();
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-485", "settings");
        _ = await fixture.Coordinator.QualifySystemShutdownOffAsync(
            true, peer, CancellationToken.None);
        Assert.True(fixture.Coordinator.GetShutdownStatus().Pending);
        var counts = fixture.CaptureHardwareCounts();

        var response = JsonSerializer.SerializeToElement(
            await fixture.Coordinator.DisableSystemShutdownOffAsync(
                peer, CancellationToken.None));

        Assert.Equal(counts, fixture.CaptureHardwareCounts());
        Assert.False(response.GetProperty("shutdown").GetProperty("Pending").GetBoolean());
        Assert.False(response.GetProperty("shutdown").GetProperty("Enabled").GetBoolean());
        Assert.False(response.GetProperty("hidAccessed").GetBoolean());
    }

    [Fact]
    public async Task ShutdownDisable_DoesNotOverwriteInvalidConfigurationFallback()
    {
        var store = new InvalidLoadConfigurationStore();
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedStore: store,
            runtimeCommissioned: true);
        var savesBeforeDisable = store.SaveCalls;
        var counts = fixture.CaptureHardwareCounts();
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-478", "settings");

        var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.DisableSystemShutdownOffAsync(peer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InternalError, rejected.Code);
        Assert.Equal(savesBeforeDisable, store.SaveCalls);
        Assert.Equal(counts, fixture.CaptureHardwareCounts());
        Assert.Equal("ConfigurationInvalid", fixture.Coordinator.GetStatus().ServiceState);
    }

    [Theory]
    [InlineData((int)ServiceStopReason.Unknown)]
    [InlineData((int)ServiceStopReason.ServiceStop)]
    public async Task HostStop_NonShutdownReasonKeepsFanAtConfirmedOneHundred(
        int reasonValue)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            initialConfiguration: CreateQualifiedShutdownOffConfiguration());
        await fixture.ReachSettledControlAsync();

        await fixture.Coordinator.FinalizeHostStopAsync((ServiceStopReason)reasonValue);

        Assert.Equal(HostStopDisposition.KeepAt100Confirmed,
            fixture.Coordinator.HostStopDisposition);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.Equal((byte)100, fixture.Device.Writes[^1]);
    }

    [Fact]
    public async Task ControllerLoopStop_InvokesFinalizerAfterBackgroundServiceStop()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        var stopIntent = new ServiceStopIntent();
        stopIntent.TrySet(ServiceStopReason.ServiceStop);
        var terminator = new RecordingProcessTerminator();
        var fatalFailure = new FatalServiceFailureHandler(
            fixture.Coordinator,
            terminator,
            NullLogger<FatalServiceFailureHandler>.Instance);
        var worker = new ControllerLoopWorker(
            fixture.Coordinator,
            stopIntent,
            fatalFailure);

        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(HostStopDisposition.KeepAt100Confirmed,
            fixture.Coordinator.HostStopDisposition);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.Empty(terminator.ExitCodes);
    }

    [Fact]
    public async Task ControllerLoop_UnexpectedInitializationFailureTerminatesWithExitCodeSeven()
    {
        var previousExitCode = Environment.ExitCode;
        try
        {
            await using var fixture = await CoordinatorFixture.CreateAsync(
                commissioned: false,
                suppliedStore: new UnexpectedLoadConfigurationStore(),
                initialize: false);
            var terminator = new RecordingProcessTerminator();
            var fatalFailure = new FatalServiceFailureHandler(
                fixture.Coordinator,
                terminator,
                NullLogger<FatalServiceFailureHandler>.Instance);
            using var worker = new ControllerLoopWorker(
                fixture.Coordinator,
                new ServiceStopIntent(),
                fatalFailure);

            await worker.StartAsync(CancellationToken.None);
            Assert.True(SpinWait.SpinUntil(
                () => terminator.ExitCodes.Count == 1,
                TimeSpan.FromSeconds(1)));

            Assert.Equal([7], terminator.ExitCodes);
            Assert.Equal(7, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task PipeWorker_UnexpectedListenerFailureTerminatesWithExitCodeSeven()
    {
        var previousExitCode = Environment.ExitCode;
        try
        {
            await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
            var terminator = new RecordingProcessTerminator();
            var fatalFailure = new FatalServiceFailureHandler(
                fixture.Coordinator,
                terminator,
                NullLogger<FatalServiceFailureHandler>.Instance);
            var handler = new PipeRequestHandler(
                fixture.Coordinator,
                new JsonlAuditLog(fixture.Paths, TimeProvider.System),
                NullLogger<PipeRequestHandler>.Instance);
            using var worker = new PipeServerWorker(
                new UnexpectedPipeFactory(),
                handler,
                fatalFailure,
                NullLogger<PipeServerWorker>.Instance);

            await worker.StartAsync(CancellationToken.None);
            Assert.True(SpinWait.SpinUntil(
                () => terminator.ExitCodes.Count == 1,
                TimeSpan.FromSeconds(1)));

            Assert.Equal([7], terminator.ExitCodes);
            Assert.Equal(7, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task HostStop_DefaultConfigurationNeverWritesOff()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        await fixture.Coordinator.TickAsync(CancellationToken.None);

        await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);

        Assert.Equal(HostStopDisposition.KeepAt100Confirmed,
            fixture.Coordinator.HostStopDisposition);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.All(fixture.Device.Writes, static percent => Assert.Equal((byte)100, percent));
    }

    [Fact]
    public async Task SameHandlePoweredOffObservationSelectsManualWithoutAnyControlWrite()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        await fixture.ReachSettledControlAsync();
        var openCalls = fixture.Locator.OpenCalls;
        var writes = fixture.Device.Writes.Count;
        var savedOnPercent = (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).ManualSpeedPercent;
        fixture.Device.SetState(100, poweredOn: false);

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(openCalls, fixture.Locator.OpenCalls);
        Assert.Equal(writes, fixture.Device.Writes.Count);
        Assert.False(fixture.Device.PoweredOn);
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        var status = fixture.Coordinator.GetStatus();
        Assert.False(status.FailSafe.Active);
        Assert.Equal(ControlMode.Manual, status.Fan.Mode);
        Assert.True(status.Fan.OffRequested);
        Assert.Equal(savedOnPercent, (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).ManualSpeedPercent);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.Equal(0, fixture.Device.LightingWrites);
        Assert.Equal(0, fixture.Device.SoftwareControlSelections);
    }

    [Fact]
    public async Task SameHandlePoweredOnSpeedObservationSelectsManualAndPersistsActualWithoutControlWrite()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        await fixture.ReachSettledControlAsync();
        var openCalls = fixture.Locator.OpenCalls;
        var writes = fixture.Device.Writes.Count;
        fixture.Device.SetState(36, poweredOn: true);

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(openCalls, fixture.Locator.OpenCalls);
        Assert.Equal(writes, fixture.Device.Writes.Count);
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal((byte)36, fixture.Device.CurrentPercent);
        var status = fixture.Coordinator.GetStatus();
        Assert.False(status.FailSafe.Active);
        Assert.Equal(ControlMode.Manual, status.Fan.Mode);
        Assert.Equal((byte)36, (await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).ManualSpeedPercent);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.Equal(0, fixture.Device.LightingWrites);
        Assert.Equal(0, fixture.Device.SoftwareControlSelections);
    }

    [Fact]
    public async Task SameHandlePoweredOffObservationNeverAttemptsRecoveryWrite()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
        await fixture.ReachSettledControlAsync();
        var openCalls = fixture.Locator.OpenCalls;
        var writes = fixture.Device.Writes.Count;
        fixture.Device.SetState(100, poweredOn: false);
        fixture.Device.WriteFailures.Enqueue(
            new IOException("Injected same-handle wake recovery write failure."));

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(openCalls, fixture.Locator.OpenCalls);
        var status = fixture.Coordinator.GetStatus();
        Assert.False(status.FailSafe.Active);
        Assert.True(status.Device.Connected);
        Assert.Equal(ControlMode.Manual, status.Fan.Mode);
        Assert.True(status.Fan.OffRequested);
        Assert.False(fixture.Device.PoweredOn);
        Assert.Equal(writes, fixture.Device.Writes.Count);
        Assert.Single(fixture.Device.WriteFailures);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.Equal(0, fixture.Device.LightingWrites);
        Assert.Equal(0, fixture.Device.SoftwareControlSelections);
    }

    [Fact]
    public async Task HostStop_KeepAt100InitialReadFailureStillSendsBoundedFallbackWrite()
    {
        var previousExitCode = Environment.ExitCode;
        try
        {
            await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
            await fixture.ReachSettledControlAsync();
            Assert.Equal((byte)68, fixture.Device.Writes[^1]);
            var writesBeforeFinalization = fixture.Device.Writes.Count;
            fixture.Device.ReadFailures.Enqueue(new IOException("Injected host-stop initial read failure."));

            await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.ServiceStop);

            Assert.Equal(writesBeforeFinalization + 1, fixture.Device.Writes.Count);
            Assert.Equal((byte)100, fixture.Device.Writes[^1]);
            Assert.Equal(HostStopDisposition.KeepAt100Unconfirmed,
                fixture.Coordinator.HostStopDisposition);
            Assert.Equal(7, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task HostStop_KeepAt100ReadbackMismatchSendsAdditionalFallbackWrite()
    {
        var previousExitCode = Environment.ExitCode;
        try
        {
            await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
            await fixture.ReachSettledControlAsync();
            var writesBeforeFinalization = fixture.Device.Writes.Count;
            fixture.Device.ReturnMismatchedReadback = true;

            await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.ServiceStop);

            Assert.Equal(writesBeforeFinalization + 2, fixture.Device.Writes.Count);
            Assert.All(
                fixture.Device.Writes.Skip(writesBeforeFinalization),
                static percent => Assert.Equal((byte)100, percent));
            Assert.Equal(HostStopDisposition.KeepAt100Unconfirmed,
                fixture.Coordinator.HostStopDisposition);
            Assert.Equal(7, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task HostStop_KeepAt100IndividualReadHasFiveHundredMillisecondDeadline()
    {
        var previousExitCode = Environment.ExitCode;
        try
        {
            await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
            await fixture.ReachSettledControlAsync();
            var writesBeforeFinalization = fixture.Device.Writes.Count;
            fixture.Device.ReadDelays.Enqueue(TimeSpan.FromSeconds(2));
            var stopwatch = Stopwatch.StartNew();

            await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.ServiceStop);

            stopwatch.Stop();
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(350), TimeSpan.FromMilliseconds(850));
            Assert.Equal(writesBeforeFinalization + 1, fixture.Device.Writes.Count);
            Assert.Equal((byte)100, fixture.Device.Writes[^1]);
            Assert.Equal(HostStopDisposition.KeepAt100Unconfirmed,
                fixture.Coordinator.HostStopDisposition);
            Assert.Equal(7, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task HostStop_QualifiedCoolSystemShutdownConfirmsOffAndIsIdempotent()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            initialConfiguration: CreateQualifiedShutdownOffConfiguration());
        await fixture.ReachSettledControlAsync();

        await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);
        var countsAfterFirstFinalization = fixture.CaptureHardwareCounts();
        await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.ServiceStop);

        Assert.Equal(HostStopDisposition.SystemShutdownOffConfirmed,
            fixture.Coordinator.HostStopDisposition);
        Assert.Equal(1, fixture.Device.ShutdownOffWrites);
        Assert.Equal(countsAfterFirstFinalization, fixture.CaptureHardwareCounts());
    }

    [Fact]
    public async Task HostStop_QualifiedConfigurationWithMissingRuntimeOwnerRefusesOff()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            runtimeOwnerMissing: true,
            initialConfiguration: CreateQualifiedShutdownOffConfiguration());
        var before = fixture.CaptureHardwareCounts();

        Assert.False(fixture.Coordinator.GetShutdownStatus().MatchesCurrentHandle);
        Assert.Equal("ConfigurationInvalid", fixture.Coordinator.GetStatus().ServiceState);
        await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);

        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.Equal(HostStopDisposition.KeepAt100Unconfirmed,
            fixture.Coordinator.HostStopDisposition);
        Assert.Equal(before, fixture.CaptureHardwareCounts());
    }

    [Fact]
    public async Task HostStop_StartupWarmupRefusesOffEvenWhenFreshTemperatureIsCool()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            initialConfiguration: CreateQualifiedShutdownOffConfiguration());
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(FailSafeReason.StartupWarmup, fixture.Coordinator.GetStatus().FailSafe.Reason);

        await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);

        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.Equal(HostStopDisposition.KeepAt100Confirmed,
            fixture.Coordinator.HostStopDisposition);
    }

    [Fact]
    public async Task HostStop_ConfigurationResetInvalidatesPreviouslySettledOffEligibility()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            initialConfiguration: CreateQualifiedShutdownOffConfiguration());
        await fixture.ReachSettledControlAsync();
        await fixture.Coordinator.SetManualSpeedAsync(
            60,
            new PipePeerIdentity("S-1-5-21-100-200-300-470", "settings"),
            CancellationToken.None);

        await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);

        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.Equal(HostStopDisposition.KeepAt100Confirmed,
            fixture.Coordinator.HostStopDisposition);
    }

    [Fact]
    public async Task HostStop_DeviceReconnectRecoveryLatchRefusesOff()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            initialConfiguration: CreateQualifiedShutdownOffConfiguration());
        await fixture.ReachSettledControlAsync();
        fixture.Device.FailReads = true;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        fixture.Device.FailReads = false;
        fixture.Thermal.CapturedAt = DateTimeOffset.UtcNow;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(FailSafeReason.DeviceReconnected,
            fixture.Coordinator.GetStatus().FailSafe.Reason);

        await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);

        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.Equal(HostStopDisposition.KeepAt100Confirmed,
            fixture.Coordinator.HostStopDisposition);
    }

    [Fact]
    public async Task HostStop_DeviceReconnectMayOffOnlyAfterRecoveryFullySettles()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedTimeProvider: time,
            initialConfiguration: CreateQualifiedShutdownOffConfiguration());
        await ReachSettledControlAsync(fixture, time);
        fixture.Device.FailReads = true;
        fixture.Thermal.CapturedAt = time.GetUtcNow();
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        fixture.Device.FailReads = false;
        time.Advance(TimeSpan.FromSeconds(1));
        fixture.Thermal.CapturedAt = time.GetUtcNow();
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(FailSafeReason.DeviceReconnected,
            fixture.Coordinator.GetStatus().FailSafe.Reason);

        for (var sample = 0; sample < 10; sample++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            fixture.Thermal.CapturedAt = time.GetUtcNow();
            await fixture.Coordinator.TickAsync(CancellationToken.None);
        }

        Assert.False(fixture.Coordinator.GetStatus().FailSafe.Active);
        await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);

        Assert.Equal(1, fixture.Device.ShutdownOffWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostStop_TelemetryRecoveryMustFullySettleBeforeOff(bool fullyRecovered)
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedTimeProvider: time,
            initialConfiguration: CreateQualifiedShutdownOffConfiguration());
        await ReachSettledControlAsync(fixture, time);

        fixture.Thermal.CpuTemperature = null;
        fixture.Thermal.CapturedAt = time.GetUtcNow();
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        fixture.Thermal.CpuTemperature = 70;

        var recoverySamples = fullyRecovered ? 10 : 1;
        for (var sample = 0; sample < recoverySamples; sample++)
        {
            if (sample > 0)
            {
                time.Advance(TimeSpan.FromSeconds(2));
            }

            fixture.Thermal.CapturedAt = time.GetUtcNow();
            await fixture.Coordinator.TickAsync(CancellationToken.None);
        }

        Assert.Equal(fullyRecovered, !fixture.Coordinator.GetStatus().FailSafe.Active);
        await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);

        Assert.Equal(fullyRecovered ? 1 : 0, fixture.Device.ShutdownOffWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostStop_EmergencyLatchRequiresSixtySecondsBelowEightyFiveBeforeOff(
        bool fullyRecovered)
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedTimeProvider: time,
            initialConfiguration: CreateQualifiedShutdownOffConfiguration());
        await ReachSettledControlAsync(fixture, time);

        fixture.Thermal.CpuTemperature = 90;
        fixture.Thermal.CapturedAt = time.GetUtcNow();
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        fixture.Thermal.CpuTemperature = 84;
        fixture.Thermal.CapturedAt = time.GetUtcNow();
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(fullyRecovered ? 60 : 59));
        fixture.Thermal.CapturedAt = time.GetUtcNow();
        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(fullyRecovered, !fixture.Coordinator.GetStatus().FailSafe.Active);
        await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);

        Assert.Equal(fullyRecovered ? 1 : 0, fixture.Device.ShutdownOffWrites);
    }

    [Fact]
    public async Task HostStop_WithoutCurrentOwnedHandleNeverEnumeratesOrOpensForOff()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            initialConfiguration: CreateQualifiedShutdownOffConfiguration());

        await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);

        Assert.Equal(HostStopDisposition.KeepAt100Unconfirmed,
            fixture.Coordinator.HostStopDisposition);
        Assert.Equal(0, fixture.Locator.EnumerateCalls);
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
    }

    [Fact]
    public async Task HostStop_DuringAttachedCommissioningConfirmsOneHundredWithoutOff()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var peer = new PipePeerIdentity("S-1-5-21-100-200-300-463", "settings");
        var sessionId = ReadSessionId(
            await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));
        await fixture.Coordinator.VerifyIdempotentAsync(
            sessionId, true, peer, CancellationToken.None);

        await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.Unknown);

        Assert.Equal(HostStopDisposition.KeepAt100Confirmed,
            fixture.Coordinator.HostStopDisposition);
        Assert.Equal((byte)100, fixture.Device.Writes[^1]);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
    }

    [Fact]
    public async Task HostStop_ConflictOrIdentityMismatchRefusesOffAndKeepsOneHundred()
    {
        await using (var conflictFixture = await CoordinatorFixture.CreateAsync(
                         commissioned: true,
                         initialConfiguration: CreateQualifiedShutdownOffConfiguration()))
        {
            await conflictFixture.ReachSettledControlAsync();
            conflictFixture.Conflict.HasConflict = true;

            await conflictFixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);

            Assert.Equal(HostStopDisposition.KeepAt100Confirmed,
                conflictFixture.Coordinator.HostStopDisposition);
            Assert.Equal(0, conflictFixture.Device.ShutdownOffWrites);
        }

        await using (var mismatchFixture = await CoordinatorFixture.CreateAsync(
                         commissioned: true,
                         initialConfiguration: CreateQualifiedShutdownOffConfiguration()))
        {
            await mismatchFixture.ReachSettledControlAsync();
            mismatchFixture.Device.Identity = SupportedIdentity with { Version = 0x1603 };

            await mismatchFixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);

            Assert.Equal(HostStopDisposition.KeepAt100Confirmed,
                mismatchFixture.Coordinator.HostStopDisposition);
            Assert.Equal(0, mismatchFixture.Device.ShutdownOffWrites);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(85d, false)]
    [InlineData(90d, false)]
    [InlineData(70d, true)]
    public async Task HostStop_MissingHotOrStaleThermalTelemetryRefusesOff(
        double? cpuCelsius,
        bool stale)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            initialConfiguration: CreateQualifiedShutdownOffConfiguration());
        await fixture.ReachSettledControlAsync();
        fixture.Thermal.CpuTemperature = cpuCelsius;
        fixture.Thermal.CapturedAt = stale
            ? DateTimeOffset.UtcNow - TimeSpan.FromSeconds(11)
            : DateTimeOffset.UtcNow;

        await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);

        Assert.Equal(HostStopDisposition.KeepAt100Confirmed,
            fixture.Coordinator.HostStopDisposition);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
        Assert.Equal((byte)100, fixture.Device.Writes[^1]);
    }

    [Fact]
    public async Task HostStop_OffReadbackFailureImmediatelyRequestsOnAtOneHundred()
    {
        var previousExitCode = Environment.ExitCode;
        try
        {
            await using var fixture = await CoordinatorFixture.CreateAsync(
                commissioned: true,
                initialConfiguration: CreateQualifiedShutdownOffConfiguration());
            await fixture.ReachSettledControlAsync();
            fixture.Device.ReturnShutdownOffMismatchedReadback = true;
            var writesBeforeFinalization = fixture.Device.Writes.Count;

            await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);

            Assert.Equal(1, fixture.Device.ShutdownOffWrites);
            Assert.Equal(writesBeforeFinalization + 3, fixture.Device.Writes.Count);
            Assert.Equal((byte)100, fixture.Device.Writes[^1]);
            Assert.Equal(HostStopDisposition.KeepAt100Unconfirmed,
                fixture.Coordinator.HostStopDisposition);
            Assert.Equal(7, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task HostStop_OffHasIndividualFiveHundredMillisecondDeadlineAndFallsBack()
    {
        var previousExitCode = Environment.ExitCode;
        try
        {
            await using var fixture = await CoordinatorFixture.CreateAsync(
                commissioned: true,
                initialConfiguration: CreateQualifiedShutdownOffConfiguration());
            await fixture.ReachSettledControlAsync();
            fixture.Device.ShutdownOffDelay = TimeSpan.FromSeconds(2);
            var writesBeforeFinalization = fixture.Device.Writes.Count;
            var stopwatch = Stopwatch.StartNew();

            await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);

            stopwatch.Stop();
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(350), TimeSpan.FromMilliseconds(850));
            Assert.Equal(1, fixture.Device.ShutdownOffWrites);
            Assert.Equal(writesBeforeFinalization + 3, fixture.Device.Writes.Count);
            Assert.Equal((byte)100, fixture.Device.Writes[^1]);
            Assert.Equal(HostStopDisposition.KeepAt100Unconfirmed,
                fixture.Coordinator.HostStopDisposition);
            Assert.Equal(7, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task HostStop_OffReadbackTimeoutStillSendsAdditionalOnAtOneHundredFallback()
    {
        var previousExitCode = Environment.ExitCode;
        try
        {
            await using var fixture = await CoordinatorFixture.CreateAsync(
                commissioned: true,
                initialConfiguration: CreateQualifiedShutdownOffConfiguration());
            await fixture.ReachSettledControlAsync();
            var writesBeforeFinalization = fixture.Device.Writes.Count;
            fixture.Device.ReadDelays.Enqueue(TimeSpan.Zero);
            fixture.Device.ReadDelays.Enqueue(TimeSpan.Zero);
            fixture.Device.ReadDelays.Enqueue(TimeSpan.Zero);
            fixture.Device.ReadDelays.Enqueue(TimeSpan.Zero);
            fixture.Device.ReadDelays.Enqueue(TimeSpan.FromSeconds(2));

            await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);

            Assert.Equal(1, fixture.Device.ShutdownOffWrites);
            Assert.Equal(writesBeforeFinalization + 3, fixture.Device.Writes.Count);
            Assert.Equal((byte)100, fixture.Device.Writes[^1]);
            Assert.Equal(HostStopDisposition.KeepAt100Unconfirmed,
                fixture.Coordinator.HostStopDisposition);
            Assert.Equal(7, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task HostStop_LateCancellationIgnoringOffRecoversOnSameWorkItemAndRetainsOwnership()
    {
        var previousExitCode = Environment.ExitCode;
        CoordinatorFixture? fixture = null;
        using var offGate = new ManualResetEventSlim(initialState: false);
        try
        {
            fixture = await CoordinatorFixture.CreateAsync(
                commissioned: true,
                initialConfiguration: CreateQualifiedShutdownOffConfiguration());
            await fixture.ReachSettledControlAsync();
            fixture.Device.ShutdownOffGate = offGate;
            fixture.Device.IgnoreShutdownOffCancellation = true;
            var stopwatch = Stopwatch.StartNew();

            var finalization = fixture.Coordinator.FinalizeHostStopAsync(
                ServiceStopReason.SystemShutdown);
            await fixture.Device.ShutdownOffStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await finalization;

            stopwatch.Stop();
            Assert.InRange(
                stopwatch.Elapsed,
                TimeSpan.FromMilliseconds(1100),
                TimeSpan.FromMilliseconds(1750));
            Assert.True(fixture.Coordinator.IsHardwarePoisoned);
            Assert.Equal(
                "TERMINAL_TRANSACTION_ABANDONED",
                fixture.Coordinator.GetStatus().HardwareSafety.Reason);
            Assert.Equal(HostStopDisposition.KeepAt100Unconfirmed,
                fixture.Coordinator.HostStopDisposition);
            Assert.True(fixture.WriterLease.IsHeld);
            Assert.Equal(0, fixture.Device.DisposeCalls);

            await fixture.Coordinator.DisposeAsync();
            Assert.True(fixture.WriterLease.IsHeld);
            Assert.Equal(0, fixture.Device.DisposeCalls);
            Assert.False(fixture.Device.PostShutdownOffRecoveryWrite.Task.IsCompleted);

            offGate.Set();
            await fixture.Device.PostShutdownOffRecoveryWrite.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var operations = fixture.Device.Operations.ToArray();
            var completedOff = Array.LastIndexOf(operations, "OFF:COMPLETE");
            var recoveredOn = Array.LastIndexOf(operations, "ON:100");
            Assert.True(completedOff >= 0);
            Assert.True(recoveredOn > completedOff);
            Assert.True(fixture.Device.PoweredOn);
            Assert.Equal((byte)100, fixture.Device.CurrentPercent);
            Assert.True(fixture.WriterLease.IsHeld);
            Assert.Equal(0, fixture.Device.DisposeCalls);
            Assert.Equal(7, Environment.ExitCode);
        }
        finally
        {
            offGate.Set();
            if (fixture is not null)
            {
                await fixture.DisposeAsync();
            }

            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public void SystemShutdownCommit_DeadlineAndExactReadbackAreLinearized()
    {
        var readback = V12FeatureReport.ParseState(
            CreateControllerStateReport(100, powerFlag: 1));
        var abandoned = new SystemShutdownOffCommit();

        Assert.True(abandoned.TryAbandon());
        Assert.False(abandoned.TryCommit(readback));
        Assert.False(abandoned.TryGetCommittedReadback(out _));

        var committed = new SystemShutdownOffCommit();
        Assert.True(committed.TryCommit(readback));
        Assert.False(committed.TryAbandon());
        Assert.True(committed.TryGetCommittedReadback(out var published));
        Assert.Same(readback, published);
    }

    [Fact]
    public async Task CompletedBoundedStagesRefreshHeartbeatAcrossLongCumulativeStartupTick()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedTimeProvider: time);
        fixture.Thermal.BeforeRead = () =>
        {
            time.Advance(TimeSpan.FromMilliseconds(1400));
            fixture.Thermal.CapturedAt = time.GetUtcNow();
        };
        fixture.Locator.BeforeEnumerate = () => time.Advance(TimeSpan.FromMilliseconds(400));
        fixture.Locator.BeforeOpen = () => time.Advance(TimeSpan.FromMilliseconds(400));
        fixture.Device.BeforeRead = () => time.Advance(TimeSpan.FromMilliseconds(400));
        fixture.Device.BeforeWrite = _ => time.Advance(TimeSpan.FromMilliseconds(400));

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.True(time.GetElapsedTime(0, time.GetTimestamp()) > TimeSpan.FromSeconds(2));
        Assert.False(fixture.Coordinator.HasStalledControlLoop(TimeSpan.FromSeconds(2)));
        var terminator = new RecordingProcessTerminator();
        var fatalFailure = new FatalServiceFailureHandler(
            fixture.Coordinator,
            terminator,
            NullLogger<FatalServiceFailureHandler>.Instance);
        var watchdog = new ControllerWatchdogWorker(
            fixture.Coordinator,
            terminator,
            fatalFailure,
            time,
            NullLogger<ControllerWatchdogWorker>.Instance);
        Assert.False(await watchdog.CheckForTerminationAsync());
        Assert.Empty(terminator.ExitCodes);
    }

    [Fact]
    public async Task HidStageStallBoundaryIsFiveHundredMilliseconds()
    {
        using var readGate = new ManualResetEventSlim(initialState: false);
        var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedTimeProvider: time);
        fixture.Device.ReadGate = readGate;
        try
        {
            var tick = fixture.Coordinator.TickAsync(CancellationToken.None);
            await fixture.Device.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            time.AdvanceMonotonic(TimeSpan.FromMilliseconds(500));
            Assert.False(fixture.Coordinator.HasStalledHardwareOperation());
            time.AdvanceMonotonic(TimeSpan.FromMilliseconds(1));
            Assert.True(fixture.Coordinator.HasStalledHardwareOperation());

            readGate.Set();
            await tick.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            readGate.Set();
        }
    }

    [Fact]
    public async Task WatchdogElapsedTimeIgnoresUtcJumpsButTerminatesAStalledStage()
    {
        var previousExitCode = Environment.ExitCode;
        using var thermalGate = new ManualResetEventSlim(initialState: false);
        try
        {
            var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
            await using var fixture = await CoordinatorFixture.CreateAsync(
                commissioned: false,
                primeCommissioningThermal: false,
                suppliedTimeProvider: time);

            time.JumpUtc(TimeSpan.FromDays(10));
            Assert.False(fixture.Coordinator.HasStalledControlLoop(TimeSpan.FromSeconds(2)));

            fixture.Thermal.ReadGate = thermalGate;
            var tick = fixture.Coordinator.TickAsync(CancellationToken.None);
            await fixture.Thermal.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            time.AdvanceMonotonic(TimeSpan.FromSeconds(3));
            time.JumpUtc(TimeSpan.FromDays(-20));

            var terminator = new RecordingProcessTerminator();
            var fatalFailure = new FatalServiceFailureHandler(
                fixture.Coordinator,
                terminator,
                NullLogger<FatalServiceFailureHandler>.Instance);
            var watchdog = new ControllerWatchdogWorker(
                fixture.Coordinator,
                terminator,
                fatalFailure,
                time,
                NullLogger<ControllerWatchdogWorker>.Instance);

            Assert.True(await watchdog.CheckForTerminationAsync());
            Assert.Equal([7], terminator.ExitCodes);
            thermalGate.Set();
            await tick.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            thermalGate.Set();
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task WatchdogStalledThermalStage_WritesOneHundredWithoutWaitingForCoordinatorGate()
    {
        var previousExitCode = Environment.ExitCode;
        using var thermalGate = new ManualResetEventSlim(initialState: false);
        using var tickCancellation = new CancellationTokenSource();
        try
        {
            var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
            await using var fixture = await CoordinatorFixture.CreateAsync(
                commissioned: true,
                suppliedTimeProvider: time);
            await fixture.ReachSettledControlAsync();
            var blockedReadStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Thermal.BeforeRead = () => blockedReadStarted.TrySetResult(true);
            fixture.Thermal.ReadGate = thermalGate;

            var tick = fixture.Coordinator.TickAsync(tickCancellation.Token);
            await blockedReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            time.AdvanceMonotonic(TimeSpan.FromSeconds(3));

            var terminator = new RecordingProcessTerminator();
            var fatalFailure = new FatalServiceFailureHandler(
                fixture.Coordinator,
                terminator,
                NullLogger<FatalServiceFailureHandler>.Instance);
            var watchdog = new ControllerWatchdogWorker(
                fixture.Coordinator,
                terminator,
                fatalFailure,
                time,
                NullLogger<ControllerWatchdogWorker>.Instance);

            Assert.True(await watchdog.CheckForTerminationAsync());
            Assert.Equal([7], terminator.ExitCodes);
            Assert.Equal((byte)100, fixture.Device.Writes.Last());
            Assert.Equal((byte)100, fixture.Device.CurrentPercent);
            using var competingLease = new SingleWriterLease(fixture.Paths.WriterLeasePath);
            Assert.False(competingLease.TryAcquire());

            tickCancellation.Cancel();
            thermalGate.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tick);
        }
        finally
        {
            tickCancellation.Cancel();
            thermalGate.Set();
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task AttachedCommissioningSession_ArmsTwoSecondHeartbeatWatchdog()
    {
        var previousExitCode = Environment.ExitCode;
        try
        {
            var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
            await using var fixture = await CoordinatorFixture.CreateAsync(
                commissioned: false,
                suppliedTimeProvider: time);
            var peer = new PipePeerIdentity("S-1-5-21-100-200-300-459", "settings");
            var sessionId = ReadSessionId(
                await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));
            _ = await fixture.Coordinator.VerifyIdempotentAsync(
                sessionId, true, peer, CancellationToken.None);
            Assert.True(fixture.Coordinator.RequiresControlHeartbeat);
            Assert.True(fixture.Coordinator.IsWatchdogArmed);
            using (var competingLease = new SingleWriterLease(fixture.Paths.WriterLeasePath))
            {
                Assert.False(competingLease.TryAcquire());
            }

            time.Advance(TimeSpan.FromSeconds(3));
            var terminator = new RecordingProcessTerminator();
            var fatalFailure = new FatalServiceFailureHandler(
                fixture.Coordinator,
                terminator,
                NullLogger<FatalServiceFailureHandler>.Instance);
            var watchdog = new ControllerWatchdogWorker(
                fixture.Coordinator,
                terminator,
                fatalFailure,
                time,
                NullLogger<ControllerWatchdogWorker>.Instance);

            Assert.True(await watchdog.CheckForTerminationAsync());
            Assert.Equal([7], terminator.ExitCodes);
            Assert.Equal(7, Environment.ExitCode);
            Assert.Equal((byte)100, fixture.Device.Writes[^1]);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task WatchdogWorker_UnexpectedFailureTerminatesWithExitCodeSeven()
    {
        var previousExitCode = Environment.ExitCode;
        try
        {
            await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
            var terminator = new RecordingProcessTerminator();
            var fatalFailure = new FatalServiceFailureHandler(
                fixture.Coordinator,
                terminator,
                NullLogger<FatalServiceFailureHandler>.Instance);
            using var watchdog = new ControllerWatchdogWorker(
                fixture.Coordinator,
                terminator,
                fatalFailure,
                new ThrowingTimeProvider(),
                NullLogger<ControllerWatchdogWorker>.Instance);

            await watchdog.StartAsync(CancellationToken.None);
            Assert.True(SpinWait.SpinUntil(
                () => terminator.ExitCodes.Count == 1,
                TimeSpan.FromSeconds(2)));

            Assert.Equal([7], terminator.ExitCodes);
            Assert.Equal(7, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task WatchdogWorker_NormalStopDoesNotInvokeFatalTermination()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var terminator = new RecordingProcessTerminator();
        var fatalFailure = new FatalServiceFailureHandler(
            fixture.Coordinator,
            terminator,
            NullLogger<FatalServiceFailureHandler>.Instance);
        using var watchdog = new ControllerWatchdogWorker(
            fixture.Coordinator,
            terminator,
            fatalFailure,
            TimeProvider.System,
            NullLogger<ControllerWatchdogWorker>.Instance);

        await watchdog.StartAsync(CancellationToken.None);
        await watchdog.StopAsync(CancellationToken.None);

        Assert.Empty(terminator.ExitCodes);
    }

    [Fact]
    public async Task ActiveDeviceDisposeFailure_PoisonsHardwareRetainsLeaseAndBlocksFurtherHardware()
    {
        var previousExitCode = Environment.ExitCode;
        CoordinatorFixture? fixture = null;
        try
        {
            fixture = await CoordinatorFixture.CreateAsync(commissioned: true);
            await fixture.Coordinator.TickAsync(CancellationToken.None);
            fixture.Device.DisposeFailure = new IOException("Injected immediate disposal failure.");
            fixture.Conflict.HasConflict = true;

            await fixture.Coordinator.TickAsync(CancellationToken.None);

            Assert.True(fixture.Coordinator.IsHardwarePoisoned);
            Assert.Equal(7, Environment.ExitCode);
            var status = fixture.Coordinator.GetStatus();
            Assert.Equal("HardwarePoisoned", status.ServiceState);
            Assert.True(status.HardwareSafety.Poisoned);
            Assert.Equal("DEVICE_DISPOSAL_FAILURE", status.HardwareSafety.Reason);
            Assert.NotNull(status.HardwareSafety.Since);
            Assert.False(status.Device.Connected);
            Assert.False(status.Device.ProtocolCompatible);
            Assert.True(status.FailSafe.Active);
            Assert.Equal((byte)100, status.Fan.RequestedPercent);

            var diagnostics = JsonSerializer.SerializeToElement(fixture.Coordinator.GetDiagnostics());
            Assert.True(diagnostics.GetProperty("hardwareSafety").GetProperty("poisoned").GetBoolean());
            Assert.Equal(
                "DEVICE_DISPOSAL_FAILURE",
                diagnostics.GetProperty("hardwareSafety").GetProperty("reason").GetString());
            var diagnosticStatus = diagnostics.GetProperty("status");
            Assert.True(diagnosticStatus.GetProperty("HardwareSafety").GetProperty("Poisoned").GetBoolean());
            Assert.Equal(
                diagnostics.GetProperty("hardwareSafety").GetProperty("reason").GetString(),
                diagnosticStatus.GetProperty("HardwareSafety").GetProperty("Reason").GetString());
            Assert.False(diagnosticStatus.GetProperty("Device").GetProperty("Connected").GetBoolean());

            using (var blockedLease = new SingleWriterLease(fixture.Paths.WriterLeasePath))
            {
                Assert.False(blockedLease.TryAcquire());
            }

            var countsAfterPoison = fixture.CaptureHardwareCounts();
            fixture.Conflict.HasConflict = false;
            await fixture.Coordinator.TickAsync(CancellationToken.None);
            await fixture.Coordinator.BestEffortWatchdogFailSafeAsync();
            await fixture.Coordinator.FinalizeHostStopAsync(ServiceStopReason.SystemShutdown);
            Assert.Equal(HostStopDisposition.KeepAt100Unconfirmed,
                fixture.Coordinator.HostStopDisposition);
            Assert.Equal(countsAfterPoison, fixture.CaptureHardwareCounts());

            var rollback = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                fixture.Coordinator.RollbackAsync(
                    startMythCool: true,
                    installer: false,
                    new PipePeerIdentity("S-1-5-21-100-200-300-460", "settings"),
                    CancellationToken.None));
            Assert.Equal(PipeErrorCodes.HidTimeout, rollback.Code);
            Assert.Equal(0, fixture.TaskManager.RestoreCalls);
            Assert.Empty(fixture.TaskManager.RestoreStartRequests);
            Assert.Equal(countsAfterPoison, fixture.CaptureHardwareCounts());

            var terminator = new RecordingProcessTerminator();
            var fatalFailure = new FatalServiceFailureHandler(
                fixture.Coordinator,
                terminator,
                NullLogger<FatalServiceFailureHandler>.Instance);
            var watchdog = new ControllerWatchdogWorker(
                fixture.Coordinator,
                terminator,
                fatalFailure,
                TimeProvider.System,
                NullLogger<ControllerWatchdogWorker>.Instance);

            Assert.True(await watchdog.CheckForTerminationAsync());
            Assert.Equal([7], terminator.ExitCodes);
            Assert.Equal(countsAfterPoison, fixture.CaptureHardwareCounts());
        }
        finally
        {
            if (fixture is not null)
            {
                await fixture.DisposeAsync();
            }

            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task CommissioningDeviceDisposeTimeout_PoisonsHardwareRetainsSessionAndLease()
    {
        var previousExitCode = Environment.ExitCode;
        CoordinatorFixture? fixture = null;
        var disposeGate = new ManualResetEventSlim(initialState: false);
        try
        {
            fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
            var peer = new PipePeerIdentity("S-1-5-21-100-200-300-461", "settings");
            var sessionId = ReadSessionId(
                await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));
            await fixture.Coordinator.VerifyIdempotentAsync(
                sessionId, true, peer, CancellationToken.None);
            fixture.Device.DisposeGate = disposeGate;

            var abort = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                fixture.Coordinator.AbortCommissioningAsync(sessionId, peer, CancellationToken.None));

            Assert.Equal(PipeErrorCodes.HidTimeout, abort.Code);
            Assert.True(fixture.Coordinator.IsHardwarePoisoned);
            Assert.Equal(7, Environment.ExitCode);
            Assert.Equal("DEVICE_DISPOSAL_TIMEOUT",
                fixture.Coordinator.GetStatus().HardwareSafety.Reason);
            Assert.Equal(1, fixture.Device.DisposeCalls);
            using (var blockedLease = new SingleWriterLease(fixture.Paths.WriterLeasePath))
            {
                Assert.False(blockedLease.TryAcquire());
            }

            var countsAfterPoison = fixture.CaptureHardwareCounts();
            var reprobe = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));
            Assert.Equal(PipeErrorCodes.HidTimeout, reprobe.Code);
            await fixture.Coordinator.TickAsync(CancellationToken.None);
            await fixture.Coordinator.BestEffortWatchdogFailSafeAsync();
            Assert.Equal(countsAfterPoison, fixture.CaptureHardwareCounts());

            var rollback = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                fixture.Coordinator.RollbackAsync(
                    startMythCool: true,
                    installer: false,
                    peer,
                    CancellationToken.None));
            Assert.Equal(PipeErrorCodes.HidTimeout, rollback.Code);
            Assert.Equal(0, fixture.TaskManager.RestoreCalls);
            Assert.Empty(fixture.TaskManager.RestoreStartRequests);
        }
        finally
        {
            disposeGate.Set();
            if (fixture is not null)
            {
                await fixture.Device.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await fixture.DisposeAsync();
            }

            disposeGate.Dispose();
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task CommissioningOpenTimeout_PoisonsLateResultAndRetainsSessionLeaseWithoutFeatureIo()
    {
        var previousExitCode = Environment.ExitCode;
        CoordinatorFixture? fixture = null;
        try
        {
            fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
            var peer = new PipePeerIdentity("S-1-5-21-100-200-300-462", "settings");
            var sessionId = ReadSessionId(
                await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));
            fixture.Locator.OpenDelay = TimeSpan.FromMilliseconds(800);
            fixture.Locator.IgnoreOpenCancellation = true;
            fixture.Locator.ReturnEphemeralDeviceOnOpen = true;

            _ = await Assert.ThrowsAsync<TimeoutException>(() =>
                fixture.Coordinator.VerifyIdempotentAsync(
                    sessionId, true, peer, CancellationToken.None));
            await fixture.Locator.LateOpenCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(SpinWait.SpinUntil(
                () => fixture.Coordinator.RetainedAbandonedOpenDeviceCount == 1,
                TimeSpan.FromSeconds(1)));

            Assert.True(fixture.Coordinator.IsHardwarePoisoned);
            Assert.Equal("DEVICE_OPEN_ABANDONED",
                fixture.Coordinator.GetStatus().HardwareSafety.Reason);
            Assert.Equal(7, Environment.ExitCode);
            Assert.Equal(0, fixture.Device.ReadCalls);
            Assert.Empty(fixture.Device.Writes);
            Assert.Equal(0, fixture.Device.ShutdownOffWrites);
            Assert.Equal(0, fixture.Device.DisposeCalls);
            Assert.Equal(1, fixture.Coordinator.RetainedAbandonedOpenDeviceCount);
            Assert.NotNull(fixture.Locator.LastEphemeralDevice);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.True(fixture.Locator.LastEphemeralDevice.TryGetTarget(out var retainedDevice));
            Assert.Equal(0, Assert.IsType<FakeDevice>(retainedDevice).DisposeCalls);
            using (var blockedLease = new SingleWriterLease(fixture.Paths.WriterLeasePath))
            {
                Assert.False(blockedLease.TryAcquire());
            }

            var countsAfterLateResult = fixture.CaptureHardwareCounts();
            var retry = await Assert.ThrowsAsync<ControllerOperationException>(() =>
                fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));
            Assert.Equal(PipeErrorCodes.HidTimeout, retry.Code);
            await fixture.Coordinator.TickAsync(CancellationToken.None);
            Assert.Equal(countsAfterLateResult, fixture.CaptureHardwareCounts());
        }
        finally
        {
            if (fixture is not null)
            {
                await fixture.DisposeAsync();
            }

            Environment.ExitCode = previousExitCode;
        }
    }

    private static TakeoverTransitionJournal CreateTransition(
        TakeoverTransitionOperation operation,
        TakeoverTransitionStage stage,
        ScheduledTaskBackupMetadata backup,
        bool startMythCool = false) => new(
        TakeoverTransitionJournal.CurrentSchemaVersion,
        Guid.NewGuid(),
        operation,
        stage,
        "S-1-5-21-test",
        backup.XmlSha256,
        backup.OriginallyEnabled,
        startMythCool,
        DateTimeOffset.UtcNow,
        CreatePendingTransitionAudit(
            operation == TakeoverTransitionOperation.Commit
                ? "commission.commit"
                : "commission.rollback"));

    private static TakeoverTransitionAudit CreatePendingTransitionAudit(string eventName)
    {
        var previous = JsonSerializer.SerializeToElement(new
        {
            commissioned = false,
            taskEnabled = true,
        });
        return new TakeoverTransitionAudit(
            eventName,
            "S-1-5-21-test",
            "test",
            TakeoverTransitionAuditResult.Pending,
            TakeoverTransitionAuditAction.Pending,
            previous,
            previous.Clone(),
            ResolvedByCallerSid: null,
            ResolvedBySource: null);
    }

    private static JsonElement CreateExpectedTakeoverAuditSnapshot(
        ControllerConfiguration configuration,
        ServiceRuntimeState runtime,
        ScheduledTaskBackupMetadata backup,
        bool taskEnabled) =>
        JsonSerializer.SerializeToElement(
            new
            {
                configurationCommissioned = configuration.Commissioned,
                runtimeCommissioned = runtime.Commissioned,
                ownerSid = runtime.OwnerSid,
                mythCoolTaskEnabled = taskEnabled,
                currentTaskXmlSha256 = backup.XmlSha256,
                exactBackupXmlMatch = taskEnabled == backup.OriginallyEnabled,
                definitionMatchesBackup = true,
                configuration = AuditConfigurationSnapshot.FromConfiguration(configuration),
                runtime = new
                {
                    commissioned = runtime.Commissioned,
                    ownerSid = runtime.OwnerSid,
                    commissionedAtUtc = runtime.CommissionedAtUtc,
                },
                task = new
                {
                    enabled = taskEnabled,
                    currentXmlSha256 = backup.XmlSha256,
                    exactBackupXmlMatch = taskEnabled == backup.OriginallyEnabled,
                    definitionMatchesBackup = true,
                },
            },
            AuditMutationJournalStore.SerializerOptions);

    private static async Task<Guid> RunToCommitAsync(CoordinatorFixture fixture, PipePeerIdentity peer)
    {
        var probe = await fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None);
        var sessionId = ReadSessionId(probe);
        await fixture.Coordinator.VerifyIdempotentAsync(sessionId, true, peer, CancellationToken.None);
        fixture.Thermal.CpuTemperature = 50;
        await fixture.Coordinator.TestCommissioningAsync(sessionId, true, peer, CancellationToken.None);
        return sessionId;
    }

    private static async Task ReachSettledControlAsync(
        CoordinatorFixture fixture,
        AdjustableTimeProvider time)
    {
        for (var sample = 0; sample < 10; sample++)
        {
            fixture.Thermal.CapturedAt = time.GetUtcNow();
            await fixture.Coordinator.TickAsync(CancellationToken.None);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.False(fixture.Coordinator.GetStatus().FailSafe.Active);
        Assert.Equal(FailSafeReason.None, fixture.Coordinator.GetStatus().FailSafe.Reason);
    }

    private static Guid ReadSessionId(object response)
    {
        var json = JsonSerializer.SerializeToElement(response);
        return json.GetProperty("sessionId").GetGuid();
    }

    [Fact]
    public async Task ForcedUninstallMarker_IsTheFirstStartupBarrierAndTickNeverTouchesHardwareOrTasks()
    {
        var markerStore = new FakeForcedUninstallRecoveryMarkerStore
        {
            Marker = CreateForcedUninstallMarker(),
        };
        var taskManager = new FakeScheduledTaskManager();
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedTaskManager: taskManager,
            suppliedForcedUninstallMarkerStore: markerStore);
        var before = fixture.CaptureHardwareCounts();

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        var status = fixture.Coordinator.GetStatus();
        Assert.Equal("ForcedUninstallRecoveryRequired", status.ServiceState);
        Assert.NotNull(status.ForcedUninstallRecovery);
        Assert.True(status.ForcedUninstallRecovery.Required);
        Assert.True(status.ForcedUninstallRecovery.MarkerValid);
        Assert.Equal(markerStore.Marker!.MarkerId.ToString("D"),
            status.ForcedUninstallRecovery.MarkerId);
        Assert.Equal(before, fixture.CaptureHardwareCounts());
        Assert.Equal(0, taskManager.BackupCalls);
        Assert.Equal(0, taskManager.RestoreCalls);
        Assert.Equal(0, taskManager.RecoveryInspectionCalls);
        Assert.Equal(0, taskManager.VerifiedBackupReadCalls);
    }

    [Fact]
    public async Task UnreadableForcedUninstallMarker_IsExplicitAndCannotBeClearedByRollback()
    {
        var markerStore = new FakeForcedUninstallRecoveryMarkerStore
        {
            LoadFailure = new UnauthorizedAccessException("Injected unreadable marker."),
        };
        var taskManager = new FakeScheduledTaskManager();
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedTaskManager: taskManager,
            suppliedForcedUninstallMarkerStore: markerStore);
        var before = fixture.CaptureHardwareCounts();

        var status = fixture.Coordinator.GetStatus();
        Assert.Equal("ForcedUninstallRecoveryMarkerInvalid", status.ServiceState);
        Assert.True(status.ForcedUninstallRecovery!.Required);
        Assert.False(status.ForcedUninstallRecovery.MarkerValid);
        await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.RollbackAsync(
                false,
                false,
                new PipePeerIdentity("S-1-5-21-test", "test"),
                CancellationToken.None));

        Assert.Equal(0, markerStore.DeleteCalls);
        Assert.Equal(before, fixture.CaptureHardwareCounts());
        Assert.Equal(0, taskManager.VerifiedBackupReadCalls);
    }

    [Fact]
    public async Task ForcedUninstallRecovery_NoBackupAndAlreadyUncommissioned_DurablyAuditsThenClearsExactMarker()
    {
        var marker = CreateForcedUninstallMarker();
        var markerStore = new FakeForcedUninstallRecoveryMarkerStore { Marker = marker };
        var taskManager = new FakeScheduledTaskManager { HasVerifiedBackup = false };
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            suppliedTaskManager: taskManager,
            suppliedForcedUninstallMarkerStore: markerStore);
        var before = fixture.CaptureHardwareCounts();

        var result = JsonSerializer.SerializeToElement(await fixture.Coordinator.RollbackAsync(
            false,
            false,
            true,
            new PipePeerIdentity("S-1-5-21-test", "test"),
            CancellationToken.None));

        Assert.False(result.GetProperty("commissioned").GetBoolean());
        Assert.False(result.GetProperty("restoredTask").GetBoolean());
        Assert.True(result.GetProperty("forcedUninstallRecoveryCleared").GetBoolean());
        Assert.Null(markerStore.Marker);
        Assert.Equal(1, markerStore.DeleteCalls);
        Assert.False(fixture.Coordinator.GetStatus().ForcedUninstallRecovery!.Required);
        Assert.Equal(before, fixture.CaptureHardwareCounts());
        Assert.Equal(0, taskManager.RecoveryInspectionCalls);
        Assert.Equal(0, taskManager.RestoreCalls);
        var records = await ReadAuditRecordsAsync(fixture.Paths.AuditLogPath);
        var recovery = Assert.Single(records, record =>
            record.TryGetProperty("transactionId", out var transaction) &&
            transaction.GetGuid() == marker.MarkerId);
        Assert.Equal("commission.rollback.forced-uninstall-recovery",
            recovery.GetProperty("eventName").GetString());
    }

    [Fact]
    public async Task ForcedUninstallRecovery_WithoutExplicitAttendedEvidencePreservesMarker()
    {
        var markerStore = new FakeForcedUninstallRecoveryMarkerStore
        {
            Marker = CreateForcedUninstallMarker(),
        };
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            suppliedTaskManager: new FakeScheduledTaskManager { HasVerifiedBackup = false },
            suppliedForcedUninstallMarkerStore: markerStore);

        var rejected = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.RollbackAsync(
                false,
                false,
                new PipePeerIdentity("S-1-5-21-test", "test"),
                CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InvalidArgument, rejected.Code);
        Assert.NotNull(markerStore.Marker);
        Assert.Equal(0, markerStore.DeleteCalls);
    }

    [Fact]
    public async Task ForcedUninstallRecovery_CommissionedState_RestoresTaskWithoutAnyHidAccess()
    {
        var marker = CreateForcedUninstallMarker();
        var markerStore = new FakeForcedUninstallRecoveryMarkerStore { Marker = marker };
        var taskManager = new FakeScheduledTaskManager { Enabled = false };
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedTaskManager: taskManager,
            suppliedForcedUninstallMarkerStore: markerStore);
        var before = fixture.CaptureHardwareCounts();

        var result = JsonSerializer.SerializeToElement(await fixture.Coordinator.RollbackAsync(
            false,
            true,
            true,
            new PipePeerIdentity("S-1-5-18", "installer"),
            CancellationToken.None));

        Assert.True(result.GetProperty("restoredTask").GetBoolean());
        Assert.False(result.GetProperty("startedMythCool").GetBoolean());
        Assert.Null(markerStore.Marker);
        Assert.True(taskManager.Enabled);
        Assert.Equal(1, taskManager.RestoreCalls);
        Assert.All(taskManager.RestoreStartRequests, Assert.False);
        Assert.Equal(before, fixture.CaptureHardwareCounts());
        Assert.Null(await fixture.TransitionJournalStore.LoadAsync(CancellationToken.None));
        Assert.False((await fixture.ConfigurationStore.LoadAsync(CancellationToken.None)).Commissioned);
        Assert.Equal(ServiceRuntimeState.Initial,
            await fixture.RuntimeStateStore.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ForcedUninstallRecovery_TaskDefinitionDriftPreservesMarkerAndNeverMutates()
    {
        var markerStore = new FakeForcedUninstallRecoveryMarkerStore
        {
            Marker = CreateForcedUninstallMarker(),
        };
        var taskManager = new FakeScheduledTaskManager
        {
            Enabled = false,
            RecoveryInspection = new ScheduledTaskRecoveryInspection(
                false,
                new string('C', 64),
                ExactBackupXmlMatch: false,
                DefinitionMatchesBackup: false),
        };
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            suppliedTaskManager: taskManager,
            suppliedForcedUninstallMarkerStore: markerStore);
        var before = fixture.CaptureHardwareCounts();

        await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.RollbackAsync(
                false,
                false,
                true,
                new PipePeerIdentity("S-1-5-21-test", "test"),
                CancellationToken.None));

        Assert.NotNull(markerStore.Marker);
        Assert.Equal(0, markerStore.DeleteCalls);
        Assert.Equal(0, taskManager.RestoreCalls);
        Assert.Equal(before, fixture.CaptureHardwareCounts());
    }

    [Fact]
    public async Task ForcedUninstallRecovery_PriorInProgressTaskMismatchIsNotReplayed()
    {
        var marker = CreateForcedUninstallMarker();
        var markerStore = new FakeForcedUninstallRecoveryMarkerStore { Marker = marker };
        var taskManager = new FakeScheduledTaskManager { Enabled = false };
        var transition = new TakeoverTransitionJournal(
            TakeoverTransitionJournal.CurrentSchemaVersion,
            marker.MarkerId,
            TakeoverTransitionOperation.Rollback,
            TakeoverTransitionStage.TaskRestoreInProgress,
            "S-1-5-21-test",
            taskManager.Backup.XmlSha256,
            taskManager.Backup.OriginallyEnabled,
            StartMythCoolRequested: false,
            marker.CreatedAtUtc,
            CreatePendingTransitionAudit(
                "commission.rollback.forced-uninstall-recovery") with
            {
                CallerSid = "S-1-5-21-test",
            });
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            transition: transition,
            suppliedTaskManager: taskManager,
            suppliedForcedUninstallMarkerStore: markerStore);

        await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.RollbackAsync(
                false,
                false,
                true,
                new PipePeerIdentity("S-1-5-21-test", "test"),
                CancellationToken.None));

        Assert.Equal(0, taskManager.RestoreCalls);
        Assert.NotNull(markerStore.Marker);
        Assert.True(TakeoverTransitionJournalStore.Equivalent(
            transition,
            Assert.IsType<TakeoverTransitionJournal>(
                await fixture.TransitionJournalStore.LoadAsync(CancellationToken.None))));
    }

    [Fact]
    public async Task ForcedUninstallRecovery_RetryAfterDurableAuditDoesNotDuplicateRecord()
    {
        var marker = CreateForcedUninstallMarker();
        var markerStore = new FakeForcedUninstallRecoveryMarkerStore { Marker = marker };
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            suppliedTaskManager: new FakeScheduledTaskManager { HasVerifiedBackup = false },
            suppliedForcedUninstallMarkerStore: markerStore,
            configureAuditMutation: async (_, audit) =>
                await audit.WriteAsync(
                    "commission.rollback.forced-uninstall-recovery",
                    "success",
                    "S-1-5-21-test",
                    "test",
                    new { priorAttempt = true },
                    CancellationToken.None,
                    marker.MarkerId));

        _ = await fixture.Coordinator.RollbackAsync(
            false,
            false,
            true,
            new PipePeerIdentity("S-1-5-21-test", "test"),
            CancellationToken.None);

        var records = await ReadAuditRecordsAsync(fixture.Paths.AuditLogPath);
        Assert.Single(records, record =>
            record.TryGetProperty("transactionId", out var transaction) &&
            transaction.GetGuid() == marker.MarkerId);
        Assert.Null(markerStore.Marker);
    }

    [Fact]
    public async Task SafetyOperationStartup_PreparedIntentIsAuditedNotAppliedWithoutHardwareAccess()
    {
        Guid transactionId = default;
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            configureSafetyOperationAudit: async (store, auditUnused) =>
            {
                var journal = store.Create(
                    PipeOperations.CommissionProbe,
                    "S-1-5-21-safety-prepared",
                    "settings",
                    SafetyOperationKind.SessionOnly,
                    new { sessionActive = false },
                    new { sessionActive = true },
                    requiresOn100Recovery: false);
                transactionId = journal.TransactionId;
                await store.PrepareAsync(journal);
            });

        Assert.Equal(default, fixture.CaptureHardwareCounts());
        Assert.Null(await fixture.SafetyOperationAuditJournalStore.LoadAsync());
        Assert.False(fixture.Coordinator.GetStatus().SafetyOperationAudit!.Pending);
        var records = await ReadAuditRecordsAsync(fixture.Paths.AuditLogPath);
        var record = Assert.Single(records, item =>
            item.GetProperty("transactionId").GetGuid() == transactionId);
        Assert.Equal("not-applied", record.GetProperty("result").GetString());
    }

    [Fact]
    public async Task SafetyOperationStartup_SessionOnlyEffectIsAuditedIndeterminateWithoutHardwareAccess()
    {
        Guid transactionId = default;
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            configureSafetyOperationAudit: async (store, _) =>
            {
                var prepared = store.Create(
                    PipeOperations.CommissionProbe,
                    "S-1-5-21-safety-session",
                    "settings",
                    SafetyOperationKind.SessionOnly,
                    new { sessionActive = false },
                    new { sessionActive = true },
                    requiresOn100Recovery: false);
                transactionId = prepared.TransactionId;
                await store.PrepareAsync(prepared);
                await store.MarkEffectInProgressAsync(prepared);
            });

        Assert.Equal(default, fixture.CaptureHardwareCounts());
        Assert.Null(await fixture.SafetyOperationAuditJournalStore.LoadAsync());
        var records = await ReadAuditRecordsAsync(fixture.Paths.AuditLogPath);
        var record = Assert.Single(records, item =>
            item.GetProperty("transactionId").GetGuid() == transactionId);
        Assert.Equal("indeterminate", record.GetProperty("result").GetString());
    }

    [Theory]
    [InlineData(PipeOperations.ShutdownQualify)]
    [InlineData(PipeOperations.PowerSet)]
    public async Task SafetyOperationStartup_HidEffectRecoversExactDeviceOnAtOneHundredThenAuditsCompensation(string operation)
    {
        Guid transactionId = default;
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            configureSafetyOperationAudit: async (store, auditUnused) =>
            {
                var prepared = store.Create(
                    operation,
                    "S-1-5-21-safety-hid",
                    "settings",
                    SafetyOperationKind.HidWithOn100Recovery,
                    operation == PipeOperations.PowerSet ? (object)new { poweredOn = true } : new { pending = false, identity = "374A:B101:1602:FF00:0001:9" },
                    operation == PipeOperations.PowerSet ? (object)new { poweredOn = false } : new { pending = true, finalPercent = 100 },
                    requiresOn100Recovery: true);
                transactionId = prepared.TransactionId;
                await store.PrepareAsync(prepared);
                await store.MarkEffectInProgressAsync(prepared);
            });

        Assert.Equal(1, fixture.Locator.EnumerateCalls);
        Assert.Equal(1, fixture.Locator.OpenCalls);
        Assert.Equal(2, fixture.Device.ReadCalls);
        Assert.Equal(new byte[] { 100 }, fixture.Device.Writes);
        Assert.Equal(new[] { "READ", "ON:100", "READ" }, fixture.Device.Operations.ToArray());
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        Assert.Null(await fixture.SafetyOperationAuditJournalStore.LoadAsync());
        var records = await ReadAuditRecordsAsync(fixture.Paths.AuditLogPath);
        var record = Assert.Single(records, item =>
            item.GetProperty("transactionId").GetGuid() == transactionId);
        Assert.Equal("compensated", record.GetProperty("result").GetString());
    }

    [Fact]
    public async Task SafetyOperationStartup_HidEffectWithoutCommissionedOwnershipStaysBlockedAndBlocksApis()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            configureSafetyOperationAudit: SeedPendingHidSafetyOperationAsync);
        var before = fixture.CaptureHardwareCounts();

        var status = fixture.Coordinator.GetStatus();
        Assert.Equal("SafetyOperationAuditRecoveryRequired", status.ServiceState);
        Assert.True(status.SafetyOperationAudit!.Blocked);
        Assert.True(status.SafetyOperationAudit.Pending);
        Assert.True(status.SafetyOperationAudit.RequiresOn100Recovery);
        Assert.Equal(default, before);

        var blocked = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.ProbeCommissioningAsync(
                true,
                new PipePeerIdentity("S-1-5-21-test", "settings"),
                CancellationToken.None));
        Assert.Equal(PipeErrorCodes.InternalError, blocked.Code);
        Assert.Equal(before, fixture.CaptureHardwareCounts());
        Assert.NotNull(await fixture.SafetyOperationAuditJournalStore.LoadAsync());
    }

    [Fact]
    public async Task SafetyOperationStartup_HidEffectWithMythCoolConflictNeverEnumeratesOrOpens()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            initialize: false,
            configureSafetyOperationAudit: SeedPendingHidSafetyOperationAsync);
        fixture.Conflict.HasConflict = true;

        await fixture.Coordinator.InitializeAsync(CancellationToken.None);

        Assert.Equal(default, fixture.CaptureHardwareCounts());
        Assert.True(fixture.Coordinator.GetStatus().SafetyOperationAudit!.Blocked);
        Assert.NotNull(await fixture.SafetyOperationAuditJournalStore.LoadAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown-firmware")]
    [InlineData("bad-readback")]
    public async Task SafetyOperationStartup_HidEffectRequiresExactDeviceAndConfirmedReadback(
        string scenario)
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: true,
            initialize: false,
            configureSafetyOperationAudit: SeedPendingHidSafetyOperationAsync);
        if (scenario == "missing")
        {
            fixture.Locator.Identities = [];
        }
        else if (scenario == "unknown-firmware")
        {
            fixture.Locator.Identities = [SupportedIdentity with { Version = 0x1603 }];
        }
        else
        {
            fixture.Device.ReturnMismatchedReadback = true;
        }

        await fixture.Coordinator.InitializeAsync(CancellationToken.None);

        var status = fixture.Coordinator.GetStatus();
        Assert.Equal("SafetyOperationAuditRecoveryRequired", status.ServiceState);
        Assert.True(status.SafetyOperationAudit!.Blocked);
        Assert.Null(status.Fan.ReportedControlPercent);
        Assert.NotNull(await fixture.SafetyOperationAuditJournalStore.LoadAsync());
        if (scenario != "bad-readback")
        {
            Assert.Empty(fixture.Device.Writes);
            Assert.Equal(0, fixture.Locator.OpenCalls);
        }
        else
        {
            Assert.Equal(new byte[] { 100 }, fixture.Device.Writes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SafetyOperationRuntime_StartBarrierWriteFailureBlocksBeforeSessionEffect(
        bool publishBeforeFailure)
    {
        var persistence = new FaultInjectingAuditMutationPersistence
        {
            FailWriteCall = 2,
            PublishBeforeWriteFailure = publishBeforeFailure,
        };
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            safetyOperationAuditStoreFactory: (_, time) =>
                new SafetyOperationAuditJournalStore(persistence, time));
        var before = fixture.CaptureHardwareCounts();

        var failure = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.ProbeCommissioningAsync(
                true,
                new PipePeerIdentity("S-1-5-21-safety-write", "settings"),
                CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InternalError, failure.Code);
        Assert.True(fixture.Coordinator.GetStatus().SafetyOperationAudit!.Blocked);
        Assert.False(fixture.Coordinator.GetStatus().Commissioning!.Active);
        Assert.Equal(before with { Enumerate = before.Enumerate + 1 }, fixture.CaptureHardwareCounts());
        Assert.NotNull(persistence.Content);
    }

    [Theory]
    [InlineData("thermal", 1, false)]
    [InlineData("thermal", 1, true)]
    [InlineData("thermal", 2, false)]
    [InlineData("thermal", 2, true)]
    [InlineData("abort", 1, false)]
    [InlineData("abort", 1, true)]
    [InlineData("abort", 2, false)]
    [InlineData("abort", 2, true)]
    [InlineData("test", 1, false)]
    [InlineData("test", 2, true)]
    public async Task SafetyOperationRuntime_StartAuditFailureStillConfirmsEmergencyOnAndReleasesSession(
        string operation,
        int failingWriteOffset,
        bool publishBeforeFailure)
    {
        var persistence = new FaultInjectingAuditMutationPersistence();
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            safetyOperationAuditStoreFactory: (_, time) =>
                new SafetyOperationAuditJournalStore(persistence, time));
        var peer = new PipePeerIdentity("S-1-5-21-safety-compensation", "settings");
        var sessionId = ReadSessionId(await fixture.Coordinator.ProbeCommissioningAsync(
            true, peer, CancellationToken.None));
        await fixture.Coordinator.VerifyIdempotentAsync(sessionId, true, peer, CancellationToken.None);
        Assert.Equal((byte)50, fixture.Device.CurrentPercent);
        var lighting = fixture.Device.LightingState;
        var readsBeforeFailure = fixture.Device.ReadCalls;
        persistence.FailWriteCall = persistence.WriteCalls + failingWriteOffset;
        persistence.PublishBeforeWriteFailure = publishBeforeFailure;
        if (operation == "thermal")
        {
            fixture.Thermal.CpuTemperature = null;
        }

        var failure = await Assert.ThrowsAsync<ControllerOperationException>(async () =>
        {
            if (operation == "thermal")
            {
                await fixture.Coordinator.TickAsync(CancellationToken.None);
            }
            else if (operation == "abort")
            {
                await fixture.Coordinator.AbortCommissioningAsync(sessionId, peer, CancellationToken.None);
            }
            else
            {
                await fixture.Coordinator.TestCommissioningAsync(sessionId, true, peer, CancellationToken.None);
            }
        });

        Assert.Equal(PipeErrorCodes.InternalError, failure.Code);
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal(lighting, fixture.Device.LightingState);
        Assert.True(fixture.Device.ReadCalls > readsBeforeFailure);
        Assert.Equal(1, fixture.Device.DisposeCalls);
        Assert.False(fixture.WriterLease.IsHeld);
        var status = fixture.Coordinator.GetStatus();
        Assert.False(status.Commissioning!.Active);
        Assert.True(status.SafetyOperationAudit!.Blocked);
        Assert.Equal("SafetyOperationAuditRecoveryRequired", status.ServiceState);
        Assert.Equal(
            failingWriteOffset == 2 || publishBeforeFailure,
            persistence.Content is not null);
        var blockedRetry = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.ProbeCommissioningAsync(true, peer, CancellationToken.None));
        Assert.Equal(PipeErrorCodes.InternalError, blockedRetry.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SafetyOperationRuntime_IdempotentAuditAppendFailureCompensatesToOneHundred(
        bool publishBeforeFailure)
    {
        FaultInjectingAuditLog? faultAudit = null;
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            auditLogFactory: (paths, time) => faultAudit = new FaultInjectingAuditLog(paths, time));
        var peer = new PipePeerIdentity("S-1-5-21-safety-idempotent-audit", "settings");
        var sessionId = ReadSessionId(await fixture.Coordinator.ProbeCommissioningAsync(
            true, peer, CancellationToken.None));
        faultAudit!.FailTransactionWrite = true;
        faultAudit.PublishBeforeFailure = publishBeforeFailure;

        var failure = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.VerifyIdempotentAsync(sessionId, true, peer, CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InternalError, failure.Code);
        Assert.Equal(new byte[] { 50, 100 }, fixture.Device.Writes);
        Assert.Equal((byte)100, fixture.Device.CurrentPercent);
        Assert.True(fixture.Device.PoweredOn);
        Assert.Equal(1, fixture.Device.DisposeCalls);
        Assert.False(fixture.WriterLease.IsHeld);
        Assert.False(fixture.Coordinator.GetStatus().Commissioning!.Active);
        Assert.True(fixture.Coordinator.GetStatus().SafetyOperationAudit!.Blocked);
        var retained = Assert.IsType<SafetyOperationAuditJournal>(
            await fixture.SafetyOperationAuditJournalStore.LoadAsync());
        Assert.Equal(SafetyOperationAuditStage.OutcomeResolved, retained.Stage);
        Assert.Equal(
            publishBeforeFailure,
            await faultAudit.ContainsTransactionAsync(retained.TransactionId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SafetyOperationRuntime_AuditAppendFailureRetainsResolvedBarrierAndRedactsStatus(
        bool publishBeforeFailure)
    {
        FaultInjectingAuditLog? faultAudit = null;
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            auditLogFactory: (paths, time) =>
                faultAudit = new FaultInjectingAuditLog(paths, time));
        faultAudit!.FailTransactionWrite = true;
        faultAudit.PublishBeforeFailure = publishBeforeFailure;
        const string callerSid = "S-1-5-21-safety-secret-caller";

        var failure = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.ProbeCommissioningAsync(
                true,
                new PipePeerIdentity(callerSid, "settings"),
                CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InternalError, failure.Code);
        var retained = Assert.IsType<SafetyOperationAuditJournal>(
            await fixture.SafetyOperationAuditJournalStore.LoadAsync());
        Assert.Equal(SafetyOperationAuditStage.OutcomeResolved, retained.Stage);
        var status = fixture.Coordinator.GetStatus().SafetyOperationAudit!;
        Assert.True(status.Blocked);
        Assert.Equal(retained.TransactionId.ToString("D"), status.TransactionId);
        var diagnostics = JsonSerializer.Serialize(fixture.Coordinator.GetDiagnostics());
        Assert.DoesNotContain(callerSid, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Paths.DataDirectory, diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            publishBeforeFailure,
            await faultAudit.ContainsTransactionAsync(retained.TransactionId));
    }

    [Fact]
    public async Task SafetyOperationRuntime_DeleteFailureRetainsAuditAppendedBarrier()
    {
        var persistence = new FaultInjectingAuditMutationPersistence { FailDelete = true };
        await using var fixture = await CoordinatorFixture.CreateAsync(
            commissioned: false,
            safetyOperationAuditStoreFactory: (_, time) =>
                new SafetyOperationAuditJournalStore(persistence, time));

        var failure = await Assert.ThrowsAsync<ControllerOperationException>(() =>
            fixture.Coordinator.ProbeCommissioningAsync(
                true,
                new PipePeerIdentity("S-1-5-21-safety-delete", "settings"),
                CancellationToken.None));

        Assert.Equal(PipeErrorCodes.InternalError, failure.Code);
        var retained = Assert.IsType<SafetyOperationAuditJournal>(
            await fixture.SafetyOperationAuditJournalStore.LoadAsync());
        Assert.Equal(SafetyOperationAuditStage.AuditAppended, retained.Stage);
        Assert.True(await new JsonlAuditLog(fixture.Paths, TimeProvider.System)
            .ContainsTransactionAsync(retained.TransactionId));
        Assert.True(fixture.Coordinator.GetStatus().SafetyOperationAudit!.Blocked);
    }

    private static async Task SeedPendingHidSafetyOperationAsync(
        SafetyOperationAuditJournalStore store,
        JsonlAuditLog auditUnused)
    {
        var prepared = store.Create(
            PipeOperations.CommissionTest,
            "S-1-5-21-safety-recovery",
            "service",
            SafetyOperationKind.HidWithOn100Recovery,
            new { stage = "IdempotentVerified", identity = "374A:B101:1602:FF00:0001:9" },
            new { stage = "TestVerified", finalPoweredOn = true, finalPercent = 100 },
            requiresOn100Recovery: true);
        await store.PrepareAsync(prepared);
        await store.MarkEffectInProgressAsync(prepared);
    }

    private static ForcedUninstallRecoveryMarker CreateForcedUninstallMarker() => new(
        ForcedUninstallRecoveryMarker.CurrentSchemaVersion,
        Guid.NewGuid(),
        ForcedUninstallRecoveryMarker.ForcedUninstallReason,
        DateTimeOffset.UtcNow);

    private static async Task<IReadOnlyList<JsonElement>> ReadAuditRecordsAsync(string path)
    {
        var records = new List<JsonElement>();
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            records.Add(document.RootElement.Clone());
        }

        return records;
    }

    private static ControllerConfiguration CreateQualifiedShutdownOffConfiguration() =>
        ControllerConfiguration.CreateDefault() with
        {
            Commissioned = true,
            SystemShutdownFanBehavior = SystemShutdownFanBehavior.TurnOffOnSystemShutdown,
            ShutdownOffQualification = new SystemShutdownOffQualification
            {
                ProtocolRevision = SystemShutdownOffQualification.CurrentProtocolRevision,
                VendorId = SupportedIdentity.VendorId,
                ProductId = SupportedIdentity.ProductId,
                FirmwareVersion = SupportedIdentity.Version,
                UsagePage = SupportedIdentity.UsagePage,
                Usage = SupportedIdentity.Usage,
                FeatureReportLength = SupportedIdentity.FeatureReportLength,
                ShutdownOffReadbackConfirmed = true,
                StartupOnAt100ReadbackConfirmed = true,
                LightingStatePreserved = true,
                QualifiedAtUtc = DateTimeOffset.UtcNow,
            },
        };

    private static byte[] CreateControllerStateReport(byte percent, byte powerFlag)
    {
        byte[] report = [0, 0x80, percent, powerFlag, 0x83, 0, 2, 128, 0];
        report[^1] = V12FeatureReport.CalculateChecksum(report.AsSpan(1, 7));
        return report;
    }

    private sealed class CoordinatorFixture : IAsyncDisposable
    {
        private readonly string directory;
        private readonly SingleWriterLease writerLease;
        private readonly HidIoDispatcher hidDispatcher;
        private readonly ThermalIoDispatcher thermalDispatcher;

        private CoordinatorFixture(
            string directory,
            ServicePaths paths,
            AtomicJsonConfigurationStore? ownedConfigurationStore,
            IConfigurationStore configurationStore,
            ServiceRuntimeStateStore runtimeStateStore,
            TakeoverTransitionJournalStore transitionJournalStore,
            AuditMutationJournalStore auditMutationJournalStore,
            SafetyOperationAuditJournalStore safetyOperationAuditJournalStore,
            FakeForcedUninstallRecoveryMarkerStore forcedUninstallRecoveryMarkerStore,
            FakeDevice device,
            FakeDeviceLocator locator,
            FakeThermalSource thermal,
            MutableConflictDetector conflict,
            FakeScheduledTaskManager taskManager,
            SingleWriterLease writerLease,
            HidIoDispatcher hidDispatcher,
            ThermalIoDispatcher thermalDispatcher,
            ControllerCoordinator coordinator)
        {
            this.directory = directory;
            Paths = paths;
            OwnedConfigurationStore = ownedConfigurationStore;
            ConfigurationStore = configurationStore;
            RuntimeStateStore = runtimeStateStore;
            TransitionJournalStore = transitionJournalStore;
            AuditMutationJournalStore = auditMutationJournalStore;
            SafetyOperationAuditJournalStore = safetyOperationAuditJournalStore;
            ForcedUninstallRecoveryMarkerStore = forcedUninstallRecoveryMarkerStore;
            Device = device;
            Locator = locator;
            Thermal = thermal;
            Conflict = conflict;
            TaskManager = taskManager;
            this.writerLease = writerLease;
            this.hidDispatcher = hidDispatcher;
            this.thermalDispatcher = thermalDispatcher;
            Coordinator = coordinator;
        }

        public AtomicJsonConfigurationStore? OwnedConfigurationStore { get; }
        public ServicePaths Paths { get; }
        public IConfigurationStore ConfigurationStore { get; }
        public ServiceRuntimeStateStore RuntimeStateStore { get; }
        public TakeoverTransitionJournalStore TransitionJournalStore { get; }
        public AuditMutationJournalStore AuditMutationJournalStore { get; }
        public SafetyOperationAuditJournalStore SafetyOperationAuditJournalStore { get; }
        public FakeForcedUninstallRecoveryMarkerStore ForcedUninstallRecoveryMarkerStore { get; }
        public FakeDevice Device { get; }
        public FakeDeviceLocator Locator { get; }
        public FakeThermalSource Thermal { get; }
        public MutableConflictDetector Conflict { get; }
        public FakeScheduledTaskManager TaskManager { get; }
        public SingleWriterLease WriterLease => writerLease;
        public HidIoDispatcher HidDispatcher => hidDispatcher;
        public ControllerCoordinator Coordinator { get; }

        public async Task CollectBackgroundThermalSamplesAsync(int count)
        {
            for (var sample = 0; sample < count; sample++)
            {
                await Coordinator.TickAsync(CancellationToken.None);
            }
        }

        public async Task ReachSettledControlAsync()
        {
            var firstSampleAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1);
            for (var sample = 0; sample < 10; sample++)
            {
                Thermal.CapturedAt = firstSampleAt.AddMilliseconds(sample);
                await Coordinator.TickAsync(CancellationToken.None);
            }

            var settledStatus = Coordinator.GetStatus();
            Assert.False(
                settledStatus.FailSafe.Active,
                $"Expected settled control, got {settledStatus.ServiceState}/" +
                $"{settledStatus.FailSafe.Reason}; hardware poison=" +
                $"{settledStatus.HardwareSafety.Reason ?? "none"}.");
            Assert.Equal(FailSafeReason.None, settledStatus.FailSafe.Reason);
        }

        public (int Enumerate, int Open, int Read, int Write, int Off, int Dispose) CaptureHardwareCounts() =>
            (Locator.EnumerateCalls, Locator.OpenCalls, Device.ReadCalls, Device.Writes.Count,
                Device.ShutdownOffWrites, Device.DisposeCalls);

        public static async Task<CoordinatorFixture> CreateAsync(
            bool commissioned,
            IConfigurationStore? suppliedStore = null,
            bool? runtimeCommissioned = null,
            bool runtimeOwnerMissing = false,
            TakeoverTransitionJournal? transition = null,
            FakeScheduledTaskManager? suppliedTaskManager = null,
            bool primeCommissioningThermal = true,
            TimeProvider? suppliedTimeProvider = null,
            ControllerConfiguration? initialConfiguration = null,
            string? rawTransitionJournalJson = null,
            bool initialize = true,
            IDeviceStateCacheStore? suppliedDeviceStateCacheStore = null,
            Func<ServicePaths, TimeProvider, AuditMutationJournalStore>? auditMutationStoreFactory = null,
            Func<ServicePaths, TimeProvider, JsonlAuditLog>? auditLogFactory = null,
            Func<AuditMutationJournalStore, JsonlAuditLog, Task>? configureAuditMutation = null,
            string? rawAuditMutationJournalJson = null,
            FakeForcedUninstallRecoveryMarkerStore? suppliedForcedUninstallMarkerStore = null,
            Func<ServicePaths, TimeProvider, SafetyOperationAuditJournalStore>? safetyOperationAuditStoreFactory = null,
            Func<SafetyOperationAuditJournalStore, JsonlAuditLog, Task>? configureSafetyOperationAudit = null,
            string? rawSafetyOperationAuditJournalJson = null,
            ISafetyProtectionStore? suppliedSafetyProtectionStore = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"v12-service-tests-{Guid.NewGuid():N}");
            var paths = new ServicePaths(directory);
            paths.EnsureDirectories();
            AtomicJsonConfigurationStore? ownedStore = null;
            var configurationStore = suppliedStore ?? (ownedStore = new AtomicJsonConfigurationStore(paths.ConfigurationPath));
            await configurationStore.SaveAsync(
                initialConfiguration ?? ControllerConfiguration.CreateDefault() with { Commissioned = commissioned },
                CancellationToken.None);
            var runtimeStore = new ServiceRuntimeStateStore(paths);
            var deviceStateCacheStore = suppliedDeviceStateCacheStore ?? new DeviceStateCacheStore(paths);
            var runtimeOwnsDevice = runtimeCommissioned ?? commissioned;
            if (runtimeOwnsDevice && runtimeOwnerMissing)
            {
                // Deliberately bypass the strict store writer to create invalid persisted
                // evidence for fail-closed startup tests.
                await File.WriteAllTextAsync(
                    paths.RuntimeStatePath,
                    JsonSerializer.Serialize(new
                    {
                        commissioned = true,
                        ownerSid = (string?)null,
                        commissionedAtUtc = DateTimeOffset.UtcNow,
                    }),
                    CancellationToken.None);
            }
            else
            {
                await runtimeStore.SaveAsync(
                    runtimeOwnsDevice
                        ? new ServiceRuntimeState(
                            true,
                            "S-1-5-21-test",
                            DateTimeOffset.UtcNow)
                        : ServiceRuntimeState.Initial,
                    CancellationToken.None);
            }
            var transitionJournalStore = new TakeoverTransitionJournalStore(paths);
            if (transition is not null)
            {
                await transitionJournalStore.SaveAsync(transition, CancellationToken.None);
            }
            else if (rawTransitionJournalJson is not null)
            {
                await File.WriteAllTextAsync(
                    paths.TransitionJournalPath,
                    rawTransitionJournalJson,
                    CancellationToken.None);
            }

            var device = new FakeDevice();
            var locator = new FakeDeviceLocator(device) { Identities = [SupportedIdentity] };
            var thermal = new FakeThermalSource(70);
            var conflict = new MutableConflictDetector();
            var taskManager = suppliedTaskManager ?? new FakeScheduledTaskManager();
            var writerLease = new SingleWriterLease(Path.Combine(directory, "device-writer.lock"));
            var timeProvider = suppliedTimeProvider ?? TimeProvider.System;
            var auditMutationJournalStore = auditMutationStoreFactory?.Invoke(paths, timeProvider) ??
                                            new AuditMutationJournalStore(paths, timeProvider);
            var safetyOperationAuditJournalStore = safetyOperationAuditStoreFactory?.Invoke(paths, timeProvider) ??
                                                   new SafetyOperationAuditJournalStore(paths, timeProvider);
            var hidDispatcher = new HidIoDispatcher(timeProvider);
            var thermalDispatcher = new ThermalIoDispatcher(timeProvider);
            var policy = new ControlPolicy(new ControlPolicyOptions
            {
                StartupValidSamples = 10,
                FaultRecoveryDuration = TimeSpan.FromSeconds(10),
            });
            var audit = auditLogFactory?.Invoke(paths, timeProvider) ??
                        new JsonlAuditLog(paths, timeProvider);
            if (rawAuditMutationJournalJson is not null)
            {
                await File.WriteAllTextAsync(
                    paths.AuditMutationPendingPath,
                    rawAuditMutationJournalJson,
                    CancellationToken.None);
            }

            if (rawSafetyOperationAuditJournalJson is not null)
            {
                await File.WriteAllTextAsync(
                    paths.SafetyOperationAuditPendingPath,
                    rawSafetyOperationAuditJournalJson,
                    CancellationToken.None);
            }

            if (configureAuditMutation is not null)
            {
                await configureAuditMutation(auditMutationJournalStore, audit);
            }
            if (configureSafetyOperationAudit is not null)
            {
                await configureSafetyOperationAudit(safetyOperationAuditJournalStore, audit);
            }
            var forcedUninstallMarkerStore = suppliedForcedUninstallMarkerStore ?? new();
            var coordinator = new ControllerCoordinator(
                configurationStore,
                runtimeStore,
                deviceStateCacheStore,
                locator,
                thermal,
                hidDispatcher,
                thermalDispatcher,
                policy,
                conflict,
                writerLease,
                taskManager,
                transitionJournalStore,
                auditMutationJournalStore,
                safetyOperationAuditJournalStore,
                forcedUninstallMarkerStore,
                audit,
                timeProvider,
                NullLogger<ControllerCoordinator>.Instance,
                suppliedSafetyProtectionStore ?? new SafetyProtectionStore(paths));
            if (initialize)
            {
                await coordinator.InitializeAsync(CancellationToken.None);
                if (!coordinator.IsCommissioned && primeCommissioningThermal)
                {
                    for (var sample = 0; sample < 10; sample++)
                    {
                        await coordinator.TickAsync(CancellationToken.None);
                    }
                }
            }

            return new CoordinatorFixture(
                directory, paths, ownedStore, configurationStore, runtimeStore, transitionJournalStore,
                auditMutationJournalStore, safetyOperationAuditJournalStore, forcedUninstallMarkerStore,
                device, locator,
                thermal, conflict, taskManager, writerLease, hidDispatcher, thermalDispatcher, coordinator);
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            hidDispatcher.Dispose();
            thermalDispatcher.Dispose();
            writerLease.Dispose();
            OwnedConfigurationStore?.Dispose();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class FakeForcedUninstallRecoveryMarkerStore :
        IForcedUninstallRecoveryMarkerStore
    {
        public ForcedUninstallRecoveryMarker? Marker { get; set; }
        public Exception? LoadFailure { get; set; }
        public Exception? DeleteFailure { get; set; }
        public int LoadCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public Task<ForcedUninstallRecoveryMarker?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCalls++;
            return LoadFailure is null
                ? Task.FromResult(Marker)
                : Task.FromException<ForcedUninstallRecoveryMarker?>(LoadFailure);
        }

        public Task<ForcedUninstallRecoveryMarker> CreateAsync(
            ForcedUninstallRecoveryMarker marker,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Marker = marker;
            return Task.FromResult(marker);
        }

        public Task DeleteExactAsync(
            ForcedUninstallRecoveryMarker expected,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCalls++;
            if (DeleteFailure is not null)
            {
                return Task.FromException(DeleteFailure);
            }

            if (Marker != expected)
            {
                return Task.FromException(
                    new InvalidDataException("Injected forced-uninstall marker mismatch."));
            }

            Marker = null;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingDeviceStateCacheStore : IDeviceStateCacheStore
    {
        private int loadCalls;

        public bool BlockLoad { get; init; }
        public bool BlockSave { get; init; }
        public int LoadCalls => Volatile.Read(ref loadCalls);
        public TaskCompletionSource<bool> LoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseLoad { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseSave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<V12DeviceState?> LoadAsync(
            DeviceIdentity expectedIdentity,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref loadCalls);
            LoadStarted.TrySetResult(true);
            if (BlockLoad)
            {
                await ReleaseLoad.Task.ConfigureAwait(false);
            }

            return null;
        }

        public async Task SaveAsync(
            DeviceIdentity identity,
            V12DeviceState state,
            DateTimeOffset capturedAtUtc,
            CancellationToken cancellationToken = default)
        {
            SaveStarted.TrySetResult(true);
            if (BlockSave)
            {
                await ReleaseSave.Task.ConfigureAwait(false);
            }
        }
    }

    private sealed class ReadyDeviceStateCacheStore(V12DeviceState? state) : IDeviceStateCacheStore
    {
        public TaskCompletionSource<bool> LoadCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<V12DeviceState?> LoadAsync(
            DeviceIdentity expectedIdentity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCompleted.TrySetResult(true);
            return Task.FromResult(state);
        }

        public Task SaveAsync(
            DeviceIdentity identity,
            V12DeviceState deviceState,
            DateTimeOffset capturedAtUtc,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class LeakyDeviceStateCacheStore(string secretPath) : IDeviceStateCacheStore
    {
        public TaskCompletionSource<bool> LoadAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<V12DeviceState?> LoadAsync(
            DeviceIdentity expectedIdentity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadAttempted.TrySetResult(true);
            return Task.FromException<V12DeviceState?>(
                new IOException($"Could not read '{secretPath}'."));
        }

        public Task SaveAsync(
            DeviceIdentity identity,
            V12DeviceState state,
            DateTimeOffset capturedAtUtc,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeDevice : IV12Device, IV12SystemShutdownDevice, IV12TrustedStateInitializer, IV12SoftwareControlDevice, IV12ManualSpeedDevice, IV12LightingDevice
    {
        public int LightingWrites { get; private set; }
        public bool IgnoreLightingWrites { get; set; }
        public bool ChangeLightingOnNextSpeedWrite { get; set; }
        public Exception? LightingWriteFailure { get; set; }
        public Action<LightingSettings>? BeforeLightingWrite { get; set; }
        public Task WriteLightingAsync(LightingSettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeLightingWrite?.Invoke(settings);
            LightingWrites++;
            Operations.Enqueue(settings.Enabled ? "RGB:ON" : "RGB:OFF");
            if (LightingWriteFailure is { } failure) throw failure;
            if (!IgnoreLightingWrites)
            {
                var report = V12FeatureReport.CreateSetLighting(state, settings);
                report[1] = 0x80;
                report[^1] = V12FeatureReport.CalculateChecksum(report.AsSpan(1, 7));
                state = V12FeatureReport.ParseState(report);
            }
            return Task.CompletedTask;
        }
        private V12DeviceState state = V12FeatureReport.ParseState(CreateStateReport(50));
        private int shutdownOffCompleted;
        private int operationCallCount;

        public DeviceIdentity Identity { get; set; } = SupportedIdentity;
        public List<byte> Writes { get; } = [];
        public ConcurrentQueue<string> Operations { get; } = new();
        public int ReadCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool FailReads { get; set; }
        public bool IgnoreNormalSpeedWrites { get; set; }
        public int SoftwareControlSelections { get; private set; }
        public Exception? SoftwareControlSelectionFailure { get; set; }
        public Task SelectSoftwareControlAtOneHundredAsync(CancellationToken cancellationToken)
        {
            SoftwareControlSelections++;
            if (SoftwareControlSelectionFailure is { } failure) throw failure;
            IgnoreNormalSpeedWrites = false;
            return WriteFanSpeedAsync(100, cancellationToken);
        }
        public List<byte> ManualSoftwareControlTargets { get; } = [];
        public int ManualSoftwareControlSelections => ManualSoftwareControlTargets.Count;
        public bool IgnoreManualSoftwareControl { get; set; }
        public Exception? ManualSoftwareControlSelectionFailure { get; set; }
        public Task SelectSoftwareControlAtSpeedAsync(byte percent, CancellationToken cancellationToken)
        {
            ManualSoftwareControlTargets.Add(percent);
            if (ManualSoftwareControlSelectionFailure is { } failure) throw failure;
            return WriteFanSpeedCoreAsync(percent, cancellationToken, manualSoftwareSelection: true);
        }
        public bool FailWrites { get; set; }
        public Action? BeforeRead { get; set; }
        public Action<byte>? BeforeWrite { get; set; }
        public TimeSpan ReadDelay { get; set; }
        public ManualResetEventSlim? ReadGate { get; set; }
        public bool IgnoreReadCancellation { get; set; }
        public TaskCompletionSource<bool> ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim? WriteGate { get; set; }
        public byte? GatedWritePercent { get; set; }
        public bool IgnoreWriteCancellation { get; set; }
        public TaskCompletionSource<byte> WriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? DisposeFailure { get; set; }
        public ManualResetEventSlim? DisposeGate { get; set; }
        public TaskCompletionSource<bool> DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> DisposeCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Queue<Exception> WriteFailures { get; } = new();
        public Queue<Exception> ReadFailures { get; } = new();
        public Queue<TimeSpan> ReadDelays { get; } = new();
        public bool ReturnMismatchedReadback { get; set; }
        public bool ReturnShutdownOffMismatchedReadback { get; set; }
        public Exception? ShutdownOffFailure { get; set; }
        public TimeSpan ShutdownOffDelay { get; set; }
        public ManualResetEventSlim? ShutdownOffGate { get; set; }
        public bool IgnoreShutdownOffCancellation { get; set; }
        public TaskCompletionSource<bool> ShutdownOffStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> PostShutdownOffRecoveryWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ShutdownOffWrites { get; private set; }
        public int OperationCallCount => Volatile.Read(ref operationCallCount);
        public int? FailAtOperationCall { get; set; }
        public Exception? ScheduledOperationFailure { get; set; }
        public bool PoweredOn => state.PoweredOn;
        public byte CurrentPercent => state.FanSpeedPercent;
        public (byte ModeAndPower, byte ColorMode, byte Speed, byte Power) LightingState =>
            (state.LightModeAndPower, state.LightColorMode, state.LightSpeed, state.LightPower);
        public int TrustedStateInitializations { get; private set; }

        public async Task<V12DeviceState> ReadStateAsync(CancellationToken cancellationToken)
        {
            BeforeRead?.Invoke();
            ReadCalls++;
            Operations.Enqueue("READ");
            ThrowIfScheduledOperationFailure();
            if (ReadGate is not null)
            {
                ReadStarted.TrySetResult(true);
                if (IgnoreReadCancellation)
                {
                    ReadGate.Wait();
                }
                else
                {
                    ReadGate.Wait(cancellationToken);
                }
            }

            var readDelay = ReadDelays.TryDequeue(out var queuedDelay) ? queuedDelay : ReadDelay;
            if (readDelay > TimeSpan.Zero)
            {
                await Task.Delay(readDelay, cancellationToken);
            }

            if (ReadFailures.TryDequeue(out var readFailure))
            {
                throw readFailure;
            }

            if (FailReads)
            {
                throw new IOException("Injected device read failure.");
            }

            if (ReturnMismatchedReadback)
            {
                var mismatchedPercent = state.FanSpeedPercent == 100 ? (byte)99 : (byte)(state.FanSpeedPercent + 1);
                return V12FeatureReport.ParseState(CreateStateReport(mismatchedPercent));
            }

            if (IgnoreNormalSpeedWrites)
            {
                var manual = state.ExportFeatureReport();
                manual[1] = 0x88;
                manual[^1] = V12FeatureReport.CalculateChecksum(manual.AsSpan(1, 7));
                return V12FeatureReport.ParseState(manual);
            }
            return state;
        }

        public Task WriteFanSpeedAsync(byte percent, CancellationToken cancellationToken) =>
            WriteFanSpeedCoreAsync(percent, cancellationToken, manualSoftwareSelection: false);

        private Task WriteFanSpeedCoreAsync(byte percent, CancellationToken cancellationToken, bool manualSoftwareSelection)
        {
            BeforeWrite?.Invoke(percent);
            Writes.Add(percent);
            if (manualSoftwareSelection ? IgnoreManualSoftwareControl : IgnoreNormalSpeedWrites)
                return Task.CompletedTask;
            Operations.Enqueue($"ON:{percent}");
            ThrowIfScheduledOperationFailure();
            if (WriteGate is not null &&
                (GatedWritePercent is null || GatedWritePercent == percent))
            {
                WriteStarted.TrySetResult(percent);
                if (IgnoreWriteCancellation)
                {
                    WriteGate.Wait();
                }
                else
                {
                    WriteGate.Wait(cancellationToken);
                }
            }

            if (WriteFailures.TryDequeue(out var failure))
            {
                return Task.FromException(failure);
            }

            if (FailWrites)
            {
                return Task.FromException(new IOException("Injected device write failure."));
            }

            if (manualSoftwareSelection) IgnoreNormalSpeedWrites = false;
            state = V12FeatureReport.ParseState(CreateStateReport(
                percent,
                0,
                state.LightModeAndPower,
                state.LightColorMode,
                state.LightSpeed,
                state.LightPower));
            if (ChangeLightingOnNextSpeedWrite)
            {
                ChangeLightingOnNextSpeedWrite = false;
                var changed = state.ExportFeatureReport();
                changed[7] ^= 1;
                changed[^1] = V12FeatureReport.CalculateChecksum(changed.AsSpan(1, 7));
                state = V12FeatureReport.ParseState(changed);
            }
            if (Volatile.Read(ref shutdownOffCompleted) != 0 && percent == 100)
            {
                PostShutdownOffRecoveryWrite.TrySetResult(true);
            }

            return Task.CompletedTask;
        }

        public Task InitializeTrustedStateAsync(
            V12DeviceState trustedState,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state = trustedState ?? throw new ArgumentNullException(nameof(trustedState));
            TrustedStateInitializations++;
            Operations.Enqueue("CACHE:INITIALIZE");
            return Task.CompletedTask;
        }

        public async Task WriteSystemShutdownOffAsync(CancellationToken cancellationToken)
        {
            ShutdownOffWrites++;
            Operations.Enqueue("OFF:START");
            ThrowIfScheduledOperationFailure();
            ShutdownOffStarted.TrySetResult(true);
            if (ShutdownOffGate is not null)
            {
                if (IgnoreShutdownOffCancellation)
                {
                    ShutdownOffGate.Wait();
                }
                else
                {
                    ShutdownOffGate.Wait(cancellationToken);
                }
            }

            if (ShutdownOffDelay > TimeSpan.Zero)
            {
                await Task.Delay(ShutdownOffDelay, cancellationToken);
            }

            if (ShutdownOffFailure is not null)
            {
                throw ShutdownOffFailure;
            }

            state = V12FeatureReport.ParseState(CreateStateReport(
                100,
                ReturnShutdownOffMismatchedReadback ? (byte)0 : (byte)1,
                state.LightModeAndPower,
                state.LightColorMode,
                state.LightSpeed,
                state.LightPower));
            Volatile.Write(ref shutdownOffCompleted, 1);
            Operations.Enqueue("OFF:COMPLETE");
        }

        public void SetState(byte percent, bool poweredOn)
        {
            state = V12FeatureReport.ParseState(CreateStateReport(
                percent,
                poweredOn ? (byte)0 : (byte)1,
                state.LightModeAndPower,
                state.LightColorMode,
                state.LightSpeed,
                state.LightPower));
        }

        public void SetLightingState(LightingSettings settings)
        {
            settings.Validate();
            state = V12FeatureReport.ParseState(CreateStateReport(
                state.FanSpeedPercent, state.PoweredOn ? (byte)0 : (byte)1,
                (byte)(settings.Mode | (settings.Enabled ? 0 : 0x80)),
                (byte)settings.Color, (byte)(3 - settings.Speed), (byte)settings.Brightness));
        }

        private void ThrowIfScheduledOperationFailure()
        {
            var operation = Interlocked.Increment(ref operationCallCount);
            if (FailAtOperationCall == operation && ScheduledOperationFailure is { } failure)
            {
                FailAtOperationCall = null;
                throw failure;
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            DisposeStarted.TrySetResult(true);
            try
            {
                DisposeGate?.Wait();
                return DisposeFailure is null
                    ? ValueTask.CompletedTask
                    : ValueTask.FromException(DisposeFailure);
            }
            finally
            {
                DisposeCompleted.TrySetResult(true);
            }
        }

        private static byte[] CreateStateReport(
            byte percent,
            byte powerFlag = 0,
            byte lightModeAndPower = 0x83,
            byte lightColorMode = 0,
            byte lightSpeed = 2,
            byte lightPower = 128)
        {
            byte[] report =
            [
                0,
                0x80,
                percent,
                powerFlag,
                lightModeAndPower,
                lightColorMode,
                lightSpeed,
                lightPower,
                0,
            ];
            report[^1] = V12FeatureReport.CalculateChecksum(report.AsSpan(1, 7));
            return report;
        }
    }

    private sealed class FakeDeviceLocator(FakeDevice device) : IV12DeviceLocator
    {
        public IReadOnlyList<DeviceIdentity> Identities { get; set; } = [];
        public int EnumerateCalls { get; private set; }
        public int OpenCalls { get; private set; }
        public DeviceIdentity? LastExpectedIdentity { get; private set; }
        public V12DeviceOpenStatus OpenStatus { get; set; } = V12DeviceOpenStatus.Opened;
        public TimeSpan OpenDelay { get; set; }
        public Action? BeforeEnumerate { get; set; }
        public Action? BeforeOpen { get; set; }
        public bool IgnoreOpenCancellation { get; set; }
        public bool ReturnEphemeralDeviceOnOpen { get; set; }
        public WeakReference<IV12Device>? LastEphemeralDevice { get; private set; }
        public TaskCompletionSource<bool> LateOpenCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<DeviceIdentity>> EnumerateAsync(CancellationToken cancellationToken)
        {
            BeforeEnumerate?.Invoke();
            EnumerateCalls++;
            return Task.FromResult(Identities);
        }

        public async Task<V12DeviceOpenResult> OpenExpectedAsync(
            DeviceIdentity expectedIdentity,
            CancellationToken cancellationToken)
        {
            BeforeOpen?.Invoke();
            OpenCalls++;
            LastExpectedIdentity = expectedIdentity;
            if (OpenDelay > TimeSpan.Zero)
            {
                await Task.Delay(
                    OpenDelay,
                    IgnoreOpenCancellation ? CancellationToken.None : cancellationToken);
            }

            LateOpenCompleted.TrySetResult(true);
            var openedDevice = device;
            if (ReturnEphemeralDeviceOnOpen)
            {
                openedDevice = new FakeDevice();
                LastEphemeralDevice = new WeakReference<IV12Device>(openedDevice);
            }

            return OpenStatus == V12DeviceOpenStatus.Opened
                ? V12DeviceOpenResult.Opened(openedDevice)
                : V12DeviceOpenResult.Failed(OpenStatus);
        }
    }

    private sealed class FakeThermalSource(double? temperature) : IThermalSource
    {
        public double? CpuTemperature { get; set; } = temperature;
        public DateTimeOffset? CapturedAt { get; set; }
        public bool FailReads { get; set; }
        public Action? BeforeRead { get; set; }
        public ManualResetEventSlim? ReadGate { get; set; }
        public bool IgnoreReadCancellation { get; set; }
        public TaskCompletionSource<bool> ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ThermalSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            BeforeRead?.Invoke();
            if (ReadGate is not null)
            {
                ReadStarted.TrySetResult(true);
                if (IgnoreReadCancellation)
                {
                    ReadGate.Wait();
                }
                else
                {
                    ReadGate.Wait(cancellationToken);
                }
            }

            if (FailReads)
            {
                return Task.FromException<ThermalSnapshot>(
                    new IOException("Injected thermal read failure."));
            }

            return Task.FromResult(
                new ThermalSnapshot(CapturedAt ?? DateTimeOffset.UtcNow, CpuTemperature, []));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MutableConflictDetector : IMythCoolConflictDetector
    {
        public bool HasConflict { get; set; }
        public bool InspectionFailed { get; set; }
        public MythCoolConflictSnapshot Inspect() =>
            InspectionFailed
                ? new(true, [], InspectionFailed: true)
                : new(HasConflict, HasConflict ? ["MythCool.exe"] : []);
    }

    private sealed class RecordingProcessTerminator : IProcessTerminator
    {
        public List<int> ExitCodes { get; } = [];

        public void Terminate(int exitCode) => ExitCodes.Add(exitCode);
    }

    private sealed class FakeScheduledTaskManager : IMythCoolScheduledTaskManager
    {
        public ScheduledTaskBackupMetadata Backup { get; set; } = new(
            ServiceConstants.MythCoolTaskName,
            true,
            new string('A', 64),
            DateTimeOffset.UtcNow);

        public int BackupCalls { get; private set; }
        public int DisableCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public int GetEnabledCalls { get; private set; }
        public int RecoveryInspectionCalls { get; private set; }
        public int VerifiedBackupReadCalls { get; private set; }
        public bool Enabled { get; set; } = true;
        public bool HasVerifiedBackup { get; set; } = true;
        public ScheduledTaskRecoveryInspection? RecoveryInspection { get; set; }
        public Exception? RecoveryInspectionFailure { get; set; }
        public bool FailBackup { get; set; }
        public bool FailRestore { get; set; }
        public bool ApplyRestoreStateBeforeFailure { get; set; }
        public Exception? DisableFailure { get; set; }
        public Exception? RestoreFailure { get; set; }
        public TaskCompletionSource<bool>? DisableGate { get; set; }
        public TaskCompletionSource<bool> DisableStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool>? RestoreGate { get; set; }
        public TaskCompletionSource<bool> RestoreStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<bool> RestoreStartRequests { get; } = [];

        public Task<ScheduledTaskBackupMetadata> BackupCurrentAsync(CancellationToken cancellationToken = default)
        {
            BackupCalls++;
            return FailBackup
                ? Task.FromException<ScheduledTaskBackupMetadata>(
                    new IOException("Injected scheduled-task backup failure."))
                : Task.FromResult(Backup);
        }

        public async Task DisableAsync(CancellationToken cancellationToken = default)
        {
            DisableCalls++;
            DisableStarted.TrySetResult(true);
            if (DisableGate is not null)
            {
                // Models the production point-of-no-cancellation contract after the mutating
                // child starts: caller cancellation cannot interrupt its exit/readback closure.
                await DisableGate.Task.ConfigureAwait(false);
            }

            Enabled = false;
            if (DisableFailure is not null)
            {
                throw DisableFailure;
            }
        }

        public async Task RestoreAsync(bool startMythCool, CancellationToken cancellationToken = default)
        {
            RestoreCalls++;
            RestoreStartRequests.Add(startMythCool);
            RestoreStarted.TrySetResult(true);
            if (RestoreGate is not null)
            {
                await RestoreGate.Task.ConfigureAwait(false);
            }

            if (ApplyRestoreStateBeforeFailure)
            {
                Enabled = Backup.OriginallyEnabled;
            }

            if (RestoreFailure is not null)
            {
                throw RestoreFailure;
            }

            if (FailRestore)
            {
                throw new IOException("Injected scheduled-task restore failure.");
            }

            Enabled = Backup.OriginallyEnabled;
        }

        public Task<bool> GetEnabledAsync(CancellationToken cancellationToken = default)
        {
            GetEnabledCalls++;
            return Task.FromResult(Enabled);
        }

        public Task<ScheduledTaskBackupMetadata?> TryReadVerifiedBackupAsync(
            CancellationToken cancellationToken = default)
        {
            VerifiedBackupReadCalls++;
            return Task.FromResult<ScheduledTaskBackupMetadata?>(HasVerifiedBackup ? Backup : null);
        }

        public Task<ScheduledTaskRecoveryInspection> InspectCurrentAgainstBackupAsync(
            ScheduledTaskBackupMetadata expectedBackup,
            CancellationToken cancellationToken = default)
        {
            RecoveryInspectionCalls++;
            if (RecoveryInspectionFailure is not null)
            {
                return Task.FromException<ScheduledTaskRecoveryInspection>(RecoveryInspectionFailure);
            }

            if (!HasVerifiedBackup || expectedBackup != Backup)
            {
                return Task.FromException<ScheduledTaskRecoveryInspection>(
                    new InvalidDataException("Injected recovery backup mismatch."));
            }

            var exact = Enabled == Backup.OriginallyEnabled;
            return Task.FromResult(RecoveryInspection ?? new ScheduledTaskRecoveryInspection(
                Enabled,
                exact ? Backup.XmlSha256 : new string('C', 64),
                exact,
                DefinitionMatchesBackup: true));
        }
    }

    private sealed class FaultInjectingAuditMutationPersistence :
        IAuditMutationJournalPersistence,
        ISafetyOperationAuditJournalPersistence
    {
        public string? Content { get; private set; }
        public int WriteCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public int? FailWriteCall { get; set; }
        public bool PublishBeforeWriteFailure { get; set; }
        public bool FailDelete { get; set; }

        public Task<string?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Content);
        }

        public Task WriteAsync(string json, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCalls++;
            if (FailWriteCall == WriteCalls)
            {
                if (PublishBeforeWriteFailure)
                {
                    Content = json;
                }

                throw new IOException($"Injected audit journal write failure {WriteCalls}.");
            }

            Content = json;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCalls++;
            if (FailDelete)
            {
                throw new IOException("Injected audit journal delete failure.");
            }

            Content = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FaultInjectingAuditLog(
        ServicePaths paths,
        TimeProvider timeProvider) : JsonlAuditLog(paths, timeProvider)
    {
        public bool FailTransactionWrite { get; set; }
        public bool PublishBeforeFailure { get; set; }

        public override async Task WriteAsync(
            string eventName,
            string result,
            string? callerSid = null,
            string? source = null,
            object? details = null,
            CancellationToken cancellationToken = default,
            Guid? transactionId = null)
        {
            if (!FailTransactionWrite || transactionId is null)
            {
                await base.WriteAsync(
                    eventName,
                    result,
                    callerSid,
                    source,
                    details,
                    cancellationToken,
                    transactionId);
                return;
            }

            if (PublishBeforeFailure)
            {
                await base.WriteAsync(
                    eventName,
                    result,
                    callerSid,
                    source,
                    details,
                    cancellationToken,
                    transactionId);
            }

            throw new IOException("Injected durable audit append failure.");
        }
    }

    private sealed class FaultInjectingConfigurationStore : IConfigurationStore
    {
        public ControllerConfiguration Current { get; private set; } =
            ControllerConfiguration.CreateDefault();

        public bool FailLoads { get; set; }

        public Task<ControllerConfiguration> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return FailLoads
                ? Task.FromException<ControllerConfiguration>(
                    new IOException("Injected configuration readback failure."))
                : Task.FromResult(Current);
        }

        public Task SaveAsync(
            ControllerConfiguration configuration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Current = configuration;
            return Task.CompletedTask;
        }
    }

    private sealed class ShutdownQualificationConfigurationStore : IConfigurationStore
    {
        private bool enabledSaveAttempted;

        public ControllerConfiguration Current { get; private set; } =
            ControllerConfiguration.CreateDefault();

        public Exception? EnabledSaveFailure { get; set; }

        public bool PublishEnabledBeforeFailure { get; set; }

        public Exception? DisabledCompensationFailure { get; set; }

        public int SaveCalls { get; private set; }

        public Task<ControllerConfiguration> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Current);

        public Task SaveAsync(
            ControllerConfiguration configuration,
            CancellationToken cancellationToken)
        {
            SaveCalls++;
            if (configuration.SystemShutdownFanBehavior ==
                SystemShutdownFanBehavior.TurnOffOnSystemShutdown)
            {
                enabledSaveAttempted = true;
                if (PublishEnabledBeforeFailure)
                {
                    Current = configuration;
                }

                if (EnabledSaveFailure is not null)
                {
                    throw EnabledSaveFailure;
                }
            }

            if (enabledSaveAttempted &&
                configuration.SystemShutdownFanBehavior == SystemShutdownFanBehavior.KeepAt100 &&
                DisabledCompensationFailure is not null)
            {
                throw DisabledCompensationFailure;
            }

            Current = configuration;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingCommitConfigurationStore(bool failCompensation) : IConfigurationStore
    {
        private int commissionedSaveAttempts;
        public ControllerConfiguration Current { get; private set; } = ControllerConfiguration.CreateDefault();

        public Task<ControllerConfiguration> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Current);

        public Task SaveAsync(ControllerConfiguration configuration, CancellationToken cancellationToken)
        {
            if (configuration.Commissioned)
            {
                commissionedSaveAttempts++;
                throw new IOException("Injected commissioned-config write failure.");
            }

            if (commissionedSaveAttempts > 0 && failCompensation)
            {
                throw new IOException("Injected compensation write failure.");
            }

            Current = configuration;
            return Task.CompletedTask;
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => current;

        public override long GetTimestamp() => Interlocked.Read(ref timestamp);

        public void Advance(TimeSpan duration)
        {
            current += duration;
            Interlocked.Add(ref timestamp, duration.Ticks);
        }

        public void AdvanceMonotonic(TimeSpan duration) =>
            Interlocked.Add(ref timestamp, duration.Ticks);

        public void JumpUtc(TimeSpan duration) => current += duration;
    }

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public override long GetTimestamp() =>
            throw new InvalidOperationException("Injected watchdog clock failure.");
    }

    private sealed class InvalidLoadConfigurationStore : IConfigurationStore
    {
        public int SaveCalls { get; private set; }

        public Task<ControllerConfiguration> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromException<ControllerConfiguration>(
                new ConfigurationValidationException("Injected invalid controller configuration."));

        public Task SaveAsync(ControllerConfiguration configuration, CancellationToken cancellationToken)
        {
            SaveCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class LeakyInvalidConfigurationStore(string secretMaterial) : IConfigurationStore
    {
        public Task<ControllerConfiguration> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<ControllerConfiguration>(
                new ConfigurationValidationException(secretMaterial));
        }

        public Task SaveAsync(
            ControllerConfiguration configuration,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class UnexpectedLoadConfigurationStore : IConfigurationStore
    {
        public Task<ControllerConfiguration> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromException<ControllerConfiguration>(
                new InvalidOperationException("Injected unexpected initialization failure."));

        public Task SaveAsync(ControllerConfiguration configuration, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class UnexpectedPipeFactory : ISecurePipeFactory
    {
        public NamedPipeServerStream Create(string pipeName, bool firstInstance) =>
            throw new InvalidOperationException("Injected unexpected listener failure.");
    }
}
