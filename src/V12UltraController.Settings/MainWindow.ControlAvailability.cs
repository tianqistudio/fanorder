using V12UltraController.Core.Control;
using V12UltraController.Core.Status;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    private bool ServiceReadAvailable => _lastStatus is not null && !_commandInProgress && !_disableSafetyInProgress &&
        !_closing && !_closed && (_lastStatus.Software is null || _lastStatus.Software.UiContractVersion == UiCompatibility.ContractVersion);
    private bool ExplicitManualContext => PureManualSelected || SafetyAutomaticOutputStopped;
    private bool ExplicitManualAvailable => StateMutationAvailable && ExplicitManualContext &&
        _lastStatus?.Commissioned == true && !_serviceCommissioningActive && _currentMode != ControlMode.Released;
    private bool ManualSpeedAvailable => ManualSpeedAllowed(allowBusy: false);
    private bool ManualSpeedAllowed(bool allowBusy) => StateMutationAllowed(allowBusy) && _lastStatus?.Commissioned == true &&
        !(_lastStatus.Commissioning?.Active ?? _serviceCommissioningActive) &&
        (_currentMode is ControlMode.Auto or ControlMode.Manual or ControlMode.PureManual) &&
        (ExplicitManualContext || _lastStatus.Device.Connected);
    private string ManualSpeedActionHelp => PureManualSelected
        ? "应用手动转速，保持纯手动；必要时后台会重连。"
        : SafetyAutomaticOutputStopped
            ? "应用手动转速并进入手动；安全保护仍禁用，必要时后台会重连。"
            : "应用转速并进入手动；保护设置不变，数值以回读为准。";
    private bool ExplicitLightingSupported => _lastStatus?.Software?.ManualActionsWhileProtectionDisabledSupported == true;
    private bool LightingOutputAvailable => StateMutationAvailable && _lastStatus?.Commissioned == true &&
        !_serviceCommissioningActive && _currentMode != ControlMode.Released &&
        (ExplicitManualContext ? ExplicitManualAvailable && ExplicitLightingSupported && _lastStatus.Fan.PoweredOn != false :
            _lastStatus.Device.Connected && _lastStatus.Fan.PoweredOn == true && !_lastStatus.FailSafe.Active &&
            _lastStatus.Software?.LightingSupported == true);
    private string ControlBlockedReason => _lastStatus is null ? "后台未连接，请先启动后台。" :
        _disableSafetyInProgress ? "正在优先禁用安全保护，请等待确认。" :
        _commandInProgress ? "正在处理请求，请等待确认。" :
        _lastStatus.Software is { } software && software.UiContractVersion != UiCompatibility.ContractVersion ? "界面与后台版本不兼容。" :
        _lastStatus.HardwareSafety.Poisoned ? "设备通信存在恢复屏障，请查看概览中的故障详情。" :
        _lastStatus.AuditMutation is { Blocked: true } or { Pending: true } ||
        _lastStatus.SafetyOperationAudit is { Blocked: true } or { Pending: true } ||
        _lastStatus.ForcedUninstallRecovery is { Required: true } ? "后台仍有恢复事务需要处理，请查看概览详情。" :
        !_lastStatus.Commissioned ? "尚未完成接管，请先完成设备配对与接管。" :
        (_lastStatus.Commissioning?.Active ?? _serviceCommissioningActive) ? "正在配对验证，请先完成或退出验证。" :
        _currentMode == ControlMode.Released ? "控制已释放，请先选择纯手动重新接管。" :
        !_lastStatus.Device.Connected && !ExplicitManualContext ? "设备尚未连接，等待后台确认。" :
        "当前不可执行，请刷新后台状态。";
    private string LightingBlockedReason => !StateMutationAvailable || _lastStatus?.Commissioned != true ||
        _serviceCommissioningActive || _currentMode == ControlMode.Released ? ControlBlockedReason :
        ExplicitManualContext && !ExplicitLightingSupported ? "当前后台不支持停控灯光，请更新后台；草稿仍可编辑。" :
        _lastStatus?.Fan.PoweredOn == false ? "风扇已关闭；灯光操作不会开扇。" :
        _lastStatus?.Software?.LightingSupported != true ? "后台不支持灯光控制。" :
        _lastStatus?.FailSafe.Active == true && !ExplicitManualContext ? "温控保护中，暂不能调灯。" : ControlBlockedReason;
}
