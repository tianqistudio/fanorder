using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    private bool? quickLightingOn;
    private DateTimeOffset? quickLightingRead;
    private bool quickLightingReading;
    private long quickLightingGeneration = -1;
    private long quickLightingRevision;

    private void InvalidateQuickLighting()
    {
        quickLightingRevision++;
        quickLightingOn = null;
        quickLightingRead = null;
    }

    private bool FreshDeviceRead(DateTimeOffset? stamp) => _lastStatus is
        { Device.Connected: true, HardwareSafety.Poisoned: false } &&
        stamp is { } at && at <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow - at < TimeSpan.FromSeconds(10);

    private bool? QuickFanState => FreshDeviceRead(_lastStatus?.LastSuccessfulRead) ? _lastStatus?.Fan.PoweredOn : null;
    private bool? QuickLightState => quickLightingGeneration == _controlRequestGeneration &&
        FreshDeviceRead(quickLightingRead) &&
        (_lastStatus?.LastSuccessfulWrite is not { } write || quickLightingRead >= write) ? quickLightingOn : null;

    private void RenderQuickToggles()
    {
        if (HomeFanToggleButton is null || HomeLightToggleButton is null) return;
        var fan = QuickFanState;
        HomeFanToggleButton.Content = fan switch
        {
            true => "风扇已开 · 关闭…", false => "风扇已关 · 开启", null => "风扇未知 · 刷新",
        };
        HomeFanToggleButton.IsEnabled = fan switch
        {
            true => FanOffButton.IsEnabled, false => FanOnButton.IsEnabled, null => ServiceReadAvailable,
        };
        HomeFanToggleButton.ToolTip = fan switch
        {
            true => FanOffButton.ToolTip, false => FanOnButton.ToolTip,
            null => "状态未知或已过期；点击只刷新后台缓存，不切换电源。",
        };
        HomeLightToggleButton.Content = QuickLightState switch
        {
            true => "灯光已开 · 关闭", false => "灯光已关 · 开启", null => "灯光未知 · 刷新",
        };
        HomeLightToggleButton.IsEnabled = LightingOutputAvailable && !_homeLightingCacheUnavailable && !quickLightingReading;
        HomeLightToggleButton.ToolTip = !LightingOutputAvailable ? LightingBlockedReason :
            _homeLightingCacheUnavailable ? "没有完整灯光缓存；请先在灯光页应用完整设置。" :
            QuickLightState is null ? "状态未知或已过期；点击只刷新后台缓存，不切换灯光。" :
            "根据后台确认状态切换灯光；不提交灯光编辑页的草稿。";
        foreach (var button in new[] { HomeFanToggleButton, HomeLightToggleButton })
            ToolTipService.SetShowOnDisabled(button, true);
    }

    private async void HomeFanToggle_Click(object sender, RoutedEventArgs e)
    {
        RenderQuickToggles();
        if (!HomeFanToggleButton.IsEnabled) return;
        switch (QuickFanState)
        {
            case true: FanOffButton_Click(sender, e); break;
            case false: await SetFanPowerAsync(true); break;
            default: await RefreshStatusAsync(showErrors: true); break;
        }
    }

    private async void HomeLightToggle_Click(object sender, RoutedEventArgs e)
    {
        RenderQuickToggles();
        if (!HomeLightToggleButton.IsEnabled) return;
        if (QuickLightState is not { } on)
        {
            await RefreshQuickLightingAsync();
            return;
        }
        // An explicit desired state, not a blind toggle after an asynchronous read.
        quickLightingOn = null;
        quickLightingRead = null;
        await SetHomeLightingAsync(!on);
        await RefreshQuickLightingAsync();
    }

    internal async Task RefreshQuickLightingAsync()
    {
        if (!ServiceReadAvailable || quickLightingReading || _lightingWriting) return;
        var generation = _controlRequestGeneration;
        var revision = quickLightingRevision;
        quickLightingReading = true;
        RenderQuickToggles();
        try
        {
            // Cache-only API: no device query, and no changes to RGB editor drafts.
            var response = await SendCheckedAsync(PipeOperations.LightingGet, new { });
            if (_closing || _closed || generation != _controlRequestGeneration || revision != quickLightingRevision) return;
            quickLightingOn = null;
            quickLightingRead = null;
            if (response.Data is { } data && data.TryGetProperty("settings", out var value) &&
                value.ValueKind == JsonValueKind.Object && data.TryGetProperty("lastSuccessfulRead", out var stamp) &&
                stamp.ValueKind == JsonValueKind.String && stamp.TryGetDateTimeOffset(out var at))
            {
                var settings = value.Deserialize<LightingSettings>(JsonOptions);
                settings?.Validate();
                quickLightingGeneration = generation;
                quickLightingOn = settings?.Enabled;
                quickLightingRead = at;
            }
        }
        catch (Exception exception) when (IsExpectedClientException(exception) || exception is ArgumentException)
        {
            if (generation == _controlRequestGeneration) { quickLightingOn = null; quickLightingRead = null; }
        }
        finally { quickLightingReading = false; RenderQuickToggles(); }
    }
}
