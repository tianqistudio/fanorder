using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Service;

internal sealed partial class ControllerCoordinator
{
    public async Task<object> GetLightingAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var persisted = Volatile.Read(ref deviceStateCacheSnapshot);
            var cachedState = lastDeviceState ?? (persisted?.Identity == SupportedDeviceIdentity ? persisted.State : null);
            return new
            {
                supported = device is IV12LightingDevice,
                settings = cachedState is null ? null : LightingSettings.FromState(cachedState),
                lastSuccessfulRead,
                cached = true,
                settingsFromPersistedCache = lastDeviceState is null && cachedState is not null,
            };
        }
        finally { gate.Release(); }
    }

    public async Task<object> SetLightingAsync(
        LightingSettings settings, PipePeerIdentity peer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken = ControllerRequestAdmissionScope.Admit(cancellationToken);
            ForgetFanLightingRestore();
            if (AutomaticAdjustmentsDisabled)
                return await SetExplicitLightingUnderGateAsync(settings, peer, cancellationToken).ConfigureAwait(false);
            return await SetLightingUnderGateAsync(settings, peer, cancellationToken).ConfigureAwait(false);
        }
        finally { MarkHeartbeat(); gate.Release(); }
    }

    private async Task<object> SetLightingUnderGateAsync(
        LightingSettings settings, PipePeerIdentity peer, CancellationToken cancellationToken,
        Action? onLightingWriteStarting = null)
    {
        EnsureInitialized();
        ThrowIfHardwarePoisoned();
        if (AutomaticAdjustmentsDisabled) throw Invalid("RGB changes are unavailable in passive modes; existing lights are preserved.");
        ThrowIfHardwareAccessBlocked();
        if (!configuration.Commissioned || !runtimeState.Commissioned)
            throw new ControllerOperationException(PipeErrorCodes.NotCommissioned,
                "Complete attended commissioning before lighting control.");
        if (commissioningSession is not null || !writerLease.IsHeld)
            throw Invalid("Lighting requires exclusive normal service ownership.");
        RequireNoMythCoolConflictForShutdownQualification();
        if (lastDeviceState?.PoweredOn != true)
            throw Invalid("Turn the fan on before editing lighting; RGB never changes the fan power intent.");
        var target = device ?? throw new ControllerOperationException(PipeErrorCodes.DeviceNotFound,
            "No verified device handle is available.");
        if (target is not IV12LightingDevice lighting || !DeviceAllowList.IsSupported(target.Identity))
            throw new ControllerOperationException(PipeErrorCodes.UnsupportedDevice,
                "This device does not support verified lighting control.");
        await UpdateThermalAsync(cancellationToken).ConfigureAwait(false);
        RequireLightingSafety();
        SafetyOperationAuditJournal? journal = null;
        try
        {
            journal = await BeginSafetyOperationAuditAsync(PipeOperations.LightingSet,
                peer.CallerSid, peer.Source, SafetyOperationKind.HidWithOn100Recovery,
                new { settings = lastDeviceState is null ? null : LightingSettings.FromState(lastDeviceState) },
                new { settings }, cancellationToken).ConfigureAwait(false);
            var original = await ReadDeviceStateAsync(target, cancellationToken).ConfigureAwait(false);
            RequireLightingSafety();
            RequireNoMythCoolConflictForShutdownQualification();
            // A physical knob movement or power change must be resolved by the
            // normal fan control loop, not silently echoed by a lighting frame.
            if (!original.PoweredOn || original.RawModeStatus != 0x80 ||
                original.FanSpeedPercent != lastWrittenPercent)
                throw Invalid("Fan state changed; wait for normal control before editing lighting.");
            onLightingWriteStarting?.Invoke();
            await RunHidOperationAsync(async token =>
            {
                ThrowIfAutomaticOutputStopped();
                await lighting.WriteLightingAsync(settings, token).ConfigureAwait(false);
                return true;
            }, HidTimeout, cancellationToken, timeoutPoisonResource: "lighting write",
                timeoutRecoveryTarget: target).ConfigureAwait(false);
            var readback = await ReadDeviceStateAsync(target, cancellationToken).ConfigureAwait(false);
            if (!readback.PoweredOn || readback.FanSpeedPercent != original.FanSpeedPercent ||
                LightingSettings.FromState(readback) != settings)
                throw new V12ProtocolException("Lighting or preserved fan state was not confirmed.");
            lastDeviceState = readback;
            lastSuccessfulWrite = lastSuccessfulRead = timeProvider.GetUtcNow();
            await ResolveAndFinishSafetyOperationAuditAsync(journal, SafetyOperationAuditOutcome.Success,
                new { settings, readbackConfirmed = true, fanPercent = readback.FanSpeedPercent },
                CancellationToken.None).ConfigureAwait(false);
            UpdateStatus("Running");
            return new { settings, readbackConfirmed = true };
        }
        catch (SafetyOutputSuppressedException exception)
        {
            if (journal is not null)
                LatchSafetyOperationAuditBlocked(exception, journal, "lighting interrupted by stop intent");
            UpdateStatus("SafetyProtectionDisabled");
            throw;
        }
        catch
        {
            // Lighting may be partially applied. Do not claim rollback of the lights;
            // recovery guarantees cooling only, independently of caller cancellation.
            try
            {
                await ConfirmManualPowerOnAsync(target, CancellationToken.None).ConfigureAwait(false);
                if (journal is not null && !safetyOperationAuditBlocked)
                    await ResolveAndFinishSafetyOperationAuditAsync(journal,
                        SafetyOperationAuditOutcome.Compensated,
                        new { poweredOn = true, fanPercent = 100, lightingOutcome = "unconfirmed" },
                        CancellationToken.None).ConfigureAwait(false);
            }
            catch (SafetyOutputSuppressedException exception)
            {
                if (journal is not null)
                    LatchSafetyOperationAuditBlocked(exception, journal, "lighting recovery stopped by user");
            }
            catch (Exception exception)
            {
                await BestEffortFailSafeAsync(CancellationToken.None).ConfigureAwait(false);
                MarkHardwarePoisoned("LIGHTING_RECOVERY_UNCONFIRMED", "lighting emergency recovery", exception);
            }
            policy.Reset();
            UpdateStatus("FailSafe", FailSafeReason.HidFailure);
            throw;
        }
    }

    private void RequireLightingSafety()
    {
        var age = timeProvider.GetUtcNow() - lastThermal.CapturedAt;
        if (configurationError is not null || lastDecision?.FailSafeActive != false ||
            !lastThermal.CpuIsValid || lastThermal.ControlCelsius is not { } temperature ||
            temperature >= 90 || age < TimeSpan.Zero || age > TimeSpan.FromSeconds(2))
            throw new ControllerOperationException(PipeErrorCodes.FailSafeActive,
                "Lighting changes wait until temperature monitoring and normal fan control are healthy.");
    }
}
