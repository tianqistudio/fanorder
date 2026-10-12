using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TianqiStudio.DesktopShortcuts.Wpf;

/// <summary>A dedicated message-only window survives the main window being hidden to the tray.</summary>
public sealed class WindowsHotkeyRegistrar : IHotkeyRegistrar
{
    private readonly HwndSource source = new(new HwndSourceParameters("TianqiStudio.DesktopShortcuts")
    {
        ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0,
    });
    private readonly HashSet<int> ids = new();
    private bool disposed;
    public event Action<int>? Pressed;

    public WindowsHotkeyRegistrar() => source.AddHook(WindowMessage);

    public bool TryRegister(int id, ShortcutChord chord, out string? error)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!RegisterHotKey(source.Handle, id, (uint)chord.Modifiers | 0x4000, (uint)chord.VirtualKey))
        {
            var code = Marshal.GetLastWin32Error();
            error = code == 1409 ? "组合已被其他程序占用。" : $"全局注册失败（{code}）：{new Win32Exception(code).Message}";
            return false;
        }
        ids.Add(id);
        error = null;
        return true;
    }

    public void Unregister(int id)
    {
        if (disposed || !ids.Remove(id)) return;
        UnregisterHotKey(source.Handle, id);
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && ids.Contains(wParam.ToInt32()))
        {
            handled = true;
            Pressed?.Invoke(wParam.ToInt32());
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (disposed) return;
        foreach (var id in ids.ToArray()) Unregister(id);
        disposed = true;
        source.RemoveHook(WindowMessage);
        source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
