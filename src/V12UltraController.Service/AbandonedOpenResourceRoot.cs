using System.Collections.Concurrent;
using V12UltraController.Core.Hardware;

namespace V12UltraController.Service;

/// <summary>
/// Process-lifetime strong roots for exact-open attempts whose caller abandoned the result.
/// A cancellation-insensitive native open may return an exclusive device after the waiting
/// coordinator has timed out. Such a device must remain alive, undisposed and paired with the
/// retained writer lease until process exit; GC finalization must not release it early.
/// </summary>
internal static class AbandonedOpenResourceRoot
{
    private static readonly ConcurrentDictionary<OpenAttemptHolder, byte> Attempts = new();

    public static OpenAttemptHolder Register(Guid scope)
    {
        var holder = new OpenAttemptHolder(scope);
        if (!Attempts.TryAdd(holder, 0))
        {
            throw new InvalidOperationException("Could not register an exact-open retention holder.");
        }

        return holder;
    }

    public static void ReleaseAfterSuccessfulHandoff(OpenAttemptHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        _ = Attempts.TryRemove(holder, out _);
    }

    public static int CountCapturedDevices(Guid scope) =>
        Attempts.Keys.Count(holder => holder.Scope == scope && holder.HasCapturedDevice);

    internal sealed class OpenAttemptHolder(Guid scope)
    {
        private IV12Device? capturedDevice;

        public Guid Scope { get; } = scope;

        public bool HasCapturedDevice => Volatile.Read(ref capturedDevice) is not null;

        public void Capture(V12DeviceOpenResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            if (result.Status == V12DeviceOpenStatus.Opened && result.Device is { } openedDevice)
            {
                _ = Interlocked.CompareExchange(ref capturedDevice, openedDevice, null);
            }
        }
    }
}
