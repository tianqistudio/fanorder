using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Configuration;

namespace V12UltraController.Service;

internal sealed partial class ControllerCoordinator
{
    public object GetStartup()
    {
        var result = startupSettings.Read();
        return new
        {
            result.Enabled,
            result.Changed,
            result.ReadbackConfirmed,
            startupFanPercent = configuration.StartupFanPercent,
            defaultStartupFanPercent = ControllerConfiguration.DefaultStartupFanPercent,
        };
    }
    public async Task<object> SetStartupAsync(bool enabled, PipePeerIdentity peer, CancellationToken cancellationToken) =>
        await startupSettings.SetAsync(enabled, peer, cancellationToken).ConfigureAwait(false);

    public object GetStartupFan() => new
    {
        percent = configuration.StartupFanPercent,
        defaultPercent = ControllerConfiguration.DefaultStartupFanPercent,
        readbackConfirmed = true,
    };

    public async Task<object> SetStartupFanPercentAsync(
        byte percent,
        PipePeerIdentity peer,
        CancellationToken cancellationToken)
    {
        if (percent is < 10 or > 100)
            throw Invalid("启动后起始转速必须在 10% 到 100% 之间。");

        await UpdateConfigurationAsync(
                current => current with { StartupFanPercent = percent },
                PipeOperations.StartupFanSet,
                peer,
                cancellationToken)
            .ConfigureAwait(false);
        return new
        {
            percent = configuration.StartupFanPercent,
            defaultPercent = ControllerConfiguration.DefaultStartupFanPercent,
            readbackConfirmed = true,
        };
    }

    private readonly ManualFanPowerPolicy manualPower = new();

    private ManualFanPowerInput CreateManualPowerInput() => new(
        configuration.Commissioned && runtimeState.Commissioned && writerLease.IsHeld && device is not null,
        !IsHardwarePoisoned && !hardwareAccessBlocked && configurationError is null,
        lastDecision?.FailSafeActive != false,
        lastThermal.CpuIsValid,
        lastThermal.ControlCelsius,
        timeProvider.GetUtcNow() - lastThermal.CapturedAt);

