using V12UltraController.Core.Hardware;

namespace V12UltraController.Tests.Hardware;

public sealed class V12DeviceTests
{
    [Fact]
    public async Task LightingRequiresReadAndSubsequentFanWritePreservesNewLights()
    {
        var transport = new FakeHidFeatureTransport(SupportedIdentity,
            V12FeatureReportTests.CreateStateReport(0x80, 40, 0, 0x83, 4, 2, 177));
        await using var device = new V12Device(transport, (_, _) => Task.CompletedTask);
        var settings = new LightingSettings(true, 1, 2, 220, 3);
        await Assert.ThrowsAsync<InvalidOperationException>(() => device.WriteLightingAsync(settings, CancellationToken.None));
        Assert.Empty(transport.SetReports);
        await device.ReadStateAsync(CancellationToken.None);
        await device.WriteLightingAsync(settings, CancellationToken.None);
        Assert.Equal(settings, LightingSettings.FromState(V12FeatureReport.ParseState(transport.SetReports[1])));
        await device.WriteFanSpeedAsync(80, CancellationToken.None);
        Assert.Equal(settings, LightingSettings.FromState(V12FeatureReport.ParseState(transport.SetReports[2])));
    }
    [Fact]
    public void AllowList_RejectsUnexpectedFeatureReportLength()
    {
        var identity = SupportedIdentity with { FeatureReportLength = 8 };

        Assert.False(identity.IsSupportedV12Ultra);
    }

    private static readonly DeviceIdentity SupportedIdentity = new(0x374A, 0xB101, 0x1602, 0xFF00, 1);

    [Theory]
    [InlineData(0x80)]
    [InlineData(0x88)]
    [InlineData(0xFF)]
    public async Task ReadThenWrite_UsesQueryAndPreservesUnmanagedLightingFields(byte modeStatus)
    {
        var transport = new FakeHidFeatureTransport(
            SupportedIdentity,
            V12FeatureReportTests.CreateStateReport(modeStatus, 40, 0, 0x83, 4, 2, 177));
        await using var device = new V12Device(transport);

        var state = await device.ReadStateAsync(TestContext.Current.CancellationToken);
        await device.WriteFanSpeedAsync(70, TestContext.Current.CancellationToken);

        Assert.Equal(modeStatus, state.RawModeStatus);
        Assert.Equal(40, state.FanSpeedPercent);
        Assert.Equal(2, transport.SetReports.Count);
        Assert.Equal(V12FeatureReport.CreateReadQuery(), transport.SetReports[0]);
        Assert.Equal(new byte[] { 0x83, 4, 2, 177 }, transport.SetReports[1][4..8]);
        Assert.Equal(70, transport.SetReports[1][2]);
        Assert.Equal(0, transport.SetReports[1][1]);
        Assert.Equal(0, transport.SetReports[1][3]);
        Assert.True(V12FeatureReport.HasValidChecksum(transport.SetReports[1]));
    }


    [Fact]
    public async Task WriteBeforeVerifiedRead_IsRejectedWithoutTouchingTransport()
    {
        var transport = new FakeHidFeatureTransport(
            SupportedIdentity,
            V12FeatureReportTests.CreateStateReport(0, 40, 0, 0, 0, 0, 20));
        await using var device = new V12Device(transport);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => device.WriteFanSpeedAsync(50, TestContext.Current.CancellationToken));

