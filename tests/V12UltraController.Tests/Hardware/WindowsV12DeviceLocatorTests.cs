using System.Text.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using V12UltraController.Core.Hardware;

namespace V12UltraController.Tests.Hardware;

public sealed class WindowsV12DeviceLocatorTests
{
    private const string PrivatePath =
        @"\\?\hid#vid_374a&pid_b101#SERIAL-SECRET#{00000000-0000-0000-0000-000000000000}";

    private static readonly DeviceIdentity SupportedIdentity = new(
        DeviceAllowList.V12UltraVendorId,
        DeviceAllowList.V12UltraProductId,
        DeviceAllowList.V12UltraFirmwareVersion,
        DeviceAllowList.V12UltraUsagePage,
        DeviceAllowList.V12UltraUsage,
        V12FeatureReport.ReportLength);

    [Fact]
    public async Task OpenExpected_NoV12FamilyCandidateFailsBeforeOpen()
    {
        var backend = new FakeWindowsHidBackend();
        var locator = new WindowsV12DeviceLocator(backend);

        var result = await locator.OpenExpectedAsync(SupportedIdentity, CancellationToken.None);

        Assert.Equal(V12DeviceOpenStatus.DeviceNotFound, result.Status);
        Assert.Null(result.Device);
        Assert.Equal(1, backend.EnumerateCalls);
        Assert.Equal(0, backend.OpenCalls);
        Assert.Equal(0, backend.HandleIdentityReadCalls);
    }

    [Fact]
    public async Task OpenExpected_DuplicateExpectedCandidatesFailsBeforeOpen()
    {
        var backend = new FakeWindowsHidBackend
        {
            Descriptors =
            [
                new WindowsHidInterfaceDescriptor(PrivatePath, SupportedIdentity),
                new WindowsHidInterfaceDescriptor(PrivatePath + "-second", SupportedIdentity),
            ],
        };
        var locator = new WindowsV12DeviceLocator(backend);

        var result = await locator.OpenExpectedAsync(SupportedIdentity, CancellationToken.None);

        Assert.Equal(V12DeviceOpenStatus.MultipleCandidates, result.Status);
        Assert.Null(result.Device);
        Assert.Equal(0, backend.OpenCalls);
    }

    [Fact]
    public async Task OpenExpected_UnknownFirmwareCoexistingWithExpectedFailsAsNonUniqueFamily()
    {
        var unknownFirmware = SupportedIdentity with
        {
            Version = (ushort)(SupportedIdentity.Version + 1),
        };
        var backend = new FakeWindowsHidBackend
        {
            Descriptors =
            [
                new WindowsHidInterfaceDescriptor(PrivatePath, SupportedIdentity),
                new WindowsHidInterfaceDescriptor(PrivatePath + "-unknown", unknownFirmware),
            ],
        };
        var locator = new WindowsV12DeviceLocator(backend);

        var result = await locator.OpenExpectedAsync(SupportedIdentity, CancellationToken.None);

        Assert.Equal(V12DeviceOpenStatus.MultipleCandidates, result.Status);
        Assert.Null(result.Device);
        Assert.Equal(0, backend.OpenCalls);
    }

