using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace V12UltraController.Core.Hardware;

public sealed class WindowsV12DeviceLocator : IV12DeviceLocator
{
    private readonly IWindowsHidBackend _backend;

    public WindowsV12DeviceLocator()
        : this(new WindowsHidBackend())
    {
    }

    internal WindowsV12DeviceLocator(IWindowsHidBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public Task<IReadOnlyList<DeviceIdentity>> EnumerateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = _backend.EnumerateSnapshot(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfPotentialTargetWasUnresolved(snapshot);
        IReadOnlyList<DeviceIdentity> identities = snapshot.Descriptors
            .Select(static descriptor => descriptor.Identity)
            .ToArray();
        return Task.FromResult(identities);
    }

    public Task<V12DeviceOpenResult> OpenExpectedAsync(
        DeviceIdentity expectedIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _backend.EnumerateSnapshot(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfPotentialTargetWasUnresolved(snapshot);
        var candidates = snapshot.Descriptors
            .Where(static descriptor => IsV12InterfaceFamily(descriptor.Identity))
            .ToArray();
        if (candidates.Length == 0)
        {
            return Task.FromResult(V12DeviceOpenResult.Failed(V12DeviceOpenStatus.DeviceNotFound));
        }

        if (candidates.Length != 1)
        {
            return Task.FromResult(V12DeviceOpenResult.Failed(V12DeviceOpenStatus.MultipleCandidates));
        }

        var descriptor = candidates[0];
        if (descriptor.Identity != expectedIdentity)
        {
            return Task.FromResult(
                V12DeviceOpenResult.Failed(V12DeviceOpenStatus.DescriptorIdentityMismatch));
        }

        if (!DeviceAllowList.IsSupported(expectedIdentity))
        {
            return Task.FromResult(
                V12DeviceOpenResult.Failed(V12DeviceOpenStatus.ExpectedIdentityUnsupported));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var handle = _backend.TryOpenExclusive(descriptor.Path);
        if (handle is null)
        {
            return Task.FromResult(V12DeviceOpenResult.Failed(V12DeviceOpenStatus.DeviceBusy));
        }

        var ownershipTransferred = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handleIdentity = _backend.TryReadIdentity(handle);
            cancellationToken.ThrowIfCancellationRequested();
            if (handleIdentity is null)
            {
                return Task.FromResult(
                    V12DeviceOpenResult.Failed(V12DeviceOpenStatus.HandleValidationFailed));
            }

            if (handleIdentity != descriptor.Identity ||
                handleIdentity != expectedIdentity ||
                !DeviceAllowList.IsSupported(handleIdentity))
            {
                return Task.FromResult(
                    V12DeviceOpenResult.Failed(V12DeviceOpenStatus.HandleIdentityMismatch));
            }

            cancellationToken.ThrowIfCancellationRequested();
            IV12Device device = new V12Device(
                new WindowsHidFeatureTransport(
                    handle,
                    handleIdentity,
                    handleIdentity.FeatureReportLength));
            ownershipTransferred = true;
            return Task.FromResult(V12DeviceOpenResult.Opened(device));
        }
        finally
        {
            if (!ownershipTransferred)
            {
                handle.Dispose();
            }
        }
    }

    private static bool IsV12InterfaceFamily(DeviceIdentity identity) =>
        identity.VendorId == DeviceAllowList.V12UltraVendorId &&
        identity.ProductId == DeviceAllowList.V12UltraProductId &&
        identity.UsagePage == DeviceAllowList.V12UltraUsagePage &&
        identity.Usage == DeviceAllowList.V12UltraUsage;

    private static void ThrowIfPotentialTargetWasUnresolved(WindowsHidEnumerationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.UnresolvedPotentialTargetInterfaces > 0)
        {
            throw new IOException(
                "Windows HID discovery could not validate every potential V12 Ultra interface; " +
                "device selection was refused.");
        }
    }
}

internal sealed record WindowsHidInterfaceDescriptor(string Path, DeviceIdentity Identity);

internal sealed record WindowsHidEnumerationSnapshot(
    IReadOnlyList<WindowsHidInterfaceDescriptor> Descriptors,
    int UnresolvedPotentialTargetInterfaces,
    int UnresolvedNonTargetInterfaces)
{
    public WindowsHidEnumerationSnapshot(
        IReadOnlyList<WindowsHidInterfaceDescriptor> descriptors)
        : this(descriptors, 0, 0)
    {
    }
}

internal enum WindowsHidHardwareIdClassification
{
    Unknown,
    NonTarget,
    PotentialTarget,
}

internal interface IWindowsHidBackend
{
    WindowsHidEnumerationSnapshot EnumerateSnapshot(
        CancellationToken cancellationToken);

