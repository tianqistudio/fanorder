using System.Diagnostics;
using System.Security.Principal;

namespace V12UltraController.Settings;

public partial class App
{
    private Mutex? instanceMutex;
    private EventWaitHandle? activateEvent;
    private RegisteredWaitHandle? activationWait;
    private bool ownsInstance;

    private bool EnterSingleInstance(bool trayLaunch)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        var name = $@"Local\FanOrder.Settings.{identity.User?.Value}.{process.SessionId}";
        activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate");
        instanceMutex = new Mutex(false, name);
        try { ownsInstance = instanceMutex.WaitOne(0); }
        catch (AbandonedMutexException) { ownsInstance = true; }
        if (!ownsInstance)
        {
            if (!trayLaunch) activateEvent.Set();
            return false;
        }
        activationWait = ThreadPool.RegisterWaitForSingleObject(activateEvent, (_, _) =>
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ActivateExistingWindow());
        }, null, Timeout.Infinite, executeOnlyOnce: false);
        return true;
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        activationWait?.Unregister(null);
        activateEvent?.Dispose();
        if (ownsInstance) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