    [Fact]
    public async Task OpenExpected_OneReadableAndOneUnresolvedPotentialTargetFailsClosed()
    {
        var backend = CreateSingleCandidateBackend();
        backend.UnresolvedPotentialTargetInterfaces = 1;
        var locator = new WindowsV12DeviceLocator(backend);

        var exception = await Assert.ThrowsAsync<IOException>(
            () => locator.OpenExpectedAsync(SupportedIdentity, CancellationToken.None));

        Assert.Equal(0, backend.OpenCalls);
        Assert.DoesNotContain(PrivatePath, exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SERIAL-SECRET", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Enumerate_OnlyUnresolvedPotentialTargetFailsClosed()
    {
        var backend = new FakeWindowsHidBackend
        {
            UnresolvedPotentialTargetInterfaces = 1,
        };
        var locator = new WindowsV12DeviceLocator(backend);

        _ = await Assert.ThrowsAsync<IOException>(
            () => locator.EnumerateAsync(CancellationToken.None));

        Assert.Equal(0, backend.OpenCalls);
    }

    [Fact]
    public async Task OpenExpected_UnresolvedKnownNonTargetDoesNotHideReadableTarget()
    {
        var backend = CreateSingleCandidateBackend();
        backend.UnresolvedNonTargetInterfaces = 1;
        var locator = new WindowsV12DeviceLocator(backend);

        var result = await locator.OpenExpectedAsync(SupportedIdentity, CancellationToken.None);

        Assert.Equal(V12DeviceOpenStatus.Opened, result.Status);
        await result.Device!.DisposeAsync();
    }

    [Fact]
    public async Task OpenExpected_DescriptorMismatchFailsBeforeOpeningItsPrivatePath()
    {
        var changedDescriptor = SupportedIdentity with
        {
            FeatureReportLength = (ushort)(SupportedIdentity.FeatureReportLength + 1),
        };
        var backend = new FakeWindowsHidBackend
        {
            Descriptors = [new WindowsHidInterfaceDescriptor(PrivatePath, changedDescriptor)],
        };
        var locator = new WindowsV12DeviceLocator(backend);

        var result = await locator.OpenExpectedAsync(SupportedIdentity, CancellationToken.None);

        Assert.Equal(V12DeviceOpenStatus.DescriptorIdentityMismatch, result.Status);
        Assert.Null(result.Device);
        Assert.Equal(0, backend.OpenCalls);
        Assert.Equal(0, backend.HandleIdentityReadCalls);
    }

    [Fact]
    public async Task OpenExpected_HandleIdentityDriftClosesHandleWithoutCreatingDevice()
    {
        var handleIdentity = SupportedIdentity with
        {
            Version = (ushort)(SupportedIdentity.Version + 1),
        };
        var backend = CreateSingleCandidateBackend();
        backend.HandleIdentity = handleIdentity;
        var locator = new WindowsV12DeviceLocator(backend);

        var result = await locator.OpenExpectedAsync(SupportedIdentity, CancellationToken.None);

        Assert.Equal(V12DeviceOpenStatus.HandleIdentityMismatch, result.Status);
        Assert.Null(result.Device);
        Assert.Equal(PrivatePath, backend.LastOpenedPath);
        Assert.Equal(1, backend.HandleIdentityReadCalls);
        Assert.True(backend.LastHandle!.IsClosed);
    }

    [Fact]
    public async Task OpenExpected_ExclusiveHandleContentionReturnsDeviceBusyWithoutIdentityRead()
    {
        var backend = CreateSingleCandidateBackend();
        backend.OpenSucceeds = false;
        var locator = new WindowsV12DeviceLocator(backend);

        var result = await locator.OpenExpectedAsync(SupportedIdentity, CancellationToken.None);

        Assert.Equal(V12DeviceOpenStatus.DeviceBusy, result.Status);
        Assert.Null(result.Device);
        Assert.Equal(1, backend.OpenCalls);
        Assert.Equal(0, backend.HandleIdentityReadCalls);
    }

    [Fact]
    public async Task OpenExpected_UnreadableOpenedHandleReturnsValidationFailureAndClosesHandle()
    {
        var backend = CreateSingleCandidateBackend();
        backend.HandleIdentity = null;
        var locator = new WindowsV12DeviceLocator(backend);

        var result = await locator.OpenExpectedAsync(SupportedIdentity, CancellationToken.None);

        Assert.Equal(V12DeviceOpenStatus.HandleValidationFailed, result.Status);
        Assert.Null(result.Device);
        Assert.Equal(1, backend.HandleIdentityReadCalls);
        Assert.True(backend.LastHandle!.IsClosed);
    }

    [Fact]
    public async Task OpenExpected_CancellationAfterEnumerationNeverOpensPrivatePath()
    {
        using var cancellation = new CancellationTokenSource();
        var backend = CreateSingleCandidateBackend();
        backend.AfterEnumerate = cancellation.Cancel;
        var locator = new WindowsV12DeviceLocator(backend);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => locator.OpenExpectedAsync(SupportedIdentity, cancellation.Token));

        Assert.Equal(0, backend.OpenCalls);
        Assert.Null(backend.LastOpenedPath);
    }

    [Fact]
    public async Task OpenExpected_CancellationAfterExclusiveOpenClosesHandleBeforeIdentityRead()
    {
        using var cancellation = new CancellationTokenSource();
        var backend = CreateSingleCandidateBackend();
        backend.AfterOpen = cancellation.Cancel;
        var locator = new WindowsV12DeviceLocator(backend);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => locator.OpenExpectedAsync(SupportedIdentity, cancellation.Token));

        Assert.Equal(1, backend.OpenCalls);
        Assert.Equal(0, backend.HandleIdentityReadCalls);
        Assert.True(backend.LastHandle!.IsClosed);
    }

