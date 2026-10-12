using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using V12UltraController.Core.Security;

namespace V12UltraController.Service;

internal interface ISecurePipeFactory
{
    NamedPipeServerStream Create(string pipeName, bool firstInstance);
}

internal sealed partial class SecurePipeFactory(
    IInstallerEnrollmentReader enrollmentReader,
    ILogger<SecurePipeFactory> logger)
    : ISecurePipeFactory
{
    // CreateNamedPipeW dwOpenMode flags. FirstPipeInstance is intentionally used
    // only for the bootstrap instance; later instances must coexist with it.
    internal const uint FileFlagFirstPipeInstance = 0x0008_0000;
    internal const uint FileFlagOverlapped = 0x4000_0000;
    internal const uint FileFlagWriteThrough = 0x8000_0000;

    // CreateNamedPipeW dwPipeMode flag. This is an OS-enforced rejection and is
    // independent of the DACL and the application-level peer checks below.
    internal const uint PipeRejectRemoteClients = 0x0000_0008;

    private const uint PipeAccessDuplex = 0x0000_0003;
    private const uint PipeTypeByte = 0x0000_0000;
    private const uint PipeReadModeByte = 0x0000_0000;
    private const uint PipeWait = 0x0000_0000;
    // 255 is PIPE_UNLIMITED_INSTANCES. The worker still creates only one waiting
    // listener at a time, but connected requests do not consume the last slot and
    // prevent creation of the replacement listener.
    private const uint MaximumInstances = 255;
    private const uint BufferSize = 65_536;
    private readonly SecurityIdentifier serverPrincipal =
        new(WellKnownSidType.LocalSystemSid, null);

    internal SecurePipeFactory(
        IInstallerEnrollmentReader enrollmentReader,
        ILogger<SecurePipeFactory> logger,
        SecurityIdentifier serverPrincipal)
        : this(enrollmentReader, logger)
    {
        this.serverPrincipal = serverPrincipal ?? throw new ArgumentNullException(nameof(serverPrincipal));
    }

    public NamedPipeServerStream Create(string pipeName, bool firstInstance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        var security = new PipeSecurity();
        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        AddAllowRule(
            security,
            localSystem,
            allowCreatingServerInstances: true);
        if (!serverPrincipal.Equals(localSystem))
        {
            // Test-only constructor support: production always runs with LocalSystem.
            AddAllowRule(security, serverPrincipal, allowCreatingServerInstances: true);
        }

        AddAllowRule(
            security,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            allowCreatingServerInstances: false);

        var authorizedUser = TryReadAuthorizedUser();
        if (authorizedUser is not null)
        {
            AddAllowRule(security, authorizedUser, allowCreatingServerInstances: false);
        }
        else
        {
            logger.LogWarning(
                "No valid authorized-user SID is installed; only LocalSystem and local administrators can use the API.");
        }

        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var descriptorPointer = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, descriptorPointer, descriptor.Length);
            var attributes = new SecurityAttributes
            {
                Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
                SecurityDescriptor = descriptorPointer,
                InheritHandle = 0,
            };

            var handle = CreateNamedPipe(
                $@"\\.\pipe\{pipeName}",
                BuildOpenMode(firstInstance),
                BuildPipeMode(),
                MaximumInstances,
                BufferSize,
                BufferSize,
                0,
                ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException(
                    $"Cannot create the secured local named pipe (Win32 {error}).",
                    new Win32Exception(error));
            }

            try
            {
                return new NamedPipeServerStream(
                    PipeDirection.InOut,
                    isAsync: true,
                    isConnected: false,
                    handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(descriptorPointer);
        }
    }

    internal static uint BuildOpenMode(bool firstInstance) =>
        PipeAccessDuplex |
        FileFlagOverlapped |
        FileFlagWriteThrough |
        (firstInstance ? FileFlagFirstPipeInstance : 0);

    internal static uint BuildPipeMode() =>
        PipeTypeByte | PipeReadModeByte | PipeWait | PipeRejectRemoteClients;

    internal static uint InspectNativePipeFlags(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!GetNamedPipeInfo(pipe.SafePipeHandle, out var flags, out _, out _, out _))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error,
                $"Cannot inspect the named-pipe kernel flags (Win32 {error}).");
        }

        return flags;
    }

    private SecurityIdentifier? TryReadAuthorizedUser()
    {
        try
        {
            var value = InstallerEnrollmentSid.ReadAndValidate(enrollmentReader);
            return value is null ? null : new SecurityIdentifier(value);
        }
        catch (Exception exception) when (InstallerEnrollmentSid.IsExpectedReadFailure(exception))
        {
            logger.LogError(exception,
                "The 64-bit HKLM installer enrollment is missing, unreadable, or invalid.");
            return null;
        }
    }

    private static void AddAllowRule(
        PipeSecurity security,
        SecurityIdentifier sid,
        bool allowCreatingServerInstances)
    {
        var rights = PipeAccessRights.ReadWrite;
        if (allowCreatingServerInstances)
        {
            // Only the LocalSystem service creates successor server instances.
            // Client principals must not be able to consume the instance quota.
            rights |= PipeAccessRights.CreateNewInstance;
        }

        security.AddAccessRule(new PipeAccessRule(sid, rights, AccessControlType.Allow));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal uint Length;
        internal IntPtr SecurityDescriptor;

        internal int InheritHandle;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafePipeHandle CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint maximumInstances,
        uint outputBufferSize,
        uint inputBufferSize,
        uint defaultTimeoutMilliseconds,
        ref SecurityAttributes securityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetNamedPipeInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeInfo(
        SafePipeHandle pipe,
        out uint flags,
        out uint outputBufferSize,
        out uint inputBufferSize,
        out uint maximumInstances);
}

