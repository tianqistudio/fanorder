using System.ComponentModel;
using System.Runtime.InteropServices;

namespace V12UltraController.Settings;

internal interface ISystemShutdownRequester
{
    bool TryRequestShutdown(out string? error);
}

/// <summary>
/// Requests Windows shutdown through user32. The service receives the shutdown
/// notification and performs its already-qualified terminal fan transaction.
/// </summary>
internal sealed class WindowsSystemShutdownRequester : ISystemShutdownRequester
{
    private const uint ExitWindowsShutdown = 0x00000001;
    private const uint ExitWindowsForceIfHung = 0x00000010;

    public bool TryRequestShutdown(out string? error)
    {
        if (ExitWindowsEx(ExitWindowsShutdown | ExitWindowsForceIfHung, 0))
        {
            error = null;
            return true;
        }

        error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);
}
