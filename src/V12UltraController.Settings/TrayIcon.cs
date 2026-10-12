using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;

namespace V12UltraController.Settings;

/// <summary>Native notification icon. Never opens hardware or sends IPC inside a mouse hook.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private readonly HwndSource source;
    private readonly HookProc hookProc;
    private readonly uint taskbarCreated;
    private readonly TrayHookLease hook;
    private readonly DispatcherTimer hookRenewal;
    private nint ownedIcon;
    private IconData data;
    private bool disposed;
    public event Action<int>? Wheel;
    public event Action? Open;
    public event Action? Menu;
    public bool WheelAvailable => hook.Handle != 0;

    public TrayIcon()
    {
        source = new HwndSource(new HwndSourceParameters("V12 Ultra tray") { Width = 0, Height = 0, WindowStyle = 0 });
        source.AddHook(WindowMessage);
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        ownedIcon = CreateFanIcon();
        data = new IconData
        {
            Size = (uint)Marshal.SizeOf<IconData>(), Window = source.Handle, Id = 1,
            Flags = 1 | 2 | 4, Callback = CallbackMessage,
            Icon = ownedIcon != 0 ? ownedIcon : LoadIcon(0, (nint)32512), Tip = "风序 · V12 Ultra — 正在连接后台",
            Info = "", InfoTitle = "",
        };
        if (!Shell_NotifyIcon(0, ref data))
        {
            source.Dispose();
            if (ownedIcon != 0) _ = DestroyIcon(ownedIcon);
            throw new Win32Exception("无法创建风扇托盘图标。");
        }
        hookProc = MouseHook;
        hook = new TrayHookLease(() => SetWindowsHookEx(14, hookProc, GetModuleHandle(null), 0),
            handle => { _ = UnhookWindowsHookEx(handle); });
        hook.Renew();
        hookRenewal = new DispatcherTimer(DispatcherPriority.Background, source.Dispatcher)
            { Interval = TimeSpan.FromSeconds(15) };
        hookRenewal.Tick += RenewHook;
        hookRenewal.Start();
    }

    private void RenewHook(object? sender, EventArgs args) => hook.Renew();

    public void SetText(string text)
    {
        if (disposed) return;
        data.Tip = text.Length > 127 ? text[..127] : text;
        _ = Shell_NotifyIcon(1, ref data);
    }

    public void ShowNotice(string title, string message)
    {
        if (disposed) return;
        // Use a copy so later tooltip updates never replay the notification.
        var notice = data;
        notice.Flags = 0x10; // NIF_INFO
        notice.InfoTitle = title.Length > 63 ? title[..63] : title;
        notice.Info = message.Length > 255 ? message[..255] : message;
        notice.InfoFlags = 0x2 | 0x10; // NIIF_WARNING | NIIF_NOSOUND
        _ = Shell_NotifyIcon(1, ref notice);
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)message == taskbarCreated && !disposed)
        {
            _ = Shell_NotifyIcon(0, ref data);
            hook.Renew();
        }
        if ((uint)message == CallbackMessage)
        {
            if ((int)lParam == 0x202) Open?.Invoke();
            if ((int)lParam == 0x405) Open?.Invoke(); // NIN_BALLOONUSERCLICK
            if ((int)lParam == 0x205) Menu?.Invoke();
            handled = true;
        }
        return 0;
    }

    private nint MouseHook(int code, nint message, nint pointer)
    {
        // No HID, IPC, logging or blocking waits here; dispatch work after returning.
        if (code >= 0 && message == (nint)0x20A && !disposed)
        {
            var input = Marshal.PtrToStructure<MouseData>(pointer);
            var id = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = source.Handle, Id = 1 };
            if ((input.Flags & 1) == 0 && Shell_NotifyIconGetRect(ref id, out var rect) == 0 &&
                input.Point.X >= rect.Left && input.Point.X < rect.Right &&
                input.Point.Y >= rect.Top && input.Point.Y < rect.Bottom &&
                TrayHookLease.HitTest(input.Point.X, input.Point.Y, rect.Left, rect.Top, rect.Right, rect.Bottom,
                    (input.Flags & 1) != 0, TraySurface(input.Point)))
            {
                var delta = (short)(input.MouseDataValue >> 16);
                source.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (!disposed) Wheel?.Invoke(delta);
                }));
                return 1;
            }
        }
        return CallNextHookEx(hook.Handle, code, message, pointer);
    }

    private static string TraySurface(NativePoint point)
    {
        var window = GetAncestor(WindowFromPoint(point), 2);
        var name = new StringBuilder(128);
        _ = GetClassName(window, name, name.Capacity);
        // Reject overlapping apps at the same screen coordinates. Unknown Shell surfaces
        // remain non-interactive, not an excuse to intercept another application's wheel.
        return name.ToString();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        hookRenewal.Stop();
        hookRenewal.Tick -= RenewHook;
        hook.Dispose();
        _ = Shell_NotifyIcon(2, ref data);
        if (ownedIcon != 0) { _ = DestroyIcon(ownedIcon); ownedIcon = 0; }
        source.Dispose();
        GC.KeepAlive(hookProc);
    }

    private delegate nint HookProc(int code, nint message, nint pointer);
    private static nint CreateFanIcon()
    {
        var mask = Enumerable.Repeat((byte)255, 128).ToArray();
        var pixels = new byte[32 * 32 * 4];
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 32; x++)
        {
            var dx = x - 15.5; var dy = y - 15.5;
            var radius = Math.Sqrt(dx * dx + dy * dy);
            var blade = Math.Sin(4 * Math.Atan2(dy, dx) + radius / 8);
            if (!(radius < 3.5 || radius is > 12 and < 14 || radius < 11 && blade > 0)) continue;
            mask[y * 4 + x / 8] &= (byte)~(128 >> (x % 8));
            var offset = (y * 32 + x) * 4;
            pixels[offset] = 250; pixels[offset + 1] = 204; pixels[offset + 2] = 90; pixels[offset + 3] = 255;
        }
        return CreateIcon(0, 32, 32, 1, 32, mask, pixels);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData
    { public NativePoint Point; public uint MouseDataValue, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct IconIdentifier
    { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct IconData
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref IconData data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier id, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll")] private static extern nint CreateIcon(nint instance, int width, int height, byte planes, byte bits, byte[] mask, byte[] pixels);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int size);
}
