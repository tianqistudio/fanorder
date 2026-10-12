using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Service;

internal sealed partial class ControllerCoordinator
{
    private bool manualCommandUnconfirmed;

    private static bool FanStateChanged(V12DeviceState before, V12DeviceState after) =>
        before.PoweredOn != after.PoweredOn || before.FanSpeedPercent != after.FanSpeedPercent;

    private static Task SelectManualSpeedAsync(IV12Device target, byte percent, CancellationToken token) =>
        target is IV12ManualSpeedDevice manual
            ? manual.SelectSoftwareControlAtSpeedAsync(percent, token)
            : throw new ControllerOperationException(PipeErrorCodes.UnsupportedDevice,
                "当前设备不支持指定转速的软件手动控制；未发送替代满速指令。");

    // Normal protected control still saves a requested target first. When the loop
    // is healthy, commit exactly that target with software-mode selection and GET.
    // A failed acknowledgement is not evidence of a newer physical input.
    private async Task<bool> WriteConfiguredManualTargetAsync(IV12Device target, byte percent,
        CancellationToken cancellationToken)
    {
        var before = lastDeviceState ?? throw new InvalidOperationException("Manual output requires a current read.");
        var revision = Interlocked.Read(ref manualRequestRevision);
        var lightingRestored = false;
        var previousScope = manualOperationRevision.Value;
        manualOperationRevision.Value = revision;
        try
        {
            var after = await RunHidOperationAsync(async token =>
            {
                ThrowIfAutomaticOutputStopped();
                RequireCurrentManualRequest(revision);
                await SelectManualSpeedAsync(target, percent, token).ConfigureAwait(false);
                var actual = await target.ReadStateAsync(token).ConfigureAwait(false);
                var restored = await RestoreFanLightingAfterOnAsync(target, before, actual, percent,
                    revision, token).ConfigureAwait(false);
                lightingRestored = restored.Restored;
                return restored.State;
            }, HidTimeout, cancellationToken, timeoutPoisonResource: "manual target selection and readback")
                .ConfigureAwait(false);
            lastDeviceState = after;
            lastSuccessfulRead = timeProvider.GetUtcNow();
            if (revision != Interlocked.Read(ref manualRequestRevision) || ManualRequestPending)
            {
                UpdateStatus("ManualRequestPending");
                return false;
            }
            RecordManualObservationBaseline(target, after);
            if (after.PoweredOn && after.FanSpeedPercent == percent &&
                (lightingRestored || LightingMatches(before, after)))
            {
                manualCommandUnconfirmed = false;
                return true;
            }
            if (FanStateChanged(before, after) && LightingMatches(before, after) &&
                await AdoptObservedManualStateAsync(after, cancellationToken, revision).ConfigureAwait(false))
                return false;

            manualCommandUnconfirmed = true;
            await SafeAuditAsync("manual.speed", "unconfirmed", runtimeState.OwnerSid, "control-loop",
                new { requestedPercent = percent, beforePercent = before.FanSpeedPercent,
                    reportedPercent = after.FanSpeedPercent, reportedPoweredOn = after.PoweredOn,
                    lightingMatches = LightingMatches(before, after) }, CancellationToken.None).ConfigureAwait(false);
            UpdateStatus("ManualCommandUnconfirmed");
            return false;
        }
        catch (ControllerOperationException exception) when (exception.Code == PipeErrorCodes.DeviceStateChanged &&
            revision != Interlocked.Read(ref manualRequestRevision))
        {
            UpdateStatus("ManualRequestPending");
            return false;
        }
        catch (ControllerOperationException exception) when (
            exception.Code is PipeErrorCodes.DeviceStateChanged or PipeErrorCodes.CommandReadbackMismatch)
        {
            if (exception.Code == PipeErrorCodes.DeviceStateChanged && lastDeviceState is { } actual &&
                await AdoptObservedManualStateAsync(actual, cancellationToken, revision).ConfigureAwait(false))
                return false;
            manualCommandUnconfirmed = true;
            if (lastDeviceState is { } final) RecordManualObservationBaseline(target, final);
            await SafeAuditAsync("manual.speed", "unconfirmed", runtimeState.OwnerSid, "control-loop",
                new { requestedPercent = percent, lightingLink = true, failure = exception.Message },
                CancellationToken.None).ConfigureAwait(false);
            UpdateStatus("ManualCommandUnconfirmed");
            return false;
        }
        finally { manualOperationRevision.Value = previousScope; }
    }
}
