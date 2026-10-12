using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Service;

internal sealed partial class ControllerCoordinator
{
    private async Task<object> SetExplicitLightingUnderGateAsync(LightingSettings settings, PipePeerIdentity peer,
        CancellationToken cancellationToken, Action? onLightingWriteStarting = null)
    {
        var previousExplicitScope = explicitManualIo.Value;
        V12DeviceState? observed = null;
        var writeStarted = false;
        var readStarted = false;
        var readbackReceived = false;
        var resultLogged = false;
        try
        {
            explicitManualIo.Value = true;
            settings.Validate();
            ForgetFanLightingRestore();
            var target = await RequireExplicitDeviceUnderGateAsync(cancellationToken).ConfigureAwait(false);
            if (target is not IV12LightingDevice lighting)
                throw new ControllerOperationException(PipeErrorCodes.UnsupportedDevice, "当前设备不支持已验证的灯光预设。");
            // A manual RGB request has no promised automatic ON/100 recovery. Keep its
            // evidence in the same requested/confirmed/unconfirmed log used by manual fan commands.
            await auditLog.WriteAsync("lighting.explicit", "requested", peer.CallerSid, peer.Source,
                new { settings }, cancellationToken).ConfigureAwait(false);
            var comparison = await RunHidOperationAsync(async token =>
            {
                readStarted = true;
                var before = await target.ReadStateAsync(token).ConfigureAwait(false);
                observed = before;
                if (!before.PoweredOn) throw Invalid("风扇关闭时不能更改灯光；请先明确开启风扇。");
                token.ThrowIfCancellationRequested();
                onLightingWriteStarting?.Invoke();
                writeStarted = true;
                await lighting.WriteLightingAsync(settings, token).ConfigureAwait(false);
                var after = await target.ReadStateAsync(token).ConfigureAwait(false);
                observed = after;
                readbackReceived = true;
                return (Before: before, After: after);
            }, HidTimeout, cancellationToken, timeoutPoisonResource: "explicit lighting command").ConfigureAwait(false);
            lastDeviceState = comparison.After;
            lastSuccessfulRead = lastSuccessfulWrite = timeProvider.GetUtcNow();
            var fanMatches = comparison.After.PoweredOn == comparison.Before.PoweredOn &&
                comparison.After.FanSpeedPercent == comparison.Before.FanSpeedPercent;
            var lightingMatches = LightingSettings.FromState(comparison.After) == settings;
            SetPassiveDecision();
            UpdateStatus(SafetyProtectionDisabled ? "SafetyProtectionDisabled" : "PureManual");
            if (!fanMatches || !lightingMatches)
            {
                await auditLog.WriteAsync("lighting.explicit", "unconfirmed", peer.CallerSid, peer.Source,
                    new { settings, fanMatches, lightingMatches, reportedPercent = comparison.After.FanSpeedPercent,
                        reportedPoweredOn = comparison.After.PoweredOn }, CancellationToken.None).ConfigureAwait(false);
                resultLogged = true;
                throw new ControllerOperationException(!fanMatches && lightingMatches
                    ? PipeErrorCodes.DeviceStateChanged : PipeErrorCodes.CommandReadbackMismatch,
                    "灯光或风扇保持状态未确认；已停止本次操作，未重试、未自动满速。");
            }
            await auditLog.WriteAsync("lighting.explicit", "confirmed", peer.CallerSid, peer.Source,
                new { settings, fanPercent = comparison.After.FanSpeedPercent, poweredOn = comparison.After.PoweredOn },
                CancellationToken.None).ConfigureAwait(false);
            return new { settings, readbackConfirmed = true, status = GetStatus() };
        }
        catch (Exception exception)
        {
            // A valid readback stays visible, including an OFF preflight refusal. A write
            // without later GET is unknown; never replace that uncertainty with a 100% action.
            if (readStarted) lastDeviceState = readbackReceived || !writeStarted ? observed : null;
            if (readStarted && lastDeviceState is not null) lastSuccessfulRead = timeProvider.GetUtcNow();
            if (readStarted && !resultLogged)
                await SafeAuditAsync("lighting.explicit", "unconfirmed", peer.CallerSid, peer.Source,
                    new { writeStarted, readbackReceived, failure = exception.GetType().Name }, CancellationToken.None).ConfigureAwait(false);
            UpdateStatus(SafetyProtectionDisabled ? "SafetyProtectionDisabled" : "ExplicitCommandFailed");
            throw;
        }
        finally { explicitManualIo.Value = previousExplicitScope; MarkHeartbeat(); }
    }
}
