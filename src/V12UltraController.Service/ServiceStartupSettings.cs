using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Service;

internal interface IServiceStartupRegistration
{
    bool ReadEnabled();
    void WriteEnabled(bool enabled);
}

/// <summary>Changes only this service's next-boot start mode, never starts/stops it.</summary>
internal sealed class WindowsServiceStartupRegistration : IServiceStartupRegistration
{
    public bool ReadEnabled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\" + PipeProtocol.ServiceName, writable: false);
        return key?.GetValue("Start") switch
        {
            2 => true,
            3 => false,
            _ => throw new InvalidOperationException("Service registration is absent or uses an unsupported start mode."),
        };
    }

    public void WriteEnabled(bool enabled)
    {
        // Do not reinterpret Disabled/Boot/System as a user preference or silently
        // enable a registration changed by an administrator outside this product.
        _ = ReadEnabled();
        using var manager = OpenSCManager(null, null, 1); // CONNECT
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var service = OpenService(manager, PipeProtocol.ServiceName, 2); // CHANGE_CONFIG
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!ChangeServiceConfig(service, uint.MaxValue, enabled ? 2u : 3u, uint.MaxValue,
                null, null, IntPtr.Zero, null, null, null, null))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(ServiceHandle service, uint type, uint start, uint error,
        string? binaryPath, string? group, IntPtr tag, string? dependencies, string? account,
        string? password, string? displayName);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

internal sealed record StartupSettingResult(bool Enabled, bool Changed, bool ReadbackConfirmed);

internal sealed class ServiceStartupSettings(IServiceStartupRegistration registration, JsonlAuditLog audit)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public StartupSettingResult Read() => new(registration.ReadEnabled(), false, true);

    public async Task<StartupSettingResult> SetAsync(bool enabled, PipePeerIdentity peer, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var previous = registration.ReadEnabled();
            if (previous == enabled) return new(enabled, false, true);
            var transactionId = Guid.NewGuid();
            // Persist intention first. SCM is authoritative after restart; we never
            // maintain a second startup bool inside thermal/shutdown configuration.
            await audit.WriteAsync("startup.set", "intent", peer.CallerSid, peer.Source,
                new { previous, enabled }, token, transactionId).ConfigureAwait(false);
            var attempted = false;
            try
            {
                token.ThrowIfCancellationRequested();
                attempted = true;
                registration.WriteEnabled(enabled);
                if (registration.ReadEnabled() != enabled)
                    throw new IOException("The service startup setting did not match the requested value.");
                await audit.WriteAsync("startup.set", "success", peer.CallerSid, peer.Source,
                    new { previous, enabled, readbackConfirmed = true }, CancellationToken.None, transactionId)
                    .ConfigureAwait(false);
                return new(enabled, true, true);
            }
            catch
            {
                // On API/persistence failure, restore the previous SCM setting where
                // possible, and never return a success based only on a write attempt.
                if (attempted) registration.WriteEnabled(previous);
                if (registration.ReadEnabled() != previous)
                    throw new IOException("Startup setting recovery could not be confirmed; refresh the setting before retrying.");
                await audit.WriteAsync("startup.set", "compensated", peer.CallerSid, peer.Source,
                    new { previous, enabled = previous }, CancellationToken.None, transactionId).ConfigureAwait(false);
                throw;
            }
        }
        finally { gate.Release(); }
    }
}
