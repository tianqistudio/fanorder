using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace V12UltraController.Core.Ipc;

internal interface INamedPipeServerVerifier
{
    void Verify(NamedPipeClientStream pipe);
}

internal sealed class NamedPipeServerVerifier(IPipeServerIdentitySource identitySource)
    : INamedPipeServerVerifier
{
    private static readonly string LocalSystemSid =
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;

    public void Verify(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!pipe.IsConnected)
        {
            throw new InvalidOperationException("The named pipe must be connected before its server can be verified.");
        }

        PipeServerIdentity identity;
        try
        {
            identity = identitySource.Query(pipe, PipeProtocol.ServiceName);
        }
        catch (Win32Exception exception)
        {
            throw new IOException(
                "Windows could not verify the named-pipe server against the registered service.",
                exception);
        }
        if (!identity.ServiceIsRunning || identity.ServiceProcessId == 0)
        {
            throw new UnauthorizedAccessException(
                $"Windows service '{PipeProtocol.ServiceName}' is not running with a verifiable process ID.");
        }

        if (identity.PipeServerProcessId != identity.ServiceProcessId)
        {
            throw new UnauthorizedAccessException(
                "The named-pipe server process does not match the registered Windows service process.");
        }

        // Standard users can reliably obtain the service PID through SCM. Windows
        // may deny TOKEN_QUERY on a LocalSystem process; when it permits the query,
        // a non-LocalSystem token is a hard identity failure.
        if (identity.ProcessUserSid is not null &&
            !string.Equals(identity.ProcessUserSid, LocalSystemSid, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "The registered Windows service process is not running as LocalSystem.");
        }
    }
}

internal interface IPipeServerIdentitySource
{
    PipeServerIdentity Query(NamedPipeClientStream pipe, string serviceName);
}

internal sealed record PipeServerIdentity(
    uint PipeServerProcessId,
    uint ServiceProcessId,
    bool ServiceIsRunning,
    string? ProcessUserSid);

internal sealed partial class WindowsPipeServerIdentitySource : IPipeServerIdentitySource
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceRunning = 0x0000_0004;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUserInformationClass = 1;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInsufficientBuffer = 122;

    public PipeServerIdentity Query(NamedPipeClientStream pipe, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pipeServerProcessId))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error,
                $"Cannot identify the named-pipe server process (Win32 {error}).");
        }

        var service = QueryService(serviceName);
        var processSid = TryQueryProcessUserSid(pipeServerProcessId);
        return new PipeServerIdentity(
            pipeServerProcessId,
            service.ProcessId,
            service.CurrentState == ServiceRunning,
            processSid);
    }

    private static ServiceStatusProcess QueryService(string serviceName)
    {
        using var manager = new SafeServiceHandle(OpenScManager(null, null, ScManagerConnect));
        if (manager.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error,
                $"Cannot connect to the Windows Service Control Manager (Win32 {error}).");
        }

        using var service = new SafeServiceHandle(OpenService(
            manager.DangerousGetHandle(),
            serviceName,
            ServiceQueryStatus));
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error,
                $"Cannot query Windows service '{serviceName}' (Win32 {error}).");
        }

        var size = checked((uint)Marshal.SizeOf<ServiceStatusProcess>());
        if (!QueryServiceStatusEx(
                service.DangerousGetHandle(),
                ScStatusProcessInfo,
                out var status,
                size,
                out _))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error,
                $"Cannot query Windows service '{serviceName}' status (Win32 {error}).");
        }

        return status;
    }

    private static string? TryQueryProcessUserSid(uint processId)
    {
        using var process = new SafeKernelObjectHandle(
            OpenProcess(ProcessQueryLimitedInformation, false, processId));
        if (process.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorAccessDenied)
            {
                return null;
            }

            throw new Win32Exception(error,
                $"Cannot open the named-pipe server process (Win32 {error}).");
        }

        if (!OpenProcessToken(process.DangerousGetHandle(), TokenQuery, out var tokenHandle))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorAccessDenied)
            {
                return null;
            }

            throw new Win32Exception(error,
                $"Cannot query the named-pipe server process token (Win32 {error}).");
        }

        using (var token = new SafeKernelObjectHandle(tokenHandle))
        {
            _ = GetTokenInformation(
                token.DangerousGetHandle(),
                TokenUserInformationClass,
                IntPtr.Zero,
                0,
                out var required);
            var firstError = Marshal.GetLastWin32Error();
            if (required == 0 || firstError != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(firstError,
                    $"Cannot size the named-pipe server token identity (Win32 {firstError}).");
            }

            var buffer = Marshal.AllocHGlobal(checked((int)required));
            try
            {
                if (!GetTokenInformation(
                        token.DangerousGetHandle(),
                        TokenUserInformationClass,
                        buffer,
                        required,
                        out _))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(error,
                        $"Cannot read the named-pipe server token identity (Win32 {error}).");
                }

                var tokenUser = Marshal.PtrToStructure<TokenUser>(buffer);
                return new SecurityIdentifier(tokenUser.User.Sid).Value;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
        internal uint ProcessId;
        internal uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        internal IntPtr Sid;
        internal uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUser
    {
        internal SidAndAttributes User;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeServiceHandle(IntPtr handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    private sealed class SafeKernelObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeKernelObjectHandle(IntPtr handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetNamedPipeServerProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenScManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenService(
        IntPtr serviceManager,
        string serviceName,
        uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(
        IntPtr service,
        int infoLevel,
        out ServiceStatusProcess buffer,
        uint bufferSize,
        out uint bytesNeeded);

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static partial IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        IntPtr process,
        uint desiredAccess,
        out IntPtr token);

    [LibraryImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        IntPtr token,
        int informationClass,
        IntPtr information,
        uint informationLength,
        out uint returnLength);

    [LibraryImport("advapi32.dll", EntryPoint = "CloseServiceHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr serviceHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
