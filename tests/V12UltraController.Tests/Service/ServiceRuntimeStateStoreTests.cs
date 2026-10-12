using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed class ServiceRuntimeStateStoreTests
{
    [Fact]
    public async Task Load_MissingFileReturnsUncommissionedInitialState()
    {
        await using var fixture = new RuntimeStateFixture();

        var loaded = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceRuntimeState.Initial, loaded);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsCanonicalStates()
    {
        await using var fixture = new RuntimeStateFixture();
        var commissioned = new ServiceRuntimeState(
            true,
            "S-1-5-21-1000",
            new DateTimeOffset(2026, 9, 5, 1, 2, 3, TimeSpan.Zero));

        await fixture.Store.SaveAsync(commissioned, TestContext.Current.CancellationToken);
        Assert.Equal(
            commissioned,
            await fixture.Store.LoadAsync(TestContext.Current.CancellationToken));

        await fixture.Store.SaveAsync(
            ServiceRuntimeState.Initial,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            ServiceRuntimeState.Initial,
            await fixture.Store.LoadAsync(TestContext.Current.CancellationToken));
    }

    public static TheoryData<string> InvalidDocuments => new()
    {
        "{not-json",
        "[]",
        "{}",
        "{\"commissioned\":false,\"ownerSid\":null}",
        "{\"Commissioned\":false,\"ownerSid\":null,\"commissionedAtUtc\":null}",
        "{\"commissioned\":false,\"commissioned\":false,\"ownerSid\":null,\"commissionedAtUtc\":null}",
        "{\"commissioned\":false,\"ownerSid\":null,\"commissionedAtUtc\":null,\"extra\":true}",
        "{\"commissioned\":false,\"ownerSid\":\"S-1-5-21-1000\",\"commissionedAtUtc\":null}",
        "{\"commissioned\":true,\"ownerSid\":null,\"commissionedAtUtc\":\"2026-09-05T01:02:03Z\"}",
        "{\"commissioned\":true,\"ownerSid\":\"not-a-sid\",\"commissionedAtUtc\":\"2026-09-05T01:02:03Z\"}",
        "{\"commissioned\":true,\"ownerSid\":\"S-1-5-21-1000\",\"commissionedAtUtc\":\"2026-09-05T09:02:03+08:00\"}",
    };

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public async Task Load_RejectsMalformedNonCanonicalOrInconsistentState(string json)
    {
        await using var fixture = new RuntimeStateFixture();
        await File.WriteAllTextAsync(
            fixture.Paths.RuntimeStatePath,
            json,
            TestContext.Current.CancellationToken);

        _ = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Load_ExistingUnreadablePathIsNotTreatedAsMissing()
    {
        await using var fixture = new RuntimeStateFixture();
        Directory.CreateDirectory(fixture.Paths.RuntimeStatePath);

        var exception = await Record.ExceptionAsync(
            () => fixture.Store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.NotNull(exception);
        Assert.False(exception is FileNotFoundException);
        Assert.False(exception is DirectoryNotFoundException);
    }

    [Fact]
    public async Task Load_RejectsOversizedExistingDocument()
    {
        await using var fixture = new RuntimeStateFixture();
        await File.WriteAllTextAsync(
            fixture.Paths.RuntimeStatePath,
            new string(' ', 4097),
            TestContext.Current.CancellationToken);

        _ = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Save_RejectsInconsistentStateWithoutCreatingAFile()
    {
        await using var fixture = new RuntimeStateFixture();
        var invalid = new ServiceRuntimeState(true, null, DateTimeOffset.UtcNow);

        _ = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.SaveAsync(invalid, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(fixture.Paths.RuntimeStatePath));
    }

    private sealed class RuntimeStateFixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(), $"v12-runtime-state-tests-{Guid.NewGuid():N}");

        public RuntimeStateFixture()
        {
            Paths = new ServicePaths(directory);
            Paths.EnsureDirectories();
            Store = new ServiceRuntimeStateStore(Paths);
        }

        public ServicePaths Paths { get; }
        public ServiceRuntimeStateStore Store { get; }

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
