using V12UltraController.Settings;

internal static class TrayHookChecks
{
    internal static void Run(Action<bool, string> assert)
    {
        var active = new HashSet<nint>();
        var next = 0;
        var fail = false;
        var installs = 0;
        var lease = new TrayHookLease(() =>
        {
            installs++;
            if (fail) return 0;
            var handle = (nint)(++next); active.Add(handle); return handle;
        }, handle => active.Remove(handle));
        lease.Renew();
        assert(active.SetEquals([lease.Handle]), "hook starts with exactly one registration");
        var stale = lease.Handle;
        active.Clear(); // Simulate silent Windows removal with a nonzero cached handle.
        lease.Renew();
        assert(lease.Handle != stale && active.SetEquals([lease.Handle]), "renewal repairs a silently removed hook");
        fail = true; var previous = lease.Handle; lease.Renew();
        assert(lease.Handle == previous && active.Contains(previous), "failed renewal preserves an existing registration");
        fail = false;
        for (var i = 0; i < 20; i++) lease.Renew();
        assert(active.SetEquals([lease.Handle]), "repeated timer or Explorer recovery never accumulates hooks");
        lease.Dispose(); lease.Dispose(); var count = installs; lease.Renew();
        assert(active.Count == 0 && lease.Handle == 0 && count == installs, "dispose releases once and prevents resurrection");
        using var retry = new TrayHookLease(() => fail ? 0 : (nint)99, _ => { });
        fail = true; retry.Renew(); assert(retry.Handle == 0, "initial registration failure remains unavailable");
        fail = false; retry.Renew(); assert(retry.Handle == 99, "later renewal retries initial failure");
        foreach (var surface in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland" })
            assert(TrayHookLease.HitTest(-90, 15, -100, 10, -70, 40, false, surface), "hit accepts tray surface " + surface);
        assert(!TrayHookLease.HitTest(15, 15, 10, 10, 30, 30, true, "Shell_TrayWnd"), "injected wheel cannot control the device");
        assert(!TrayHookLease.HitTest(15, 15, 10, 10, 30, 30, false, "OtherApp"), "overlapping and unknown windows retain their own wheel");
        assert(!TrayHookLease.HitTest(30, 15, 10, 10, 30, 30, false, "Shell_TrayWnd") &&
            !TrayHookLease.HitTest(9, 15, 10, 10, 30, 30, false, "Shell_TrayWnd") &&
            !TrayHookLease.HitTest(15, 30, 10, 10, 30, 30, false, "Shell_TrayWnd"), "wheel outside icon bounds is not intercepted");
    }
}
