using V12UltraController.Core.Hardware;

namespace V12UltraController.Service;

internal sealed partial class ControllerCoordinator
{
    // Called only while an already commissioned controller is establishing its
    // ON/100 baseline after opening a handle or observing same-handle drift.
    // It never adopts an uncommissioned device or offers an arbitrary mode byte.
    private async Task<V12DeviceState> RestoreOwnedSoftwareModeAsync(
        IV12Device target, V12DeviceState before, V12DeviceState observed,
        CancellationToken cancellationToken)
    {
        if (observed.RawModeStatus == 0x80 || target is not IV12SoftwareControlDevice softwareControl)
            return observed;

        if (!configuration.Commissioned || !runtimeState.Commissioned ||
            string.IsNullOrWhiteSpace(runtimeState.OwnerSid) || configurationError is not null ||
            hardwareAccessBlocked || IsHardwarePoisoned || HasForcedUninstallRecoveryBarrier ||
            !ReferenceEquals(device, target) || target.Identity != SupportedDeviceIdentity ||
            !LightingMatches(before, observed) || conflictDetector.Inspect().HasConflict ||
            !writerLease.TryAcquire())
            throw new V12ProtocolException("Software control restoration requires healthy commissioned ownership and unchanged lighting.");

        // At physical 100%, an ordinary ON/100 write can appear successful even
        // while subsequent lower writes will be ignored. Restore software mode
        // here rather than treating that matching percentage as mode evidence.
        logger.LogInformation("Restoring owned V12 software control at ON/100 after open or drift.");
        await RunHidOperationAsync(async token =>
        {
            ThrowIfAutomaticOutputStopped();
            await softwareControl.SelectSoftwareControlAtOneHundredAsync(token).ConfigureAwait(false);
            return true;
        }, HidTimeout, cancellationToken, timeoutPoisonResource: "owned software control restoration at 100",
            timeoutRecoveryTarget: target).ConfigureAwait(false);
        var restored = await ReadDeviceStateAsync(target, cancellationToken).ConfigureAwait(false);
        if (!restored.PoweredOn || restored.FanSpeedPercent != 100 || !LightingMatches(before, restored))
            throw new V12ProtocolException("Software control restoration did not confirm ON/100 with lighting preserved.");
        logger.LogInformation("Owned V12 software control restoration confirmed ON/100 with lighting preserved.");
        return restored;
    }
}
