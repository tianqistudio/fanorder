using System.ComponentModel;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed class MythCoolConflictDetectorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Inspect_ProcessInspectionFailureFailsClosedWithoutSensitiveDetails(int failureKind)
    {
        Exception failure = failureKind switch
        {
            0 => new Win32Exception(5, @"secret C:\Users\example\process.exe"),
            1 => new NotSupportedException("secret platform detail"),
            2 => new InvalidOperationException("secret process detail"),
            3 => new UnauthorizedAccessException("secret account detail"),
            4 => new IOException("secret transport detail"),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind)),
        };
        var source = new DelegateProcessPresenceSource(_ => throw failure);
        var detector = new MythCoolConflictDetector(source);

        var snapshot = detector.Inspect();

        Assert.True(snapshot.HasConflict);
        Assert.True(snapshot.InspectionFailed);
        Assert.Empty(snapshot.Processes);
        Assert.Single(source.RequestedNames);
        Assert.Equal("MythCool", source.RequestedNames[0]);
    }

    [Fact]
    public void Inspect_ClearSnapshotChecksBothFixedProcessNames()
    {
        var source = new DelegateProcessPresenceSource(_ => false);
        var detector = new MythCoolConflictDetector(source);

        var snapshot = detector.Inspect();

        Assert.False(snapshot.HasConflict);
        Assert.False(snapshot.InspectionFailed);
        Assert.Empty(snapshot.Processes);
        Assert.Equal(["MythCool", "GPP_USB_Center"], source.RequestedNames);
    }

    [Fact]
    public void Inspect_ConfirmedConflictReturnsOnlyFixedNonSensitiveDisplayName()
    {
        var source = new DelegateProcessPresenceSource(
            processName => string.Equals(processName, "MythCool", StringComparison.Ordinal));
        var detector = new MythCoolConflictDetector(source);

        var snapshot = detector.Inspect();

        Assert.True(snapshot.HasConflict);
        Assert.False(snapshot.InspectionFailed);
        Assert.Equal(["MythCool.exe"], snapshot.Processes);
        Assert.Equal(["MythCool", "GPP_USB_Center"], source.RequestedNames);
    }

    private sealed class DelegateProcessPresenceSource(Func<string, bool> inspect) : IProcessPresenceSource
    {
        public List<string> RequestedNames { get; } = [];

        public bool HasProcessNamed(string processName)
        {
            RequestedNames.Add(processName);
            return inspect(processName);
        }
    }
}