    [Fact]
    public async Task OpenExpected_CancellationAfterHandleIdentityReadClosesValidatedHandle()
    {
        using var cancellation = new CancellationTokenSource();
        var backend = CreateSingleCandidateBackend();
        backend.AfterHandleIdentityRead = cancellation.Cancel;
        var locator = new WindowsV12DeviceLocator(backend);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => locator.OpenExpectedAsync(SupportedIdentity, cancellation.Token));

        Assert.Equal(1, backend.HandleIdentityReadCalls);
        Assert.True(backend.LastHandle!.IsClosed);
    }

    [Fact]
    public async Task OpenExpected_SuccessTransfersSameValidatedHandleToTransport()
    {
        var backend = CreateSingleCandidateBackend();
        var locator = new WindowsV12DeviceLocator(backend);

        var result = await locator.OpenExpectedAsync(SupportedIdentity, CancellationToken.None);

        Assert.Equal(V12DeviceOpenStatus.Opened, result.Status);
        var device = Assert.IsAssignableFrom<IV12Device>(result.Device);
        Assert.Equal(SupportedIdentity, device.Identity);
        Assert.Equal(PrivatePath, backend.LastOpenedPath);
        Assert.Equal(1, backend.OpenCalls);
        Assert.Equal(1, backend.HandleIdentityReadCalls);
        Assert.False(backend.LastHandle!.IsClosed);

        await device.DisposeAsync();
        Assert.True(backend.LastHandle.IsClosed);
    }

    [Fact]
    public async Task OpenExpected_PublicResultsDoNotExposePrivatePathOrSerial()
    {
        var backend = CreateSingleCandidateBackend();
        backend.HandleIdentity = SupportedIdentity with
        {
            Usage = (ushort)(SupportedIdentity.Usage + 1),
        };
        var locator = new WindowsV12DeviceLocator(backend);

        var result = await locator.OpenExpectedAsync(SupportedIdentity, CancellationToken.None);
        var serialized = JsonSerializer.Serialize(result);
        var rendered = result.ToString();

        Assert.Equal(V12DeviceOpenStatus.HandleIdentityMismatch, result.Status);
        Assert.DoesNotContain(PrivatePath, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SERIAL-SECRET", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PrivatePath, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SERIAL-SECRET", rendered, StringComparison.OrdinalIgnoreCase);

        var successBackend = CreateSingleCandidateBackend();
        var success = await new WindowsV12DeviceLocator(successBackend)
            .OpenExpectedAsync(SupportedIdentity, CancellationToken.None);
        var successSerialized = JsonSerializer.Serialize(success);
        var successRendered = success.ToString();
        Assert.DoesNotContain(PrivatePath, successSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SERIAL-SECRET", successSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PrivatePath, successRendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SERIAL-SECRET", successRendered, StringComparison.OrdinalIgnoreCase);
        await success.Device!.DisposeAsync();
    }

    [Theory]
    [InlineData("HidD_GetAttributes")]
    [InlineData("HidD_GetPreparsedData")]
    [InlineData("HidD_FreePreparsedData")]
    [InlineData("HidD_SetFeature")]
    [InlineData("HidD_GetFeature")]
    public void HidBooleanImports_UseOneByteBoolean(string methodName)
    {
        var method = typeof(NativeMethods).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);

        var marshalAs = Assert.IsType<MarshalAsAttribute>(
            method?.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>());
        Assert.Equal(UnmanagedType.U1, marshalAs.Value);
    }

    [Theory]
    [InlineData("SetupDiEnumDeviceInterfaces")]
    [InlineData("SetupDiGetDeviceInterfaceDetail")]
    [InlineData("SetupDiGetDeviceRegistryProperty")]
    [InlineData("SetupDiDestroyDeviceInfoList")]
    public void SetupApiBooleanImports_RemainFourByteWin32Bool(string methodName)
    {
        var method = typeof(NativeMethods).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);

        var marshalAs = Assert.IsType<MarshalAsAttribute>(
            method?.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>());
        Assert.Equal(UnmanagedType.Bool, marshalAs.Value);
    }

    [Theory]
    [InlineData("HID\\VID_374A&PID_B101&REV_1602", 2)]
    [InlineData("hid\\vid_374a&pid_b101", 2)]
    [InlineData("HID\\VID_1234&PID_5678", 1)]
    public void HardwareIdClassification_UsesExactVidPidTokens(
        string hardwareId,
        int expectedValue)
    {
        var expected = (WindowsHidHardwareIdClassification)expectedValue;
        Assert.Equal(expected, WindowsHidBackend.ClassifyHardwareIds([hardwareId]));
    }

    [Fact]
    public void HardwareIdClassification_MissingEvidenceIsUnknown()
    {
        Assert.Equal(
            WindowsHidHardwareIdClassification.Unknown,
            WindowsHidBackend.ClassifyHardwareIds(null));
    }

    private static FakeWindowsHidBackend CreateSingleCandidateBackend() => new()
    {
        Descriptors = [new WindowsHidInterfaceDescriptor(PrivatePath, SupportedIdentity)],
        HandleIdentity = SupportedIdentity,
    };

    private sealed class FakeWindowsHidBackend : IWindowsHidBackend
    {
        public IReadOnlyList<WindowsHidInterfaceDescriptor> Descriptors { get; set; } = [];
        public DeviceIdentity? HandleIdentity { get; set; }
        public bool OpenSucceeds { get; set; } = true;
        public int UnresolvedPotentialTargetInterfaces { get; set; }
        public int UnresolvedNonTargetInterfaces { get; set; }
        public Action? AfterEnumerate { get; set; }
        public Action? AfterOpen { get; set; }
        public Action? AfterHandleIdentityRead { get; set; }
        public int EnumerateCalls { get; private set; }
        public int OpenCalls { get; private set; }
        public int HandleIdentityReadCalls { get; private set; }
        public string? LastOpenedPath { get; private set; }
        public SafeFileHandle? LastHandle { get; private set; }

        public WindowsHidEnumerationSnapshot EnumerateSnapshot(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnumerateCalls++;
            AfterEnumerate?.Invoke();
            return new WindowsHidEnumerationSnapshot(
                Descriptors,
                UnresolvedPotentialTargetInterfaces,
                UnresolvedNonTargetInterfaces);
        }

        public SafeFileHandle? TryOpenExclusive(string path)
        {
            OpenCalls++;
            LastOpenedPath = path;
            if (!OpenSucceeds)
            {
                return null;
            }

            LastHandle = new SafeFileHandle(new IntPtr(0x1234), ownsHandle: false);
            AfterOpen?.Invoke();
            return LastHandle;
        }

        public DeviceIdentity? TryReadIdentity(SafeFileHandle handle)
        {
            Assert.Same(LastHandle, handle);
            HandleIdentityReadCalls++;
            AfterHandleIdentityRead?.Invoke();
            return HandleIdentity;
        }
    }
}
