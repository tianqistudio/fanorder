using System.Reflection;
using System.Text.Json;
using V12UltraController.Core.Security;

namespace V12UltraController.Tests.Security;

public sealed class ForcedUninstallRecoveryMarkerStoreTests
{
    private static readonly ForcedUninstallRecoveryMarker Marker = new(
        ForcedUninstallRecoveryMarker.CurrentSchemaVersion,
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        ForcedUninstallRecoveryMarker.ForcedUninstallReason,
        new DateTimeOffset(2026, 9, 6, 1, 2, 3, TimeSpan.Zero));

    [Fact]
    public void ProductionStore_ExposesOnlyParameterlessConstructionAndOneFixedPath()
    {
        var constructors = typeof(ForcedUninstallRecoveryMarkerStore)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public);

        var constructor = Assert.Single(constructors);
        Assert.Empty(constructor.GetParameters());
        Assert.Equal(
            @"C:\ProgramData\TianqiStudio\V12UltraController\forced-uninstall-recovery.json",
            ForcedUninstallRecoveryMarkerStore.FixedPath);
    }

    [Fact]
    public async Task Create_LoadAndExactDelete_RoundTripStrictMarker()
    {
        using var fixture = new MarkerStoreFixture();

        var created = await fixture.Store.CreateAsync(Marker, TestContext.Current.CancellationToken);
        var loaded = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Marker, created);
        Assert.Equal(Marker, loaded);
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                   fixture.Path,
                   TestContext.Current.CancellationToken)))
        {
            Assert.Equal(
                ["schemaVersion", "markerId", "reason", "createdAtUtc"],
                document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.Equal(
                "2026-09-06T01:02:03.0000000+00:00",
                document.RootElement.GetProperty("createdAtUtc").GetString());
        }

        await fixture.Store.DeleteExactAsync(Marker, TestContext.Current.CancellationToken);

        Assert.Null(await fixture.Store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task Create_IsIdempotentOnlyForTheExactSameMarker()
    {
        using var fixture = new MarkerStoreFixture();
        await fixture.Store.CreateAsync(Marker, TestContext.Current.CancellationToken);

        Assert.Equal(
            Marker,
            await fixture.Store.CreateAsync(Marker, TestContext.Current.CancellationToken));

        var different = Marker with { MarkerId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee") };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Store.CreateAsync(different, TestContext.Current.CancellationToken));
        Assert.Equal(
            Marker,
            await fixture.Store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Create_RefusesToOverwriteACorruptExistingMarker()
    {
        using var fixture = new MarkerStoreFixture();
        const string corrupt = "{\"schemaVersion\":1,\"reason\":\"tampered\"}";
        await File.WriteAllTextAsync(
            fixture.Path,
            corrupt,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Store.CreateAsync(Marker, TestContext.Current.CancellationToken));

        Assert.Equal(corrupt, await File.ReadAllTextAsync(
            fixture.Path,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Load_DirectoryAtMarkerPathIsUnreadableNotMissing()
    {
        using var fixture = new MarkerStoreFixture();
        Directory.CreateDirectory(fixture.Path);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Create_DirectoryAtMarkerPathFailsClosedAndPreservesIt()
    {
        using var fixture = new MarkerStoreFixture();
        Directory.CreateDirectory(fixture.Path);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Store.CreateAsync(Marker, TestContext.Current.CancellationToken));

        Assert.True(Directory.Exists(fixture.Path));
    }

    [Fact]
    public async Task DeleteExact_RefusesMismatchAndPreservesOriginal()
    {
        using var fixture = new MarkerStoreFixture();
        await fixture.Store.CreateAsync(Marker, TestContext.Current.CancellationToken);
        var different = Marker with { MarkerId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee") };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Store.DeleteExactAsync(different, TestContext.Current.CancellationToken));

        Assert.Equal(
            Marker,
            await fixture.Store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public async Task Load_RejectsAnyNonExactShapeOrValue(string document)
    {
        using var fixture = new MarkerStoreFixture();
        await File.WriteAllTextAsync(
            fixture.Path,
            document,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Store.LoadAsync(TestContext.Current.CancellationToken));
    }

    public static TheoryData<string> InvalidDocuments => new()
    {
        "{}",
        "[]",
        "{\"schemaVersion\":2,\"markerId\":\"11111111-2222-3333-4444-555555555555\",\"reason\":\"forcedUninstallSkippedVerifiedRollback\",\"createdAtUtc\":\"2026-09-06T01:02:03.0000000+00:00\"}",
        "{\"SchemaVersion\":1,\"markerId\":\"11111111-2222-3333-4444-555555555555\",\"reason\":\"forcedUninstallSkippedVerifiedRollback\",\"createdAtUtc\":\"2026-09-06T01:02:03.0000000+00:00\"}",
        "{\"schemaVersion\":1,\"schemaVersion\":1,\"markerId\":\"11111111-2222-3333-4444-555555555555\",\"reason\":\"forcedUninstallSkippedVerifiedRollback\",\"createdAtUtc\":\"2026-09-06T01:02:03.0000000+00:00\"}",
        "{\"schemaVersion\":1,\"markerId\":\"11111111-2222-3333-4444-555555555555\",\"reason\":\"other\",\"createdAtUtc\":\"2026-09-06T01:02:03.0000000+00:00\"}",
        "{\"schemaVersion\":1,\"markerId\":\"AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE\",\"reason\":\"forcedUninstallSkippedVerifiedRollback\",\"createdAtUtc\":\"2026-09-06T01:02:03.0000000+00:00\"}",
        "{\"schemaVersion\":1,\"markerId\":\"11111111-2222-3333-4444-555555555555\",\"reason\":\"forcedUninstallSkippedVerifiedRollback\",\"createdAtUtc\":\"2026-09-06T01:02:03.0000000Z\"}",
        "{\"schemaVersion\":1,\"markerId\":\"11111111-2222-3333-4444-555555555555\",\"reason\":\"forcedUninstallSkippedVerifiedRollback\",\"createdAtUtc\":\"2026-09-06T09:02:03.0000000+08:00\"}",
        "{\"schemaVersion\":1,\"markerId\":\"11111111-2222-3333-4444-555555555555\",\"reason\":\"forcedUninstallSkippedVerifiedRollback\",\"createdAtUtc\":\"2026-09-06T01:02:03.0000000+00:00\",\"extra\":true}",
    };

    private sealed class MarkerStoreFixture : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"v12-force-uninstall-marker-{Guid.NewGuid():N}");

        public MarkerStoreFixture()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "forced-uninstall-recovery.json");
            Store = new ForcedUninstallRecoveryMarkerStore(Path);
        }

        public string Path { get; }

        public ForcedUninstallRecoveryMarkerStore Store { get; }

        public void Dispose()
        {
            Store.Dispose();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
