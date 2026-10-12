using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    private bool appearanceReady;
    private void Appearance_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (MainTabs is null || !appearanceReady) return;
        ApplyAppearance();
        try { _appearancePreference?.SaveClassic(AppearanceCombo.SelectedIndex == 1); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { ActivityText.Text = "界面已切换，但无法保存外观偏好；下次可能恢复默认。"; }
    }

    private void ApplyAppearance()
    {
        var classic = AppearanceCombo.SelectedIndex == 1;
        // Palette choice never changes the navigation hierarchy or control layout.
        MainTabs.TabStripPlacement = Dock.Left;
        var palette = classic
            ? new[] { "#11151D", "#191F2A", "#222A37", "#344052", "#4AA8FF" }
            : new[] { "#202020", "#282828", "#323232", "#454545", "#60CDFF" };
        var keys = new[] { "WindowBrush", "SurfaceBrush", "SurfaceRaisedBrush", "BorderBrush", "PrimaryBrush" };
        for (var i = 0; i < keys.Length; i++)
            Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[i]));
        Application.Current.Resources["PrimaryButtonTextBrush"] = new SolidColorBrush(classic ? Colors.White : Color.FromRgb(16,16,16));
    }

    private void OpenLighting_Click(object sender, RoutedEventArgs e) => NavigateToPage("lighting");
    private async void HomeLightOn_Click(object sender, RoutedEventArgs e) => await SetHomeLightingAsync(true);
    private async void HomeLightOff_Click(object sender, RoutedEventArgs e) => await SetHomeLightingAsync(false);

    private async Task SetHomeLightingAsync(bool enabled) => await RunCommandAsync(async () =>
    {
        BeginLightingWrite();
        try
        {
        if (_currentMode == V12UltraController.Core.Control.ControlMode.Released || ExplicitManualContext && !ExplicitLightingSupported)
        {
            ActivityText.Text = LightingBlockedReason;
            return;
        }
        // Do not reuse or submit the RGB editor's unsaved draft.
        var read = await SendCheckedAsync(PipeOperations.LightingGet, new { });
        if (read.Data is not { } data ||
            !(data.TryGetProperty("supported", out var supported) && supported.ValueKind == JsonValueKind.True ||
              ExplicitManualContext && ExplicitLightingSupported) || !data.TryGetProperty("settings", out var settingsJson))
        {
            _homeLightingCacheUnavailable = true;
            ActivityText.Text = "没有完整灯光缓存；请先在灯光页应用完整设置。";
            return;
        }
        if (settingsJson.ValueKind != JsonValueKind.Object)
        {
            _homeLightingCacheUnavailable = true;
            ActivityText.Text = "没有灯光预设；请先在灯光页应用完整设置。";
            return;
        }
        var current = settingsJson.Deserialize<LightingSettings>(JsonOptions)
            ?? throw new InvalidOperationException("当前灯光预设未知，不能猜测其余设置。");
        current.Validate();
        var desired = current with { Enabled = enabled };
        var response = await SendCheckedAsync(PipeOperations.LightingSet, desired);
        if (response.Data is not { } result || !result.TryGetProperty("readbackConfirmed", out var confirmed) ||
            confirmed.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("灯光开关尚未取得设备确认。");
        _lightingBaseline = desired;
        _lightingCacheReadAt = null;
        _homeLightingCacheUnavailable = false;
        SetLightingFeedback("灯光已由设备确认。", null);
        ActivityText.Text = enabled ? "灯光开启已确认；未提交 RGB 编辑草稿。" : "灯光关闭已确认；未改变风扇电源。";
        }
        finally { EndLightingWrite(); }
    });

    private void RenderUsability()
    {
        if (ControlAvailabilityText is null) return;
        var status = _lastStatus;
        var blocked = status is null ? "后台未连接，请先启动或安装后台。" :
            _commandInProgress ? "正在处理请求，请等待确认。" :
            status.Software is { } info && info.UiContractVersion != UiCompatibility.ContractVersion ? "界面协议不匹配，请使用同一发布包中的 UI 与后台。" :
            !StateMutationAvailable ? "存在恢复屏障；请查看首页故障原因。" :
            !status.Commissioned ? "尚未正式接管：可保存控制设置，但不能直接操作设备。" :
            !status.Device.Connected ? "设备未连接，当前转速与开关状态未知。" :
            _serviceCommissioningActive ? "正在配对验证，请先完成或退出验证。" :
            status.FailSafe.Active ? "故障安全已激活；暂停灯光和关闭风扇操作。" :
            !status.Thermal.Healthy ? "温度信号无效，不能关闭风扇。" :
            status.Thermal.ControlCelsius is >= 60 ? "温度不低于 60°C，暂不能关闭风扇。" :
            !status.Fan.ManualPowerSupported ? "后台不支持风扇开关，请检查版本。" :
            "设备控制可用；关闭风扇仍需单独确认。";
        ControlAvailabilityText.Text = ExplicitManualAvailable
            ? "自动调节已暂停；手动转速和开关可用。后台会核验设备。" : blocked;
        
        foreach (var control in new Control[] { FanOnButton, FanOffButton, ApplyManualSpeedButton })
        {
            ToolTipService.SetShowOnDisabled(control, true);
            control.ToolTip = ControlAvailabilityText.Text;
        }
        ApplyManualSpeedButton.ToolTip = ApplyManualSpeedButton.IsEnabled
            ? ManualSpeedActionHelp
            : !_manualInputValid ? "请输入10–100之间的整数。" : ControlBlockedReason;
        FanOnButton.ToolTip = FanOnButton.IsEnabled
            ? (ExplicitManualContext ? "按保存目标开扇；必要时后台重连。" : "开扇，后台确认恢复散热。")
                + (FanLightingLinkSupported ? "\n" + FanLightingLinkHelp : "") : ControlBlockedReason;
        FanOffButton.ToolTip = FanOffButton.IsEnabled ? FanOffConfirmationText :
            !StateMutationAvailable || _currentMode == V12UltraController.Core.Control.ControlMode.Released ? ControlBlockedReason :
            !status!.Thermal.Healthy ? "温度信号无效，普通保护模式不能关闭风扇。" :
            status.Thermal.ControlCelsius is >= 60 ? "普通保护模式要求低于 60°C 才能关闭风扇。" : blocked;
        AutoModeButton.ToolTip = !AutoModeButton.IsEnabled ? ControlBlockedReason : SafetyProtectionDisabled
            ? "只保存自动模式；自动调节仍暂停，恢复保护需单独操作。" : "选择自动曲线控制。";
        ManualModeButton.ToolTip = !ManualModeButton.IsEnabled ? ControlBlockedReason : SafetyProtectionDisabled
            ? "保存手动模式；保护仍禁用，也可直接应用转速。" : "选择普通手动；保留温控保护，应用转速也会进入手动。";
        RenderQuickToggles();
        UpdateAdviceAvailability();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "未知";
        VersionStatusText.Text = $"设置界面：{version}\n后台版本：{status?.Software?.ServiceVersion ?? "未知（未连接或旧后台未提供）"}\n界面协议：{UiCompatibility.ContractVersion}";
        CompatibilityText.Text = status?.Software is not { } software ? "兼容性未确认：旧后台缺少版本信息。未提供信息不等于已兼容。" :
            software.UiContractVersion != UiCompatibility.ContractVersion ? "界面协议不一致，请使用匹配的 UI 与后台。" :
            software.ServiceVersion != version ? "界面协议一致，但程序版本不同；请核对发布包。" : "程序版本与界面协议一致。此结果不代表实机验收通过。";
        AcceptanceText.Text = $"正式接管：{(status?.Commissioned == true ? "后台报告已接管" : "未确认")}\n关机停扇资格：{(status?.Shutdown?.Qualified == true ? "后台存在资格记录（仍须核对设备匹配）" : "未确认")}\n设备身份匹配：{(status?.Device.ProtocolCompatible == true ? "后台报告协议兼容" : "未确认")}";
        RenderNavigationStatus();
    }
}