internal static partial class PipePeer
{
    private const int ErrorPipeLocal = 229;

    public static PipePeerIdentity Inspect(NamedPipeServerStream pipe)
    {
        WarmUpWindowsIdentityDependencies();

        var computerNameBuffer = new char[256];
        var computerNameAvailable = GetNamedPipeClientComputerName(
                pipe.SafePipeHandle,
                computerNameBuffer,
                checked((uint)computerNameBuffer.Length));
        var computerNameError = computerNameAvailable ? 0 : Marshal.GetLastWin32Error();
        if (!computerNameAvailable && computerNameError != ErrorPipeLocal)
        {
            throw new Win32Exception(computerNameError,
                $"Cannot verify the named-pipe client computer (Win32 {computerNameError}).");
        }

        var local = computerNameError == ErrorPipeLocal;
        if (computerNameAvailable)
        {
            var terminator = Array.IndexOf(computerNameBuffer, '\0');
            var computerName = new string(computerNameBuffer, 0,
                terminator < 0 ? computerNameBuffer.Length : terminator).TrimStart('\\');
            local = string.Equals(computerName, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(computerName, ".", StringComparison.Ordinal);
        }

        if (!local)
        {
            throw new UnauthorizedAccessException("Remote named-pipe clients are not permitted.");
        }

        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Cannot verify the named-pipe client process.");
        }

        string? callerSid = null;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                callerSid = identity.User?.Value;
            });
        }
        catch (Exception exception) when (exception is Win32Exception or UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException(
                "Cannot impersonate the named-pipe client for SID verification.", exception);
        }

        if (string.IsNullOrWhiteSpace(callerSid))
        {
            throw new UnauthorizedAccessException("The named-pipe client has no verifiable Windows SID.");
        }

        // The PID and process name are audit labels only. Authorization is bound to the
        // impersonation token above, so PID reuse can never change the caller identity.
        var source = "unknown";
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(checked((int)processId));
            source = process.ProcessName;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or OverflowException or Win32Exception)
        {
            source = $"pid:{processId}";
        }

        return new PipePeerIdentity(callerSid, source);
    }

    internal static void WarmUpWindowsIdentityDependencies()
    {
        // The first call into a dependency from inside an impersonated callback
        // can fail assembly resolution (dotnet/runtime#91771). Exercise the same
        // WindowsIdentity.User path under the service's primary token before
        // RunAsClient changes the thread token. Merely touching ClaimsIdentity's
        // type is insufficient because the cold path has further dependencies.
        using var processIdentity = WindowsIdentity.GetCurrent();
        GC.KeepAlive(processIdentity.User?.Value);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", EntryPoint = "GetNamedPipeClientComputerNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientComputerName(
        SafePipeHandle pipe,
        [Out] char[] clientComputerName,
        uint clientComputerNameLength);

}

internal sealed record PipePeerIdentity(string CallerSid, string Source);
