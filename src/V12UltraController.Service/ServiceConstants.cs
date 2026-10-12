namespace V12UltraController.Service;

using V12UltraController.Core.Ipc;

internal static class ServiceConstants
{
    public const string ServiceName = PipeProtocol.ServiceName;
    public const string DisplayName = "TianqiStudio V12 Ultra Controller";
    public const string MythCoolTaskName = @"\Myth.Cool";
    public static readonly TimeSpan CommissioningSessionLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ShutdownQualificationPendingLifetime = TimeSpan.FromMinutes(2);
}