    SafeFileHandle? TryOpenExclusive(string path);

    DeviceIdentity? TryReadIdentity(SafeFileHandle handle);
}

internal sealed class WindowsHidBackend : IWindowsHidBackend
{
    public WindowsHidEnumerationSnapshot EnumerateSnapshot(
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        NativeMethods.HidD_GetHidGuid(out var hidGuid);
        var deviceInfoSet = NativeMethods.SetupDiGetClassDevs(
            ref hidGuid,
            null,
            IntPtr.Zero,
            NativeMethods.DigcfPresent | NativeMethods.DigcfDeviceInterface);

        if (deviceInfoSet == NativeMethods.InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to enumerate Windows HID devices.");
        }

        var descriptors = new List<WindowsHidInterfaceDescriptor>();
        var unresolvedPotentialTargetInterfaces = 0;
        var unresolvedNonTargetInterfaces = 0;
        try
        {
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var interfaceData = new NativeMethods.SpDeviceInterfaceData
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.SpDeviceInterfaceData>(),
                };

                if (!NativeMethods.SetupDiEnumDeviceInterfaces(
                        deviceInfoSet,
                        IntPtr.Zero,
                        ref hidGuid,
                        index,
                        ref interfaceData))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == NativeMethods.ErrorNoMoreItems)
                    {
                        break;
                    }

                    throw new Win32Exception(error, "Unable to enumerate a Windows HID interface.");
                }

