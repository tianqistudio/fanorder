using System.Text.Json;
using V12UltraController.Core.Hardware;
using V12UltraController.Service;
using V12UltraController.Tests.Hardware;

namespace V12UltraController.Tests.Service;

public sealed class DeviceStateCacheStoreTests
{
    private static readonly DeviceIdentity SupportedIdentity = new(
        DeviceAllowList.V12UltraVendorId,
        DeviceAllowList.V12UltraProductId,
        DeviceAllowList.V12UltraFirmwareVersion,
        DeviceAllowList.V12UltraUsagePage,
        DeviceAllowList.V12UltraUsage);

    [Fact]
    public async Task SaveAndLoad_RoundTripsExactStateWithoutPathOrSerial()
    {
        await using var fixture = new CacheFixture();
        var state = CreateState(0x80, 63, 0, 0x83, 4, 2, 177);
        var capturedAt = new DateTimeOffset(2026, 9, 5, 1, 2, 3, TimeSpan.Zero);

        await fixture.Store.SaveAsync(SupportedIdentity, state, capturedAt);
        var loaded = await fixture.Store.LoadAsync(SupportedIdentity);

        Assert.NotNull(loaded);
        Assert.Equal(state.ExportFeatureReport(), loaded.ExportFeatureReport());
        var json = await File.ReadAllTextAsync(fixture.Paths.DeviceStateCachePath);
        Assert.DoesNotContain("serial", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("devicePath", json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.DataDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Load_RejectsIdentityMismatchWithoutReturningState()
    {
        await using var fixture = new CacheFixture();
        await fixture.Store.SaveAsync(
            SupportedIdentity,
            CreateState(0, 50, 0, 0x83, 0, 2, 128),
            DateTimeOffset.UtcNow);

        var loaded = await fixture.Store.LoadAsync(SupportedIdentity with { Version = 0x1603 });

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Load_RejectsTamperedReportEvenWhenJsonIsWellFormed()
    {
        await using var fixture = new CacheFixture();
        await fixture.Store.SaveAsync(
            SupportedIdentity,
            CreateState(0, 50, 0, 0x83, 0, 2, 128),
            DateTimeOffset.UtcNow);
        var json = await File.ReadAllTextAsync(fixture.Paths.DeviceStateCachePath);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tampered = new
        {
            schemaVersion = root.GetProperty("schemaVersion").GetInt32(),
            identity = root.GetProperty("identity"),
            featureReport = Convert.ToBase64String(
                V12FeatureReportTests.CreateStateReport(0, 51, 0, 0x83, 0, 2, 128)),
            capturedAtUtc = root.GetProperty("capturedAtUtc").GetDateTimeOffset(),
            sha256 = root.GetProperty("sha256").GetString(),
        };
        await File.WriteAllTextAsync(
            fixture.Paths.DeviceStateCachePath,
            JsonSerializer.Serialize(tampered, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        _ = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.LoadAsync(SupportedIdentity));
    }

    [Fact]
    public async Task Load_RejectsMalformedJson()
    {
        await using var fixture = new CacheFixture();
        await File.WriteAllTextAsync(fixture.Paths.DeviceStateCachePath, "{not-json");

        _ = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.LoadAsync(SupportedIdentity));
    }

    [Fact]
    public async Task Load_ExistingUnreadablePathIsNotTreatedAsMissing()
    {
        await using var fixture = new CacheFixture();
        Directory.CreateDirectory(fixture.Paths.DeviceStateCachePath);

        var exception = await Record.ExceptionAsync(
            () => fixture.Store.LoadAsync(SupportedIdentity));

        Assert.NotNull(exception);
        Assert.False(exception is FileNotFoundException);
        Assert.False(exception is DirectoryNotFoundException);
    }

    [Fact]
    public async Task Load_RejectsUnknownDuplicateAndWrongCaseProperties()
    {
        await using var fixture = new CacheFixture();
        await fixture.Store.SaveAsync(
            SupportedIdentity,
            CreateState(0, 50, 0, 0x83, 0, 2, 128),
            DateTimeOffset.UtcNow);
        var canonical = await File.ReadAllTextAsync(fixture.Paths.DeviceStateCachePath);
        var documents = new[]
        {
            canonical.TrimEnd()[..^1] + ",\"unknown\":true}",
            "{\"schemaVersion\":1," + canonical.TrimStart()[1..],
            canonical.Replace("\"featureReport\"", "\"FeatureReport\"", StringComparison.Ordinal),
            canonical.Replace(
                "\"vendorId\"",
                "\"vendorId\":14154,\"vendorId\"",
                StringComparison.Ordinal),
        };

        foreach (var document in documents)
        {
            await File.WriteAllTextAsync(fixture.Paths.DeviceStateCachePath, document);
            _ = await Assert.ThrowsAsync<InvalidDataException>(
                () => fixture.Store.LoadAsync(SupportedIdentity));
        }
    }

    [Fact]
    public async Task Load_RejectsOversizedExistingDocument()
    {
        await using var fixture = new CacheFixture();
        await File.WriteAllTextAsync(
            fixture.Paths.DeviceStateCachePath,
            new string(' ', 16 * 1024 + 1));

        _ = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.LoadAsync(SupportedIdentity));
    }

    private static V12DeviceState CreateState(
        byte mode,
        byte speed,
        byte power,
        byte lightModeAndPower,
        byte lightColor,
        byte lightSpeed,
        byte lightPower) =>
        V12FeatureReport.ParseState(
            V12FeatureReportTests.CreateStateReport(
                mode, speed, power, lightModeAndPower, lightColor, lightSpeed, lightPower));

    private sealed class CacheFixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(), $"v12-state-cache-tests-{Guid.NewGuid():N}");

        public CacheFixture()
        {
            Paths = new ServicePaths(directory);
            Paths.EnsureDirectories();
            Store = new DeviceStateCacheStore(Paths);
        }

        public ServicePaths Paths { get; }
        public DeviceStateCacheStore Store { get; }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
