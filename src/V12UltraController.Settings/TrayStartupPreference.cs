using System.IO;
using Microsoft.Win32;

namespace V12UltraController.Settings;

internal interface ITrayStartupPreference
{
    bool ReadEnabled();
    bool ReadBlocked();
    void SetEnabled(bool enabled);
}

internal sealed class TrayStartupPreference : ITrayStartupPreference
{
    internal const string ValueName = "FanOrder";
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static string Command(string executable) => $"\"{Path.GetFullPath(executable)}\" --tray";
    private string ExpectedCommand => Command(Environment.ProcessPath ?? throw new IOException("无法确定程序路径。"));

    public bool ReadEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(ValueName) as string, ExpectedCommand, StringComparison.OrdinalIgnoreCase);
    }

    public bool ReadBlocked()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
        return key?.GetValue(ValueName) is byte[] { Length: > 0 } state && state[0] is 3 or 7;
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        var existing = key.GetValue(ValueName) as string;
        if (existing is not null && !string.Equals(existing, ExpectedCommand, StringComparison.OrdinalIgnoreCase))
            throw new IOException("同名启动项指向其他位置，请先核对 Windows 启动应用。原启动项未修改。");
        if (enabled) key.SetValue(ValueName, ExpectedCommand, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
        if (ReadEnabled() != enabled) throw new IOException("登录启动设置未通过读取确认。");
    }
}
