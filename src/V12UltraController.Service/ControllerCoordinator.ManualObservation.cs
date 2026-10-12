using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Service;

internal sealed partial class ControllerCoordinator
{
    private IV12Device? lastObservedManualDevice;
    private V12DeviceState? lastObservedManualDeviceBaseline;
    private bool observedManualControlHold;
    private bool observedManualPersistenceUnconfirmed;
    private bool observedManualPowerOff;
    private byte? observedManualSpeedPercent;
    private bool explicitControlBaselineResetPending;

    // The caller holds the coordinator gate. This path permits only an owned open
    // and the protocol's validated query, never any fan, power, RGB or takeover command.
    private async Task<bool> ObserveManualDeviceAsync(CancellationToken cancellationToken)
    {
        if (!CanObserveManualDevice() ||
            (!SafetyProtectionDisabled && configuration.Mode != ControlMode.PureManual))
            return false;

        var requestRevision = Interlocked.Read(ref manualRequestRevision);
        var leaseAlreadyHeld = writerLease.IsHeld;
        try
        {
            if (conflictDetector.Inspect().HasConflict || !writerLease.TryAcquire()) return false;
            if (device is null)
            {
                var opened = await OpenExpectedDeviceAsync(SupportedDeviceIdentity, cancellationToken,
                    allowWhenProtectionDisabled: true).ConfigureAwait(false);
                if (opened.Status != V12DeviceOpenStatus.Opened)
                {
                    if (!leaseAlreadyHeld) writerLease.Release();
                    lastDeviceState = null;
                    UpdateStatusForOpenFailure(opened.Status, SupportedDeviceIdentity);
                    return false;
                }
                device = opened.Device!;
            }

            var target = device;
            if (!DeviceAllowList.IsSupported(target.Identity)) return false;
            var baseline = ReferenceEquals(lastObservedManualDevice, target)
                ? lastObservedManualDeviceBaseline : null;
            // ReadStateAsync sends the fixed read-query feature report and validates GET.
            // It cannot be replaced by an arbitrary raw GET, and has no recovery write.
            var observed = await RunHidOperationAsync(token => target.ReadStateAsync(token),
                HidTimeout, cancellationToken, timeoutPoisonResource: "manual observation query",
                allowWhenProtectionDisabled: true).ConfigureAwait(false);
            lastDeviceState = observed;
            lastSuccessfulRead = timeProvider.GetUtcNow();

            // A first read establishes a baseline; it is not evidence of a button press.
            // A later explicit request also wins over an observation already in flight.
            // Keep the adoption baseline if that request is pending: a later cancellation
            // must not consume an already-observed physical change without adopting it.
            if (ManualRequestPending || requestRevision != Interlocked.Read(ref manualRequestRevision))
                return false;
            DiscardChangedFanLightingRestore(target, observed);
            if (explicitControlBaselineResetPending || baseline is null ||
                baseline.FanSpeedPercent == observed.FanSpeedPercent && baseline.PoweredOn == observed.PoweredOn)
            {
                RecordManualObservationBaseline(target, observed);
                return false;
            }

            return await AdoptObservedManualStateAsync(observed, cancellationToken, requestRevision)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lastDeviceState = null;
            throw;
        }
        catch (Exception exception) when (!IsFatalPersistenceFailure(exception))
        {
            // A failed query is not a physical change. Keep its last confirmed baseline,
            // but do not expose an older state with a new successful-read timestamp.
            lastDeviceState = null;
            if (device is null && !leaseAlreadyHeld && !IsHardwarePoisoned) writerLease.Release();
            logger.LogWarning(exception, "Manual device observation failed; no control write was attempted.");
            UpdateStatus("ManualObservationUnavailable");
            return false;
        }
        finally { MarkHeartbeat(); }
    }

