using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Service;

internal sealed partial class ControllerCoordinator
{
    private bool AutomaticAdjustmentsDisabled => SafetyProtectionDisabled || configuration.Mode is ControlMode.PureManual or ControlMode.Released;

    private void SetPassiveDecision() => lastDecision = new ControlDecision(
        observedManualControlHold && observedManualSpeedPercent is { } observedPercent
            ? observedPercent : configuration.ManualSpeedPercent,
        false, false, FailSafeReason.None, null, lastThermal.ControlCelsius);

    private async Task<bool> ExplicitManualCommandAsync(byte percent, bool poweredOn, PipePeerIdentity peer,
        CancellationToken cancellationToken, long? manualRevision = null)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken = ControllerRequestAdmissionScope.Admit(cancellationToken);
            if (manualRevision is { } revision) RequireCurrentManualRequest(revision);
            return await ExplicitManualCommandUnderGateAsync(percent, poweredOn, peer, cancellationToken,
                manualRevision: manualRevision).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<bool> ExplicitManualCommandUnderGateAsync(byte percent, bool poweredOn, PipePeerIdentity peer,
        CancellationToken cancellationToken, bool persistConfiguration = true, long? manualRevision = null)
    {
        if (percent is < 10 or > 100) throw Invalid("percent must be between 10 and 100.");
        bool powerMatches = false, lightingMatches = false;
        V12DeviceState? beforeCommand = null;
        var correctionAttempted = false;
        var lightingRestored = false;
        var previousExplicitScope = explicitManualIo.Value;
        try
        {
            explicitManualIo.Value = true;
            var target = await RequireExplicitDeviceUnderGateAsync(cancellationToken).ConfigureAwait(false);
            if (manualRevision is { } currentRevision) RequireCurrentManualRequest(currentRevision);
            if (persistConfiguration)
                SupersedeProfileApplicationUnderGate("已由新的手动输入取代；原方案不会继续应用灯光。");
            await auditLog.WriteAsync("puremanual.command", "requested", peer.CallerSid, peer.Source,
                new { percent, poweredOn }, cancellationToken).ConfigureAwait(false);
            var readback = await RunHidOperationAsync(async token =>
            {
                var before = await target.ReadStateAsync(token).ConfigureAwait(false);
                beforeCommand = before;
                if (manualRevision is { } beforeWriteRevision) RequireCurrentManualRequest(beforeWriteRevision);
                V12DeviceState after;
                if (poweredOn)
                {
                    await SelectManualSpeedAsync(target, percent, token).ConfigureAwait(false);
                    after = await target.ReadStateAsync(token).ConfigureAwait(false);
                }
                else after = await TurnFanOffWithLightingAsync(target, before, manualRevision, token).ConfigureAwait(false);
                // A manual fan/power request never repeats SET to fight a later physical
                // input. Profile application retains its separate bounded verification.
                if (after.PoweredOn != poweredOn || poweredOn && !LightingMatches(before, after))
                {
                    if (after.PoweredOn != poweredOn && manualRevision is null)
                    {
                        correctionAttempted = true;
                        if (manualRevision is { } correctionRevision) RequireCurrentManualRequest(correctionRevision);
                        if (poweredOn) await SelectManualSpeedAsync(target, percent, token).ConfigureAwait(false);
                        else if (target is IV12SystemShutdownDevice off)
                            await off.WriteSystemShutdownOffAsync(token).ConfigureAwait(false);
                        after = await target.ReadStateAsync(token).ConfigureAwait(false);
                    }
                    // The validated lighting command requires ON. Never turn a requested-OFF
                    // fan on just to repair RGB, or invent an unvalidated OFF lighting frame.
                    if (manualRevision is null && poweredOn && after.PoweredOn && !LightingMatches(before, after) && target is IV12LightingDevice lights &&
                        LightingSettings.FromState(before) is { } originalLighting)
                    {
                        correctionAttempted = true;
                        if (manualRevision is { } lightingRevision) RequireCurrentManualRequest(lightingRevision);
                        await lights.WriteLightingAsync(originalLighting, token).ConfigureAwait(false);
                        after = await target.ReadStateAsync(token).ConfigureAwait(false);
                    }
                }
                powerMatches = after.PoweredOn == poweredOn;
                lightingMatches = poweredOn ? LightingMatches(before, after) :
                    LightingSettings.FromState(after) is { Enabled: false };
                return after;
            }, HidTimeout, cancellationToken, timeoutPoisonResource: "explicit manual command").ConfigureAwait(false);
            lastDeviceState = readback;
            lastSuccessfulRead = timeProvider.GetUtcNow();
            RecordManualObservationBaseline(target, readback);
            if (manualRevision is { } finalRevision) RequireCurrentManualRequest(finalRevision);
            var requestedValueConfirmed = powerMatches && lightingMatches &&
                (!poweredOn || readback.FanSpeedPercent == percent);
            var observedNewFanState = FanStateChanged(beforeCommand!, readback);
            if (poweredOn && powerMatches && lightingMatches && !requestedValueConfirmed && !observedNewFanState)
            {
                manualCommandUnconfirmed = true;
                SetPassiveDecision();
                UpdateStatus("ManualCommandUnconfirmed");
                await SafeAuditAsync("puremanual.command", "unconfirmed", peer.CallerSid, peer.Source,
                    new { requestedPercent = percent, beforePercent = beforeCommand!.FanSpeedPercent,
                        reportedPercent = readback.FanSpeedPercent, requestedValueConfirmed = false,
                        observedNewFanState = false, softwareTargetSelected = true }, CancellationToken.None).ConfigureAwait(false);
                throw new ControllerOperationException(PipeErrorCodes.DeviceStateChanged,
                    $"请求 {percent}% 未生效，设备仍为 {readback.FanSpeedPercent}%；未确认新的实体调节，原保存设置保留。未重试或自动满速。");
            }
            if (requestedValueConfirmed)
            {
                manualCommandUnconfirmed = false;
                lastSuccessfulWrite = timeProvider.GetUtcNow();
                lastWrittenPercent = readback.FanSpeedPercent;
            }
            else if (observedNewFanState) manualCommandUnconfirmed = false;
            SetPassiveDecision();
            UpdateStatus(SafetyProtectionDisabled ? "SafetyProtectionDisabled" : "PureManual");
            if (persistConfiguration && (lightingMatches ||
                poweredOn && readback.PoweredOn && readback.FanSpeedPercent == percent))
            {
                var mode = configuration.Mode == ControlMode.PureManual ? ControlMode.PureManual : ControlMode.Manual;
                await PersistConfigurationMutationAsync(configuration with { Mode = mode,
                    ManualSpeedPercent = readback.PoweredOn ? readback.FanSpeedPercent : configuration.ManualSpeedPercent },
                    PipeOperations.SpeedSet, peer, CancellationToken.None).ConfigureAwait(false);
                observedManualPowerOff = !readback.PoweredOn;
                manualPower.ObserveDevicePower(readback.PoweredOn);
                observedManualControlHold = false;
                observedManualPersistenceUnconfirmed = false;
                manualCommandUnconfirmed = false;
                SetPassiveDecision();
                UpdateStatus(configuration.Mode.ToString());
            }
            if (!powerMatches || !lightingMatches)
            {
                ForgetFanLightingRestore();
                await SafeAuditAsync("puremanual.command", "unconfirmed", peer.CallerSid, peer.Source,
                    new { percent, poweredOn, reportedPercent = readback.FanSpeedPercent,
                        reportedPoweredOn = readback.PoweredOn, lightingMatches, correctionAttempted }, CancellationToken.None).ConfigureAwait(false);
                throw new ControllerOperationException(PipeErrorCodes.CommandReadbackMismatch,
                    "请求的电源或灯光状态未确认；已保留设备最终回读，停止覆盖，未自动满速。");
            }
            if (poweredOn && fanLightingRestore is not null)
            {
                // The fan phase is already confirmed and durably saved. A later
                // lighting failure must not erase that newer manual fan choice.
                var restored = await RunHidOperationAsync(token => RestoreFanLightingAfterOnAsync(target,
                    beforeCommand!, readback, percent, manualRevision, token), HidTimeout, cancellationToken,
                    timeoutPoisonResource: "manual fan lighting restore").ConfigureAwait(false);
                readback = restored.State;
                lightingRestored = restored.Restored;
                lastDeviceState = readback;
                lastSuccessfulRead = timeProvider.GetUtcNow();
                RecordManualObservationBaseline(target, readback);
                if (manualRevision is { } restoredRevision) RequireCurrentManualRequest(restoredRevision);
            }
            SetPassiveDecision();
            UpdateStatus(configuration.Mode.ToString());
            await SafeAuditAsync("puremanual.command", "confirmed", peer.CallerSid, peer.Source,
                new { requestedPercent = percent, confirmedPercent = readback.FanSpeedPercent,
                    requestedValueConfirmed, observedNewFanState,
                    poweredOn, correctionAttempted, softwareTargetSelected = poweredOn,
                    lightingLinked = true, lightingRestored }, CancellationToken.None).ConfigureAwait(false);
            return lightingRestored;
        }
        catch (ControllerOperationException exception) when (exception.Code is PipeErrorCodes.DeviceStateChanged or PipeErrorCodes.CommandReadbackMismatch)
        {
            // A valid observation is not a checksum failure and does not confirm the requested setting.
            throw;
        }
        catch
        {
            // No recovery speed write and no retry. A timed-out native call retains ownership
            // until process termination; returning an error must not claim a released handle.
            lastDeviceState = null;
            UpdateStatus("ExplicitCommandFailed");
            throw;
        }
        finally { explicitManualIo.Value = previousExplicitScope; MarkHeartbeat(); }
    }

