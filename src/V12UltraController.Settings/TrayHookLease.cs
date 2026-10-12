namespace V12UltraController.Settings;

// Windows can silently remove a timed-out low-level hook. A nonzero handle
// does not prove liveness; periodically replace it on its owning dispatcher.
internal sealed class TrayHookLease(Func<nint> install, Action<nint> uninstall) : IDisposable
{
    internal nint Handle { get; private set; }
    private bool disposed;

    internal void Renew()
    {
        if (disposed) return;
        // Install first: a transient registration failure must not discard a
        // potentially working hook. No message pumping occurs between these calls.
        var replacement = install();
        if (replacement == 0) return;
        var previous = Handle;
        Handle = replacement;
        if (previous != 0) uninstall(previous);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var previous = Handle;
        Handle = 0;
        if (previous != 0) uninstall(previous);
    }

    internal static bool HitTest(int x, int y, int left, int top, int right, int bottom,
        bool injected, string surface) => !injected && x >= left && x < right && y >= top && y < bottom &&
        surface is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";
}
