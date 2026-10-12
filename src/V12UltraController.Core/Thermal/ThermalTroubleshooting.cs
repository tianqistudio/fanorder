namespace V12UltraController.Core.Thermal;

// Guidance, not a claim that a driver or permission failure has been diagnosed.
public static class ThermalTroubleshooting
{
    public const string English =
        "CPU telemetry is invalid or stale. Check sample freshness, the official signed PawnIO driver " +
        "and SYSTEM service permissions. If a missing prerequisite was just installed, restart the " +
        "controller service while attended. GPU readings cannot replace CPU; this message does not " +
        "establish the root cause.";

    public const string Chinese =
        "CPU 温度无效或过期。请检查采样新鲜度、PawnIO 官方签名驱动及服务 SYSTEM 权限。" +
        "若刚安装缺失的驱动，请在有人看护时重启控制器服务。GPU 不能代替 CPU；此提示不代表已确定故障原因。";
}