        Assert.Empty(transport.SetReports);
    }

    [Theory]
    [InlineData(0x80)]
    [InlineData(0x88)]
    public async Task TrustedCacheInitializer_ArmsOneSafetyWriteAndPreservesLighting(byte modeStatus)
    {
        var transport = new FakeHidFeatureTransport(
            SupportedIdentity,
            V12FeatureReportTests.CreateStateReport(0, 40, 0, 0, 0, 0, 20));
        await using var device = new V12Device(transport);
        var cached = V12FeatureReport.ParseState(
            V12FeatureReportTests.CreateStateReport(modeStatus, 55, 0, 0x83, 4, 2, 177));

        await device.InitializeTrustedStateAsync(cached, TestContext.Current.CancellationToken);
        await device.WriteFanSpeedAsync(100, TestContext.Current.CancellationToken);

        var report = Assert.Single(transport.SetReports);
        Assert.Equal(0, report[1]);
        Assert.Equal(100, report[2]);
        Assert.Equal(0, report[3]);
        Assert.Equal(new byte[] { 0x83, 4, 2, 177 }, report[4..8]);
        Assert.True(V12FeatureReport.HasValidChecksum(report));
    }

    [Fact]
    public async Task TrustedCacheInitializer_IsOneTimeOnly()
    {
        var transport = new FakeHidFeatureTransport(
            SupportedIdentity,
            V12FeatureReportTests.CreateStateReport(0, 40, 0, 0, 0, 0, 20));
        await using var device = new V12Device(transport);
        var cached = V12FeatureReport.ParseState(
            V12FeatureReportTests.CreateStateReport(0, 55, 0, 0x83, 4, 2, 177));

        await device.InitializeTrustedStateAsync(cached, TestContext.Current.CancellationToken);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => device.InitializeTrustedStateAsync(cached, TestContext.Current.CancellationToken));
        Assert.Empty(transport.SetReports);
    }

    [Fact]
    public async Task SystemShutdownOffBeforeVerifiedRead_IsRejectedWithoutTouchingTransport()
    {
        var transport = new FakeHidFeatureTransport(
            SupportedIdentity,
            V12FeatureReportTests.CreateStateReport(0, 40, 0, 0, 0, 0, 20));
        await using var device = new V12Device(transport);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => device.WriteSystemShutdownOffAsync(TestContext.Current.CancellationToken));

        Assert.Empty(transport.SetReports);
    }

    [Fact]
    public async Task ReadThenSystemShutdownOff_UsesOnlyNarrowOffVectorAndPreservesLighting()
    {
        var transport = new FakeHidFeatureTransport(
            SupportedIdentity,
            V12FeatureReportTests.CreateStateReport(0x80, 40, 0, 0x83, 4, 2, 177));
        await using var device = new V12Device(transport);

        _ = await device.ReadStateAsync(TestContext.Current.CancellationToken);
        await device.WriteSystemShutdownOffAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, transport.SetReports.Count);
        Assert.Equal(V12FeatureReport.CreateReadQuery(), transport.SetReports[0]);
        Assert.Equal(0, transport.SetReports[1][1]);
        Assert.Equal(100, transport.SetReports[1][2]);
        Assert.Equal(1, transport.SetReports[1][3]);
        Assert.Equal(new byte[] { 0x83, 4, 2, 177 }, transport.SetReports[1][4..8]);
        Assert.True(V12FeatureReport.HasValidChecksum(transport.SetReports[1]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x88)]
    public async Task BadRead_DoesNotArmSubsequentWrite(byte modeStatus)
    {
        var badReport = V12FeatureReportTests.CreateStateReport(modeStatus, 40, 0, 0, 0, 0, 20);
        badReport[^1]++;
        var transport = new FakeHidFeatureTransport(SupportedIdentity, badReport);
        await using var device = new V12Device(transport);

        _ = await Assert.ThrowsAsync<V12ProtocolException>(
            () => device.ReadStateAsync(TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => device.WriteFanSpeedAsync(50, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ShortFeatureRead_IsRejectedThroughTransportAndDoesNotArmWrite()
    {
        var transport = new FakeHidFeatureTransport(SupportedIdentity, new byte[8]);
        await using var device = new V12Device(transport);

        _ = await Assert.ThrowsAsync<V12ProtocolException>(
            () => device.ReadStateAsync(TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => device.WriteFanSpeedAsync(100, TestContext.Current.CancellationToken));

        Assert.Single(transport.SetReports);
        Assert.Equal(V12FeatureReport.CreateReadQuery(), transport.SetReports[0]);
    }

    [Fact]
    public void Constructor_RejectsUnknownIdentityAndUnexpectedReportLength()
    {
        var response = V12FeatureReportTests.CreateStateReport(0, 40, 0, 0, 0, 0, 20);
        var unknown = new FakeHidFeatureTransport(SupportedIdentity with { Version = 0x1603 }, response);
        _ = Assert.Throws<NotSupportedException>(() => new V12Device(unknown));

        var wrongLength = new FakeHidFeatureTransport(SupportedIdentity, response, featureReportLength: 8);
        _ = Assert.Throws<NotSupportedException>(() => new V12Device(wrongLength));
    }

    [Fact]
    public async Task ConcurrentWrites_AreSerialized()
    {
        var transport = new FakeHidFeatureTransport(
            SupportedIdentity,
            V12FeatureReportTests.CreateStateReport(0, 40, 0, 0, 0, 0, 20),
            operationDelay: TimeSpan.FromMilliseconds(15));
        await using var device = new V12Device(transport);
        _ = await device.ReadStateAsync(TestContext.Current.CancellationToken);

        await Task.WhenAll(
            device.WriteFanSpeedAsync(50, TestContext.Current.CancellationToken),
            device.WriteFanSpeedAsync(60, TestContext.Current.CancellationToken),
            device.WriteFanSpeedAsync(70, TestContext.Current.CancellationToken));

        Assert.Equal(1, transport.MaximumConcurrency);
    }

    [Theory]
    [InlineData(0x374A, 0xB101, 0x1602, 0xFF00, 0x0001, true)]
    [InlineData(0x374B, 0xB101, 0x1602, 0xFF00, 0x0001, false)]
    [InlineData(0x374A, 0xB102, 0x1602, 0xFF00, 0x0001, false)]
    [InlineData(0x374A, 0xB101, 0x1603, 0xFF00, 0x0001, false)]
    [InlineData(0x374A, 0xB101, 0x1602, 0x0001, 0x0001, false)]
    [InlineData(0x374A, 0xB101, 0x1602, 0xFF00, 0x0002, false)]
    public void AllowList_RequiresExactIdentity(
        ushort vendor,
        ushort product,
        ushort version,
        ushort usagePage,
        ushort usage,
        bool expected)
    {
        var identity = new DeviceIdentity(vendor, product, version, usagePage, usage);

        Assert.Equal(expected, DeviceAllowList.IsSupported(identity));
        Assert.Equal(expected, identity.IsSupportedV12Ultra);
    }

    private sealed class FakeHidFeatureTransport : IHidFeatureTransport
    {
        private readonly byte[] _response;
        private readonly TimeSpan _operationDelay;
        private int _concurrency;
        private int _maximumConcurrency;

        public FakeHidFeatureTransport(
            DeviceIdentity identity,
            byte[] response,
            int featureReportLength = V12FeatureReport.ReportLength,
            TimeSpan operationDelay = default)
        {
            Identity = identity;
            _response = response;
            FeatureReportLength = featureReportLength;
            _operationDelay = operationDelay;
        }

        public DeviceIdentity Identity { get; }

        public int FeatureReportLength { get; }

        public List<byte[]> SetReports { get; } = [];

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public async ValueTask SetFeatureAsync(
            ReadOnlyMemory<byte> report,
            CancellationToken cancellationToken)
        {
            await EnterAsync(cancellationToken);
            try
            {
                SetReports.Add(report.ToArray());
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }

        public async ValueTask<byte[]> GetFeatureAsync(byte reportId, CancellationToken cancellationToken)
        {
            await EnterAsync(cancellationToken);
            try
            {
                Assert.Equal(V12FeatureReport.ReportId, reportId);
                return (byte[])_response.Clone();
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async Task EnterAsync(CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _concurrency);
            SetMaximum(current);
            if (_operationDelay > TimeSpan.Zero)
            {
                await Task.Delay(_operationDelay, cancellationToken);
            }
        }

        private void SetMaximum(int current)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _maximumConcurrency);
                if (current <= observed
                    || Interlocked.CompareExchange(ref _maximumConcurrency, current, observed) == observed)
                {
                    return;
                }
            }
        }
    }
}