                var deviceInfoData = new NativeMethods.SpDevInfoData
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.SpDevInfoData>(),
                };
                _ = NativeMethods.SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet,
                    ref interfaceData,
                    IntPtr.Zero,
                    0,
                    out var requiredSize,
                    ref deviceInfoData);

                var sizeError = Marshal.GetLastWin32Error();
                var hardwareIdClassification = sizeError == NativeMethods.ErrorInsufficientBuffer
                    ? ClassifyHardwareIds(TryGetHardwareIds(deviceInfoSet, ref deviceInfoData))
                    : WindowsHidHardwareIdClassification.Unknown;
                if (requiredSize == 0 || sizeError != NativeMethods.ErrorInsufficientBuffer)
                {
                    CountUnresolved(
                        hardwareIdClassification,
                        ref unresolvedPotentialTargetInterfaces,
                        ref unresolvedNonTargetInterfaces);
                    continue;
                }

                var detailBuffer = Marshal.AllocHGlobal(checked((int)requiredSize));
                try
                {
                    Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(
                            deviceInfoSet,
                            ref interfaceData,
                            detailBuffer,
                            requiredSize,
                            out _,
                            ref deviceInfoData))
                    {
                        CountUnresolved(
                            hardwareIdClassification,
                            ref unresolvedPotentialTargetInterfaces,
                            ref unresolvedNonTargetInterfaces);
                        continue;
                    }

                    var path = Marshal.PtrToStringUni(IntPtr.Add(detailBuffer, sizeof(uint)));
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        CountUnresolved(
                            hardwareIdClassification,
                            ref unresolvedPotentialTargetInterfaces,
                            ref unresolvedNonTargetInterfaces);
                        continue;
                    }

                    using var handle = TryOpenShared(path);
                    if (handle is not null && TryReadIdentity(handle) is { } identity)
                    {
                        descriptors.Add(new WindowsHidInterfaceDescriptor(path, identity));
                    }
                    else
                    {
                        CountUnresolved(
                            hardwareIdClassification,
                            ref unresolvedPotentialTargetInterfaces,
                            ref unresolvedNonTargetInterfaces);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuffer);
                }
            }
        }
        finally
        {
            _ = NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return new WindowsHidEnumerationSnapshot(
            descriptors,
            unresolvedPotentialTargetInterfaces,
            unresolvedNonTargetInterfaces);
    }

    internal static WindowsHidHardwareIdClassification ClassifyHardwareIds(
        IReadOnlyList<string>? hardwareIds)
    {
        if (hardwareIds is null || hardwareIds.Count == 0)
        {
            return WindowsHidHardwareIdClassification.Unknown;
        }

        foreach (var hardwareId in hardwareIds)
        {
            var components = hardwareId.Split(
                ['\\', '&'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var hasVendor = components.Contains(
                $"VID_{DeviceAllowList.V12UltraVendorId:X4}",
                StringComparer.OrdinalIgnoreCase);
            var hasProduct = components.Contains(
                $"PID_{DeviceAllowList.V12UltraProductId:X4}",
                StringComparer.OrdinalIgnoreCase);
            if (hasVendor && hasProduct)
            {
                return WindowsHidHardwareIdClassification.PotentialTarget;
            }
        }

        return WindowsHidHardwareIdClassification.NonTarget;
    }

    private static IReadOnlyList<string>? TryGetHardwareIds(
        IntPtr deviceInfoSet,
        ref NativeMethods.SpDevInfoData deviceInfoData)
    {
        _ = NativeMethods.SetupDiGetDeviceRegistryProperty(
            deviceInfoSet,
            ref deviceInfoData,
            NativeMethods.SpdrpHardwareId,
            out var registryDataType,
            IntPtr.Zero,
            0,
            out var requiredSize);
        var sizeError = Marshal.GetLastWin32Error();
        if (requiredSize == 0 ||
            requiredSize > int.MaxValue ||
            requiredSize % sizeof(char) != 0 ||
            sizeError != NativeMethods.ErrorInsufficientBuffer)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(checked((int)requiredSize));
        try
        {
            if (!NativeMethods.SetupDiGetDeviceRegistryProperty(
                    deviceInfoSet,
                    ref deviceInfoData,
                    NativeMethods.SpdrpHardwareId,
                    out registryDataType,
                    buffer,
                    requiredSize,
                    out _)
                || registryDataType != NativeMethods.RegMultiSz)
            {
                return null;
            }

            var value = Marshal.PtrToStringUni(buffer, checked((int)requiredSize / sizeof(char)));
            return value?
                .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void CountUnresolved(
        WindowsHidHardwareIdClassification classification,
        ref int unresolvedPotentialTargetInterfaces,
        ref int unresolvedNonTargetInterfaces)
    {
        if (classification == WindowsHidHardwareIdClassification.NonTarget)
        {
            unresolvedNonTargetInterfaces++;
        }
        else
        {
            // Missing hardware IDs cannot prove that an unreadable interface is unrelated.
            // Treat it as a potential target so uniqueness remains fail closed.
            unresolvedPotentialTargetInterfaces++;
        }
    }

    public SafeFileHandle? TryOpenExclusive(string path)
    {
        EnsureWindows();
        var handle = NativeMethods.CreateFile(
            path,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            0,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            0,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        handle.Dispose();
        return null;
    }

    public DeviceIdentity? TryReadIdentity(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        EnsureWindows();
        if (handle.IsInvalid || handle.IsClosed)
        {
            return null;
        }

        var attributes = new NativeMethods.HiddAttributes
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.HiddAttributes>(),
        };
        if (!NativeMethods.HidD_GetAttributes(handle, ref attributes))
        {
            return null;
        }

        if (!NativeMethods.HidD_GetPreparsedData(handle, out var preparsedData))
        {
            return null;
        }

        try
        {
            var status = NativeMethods.HidP_GetCaps(preparsedData, out var caps);
            if (status < 0)
            {
                return null;
            }

            return new DeviceIdentity(
                attributes.VendorId,
                attributes.ProductId,
                attributes.VersionNumber,
                caps.UsagePage,
                caps.Usage,
                caps.FeatureReportByteLength);
        }
        finally
        {
            _ = NativeMethods.HidD_FreePreparsedData(preparsedData);
        }
    }

    private static SafeFileHandle? TryOpenShared(string path)
    {
        var handle = NativeMethods.CreateFile(
            path,
            0,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("V12 Ultra HID discovery is supported only on Windows.");
        }
    }
}

internal sealed class WindowsHidFeatureTransport : IHidFeatureTransport
{
    private readonly SafeFileHandle _handle;
    private bool _disposed;

    public WindowsHidFeatureTransport(SafeFileHandle handle, DeviceIdentity identity, int featureReportLength)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        FeatureReportLength = featureReportLength;
    }

    public DeviceIdentity Identity { get; }

    public int FeatureReportLength { get; }

    public ValueTask SetFeatureAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (report.Length != FeatureReportLength)
        {
            throw new ArgumentException($"Feature reports must be {FeatureReportLength} bytes.", nameof(report));
        }

        var buffer = report.ToArray();
        if (!NativeMethods.HidD_SetFeature(_handle, buffer, buffer.Length))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The V12 Ultra rejected a feature report.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]> GetFeatureAsync(byte reportId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var buffer = new byte[FeatureReportLength];
        buffer[0] = reportId;
        if (!NativeMethods.HidD_GetFeature(_handle, buffer, buffer.Length))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read the V12 Ultra feature report.");
        }

        return ValueTask.FromResult(buffer);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _handle.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}

internal static partial class NativeMethods
{
    internal static readonly IntPtr InvalidHandleValue = new(-1);

    internal const uint DigcfPresent = 0x00000002;
    internal const uint DigcfDeviceInterface = 0x00000010;
    internal const int ErrorNoMoreItems = 259;
    internal const int ErrorInsufficientBuffer = 122;
    internal const uint SpdrpHardwareId = 0x00000001;
    internal const uint RegMultiSz = 7;
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDeviceInterfaceData
    {
        internal uint Size;
        internal Guid InterfaceClassGuid;
        internal uint Flags;
        internal UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDevInfoData
    {
        internal uint Size;
        internal Guid ClassGuid;
        internal uint DeviceInstance;
        internal UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HiddAttributes
    {
        internal uint Size;
        internal ushort VendorId;
        internal ushort ProductId;
        internal ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HidpCaps
    {
        internal ushort Usage;
        internal ushort UsagePage;
        internal ushort InputReportByteLength;
        internal ushort OutputReportByteLength;
        internal ushort FeatureReportByteLength;

        internal ushort Reserved00;
        internal ushort Reserved01;
        internal ushort Reserved02;
        internal ushort Reserved03;
        internal ushort Reserved04;
        internal ushort Reserved05;
        internal ushort Reserved06;
        internal ushort Reserved07;
        internal ushort Reserved08;
        internal ushort Reserved09;
        internal ushort Reserved10;
        internal ushort Reserved11;
        internal ushort Reserved12;
        internal ushort Reserved13;
        internal ushort Reserved14;
        internal ushort Reserved15;
        internal ushort Reserved16;

        internal ushort NumberLinkCollectionNodes;
        internal ushort NumberInputButtonCaps;
        internal ushort NumberInputValueCaps;
        internal ushort NumberInputDataIndices;
        internal ushort NumberOutputButtonCaps;
        internal ushort NumberOutputValueCaps;
        internal ushort NumberOutputDataIndices;
        internal ushort NumberFeatureButtonCaps;
        internal ushort NumberFeatureValueCaps;
        internal ushort NumberFeatureDataIndices;
    }

    [LibraryImport("hid.dll")]
    internal static partial void HidD_GetHidGuid(out Guid hidGuid);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        IntPtr parentWindow,
        uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        ref SpDevInfoData deviceInfoData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint property,
        out uint propertyRegistryDataType,
        IntPtr propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attributes);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool HidD_FreePreparsedData(IntPtr preparsedData);

    [LibraryImport("hid.dll")]
    internal static partial int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool HidD_SetFeature(
        SafeFileHandle device,
        [In] byte[] reportBuffer,
        int reportBufferLength);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool HidD_GetFeature(
        SafeFileHandle device,
        [Out] byte[] reportBuffer,
        int reportBufferLength);
}