    private async Task<object> SetManualPowerCoreAsync(
        bool poweredOn, bool confirmed, PipePeerIdentity peer, CancellationToken cancellationToken, long manualRevision)
    {
        if (AutomaticAdjustmentsDisabled)
        {
            if (!poweredOn && !confirmed) throw Invalid("Explicit OFF confirmation is required.");
            var lightingRestored = await ExplicitManualCommandAsync(configuration.ManualSpeedPercent, poweredOn, peer, cancellationToken, manualRevision).ConfigureAwait(false);
            return new { poweredOn, readbackConfirmed = true, lightingLinked = true, lightingRestored };
        }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken = ControllerRequestAdmissionScope.Admit(cancellationToken);
            EnsureInitialized();
            RequireCurrentManualRequest(manualRevision);
            // Another client may have entered a passive mode while this request waited.
            // Never run the ordinary ON/100 recovery path against that newer intent.
            if (AutomaticAdjustmentsDisabled)
                throw Invalid("Control mode changed concurrently; refresh before another power command.");
            ThrowIfHardwarePoisoned();
            ThrowIfHardwareAccessBlocked();
            if (!configuration.Commissioned || !runtimeState.Commissioned)
            {
                throw new ControllerOperationException(PipeErrorCodes.NotCommissioned,
                    "Complete attended commissioning before manual power control.");
            }

            if (commissioningSession is not null || !writerLease.IsHeld)
            {
                throw Invalid("Manual power requires exclusive normal service ownership.");
            }

            RequireNoMythCoolConflictForShutdownQualification();
            var target = device ?? throw new ControllerOperationException(PipeErrorCodes.DeviceNotFound,
                "No verified device handle is available.");
            if (!poweredOn)
            {
                if (target is not IV12SystemShutdownDevice)
                {
                    throw new ControllerOperationException(PipeErrorCodes.UnsupportedDevice,
                        "This device cannot perform verified power control.");
                }

                await UpdateThermalAsync(cancellationToken).ConfigureAwait(false);
                var decision = manualPower.RequestOff(CreateManualPowerInput(), confirmed);
                if (!decision.Accepted)
                {
                    throw new ControllerOperationException(
                        confirmed ? PipeErrorCodes.FailSafeActive : PipeErrorCodes.InvalidArgument,
                        decision.Reason ?? "Manual OFF was refused.");
                }
            }
            else
            {
                manualPower.RequestOn();
            }

            SafetyOperationAuditJournal? journal = null;
            var journalResolved = false;
            var lightingRestoredOn = false;
            try
            {
                journal = await BeginSafetyOperationAuditAsync(PipeOperations.PowerSet,
                    peer.CallerSid, peer.Source, SafetyOperationKind.HidWithOn100Recovery,
                    new { poweredOn = lastDeviceState?.PoweredOn }, new { poweredOn }, cancellationToken)
                    .ConfigureAwait(false);
                var original = await ReadDeviceStateAsync(target, cancellationToken).ConfigureAwait(false);
                RequireCurrentManualRequest(manualRevision);
                if (poweredOn)
                {
                    await ConfirmManualPowerOnAsync(target, cancellationToken, manualRevision, preserveLightingRestore: true).ConfigureAwait(false);
                    var restored = await RunHidOperationAsync(token => RestoreFanLightingAfterOnAsync(target,
                        original, lastDeviceState!, 100, manualRevision, token), HidTimeout, cancellationToken,
                        timeoutPoisonResource: "manual ON lighting restore", timeoutRecoveryTarget: target).ConfigureAwait(false);
                    lastDeviceState = restored.State;
                    lightingRestoredOn = restored.Restored;
                    policy.Reset();
                }
                else
                {
                    // Recheck immediately before the OFF write, after persistence and GET.
                    RequireNoMythCoolConflictForShutdownQualification();
                    if (!manualPower.RequestOff(CreateManualPowerInput(), true).Accepted)
                    {
                        throw new ControllerOperationException(PipeErrorCodes.FailSafeActive,
                            "Safety conditions changed before the power write.");
                    }

                    var readback = await RunHidOperationAsync(async token =>
                    {
                        RequireCurrentManualRequest(manualRevision);
                        ThrowIfAutomaticOutputStopped();
                        return await TurnFanOffWithLightingAsync(target, original, manualRevision, token, () =>
                        {
                            ThrowIfAutomaticOutputStopped();
                            RequireNoMythCoolConflictForShutdownQualification();
                            if (!manualPower.RequestOff(CreateManualPowerInput(), true).Accepted)
                                throw new ControllerOperationException(PipeErrorCodes.FailSafeActive,
                                    "关闭条件已变化，已停止后续风扇关闭。");
                        }).ConfigureAwait(false);
                    }, HidTimeout, cancellationToken, timeoutPoisonResource: "manual fan OFF",
                        timeoutRecoveryTarget: target).ConfigureAwait(false);
                    if (readback.PoweredOn ||
                        LightingSettings.FromState(readback) is not { Enabled: false })
                    {
                        throw new V12ProtocolException("Manual OFF was not confirmed with lighting preserved.");
                    }

                    lastDeviceState = readback;
                    lastSuccessfulWrite = lastSuccessfulRead = timeProvider.GetUtcNow();
                    lastWrittenPercent = readback.FanSpeedPercent;
                    lastSuccessfulWriteMonotonic = GetMonotonicNow();
                }

                await ResolveAndFinishSafetyOperationAuditAsync(journal, SafetyOperationAuditOutcome.Success,
                    new { poweredOn, readbackConfirmed = true, lightingLinked = true,
                        lightingRestored = lightingRestoredOn }, CancellationToken.None).ConfigureAwait(false);
                journalResolved = true;
                RequireCurrentManualRequest(manualRevision);
                await PersistConfigurationMutationAsync(configuration with { Mode = ControlMode.Manual },
                    PipeOperations.ModeSet, peer, CancellationToken.None).ConfigureAwait(false);
                observedManualPowerOff = !poweredOn;
                observedManualControlHold = false;
                if (lastDeviceState is { } confirmedState) RecordManualObservationBaseline(target, confirmedState);
                if (poweredOn) policy.RequestManualWrite();
                UpdateStatus(poweredOn ? "FailSafe" : "ManuallyOff");
                return new { poweredOn, readbackConfirmed = true, lightingLinked = true,
                    lightingRestored = lightingRestoredOn, status = GetStatus() };
            }
            catch (ControllerOperationException exception) when (exception.Code == PipeErrorCodes.DeviceStateChanged &&
                manualRevision != Interlocked.Read(ref manualRequestRevision))
            {
                // A newer manual request is not a hardware failure. Before SET this
                // journal is NotApplied; after confirmed readback it is already complete.
                if (journal is not null && !journalResolved)
                    await ResolveAndFinishSafetyOperationAuditAsync(journal, SafetyOperationAuditOutcome.NotApplied,
                        new { superseded = true }, CancellationToken.None).ConfigureAwait(false);
                if (lastDeviceState is { } observed) manualPower.ObserveDevicePower(observed.PoweredOn);
                UpdateStatus("ManualRequestPending");
                throw;
            }
            catch (SafetyOutputSuppressedException exception)
            {
                if (journal is not null)
                    LatchSafetyOperationAuditBlocked(exception, journal, "manual power interrupted by stop intent");
                UpdateStatus("SafetyProtectionDisabled");
                throw;
            }
            catch (ControllerOperationException exception) when (
                exception.Code is PipeErrorCodes.DeviceStateChanged or PipeErrorCodes.CommandReadbackMismatch or
                    PipeErrorCodes.InvalidArgument or PipeErrorCodes.UnsupportedDevice &&
                lastDeviceState is not null && !IsHardwarePoisoned)
            {
                // Valid partial observations do not justify fighting new physical
                // input or waking an OFF fan merely to repair a lighting choice.
                if (journal is not null && !journalResolved)
                    await ResolveAndFinishSafetyOperationAuditAsync(journal,
                        exception.Code is PipeErrorCodes.InvalidArgument or PipeErrorCodes.UnsupportedDevice
                            ? SafetyOperationAuditOutcome.NotApplied : SafetyOperationAuditOutcome.Indeterminate,
                        new { requestedPoweredOn = poweredOn, reportedPoweredOn = lastDeviceState.PoweredOn,
                            lighting = LightingSettings.FromState(lastDeviceState), lightingLinkUnconfirmed = true },
                        CancellationToken.None).ConfigureAwait(false);
                manualPower.ObserveDevicePower(lastDeviceState.PoweredOn);
                UpdateStatus("ManualCommandUnconfirmed");
                throw;
            }
            catch
            {
                manualPower.RequestOn();
                // Do not use the cancelled caller token for emergency compensation.
                try
                {
                    await ConfirmManualPowerOnAsync(target, CancellationToken.None).ConfigureAwait(false);
                    if (journal is not null && !safetyOperationAuditBlocked)
                    {
                        await ResolveAndFinishSafetyOperationAuditAsync(journal,
                            SafetyOperationAuditOutcome.Compensated,
                            new { poweredOn = true, readbackConfirmed = true }, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                catch (SafetyOutputSuppressedException exception)
                {
                    if (journal is not null)
                        LatchSafetyOperationAuditBlocked(exception, journal, "manual power recovery stopped by user");
                }
                catch (Exception recoveryFailure)
                {
                    await BestEffortFailSafeAsync(CancellationToken.None).ConfigureAwait(false);
                    MarkHardwarePoisoned("MANUAL_POWER_RECOVERY_UNCONFIRMED",
                        "manual power emergency recovery", recoveryFailure);
                }
                policy.Reset();
                // An unresolved durable journal remains for startup recovery; never report
                // a failed operation as a successful power transition.
                UpdateStatus("FailSafe", FailSafeReason.HidFailure);
                throw;
            }
        }
        finally
        {
            MarkHeartbeat();
            gate.Release();
        }
    }

    private async Task ConfirmManualPowerOnAsync(IV12Device target, CancellationToken cancellationToken,
        long? manualRevision = null, bool preserveLightingRestore = false)
    {
        if (!preserveLightingRestore) ForgetFanLightingRestore();
        manualPower.RequestOn();
        var original = await ReadDeviceStateAsync(target, cancellationToken).ConfigureAwait(false);
        if (manualRevision is { } revision) RequireCurrentManualRequest(revision);
        await WriteFanSpeedAsync(target, 100, cancellationToken).ConfigureAwait(false);
        var readback = await ReadDeviceStateAsync(target, cancellationToken).ConfigureAwait(false);
        if (preserveLightingRestore && readback.PoweredOn && readback.FanSpeedPercent == 100 &&
            !LightingMatches(original, readback))
        {
            lastDeviceState = readback;
            lastSuccessfulRead = timeProvider.GetUtcNow();
            ForgetFanLightingRestore();
            throw new ControllerOperationException(PipeErrorCodes.DeviceStateChanged,
                "风扇已开启，但灯光出现新的设置；已保留设备回读，未恢复旧灯光。");
        }
        if (!readback.PoweredOn || readback.FanSpeedPercent != 100 || !LightingMatches(original, readback))
        {
            throw new V12ProtocolException("Manual ON did not confirm 100% with lighting preserved.");
        }

        lastDeviceState = readback;
        lastSuccessfulWrite = lastSuccessfulRead = timeProvider.GetUtcNow();
        lastSuccessfulWriteMonotonic = GetMonotonicNow();
        lastWrittenPercent = 100;
        policy.RecordWriteSuccess(lastSuccessfulWriteMonotonic.Value, 100);
        RecordManualObservationBaseline(target, readback);
    }
}
