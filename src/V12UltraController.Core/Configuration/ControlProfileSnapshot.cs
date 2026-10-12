using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;

namespace V12UltraController.Core.Configuration;

/// <summary>A saved profile. A disabled controller can explicitly apply a manual profile once without resuming automation.</summary>
public sealed record ControlProfileSnapshot(
    string Id,
    string Name,
    ControlMode Mode,
    byte ManualSpeedPercent,
    FanCurve Curve,
    LightingSettings? Lighting)
{
    public void Validate()
    {
        ValidateLabel(Id, nameof(Id));
        ValidateLabel(Name, nameof(Name));
        if (Mode is not (ControlMode.Auto or ControlMode.Manual))
            throw new ConfigurationValidationException("方案只支持自动曲线或手动转速模式。");
        if (ManualSpeedPercent is < 10 or > 100)
            throw new ConfigurationValidationException("方案的手动转速必须在 10–100% 之间。");
        if (Curve is null)
            throw new ConfigurationValidationException("方案必须包含有效曲线。");
        Curve.Validate();
        Lighting?.Validate();
    }

    private static void ValidateLabel(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 || value.Any(char.IsControl))
            throw new ConfigurationValidationException($"方案 {field} 必须为 1–80 个非控制字符。");
    }
}
