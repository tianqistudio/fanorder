using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Thermal;

namespace V12UltraController.Tests.Control;

public sealed class ControlPolicyTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Startup_RequiresConfiguredNumberOfDistinctValidCpuSamples()
    {
        var policy = CreatePolicy(startupSamples: 3);

        var first = policy.Evaluate(Input(Epoch, 60));
        var duplicate = policy.Evaluate(Input(Epoch, 60));
        var second = policy.Evaluate(Input(Epoch.AddSeconds(1), 60));
        var third = policy.Evaluate(Input(Epoch.AddSeconds(2), 60));

        Assert.True(first.FailSafeActive);
        Assert.Equal(FailSafeReason.StartupWarmup, first.FailSafeReason);
        Assert.True(duplicate.FailSafeActive);
        Assert.True(second.FailSafeActive);
        Assert.False(third.FailSafeActive);
        Assert.Equal(57, third.RequestedPercent);
    }

    [Fact]
    public void StaleTelemetry_EntersFailSafeAndRequiresContinuousRecovery()
    {
        var policy = CreatePolicy(startupSamples: 3, faultRecovery: TimeSpan.FromSeconds(10));
        WarmStartup(policy, 3);

        var staleNow = Epoch.AddSeconds(20);
        var stale = policy.Evaluate(Input(staleNow, 60, capturedAt: Epoch));
        var recovering = policy.Evaluate(Input(staleNow.AddSeconds(1), 60));
        var onlyTwoSamples = policy.Evaluate(Input(staleNow.AddSeconds(11), 60));
        var recovered = policy.Evaluate(Input(staleNow.AddSeconds(12), 60));

        Assert.True(stale.FailSafeActive);
        Assert.Equal(FailSafeReason.TelemetryStale, stale.FailSafeReason);
        Assert.Equal(100, stale.RequestedPercent);
        Assert.True(recovering.FailSafeActive);
        Assert.Equal(FailSafeReason.Recovering, recovering.FailSafeReason);
        Assert.True(onlyTwoSamples.FailSafeActive);
        Assert.Equal(FailSafeReason.Recovering, onlyTwoSamples.FailSafeReason);
        Assert.False(recovered.FailSafeActive);
    }

    [Fact]
    public void FutureDatedTelemetry_IsInvalidAndCannotAdvanceStartupWarmup()
    {
        var policy = CreatePolicy(startupSamples: 2);

        var future = policy.Evaluate(Input(
            Epoch,
            60,
            capturedAt: Epoch.AddMilliseconds(1)));
        var firstValid = policy.Evaluate(Input(Epoch.AddSeconds(1), 60));
        var secondValid = policy.Evaluate(Input(Epoch.AddSeconds(2), 60));

        Assert.True(future.FailSafeActive);
        Assert.Equal(FailSafeReason.TelemetryStale, future.FailSafeReason);
        Assert.Equal(100, future.RequestedPercent);
        Assert.True(firstValid.FailSafeActive);
        Assert.Equal(FailSafeReason.StartupWarmup, firstValid.FailSafeReason);
        Assert.False(secondValid.FailSafeActive);
    }

    [Fact]
    public void FaultRecovery_RequiresDurationEvenAfterEnoughSamples()
    {
        var policy = CreatePolicy(startupSamples: 3, faultRecovery: TimeSpan.FromSeconds(10));
        WarmStartup(policy, 3);

        _ = policy.Evaluate(Input(Epoch.AddSeconds(20), 60, capturedAt: Epoch));
        var first = policy.Evaluate(Input(Epoch.AddSeconds(21), 60));
        var second = policy.Evaluate(Input(Epoch.AddSeconds(22), 60));
        var thirdTooSoon = policy.Evaluate(Input(Epoch.AddSeconds(23), 60));
        var recovered = policy.Evaluate(Input(Epoch.AddSeconds(31), 60));

        Assert.True(first.FailSafeActive);
        Assert.True(second.FailSafeActive);
        Assert.True(thirdTooSoon.FailSafeActive);
        Assert.Equal(FailSafeReason.Recovering, thirdTooSoon.FailSafeReason);
        Assert.False(recovered.FailSafeActive);
    }

    [Fact]
    public void EmergencyTemperature_OverridesManualAndRequiresSixtySecondsBelowEightyFive()
    {
        var policy = CreatePolicy(startupSamples: 1);
        var manual = CommissionedConfiguration(ControlMode.Manual, manualPercent: 20);
        Assert.False(policy.Evaluate(Input(Epoch, 60, manual)).FailSafeActive);

        var emergency = policy.Evaluate(Input(Epoch.AddSeconds(1), 90, manual));
        var cooling = policy.Evaluate(Input(Epoch.AddSeconds(30), 84, manual));
        var almost = policy.Evaluate(Input(Epoch.AddSeconds(89), 84, manual));
        var recovered = policy.Evaluate(Input(Epoch.AddSeconds(90), 84, manual));

        Assert.Equal(100, emergency.RequestedPercent);
        Assert.Equal(FailSafeReason.EmergencyTemperature, emergency.FailSafeReason);
        Assert.True(cooling.FailSafeActive);
        Assert.True(almost.FailSafeActive);
        Assert.False(recovered.FailSafeActive);
        Assert.Equal(20, recovered.RequestedPercent);
    }

    [Fact]
    public void GpuHotterThanCpu_DrivesEmergencyProtection()
    {
        var policy = CreatePolicy(startupSamples: 1);

        var decision = policy.Evaluate(Input(Epoch, 60, gpu: [91]));

        Assert.True(decision.FailSafeActive);
        Assert.Equal(100, decision.RequestedPercent);
        Assert.Equal(91, decision.EffectiveTemperatureCelsius);
    }

    [Fact]
    public void EmergencyRecoveryTimer_IsResetByTelemetryGap()
    {
        var policy = CreatePolicy(startupSamples: 1, faultRecovery: TimeSpan.FromSeconds(10));
        _ = policy.Evaluate(Input(Epoch, 60));
        _ = policy.Evaluate(Input(Epoch.AddSeconds(1), 90));
        _ = policy.Evaluate(Input(Epoch.AddSeconds(10), 84));

        var stale = policy.Evaluate(Input(Epoch.AddSeconds(30), 84, capturedAt: Epoch.AddSeconds(10)));
        var restarted = policy.Evaluate(Input(Epoch.AddSeconds(40), 84));
        var tooSoon = policy.Evaluate(Input(Epoch.AddSeconds(99), 84));
        var emergencyClearedButFaultRecovering = policy.Evaluate(Input(Epoch.AddSeconds(100), 84));
        var recovered = policy.Evaluate(Input(Epoch.AddSeconds(110), 84));

        Assert.Equal(FailSafeReason.TelemetryStale, stale.FailSafeReason);
        Assert.Equal(FailSafeReason.EmergencyTemperature, restarted.FailSafeReason);
        Assert.Equal(FailSafeReason.EmergencyTemperature, tooSoon.FailSafeReason);
        Assert.Equal(FailSafeReason.Recovering, emergencyClearedButFaultRecovering.FailSafeReason);
        Assert.False(recovered.FailSafeActive);
    }

    [Fact]
    public void DownshiftWindow_HoldsRecentHighButAllowsImmediateIncrease()
    {
        var policy = CreatePolicy(startupSamples: 1, downshift: TimeSpan.FromSeconds(30));

        var hot = policy.Evaluate(Input(Epoch, 80));
        var cool = policy.Evaluate(Input(Epoch.AddSeconds(1), 50));
        var afterWindow = policy.Evaluate(Input(Epoch.AddSeconds(31), 50));
        var reheated = policy.Evaluate(Input(Epoch.AddSeconds(32), 85));

        Assert.Equal(79, hot.RequestedPercent);
        Assert.Equal(79, cool.RequestedPercent);
        Assert.Equal(50, afterWindow.RequestedPercent);
        Assert.Equal(90, reheated.RequestedPercent);
    }

    [Fact]
    public void WriteDecision_UsesTwoPercentDeltaAndThirtySecondReaffirmation()
    {
        var policy = CreatePolicy(startupSamples: 1);
        var initial = policy.Evaluate(Input(Epoch, 50));
        policy.RecordWriteSuccess(TimeSpan.Zero, initial.RequestedPercent);

        var onePercent = policy.Evaluate(Input(Epoch.AddSeconds(1), 51));
        var twoPercent = policy.Evaluate(Input(Epoch.AddSeconds(2), 53));
        var reaffirm = policy.Evaluate(Input(Epoch.AddSeconds(30), 51));

        Assert.Equal(50, initial.RequestedPercent);
        Assert.False(onePercent.ShouldWrite);
        Assert.True(twoPercent.ShouldWrite);
        Assert.True(reaffirm.ShouldWrite);
    }

    [Theory]
    [InlineData(FailSafeReason.EmergencyTemperature)]
    [InlineData(FailSafeReason.TelemetryStale)]
    [InlineData(FailSafeReason.HidFailure)]
    [InlineData(FailSafeReason.ControllerStalled)]
    [InlineData(FailSafeReason.ConfigurationInvalid)]
    [InlineData(FailSafeReason.DeviceReconnected)]
    public void SafetyEscalationFromNinetyNine_BypassesNormalDeltaUntilSuccessfulWrite(
        FailSafeReason reason)
    {
        var policy = CreatePolicy(startupSamples: 1);
        var manual = CommissionedConfiguration(ControlMode.Manual, manualPercent: 99);
        var initial = policy.Evaluate(Input(Epoch, 60, manual));
        Assert.False(initial.FailSafeActive);
        Assert.Equal(99, initial.RequestedPercent);
        policy.RecordWriteSuccess(TimeSpan.Zero, initial.RequestedPercent);

        var next = Input(Epoch.AddSeconds(1), 60, manual);
        var fault = reason switch
        {
            FailSafeReason.EmergencyTemperature => next with
            {
                Thermal = new ThermalSnapshot(next.Now, 90, []),
            },
            FailSafeReason.TelemetryStale => next with
            {
                Thermal = new ThermalSnapshot(next.Now, null, [50]),
            },
            FailSafeReason.HidFailure => next with { CommunicationHealthy = false },
            FailSafeReason.ControllerStalled => next with { ControlLoopHealthy = false },
            FailSafeReason.ConfigurationInvalid => next with
            {
                Configuration = manual with { ManualSpeedPercent = 1 },
            },
            FailSafeReason.DeviceReconnected => next with { DeviceReconnected = true },
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };

        var immediate = policy.Evaluate(fault);
        var unconfirmed = policy.Evaluate(fault);
        Assert.True(immediate.FailSafeActive);
        Assert.Equal(reason, immediate.FailSafeReason);
        Assert.Equal(100, immediate.RequestedPercent);
        Assert.True(immediate.ShouldWrite);
        Assert.True(unconfirmed.ShouldWrite);

        policy.RecordWriteSuccess(fault.MonotonicNow, 100);
        var confirmed = policy.Evaluate(fault);
        Assert.True(confirmed.FailSafeActive);
        Assert.Equal(100, confirmed.RequestedPercent);
        Assert.False(confirmed.ShouldWrite);
    }

    [Fact]
    public void StartupSafetyEscalationFromNinetyNine_BypassesNormalDelta()
    {
        var policy = CreatePolicy(startupSamples: 10);
        policy.RecordWriteSuccess(TimeSpan.Zero, 99);

        var warming = policy.Evaluate(Input(Epoch.AddSeconds(1), 60));

        Assert.Equal(FailSafeReason.StartupWarmup, warming.FailSafeReason);
        Assert.Equal(100, warming.RequestedPercent);
        Assert.True(warming.ShouldWrite);
    }

    [Fact]
    public void NormalManualChangeFromNinetyNineToOneHundred_RetainsDeltaAndReaffirmation()
    {
        var policy = CreatePolicy(startupSamples: 1);
        var manual = CommissionedConfiguration(ControlMode.Manual, manualPercent: 99);
        var initial = policy.Evaluate(Input(Epoch, 60, manual));
        policy.RecordWriteSuccess(TimeSpan.Zero, initial.RequestedPercent);
        manual = manual with { ManualSpeedPercent = 100 };

        var changed = policy.Evaluate(Input(Epoch.AddSeconds(1), 60, manual));
        var reaffirm = policy.Evaluate(Input(Epoch.AddSeconds(30), 60, manual));

        Assert.False(changed.FailSafeActive);
        Assert.Equal(100, changed.RequestedPercent);
        Assert.False(changed.ShouldWrite);
        Assert.True(reaffirm.ShouldWrite);
    }

    [Theory]
    [InlineData(false, true, false, FailSafeReason.DeviceUnavailable)]
    [InlineData(true, false, false, FailSafeReason.UnsupportedDevice)]
    [InlineData(true, true, true, FailSafeReason.MythCoolConflict)]
    public void UnsafeOwnershipOrDeviceState_NeverClaimsItCanWrite(
        bool connected,
        bool compatible,
        bool conflict,
        FailSafeReason reason)
    {
        var policy = CreatePolicy(startupSamples: 1);
        var input = Input(Epoch, 60) with
        {
            DeviceConnected = connected,
            ProtocolCompatible = compatible,
            MythCoolConflict = conflict,
        };

        var decision = policy.Evaluate(input);

        Assert.True(decision.FailSafeActive);
        Assert.Equal(reason, decision.FailSafeReason);
        Assert.False(decision.ShouldWrite);
    }

    [Fact]
    public void HidFailure_RequestsFailSafeWriteWhenDeviceCanStillBeAddressed()
    {
        var policy = CreatePolicy(startupSamples: 1);
        _ = policy.Evaluate(Input(Epoch, 60));

        var decision = policy.Evaluate(Input(Epoch.AddSeconds(1), 60) with { CommunicationHealthy = false });

        Assert.True(decision.FailSafeActive);
        Assert.Equal(FailSafeReason.HidFailure, decision.FailSafeReason);
        Assert.Equal(100, decision.RequestedPercent);
        Assert.True(decision.ShouldWrite);
    }

    [Fact]
    public void NotCommissionedAndInvalidConfiguration_FailClosed()
    {
        var uncommissionedPolicy = CreatePolicy(startupSamples: 1);
        var uncommissioned = uncommissionedPolicy.Evaluate(
            Input(Epoch, 60, ControllerConfiguration.CreateDefault()));

        var invalidPolicy = CreatePolicy(startupSamples: 1);
        var invalid = invalidPolicy.Evaluate(
            Input(Epoch, 60, CommissionedConfiguration() with { ManualSpeedPercent = 1 }));

        Assert.Equal(FailSafeReason.NotCommissioned, uncommissioned.FailSafeReason);
        Assert.False(uncommissioned.ShouldWrite);
        Assert.Equal(FailSafeReason.ConfigurationInvalid, invalid.FailSafeReason);
        Assert.Equal(100, invalid.RequestedPercent);
    }

    [Fact]
    public void DeviceReconnect_RequiresRecoveryBeforeLeavingOneHundredPercent()
    {
        var policy = CreatePolicy(startupSamples: 3, faultRecovery: TimeSpan.FromSeconds(10));
        WarmStartup(policy, 3);

        var reconnect = policy.Evaluate(Input(Epoch.AddSeconds(3), 60) with { DeviceReconnected = true });
        var recovering = policy.Evaluate(Input(Epoch.AddSeconds(4), 60));
        var onlyTwoSamples = policy.Evaluate(Input(Epoch.AddSeconds(14), 60));
        var recovered = policy.Evaluate(Input(Epoch.AddSeconds(15), 60));

        Assert.Equal(FailSafeReason.DeviceReconnected, reconnect.FailSafeReason);
        Assert.Equal(100, reconnect.RequestedPercent);
        Assert.Equal(FailSafeReason.Recovering, recovering.FailSafeReason);
        Assert.True(onlyTwoSamples.FailSafeActive);
        Assert.False(recovered.FailSafeActive);
    }

    [Fact]
    public void WallClockForwardJump_DoesNotShortenEmergencyRecoveryOrDownshiftWindow()
    {
        var emergencyPolicy = CreatePolicy(startupSamples: 1);
        _ = emergencyPolicy.Evaluate(Input(Epoch, 60, monotonicNow: TimeSpan.Zero));
        _ = emergencyPolicy.Evaluate(Input(
            Epoch.AddSeconds(1), 90, monotonicNow: TimeSpan.FromSeconds(1)));
        _ = emergencyPolicy.Evaluate(Input(
            Epoch.AddSeconds(2), 84, monotonicNow: TimeSpan.FromSeconds(2)));

        var jumped = emergencyPolicy.Evaluate(Input(
            Epoch.AddDays(1), 84, monotonicNow: TimeSpan.FromSeconds(3)));
        var recovered = emergencyPolicy.Evaluate(Input(
            Epoch.AddDays(1).AddSeconds(59),
            84,
            monotonicNow: TimeSpan.FromSeconds(62)));

        Assert.True(jumped.FailSafeActive);
        Assert.Equal(FailSafeReason.EmergencyTemperature, jumped.FailSafeReason);
        Assert.False(recovered.FailSafeActive);

        var downshiftPolicy = CreatePolicy(startupSamples: 1, downshift: TimeSpan.FromSeconds(30));
        var hot = downshiftPolicy.Evaluate(Input(Epoch, 80, monotonicNow: TimeSpan.Zero));
        var forwardJump = downshiftPolicy.Evaluate(Input(
            Epoch.AddDays(5), 50, monotonicNow: TimeSpan.FromSeconds(1)));
        var afterWindow = downshiftPolicy.Evaluate(Input(
            Epoch.AddDays(5).AddSeconds(30),
            50,
            monotonicNow: TimeSpan.FromSeconds(31)));

        Assert.Equal(79, hot.RequestedPercent);
        Assert.Equal(79, forwardJump.RequestedPercent);
        Assert.Equal(50, afterWindow.RequestedPercent);
    }

    [Fact]
    public void WallClockRollback_HotterCurrentSampleImmediatelyRaisesMonotonicHighWater()
    {
        var policy = CreatePolicy(startupSamples: 1, downshift: TimeSpan.FromSeconds(30));
        var cool = policy.Evaluate(Input(Epoch, 50, monotonicNow: TimeSpan.Zero));
        policy.RecordWriteSuccess(TimeSpan.Zero, cool.RequestedPercent);

        var rolledBackHot = policy.Evaluate(Input(
            Epoch.AddDays(-1),
            89,
            monotonicNow: TimeSpan.FromSeconds(1)));
        var withinWindow = policy.Evaluate(Input(
            Epoch.AddDays(-1).AddSeconds(30),
            50,
            monotonicNow: TimeSpan.FromSeconds(31)));
        var afterWindow = policy.Evaluate(Input(
            Epoch.AddDays(-1).AddSeconds(31),
            50,
            monotonicNow: TimeSpan.FromSeconds(32)));

        Assert.Equal(50, cool.RequestedPercent);
        Assert.Equal(89, rolledBackHot.EffectiveTemperatureCelsius);
        Assert.Equal(98, rolledBackHot.RequestedPercent);
        Assert.True(rolledBackHot.ShouldWrite);
        Assert.Equal(98, withinWindow.RequestedPercent);
        Assert.Equal(50, afterWindow.RequestedPercent);
    }

    [Fact]
    public void WallClockRollback_DoesNotReplayOlderSamplesThroughFaultRecovery()
    {
        var policy = CreatePolicy(startupSamples: 2, faultRecovery: TimeSpan.FromSeconds(10));
        _ = policy.Evaluate(Input(Epoch, 60, monotonicNow: TimeSpan.Zero));
        _ = policy.Evaluate(Input(
            Epoch.AddSeconds(1), 60, monotonicNow: TimeSpan.FromSeconds(1)));
        _ = policy.Evaluate(Input(
            Epoch.AddSeconds(20),
            60,
            capturedAt: Epoch,
            monotonicNow: TimeSpan.FromSeconds(2)));

        var older = policy.Evaluate(Input(
            Epoch.AddDays(-1), 60, monotonicNow: TimeSpan.FromSeconds(3)));
        var repeated = policy.Evaluate(Input(
            Epoch.AddDays(-1).AddSeconds(10),
            60,
            capturedAt: Epoch.AddDays(-1),
            monotonicNow: TimeSpan.FromSeconds(13)));
        var stillOlder = policy.Evaluate(Input(
            Epoch.AddDays(-1).AddSeconds(11),
            60,
            monotonicNow: TimeSpan.FromSeconds(14)));

        Assert.True(older.FailSafeActive);
        Assert.True(repeated.FailSafeActive);
        Assert.Equal(FailSafeReason.Recovering, repeated.FailSafeReason);
        Assert.True(stillOlder.FailSafeActive);
        Assert.Equal(FailSafeReason.Recovering, stillOlder.FailSafeReason);
    }

    [Fact]
    public void WallClockRollback_NewMonotonicAcquisitionsCanCompleteFaultRecovery()
    {
        var policy = CreatePolicy(startupSamples: 2, faultRecovery: TimeSpan.FromSeconds(10));
        _ = policy.Evaluate(Input(
            Epoch,
            60,
            monotonicNow: TimeSpan.Zero,
            thermalSampleSequence: 1));
        _ = policy.Evaluate(Input(
            Epoch.AddSeconds(1),
            60,
            monotonicNow: TimeSpan.FromSeconds(1),
            thermalSampleSequence: 2));
        _ = policy.Evaluate(Input(
            Epoch.AddSeconds(20),
            60,
            capturedAt: Epoch,
            monotonicNow: TimeSpan.FromSeconds(2),
            thermalSampleSequence: 3));

        var firstAfterRollback = policy.Evaluate(Input(
            Epoch.AddDays(-1),
            60,
            monotonicNow: TimeSpan.FromSeconds(3),
            thermalSampleSequence: 4));
        var recovered = policy.Evaluate(Input(
            Epoch.AddDays(-1).AddSeconds(10),
            60,
            monotonicNow: TimeSpan.FromSeconds(13),
            thermalSampleSequence: 5));

        Assert.True(firstAfterRollback.FailSafeActive);
        Assert.Equal(FailSafeReason.Recovering, firstAfterRollback.FailSafeReason);
        Assert.False(recovered.FailSafeActive);
    }

    [Fact]
    public void ReplayedAcquisitionSequenceCannotAdvanceStartupWithChangingWallClock()
    {
        var policy = CreatePolicy(startupSamples: 2);

        var first = policy.Evaluate(Input(
            Epoch,
            60,
            thermalSampleSequence: 100));
        var replayed = policy.Evaluate(Input(
            Epoch.AddSeconds(1),
            60,
            thermalSampleSequence: 100));
        var genuinelyNew = policy.Evaluate(Input(
            Epoch.AddSeconds(2),
            60,
            thermalSampleSequence: 101));

        Assert.True(first.FailSafeActive);
        Assert.True(replayed.FailSafeActive);
        Assert.Equal(FailSafeReason.StartupWarmup, replayed.FailSafeReason);
        Assert.False(genuinelyNew.FailSafeActive);
    }

    private static void WarmStartup(ControlPolicy policy, int sampleCount)
    {
        for (var index = 0; index < sampleCount; index++)
        {
            _ = policy.Evaluate(Input(Epoch.AddSeconds(index), 60));
        }
    }

    private static ControlPolicy CreatePolicy(
        int startupSamples,
        TimeSpan? faultRecovery = null,
        TimeSpan? downshift = null) => new(
        new ControlPolicyOptions
        {
            StartupValidSamples = startupSamples,
            FaultRecoveryDuration = faultRecovery ?? TimeSpan.FromSeconds(10),
            DownshiftWindow = downshift ?? TimeSpan.FromSeconds(30),
        });

    private static ControllerConfiguration CommissionedConfiguration(
        ControlMode mode = ControlMode.Auto,
        byte manualPercent = 50) => new()
        {
            Commissioned = true,
            Mode = mode,
            ManualSpeedPercent = manualPercent,
        };

    private static ControlInput Input(
        DateTimeOffset now,
        double? cpu,
        ControllerConfiguration? configuration = null,
        DateTimeOffset? capturedAt = null,
        IReadOnlyList<double>? gpu = null,
        TimeSpan? monotonicNow = null,
        long? thermalSampleSequence = null)
    {
        var effectiveMonotonicNow = monotonicNow ?? now - Epoch;
        return new ControlInput(
            now,
            configuration ?? CommissionedConfiguration(),
            new ThermalSnapshot(capturedAt ?? now, cpu, gpu ?? []),
            DeviceConnected: true,
            ProtocolCompatible: true,
            CommunicationHealthy: true,
            MonotonicNow: effectiveMonotonicNow,
            ThermalSampleSequence: thermalSampleSequence ?? now.UtcTicks);
    }
}