    private bool CanObserveManualDevice() => initialized &&
        Volatile.Read(ref disposeRequested) == 0 &&
        configuration.Mode != ControlMode.Released &&
        configuration.Commissioned && runtimeState.Commissioned &&
        !string.IsNullOrWhiteSpace(runtimeState.OwnerSid) &&
        commissioningSession is null && HostStopDisposition == HostStopDisposition.None &&
        !IsHardwarePoisoned && !hardwareAccessBlocked && !invalidConfigurationWithPendingJournal &&
        !requiresProcessRestartForRecovery && !auditMutationBlocked && !safetyOperationAuditBlocked &&
        !HasForcedUninstallRecoveryBarrier && configurationError is null &&
        !configurationLoadInvalid && !runtimeStateLoadInvalid && !commissioningStateMismatch;

    // Also called after a confirmed explicit command, so that our own change does not
    // become a later "physical" change. RawModeStatus is deliberately not interpreted.
    private void RecordManualObservationBaseline(IV12Device target, V12DeviceState observed)
    {
        DiscardChangedFanLightingRestore(target, observed);
        lastObservedManualDevice = target;
        lastObservedManualDeviceBaseline = observed;
        explicitControlBaselineResetPending = false;
    }

    // Returns true once the observed intent owns control, even if its durable save fails.
    // The caller must then leave the ordinary curve/drift-recovery path for this tick.
    private async Task<bool> AdoptObservedManualStateAsync(V12DeviceState observed,
        CancellationToken cancellationToken, long? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(observed);
        var revision = expectedRevision ?? Interlocked.Read(ref manualRequestRevision);
        if (!CanObserveManualDevice() || ManualRequestPending || device is null || !writerLease.IsHeld ||
            !DeviceAllowList.IsSupported(device.Identity) || observed.FanSpeedPercent is < 10 or > 100)
            return false;

        var adoptedRevision = checked(revision + 1);
        if (Interlocked.CompareExchange(ref manualRequestRevision, adoptedRevision, revision) != revision)
            return false;

        try
        {
            ForgetFanLightingRestore();
            // Set the runtime hold before any persistence await. An audit/storage failure
            // must not send the observation back through automatic curve or ON/100 recovery.
            observedManualControlHold = true;
            manualCommandUnconfirmed = false;
            observedManualPersistenceUnconfirmed = true;
            observedManualPowerOff = !observed.PoweredOn;
            observedManualSpeedPercent = observed.FanSpeedPercent;
            manualPower.ObserveDevicePower(observed.PoweredOn);
            policy.ObserveManualTarget(observed.FanSpeedPercent);
            lastDeviceState = observed;
            lastSuccessfulRead = timeProvider.GetUtcNow();
            RecordManualObservationBaseline(device, observed);
            SupersedeProfileApplicationUnderGate("已采纳设备的手动设置；旧方案不会继续应用灯光。");

            var next = configuration with
            {
                Mode = configuration.Mode == ControlMode.PureManual ? ControlMode.PureManual : ControlMode.Manual,
                ManualSpeedPercent = observed.PoweredOn ? observed.FanSpeedPercent : configuration.ManualSpeedPercent,
            };
            var peer = new PipePeerIdentity(runtimeState.OwnerSid!, "physical-observation");
            try
            {
                await PersistConfigurationMutationAsync(next, PipeOperations.SpeedSet, peer, cancellationToken)
                    .ConfigureAwait(false);
                observedManualPersistenceUnconfirmed = false;
                SetPassiveDecision();
                UpdateStatus(SafetyProtectionDisabled ? "SafetyProtectionDisabled" :
                    configuration.Mode == ControlMode.PureManual ? "PureManual" : "ManualObserved");
            }
            catch (Exception exception) when (!IsFatalPersistenceFailure(exception))
            {
                // The original WAL remains authoritative; do not mutate its outcome or retry.
                // The in-memory hold truthfully survives an unconfirmed persistence result.
                logger.LogWarning(exception, "Observed manual control is retained in this process, but its configuration save was not confirmed.");
                SetPassiveDecision();
                UpdateStatus("ManualObservationSaveUnconfirmed");
            }
            return true;
        }
        finally { ResolveManualRequest(adoptedRevision); }
    }
}
