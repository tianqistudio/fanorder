using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Service;

internal sealed partial class ControllerCoordinator
{
    // This is a temporary undo of our own light-off, never a persisted lighting
    // preference or an instruction for the passive observation loop to write HID.
    private sealed record FanLightingRestore(IV12Device Device, LightingSettings Original, LightingSettings Dark);
    private FanLightingRestore? fanLightingRestore;

    private void ForgetFanLightingRestore() => fanLightingRestore = null;

    private void DiscardChangedFanLightingRestore(IV12Device target, V12DeviceState observed)
    {
        if (fanLightingRestore is { } saved &&
            (!ReferenceEquals(saved.Device, target) || LightingSettings.FromState(observed) != saved.Dark))
            ForgetFanLightingRestore();
    }

    private async Task<V12DeviceState> ReadLinkedFanStateAsync(IV12Device target, CancellationToken token)
    {
        var state = await target.ReadStateAsync(token).ConfigureAwait(false);
        lastDeviceState = state;
        lastSuccessfulRead = timeProvider.GetUtcNow();
        return state;
    }

    private void CheckLinkedManualRequest(long? revision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (revision is { } current) RequireCurrentManualRequest(current);
        else if (ManualRequestPending)
            throw new ControllerOperationException(PipeErrorCodes.DeviceStateChanged,
                "已有更新的手动输入，已停止旧灯光恢复步骤。");
    }

    // Called inside the existing serialized HID transaction. Only compose the
    // validated ON lighting command and the unchanged narrow OFF command.
    private async Task<V12DeviceState> TurnFanOffWithLightingAsync(IV12Device target,
        V12DeviceState before, long? revision, CancellationToken token, Action? beforeOff = null)
    {
        lastDeviceState = before;
        lastSuccessfulRead = timeProvider.GetUtcNow();
        if (target is not IV12SystemShutdownDevice power)
            throw Invalid("当前设备不支持风扇关闭。");
        var settings = LightingSettings.FromState(before) ??
            throw Invalid("当前灯光设置无法确认，未执行联动关闭。");
        DiscardChangedFanLightingRestore(target, before);
        if (!before.PoweredOn)
        {
            if (settings.Enabled)
                throw Invalid("风扇已关闭，但灯光仍开启；请先开启风扇，再执行关闭并熄灯。");
            return before; // Repeated OFF never wakes the fan or overwrites the saved choice.
        }

        var dark = settings with { Enabled = false };
        if (settings.Enabled)
        {
            if (target is not IV12LightingDevice lighting)
                throw Invalid("当前设备无法确认熄灯，未关闭风扇。");
            CheckLinkedManualRequest(revision, token);
            await lighting.WriteLightingAsync(dark, token).ConfigureAwait(false);
            var expected = await ReadLinkedFanStateAsync(target, token).ConfigureAwait(false);
            if (FanStateChanged(before, expected))
            {
                ForgetFanLightingRestore();
                throw new ControllerOperationException(PipeErrorCodes.DeviceStateChanged,
                    "熄灯期间检测到新的风扇状态，已停止后续关闭，保留设备最新设置。");
            }
            if (LightingSettings.FromState(expected) != dark)
            {
                ForgetFanLightingRestore();
                throw new ControllerOperationException(PipeErrorCodes.CommandReadbackMismatch,
                    "灯光关闭未确认，未继续关闭风扇；请查看设备当前状态。");
            }
            fanLightingRestore = new(target, settings, dark);
        }
        else if (fanLightingRestore is null)
        {
            fanLightingRestore = new(target, settings, dark);
        }

        // The OFF step gets the newly verified dark state from the same device
        // handle. A newer request can stop this second step after light-off.
        CheckLinkedManualRequest(revision, token);
        beforeOff?.Invoke();
        await power.WriteSystemShutdownOffAsync(token).ConfigureAwait(false);
        var after = await ReadLinkedFanStateAsync(target, token).ConfigureAwait(false);
        if (after.PoweredOn || LightingSettings.FromState(after) != dark)
        {
            DiscardChangedFanLightingRestore(target, after);
            throw new ControllerOperationException(PipeErrorCodes.CommandReadbackMismatch,
                "风扇关闭或熄灯未完整确认；已保留设备回读，未重试或重新开扇。");
        }
        return after;
    }

    private async Task<(V12DeviceState State, bool Restored)> RestoreFanLightingAfterOnAsync(
        IV12Device target, V12DeviceState before, V12DeviceState after, byte expectedPercent,
        long? revision, CancellationToken token)
    {
        DiscardChangedFanLightingRestore(target, before);
        if (fanLightingRestore is not { } saved) return (after, false);
        if (!after.PoweredOn || after.FanSpeedPercent != expectedPercent || !LightingMatches(before, after))
        {
            ForgetFanLightingRestore();
            return (after, false);
        }
        if (saved.Original == saved.Dark)
        {
            ForgetFanLightingRestore();
            return (after, true); // The earlier choice was already dark.
        }
        if (target is not IV12LightingDevice lighting)
        {
            ForgetFanLightingRestore();
            throw Invalid("风扇已开启，但当前设备不支持恢复灯光。");
        }
        CheckLinkedManualRequest(revision, token);
        var current = await ReadLinkedFanStateAsync(target, token).ConfigureAwait(false);
        if (FanStateChanged(after, current) || !LightingMatches(after, current))
        {
            ForgetFanLightingRestore();
            throw new ControllerOperationException(PipeErrorCodes.DeviceStateChanged,
                "开启后检测到新的设备设置，已保留最新状态，未覆盖灯光。");
        }
        CheckLinkedManualRequest(revision, token);
        // Consume before SET: failures must not replay an old lighting choice.
        ForgetFanLightingRestore();
        await lighting.WriteLightingAsync(saved.Original, token).ConfigureAwait(false);
        var restored = await ReadLinkedFanStateAsync(target, token).ConfigureAwait(false);
        if (FanStateChanged(current, restored) || LightingSettings.FromState(restored) != saved.Original)
            throw new ControllerOperationException(PipeErrorCodes.CommandReadbackMismatch,
                "风扇已执行开启，但灯光恢复或风扇保持状态未确认；保留最终回读，未重试。");
        return (restored, true);
    }
}