    // The caller holds the coordinator gate and has entered a narrow explicit-command scope.
    // Opening a verified owned handle is permitted only for this user request, never from Tick.
    private async Task<IV12Device> RequireExplicitDeviceUnderGateAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized(); ThrowIfDisposed(); ThrowIfHardwarePoisoned(); ThrowIfHardwareAccessBlocked();
        if (configuration.Mode != ControlMode.PureManual && (!SafetyProtectionDisabled || configuration.Mode == ControlMode.Released))
            throw Invalid("Control is released; explicitly select puremanual first.");
        if (!configuration.Commissioned || !runtimeState.Commissioned || string.IsNullOrWhiteSpace(runtimeState.OwnerSid) ||
            commissioningSession is not null || HostStopDisposition != HostStopDisposition.None)
            throw Invalid("Explicit manual control requires completed commissioning and an active service.");
        if (configurationError is not null || configurationLoadInvalid || runtimeStateLoadInvalid)
            throw Invalid("当前配置或所有权状态未确认，暂不能发送手动设备操作。");
        RequireNoMythCoolConflictForShutdownQualification();
        if (!writerLease.TryAcquire()) throw Invalid("Another writer owns the controller.");
        if (device is null)
        {
            var opened = await OpenExpectedDeviceAsync(SupportedDeviceIdentity, cancellationToken).ConfigureAwait(false);
            if (opened.Status != V12DeviceOpenStatus.Opened) throw CreateOpenFailureException(opened.Status);
            device = opened.Device!;
        }
        if (!DeviceAllowList.IsSupported(device.Identity))
            throw new ControllerOperationException(PipeErrorCodes.UnsupportedDevice, "Device identity is not supported.");
        return device;
    }
}
