using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Status;

namespace V12UltraController.Service;

internal sealed partial class ControllerCoordinator
{
    private int startupPureManualRecoveryAllowed;
    // Pure manual and the persisted safety latch stop automatic curve output. Startup fan-on is a
    // separate, fail-closed recovery action requested by the host: it may clear only
    // an actually-off fan once, then must prove the resulting power state by GET.
    private bool ShouldAttemptStartupFanOnRecovery =>
        initialized &&
        startupFanKickPending &&
        (SafetyProtectionDisabled && Volatile.Read(ref startupFanRecoveryAllowedFromPersistedSafetyLatch) == 1 ||
         configuration.Mode == ControlMode.PureManual && Volatile.Read(ref startupPureManualRecoveryAllowed) == 1) &&
        configuration.Commissioned &&
        runtimeState.Commissioned &&
        configuration.Mode is ControlMode.Auto or ControlMode.Manual or ControlMode.PureManual &&
        !manualPower.OffRequested &&
        commissioningSession is null &&
        HostStopDisposition == HostStopDisposition.None &&
        !IsHardwarePoisoned &&
        !hardwareAccessBlocked &&
        !invalidConfigurationWithPendingJournal &&
        !auditMutationBlocked &&
        !safetyOperationAuditBlocked &&
        !HasForcedUninstallRecoveryBarrier &&
        !requiresProcessRestartForRecovery &&
        !configurationLoadInvalid &&
        !runtimeStateLoadInvalid &&
        !commissioningStateMismatch &&
        configurationError is null;

    // The coordinator gate is held by TickAsync. This is intentionally the only
    // automatic write allowed through a persisted safety-disabled startup state.
    private async Task<bool> TryEnsureStartupFanOnUnderGateAsync(CancellationToken cancellationToken)
    {
        if (!ShouldAttemptStartupFanOnRecovery)
        {
            return false;
        }

        if (conflictDetector.Inspect().HasConflict)
        {
            UpdateStatus("BlockedByMythCool", FailSafeReason.MythCoolConflict);
            return true;
        }

        var leaseAlreadyHeld = writerLease.IsHeld;
        if (!writerLease.TryAcquire())
        {
            UpdateStatus("WriterLeaseUnavailable", FailSafeReason.HidFailure);
            return true;
        }

        try
        {
            if (device is null)
            {
                var opened = await OpenExpectedDeviceAsync(
                        SupportedDeviceIdentity,
                        cancellationToken,
                        allowWhenProtectionDisabled: true)
                    .ConfigureAwait(false);
                if (opened.Status != V12DeviceOpenStatus.Opened)
                {
                    if (!leaseAlreadyHeld) writerLease.Release();
                    lastDeviceState = null;
                    UpdateStatusForOpenFailure(opened.Status, SupportedDeviceIdentity);
                    return true;
                }

                device = opened.Device
                    ?? throw new InvalidOperationException("An opened device result did not contain a device.");
            }

            var target = device;
            if (!DeviceAllowList.IsSupported(target.Identity))
            {
                await ReleaseDeviceAsync().ConfigureAwait(false);
                UpdateStatus("UnsupportedDevice", FailSafeReason.UnsupportedDevice, target.Identity);
                return true;
            }

            var requestedPercent = configuration.Mode is ControlMode.Manual or ControlMode.PureManual
                ? configuration.ManualSpeedPercent
                : configuration.StartupFanPercent;
            // Once a device transaction begins, never replay an ambiguous result.
            // A device that was absent can still be discovered on a later tick.
            startupFanKickPending = false;
            var startupEpoch = Interlocked.Read(ref safetyProtectionEpoch);
            var result = await RunHidOperationAsync(
                    async token =>
                    {
                        var before = await target.ReadStateAsync(token).ConfigureAwait(false);
                        if (before.PoweredOn)
                        {
                            return new StartupFanOnRecoveryResult(before, before, WriteAttempted: false);
                        }

                        // Runtime stop/new manual input wins even while the startup
                        // read was in flight. Do not turn that input into an ON write.
                        if (startupEpoch != Interlocked.Read(ref safetyProtectionEpoch) || ManualRequestPending ||
                            Volatile.Read(ref startupPureManualRecoveryAllowed) == 0 &&
                            Volatile.Read(ref startupFanRecoveryAllowedFromPersistedSafetyLatch) == 0)
                            throw new SafetyOutputSuppressedException();
                        await SelectManualSpeedAsync(target, requestedPercent, token).ConfigureAwait(false);
                        var after = await target.ReadStateAsync(token).ConfigureAwait(false);
                        return new StartupFanOnRecoveryResult(before, after, WriteAttempted: true);
                    },
                    HidTimeout,
                    cancellationToken,
                    timeoutPoisonResource: "startup fan-on recovery",
                    timeoutRecoveryTarget: target,
                    allowWhenProtectionDisabled: true)
                .ConfigureAwait(false);

            lastDeviceState = result.State;
            lastSuccessfulRead = timeProvider.GetUtcNow();
            if (!result.State.PoweredOn ||
                result.WriteAttempted &&
                (result.State.FanSpeedPercent != requestedPercent ||
                 !LightingMatches(result.Before, result.State)))
            {
                throw new V12ProtocolException(
                    $"Startup fan-on recovery did not confirm ON at {requestedPercent}%.");
            }

            RecordManualObservationBaseline(target, result.State);
            Volatile.Write(ref deviceStateCacheSnapshot,
                new DeviceStateCacheSnapshot(target.Identity, result.State));
            QueueDeviceStateCachePersistence(target.Identity, result.State);
            startupFanKickPending = false;
            if (result.WriteAttempted)
            {
                var confirmedAt = timeProvider.GetUtcNow();
                lastSuccessfulWrite = confirmedAt;
                lastSuccessfulWriteMonotonic = GetMonotonicNow();
                lastWrittenPercent = result.State.FanSpeedPercent;
                policy.Reset();
                await SafeAuditAsync(
                        "startup.fan-on",
                        "confirmed",
                        runtimeState.OwnerSid,
                        "control-loop",
                        new
                        {
                            requestedPercent,
                            reportedPercent = result.State.FanSpeedPercent,
                            poweredOn = result.State.PoweredOn,
                            lightingPreserved = true,
                            safetyProtectionDisabled = SafetyProtectionDisabled,
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            manualPower.RequestOn();
            SetPassiveDecision();
            UpdateStatus(SafetyProtectionDisabled ? "SafetyProtectionDisabled" : configuration.Mode.ToString());
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatalPersistenceFailure(exception))
        {
            lastDeviceState = null;
            if (!IsHardwarePoisoned)
            {
                await ReleaseDeviceAsync().ConfigureAwait(false);
            }

            logger.LogWarning(exception,
                "Startup fan-on recovery could not confirm the fan state; automatic curve output remains disabled.");
            UpdateStatus("StartupFanRecoveryUnavailable");
            return true;
        }
    }

    private sealed record StartupFanOnRecoveryResult(
        V12DeviceState Before,
        V12DeviceState State,
        bool WriteAttempted);
}
