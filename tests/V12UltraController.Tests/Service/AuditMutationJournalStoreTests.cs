using System.Text;
using System.Text.Json;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed class AuditMutationJournalStoreTests
{
    [Fact]
    public async Task FileStore_RoundTripsFixedPathAsBomlessJson()
    {
        using var fixture = new FileFixture();
        var store = fixture.CreateStore();
        var journal = store.Create(
            "mode.set",
            "S-1-5-21-111-222-333-1001",
            "v12ctl",
            new { mode = "Auto" },
            new { mode = "Manual" });

        await store.PrepareAsync(journal);

        Assert.True(File.Exists(fixture.Paths.AuditMutationPendingPath));
        var bytes = await File.ReadAllBytesAsync(fixture.Paths.AuditMutationPendingPath);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        var loaded = await fixture.CreateStore().LoadAsync();
        Assert.NotNull(loaded);
        Assert.Equal(journal.TransactionId, loaded.TransactionId);
        Assert.Equal(AuditMutationStage.Prepared, loaded.Stage);
        Assert.True(JsonElement.DeepEquals(journal.Previous, loaded.Previous));
        Assert.True(JsonElement.DeepEquals(journal.Current, loaded.Current));
    }

    [Fact]
    public async Task CrashSnapshots_EachDurableStageCanBeReloadedByANewStore()
    {
        using var fixture = new FileFixture();
        var store = fixture.CreateStore();
        var prepared = store.Create(
            "speed.set",
            "S-1-5-21-111-222-333-1001",
            "settings",
            new { percent = 50 },
            new { percent = 70 });
        await store.PrepareAsync(prepared);

        var afterPrepareCrash = await fixture.CreateStore().LoadAsync();
        Assert.Equal(AuditMutationStage.Prepared, afterPrepareCrash!.Stage);

        var inProgress = await fixture.CreateStore().AdvanceAsync(
            afterPrepareCrash,
            AuditMutationStage.MutationInProgress);
        var afterMutationBoundaryCrash = await fixture.CreateStore().LoadAsync();
        Assert.Equal(AuditMutationStage.MutationInProgress, afterMutationBoundaryCrash!.Stage);

        var applied = await fixture.CreateStore().AdvanceAsync(
            afterMutationBoundaryCrash,
            AuditMutationStage.StateApplied);
        var afterStateCrash = await fixture.CreateStore().LoadAsync();
        Assert.Equal(AuditMutationStage.StateApplied, afterStateCrash!.Stage);

        _ = await fixture.CreateStore().AdvanceAsync(applied, AuditMutationStage.AuditAppended);
        var afterAuditCrash = await fixture.CreateStore().LoadAsync();
        Assert.Equal(AuditMutationStage.AuditAppended, afterAuditCrash!.Stage);
        Assert.Equal(prepared.TransactionId, afterAuditCrash.TransactionId);
    }

    [Fact]
    public async Task PrepareAsync_NeverOverwritesAnExistingUnresolvedTransaction()
    {
        var persistence = new FakePersistence();
        var store = new AuditMutationJournalStore(persistence, new FixedTimeProvider());
        var first = Create(store, "mode.set");
        var second = Create(store, "speed.set");
        await store.PrepareAsync(first);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.PrepareAsync(second));

        var loaded = await store.LoadAsync();
        Assert.Equal(first.TransactionId, loaded!.TransactionId);
        Assert.Equal("mode.set", loaded.EventName);
    }

    [Fact]
    public async Task AdvanceAsync_RequiresExactJournalAndStrictSequentialStage()
    {
        var persistence = new FakePersistence();
        var store = new AuditMutationJournalStore(persistence, new FixedTimeProvider());
        var prepared = Create(store, "curve.set");
        await store.PrepareAsync(prepared);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.AdvanceAsync(prepared, AuditMutationStage.StateApplied));
        var wrongExpected = prepared with { Source = "other" };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.AdvanceAsync(wrongExpected, AuditMutationStage.MutationInProgress));

        var inProgress = await store.AdvanceAsync(prepared, AuditMutationStage.MutationInProgress);
        Assert.Equal(AuditMutationStage.MutationInProgress, inProgress.Stage);
        Assert.Equal(AuditMutationStage.MutationInProgress, (await store.LoadAsync())!.Stage);
    }

    [Fact]
    public async Task FailedAdvanceWrite_LeavesPriorDurableStageUntouched()
    {
        var persistence = new FakePersistence();
        var store = new AuditMutationJournalStore(persistence, new FixedTimeProvider());
        var prepared = Create(store, "mode.set");
        await store.PrepareAsync(prepared);
        persistence.FailWrites = true;

        await Assert.ThrowsAsync<IOException>(() =>
            store.AdvanceAsync(prepared, AuditMutationStage.MutationInProgress));

        persistence.FailWrites = false;
        Assert.Equal(AuditMutationStage.Prepared, (await store.LoadAsync())!.Stage);
    }

    [Fact]
    public async Task DeleteExactAsync_RefusesWrongTransactionAndSurfacesDeleteFailure()
    {
        var persistence = new FakePersistence();
        var store = new AuditMutationJournalStore(persistence, new FixedTimeProvider());
        var prepared = Create(store, "mode.set");
        await store.PrepareAsync(prepared);
        var wrong = prepared with { TransactionId = Guid.NewGuid() };

        await Assert.ThrowsAsync<InvalidDataException>(() => store.DeleteExactAsync(wrong));
        Assert.NotNull(await store.LoadAsync());

        persistence.FailDeletes = true;
        await Assert.ThrowsAsync<IOException>(() => store.DeleteExactAsync(prepared));
        persistence.FailDeletes = false;
        Assert.NotNull(await store.LoadAsync());

        await store.DeleteExactAsync(prepared);
        Assert.Null(await store.LoadAsync());
    }

    [Theory]
    [InlineData("serialNumber")]
    [InlineData("devicePath")]
    [InlineData("taskXml")]
    [InlineData("rawXml")]
    [InlineData("hidReport")]
    [InlineData("featureBuffer")]
    [InlineData("rawPayload")]
    [InlineData("hardwareId")]
    public void Create_RejectsSensitiveSnapshotPropertyNames(string propertyName)
    {
        var store = new AuditMutationJournalStore(new FakePersistence(), new FixedTimeProvider());
        var previous = new Dictionary<string, object?> { [propertyName] = "secret" };

        Assert.Throws<InvalidDataException>(() =>
            store.Create("mode.set", "S-1-5-18", "service", previous, new { mode = "Auto" }));
    }

    [Theory]
    [InlineData("<?xml version='1.0'?>")]
    [InlineData("<Task xmlns='urn:test'/>")]
    [InlineData(@"\\?\hid#vid_374a&pid_b101")]
    [InlineData(@"HID\VID_374A&PID_B101\SERIAL")]
    public void Create_RejectsRawXmlAndDeviceIdentityStrings(string value)
    {
        var store = new AuditMutationJournalStore(new FakePersistence(), new FixedTimeProvider());

        Assert.Throws<InvalidDataException>(() =>
            store.Create("mode.set", "S-1-5-18", "service", new { value }, new { value = "safe" }));
    }

    [Fact]
    public void Create_AllowsNonsensitiveTaskHashesAndMatchFlags()
    {
        var store = new AuditMutationJournalStore(new FakePersistence(), new FixedTimeProvider());

        var journal = store.Create(
            "commission.recover-task-transition",
            "S-1-5-18",
            "service",
            new
            {
                taskBackupSha256 = new string('A', 64),
                currentTaskXmlSha256 = new string('B', 64),
                exactBackupXmlMatch = true,
            },
            new { commissioned = true });

        Assert.Equal(AuditMutationStage.Prepared, journal.Stage);
    }

    [Fact]
    public void Create_NormalizesTimeAndAllowsPublicReportLengthMetadata()
    {
        var store = new AuditMutationJournalStore(new FakePersistence(), new OffsetTimeProvider());

        var journal = store.Create(
            "commission.probe",
            "S-1-5-18",
            "service",
            new { featureReportLength = 9, hidReportByteLength = 9 },
            new { featureReportLength = 9, hidReportByteLength = 9 });

        Assert.Equal(TimeSpan.Zero, journal.PreparedAtUtc.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 3, 0, 0, TimeSpan.Zero), journal.PreparedAtUtc);
    }

    [Fact]
    public async Task LoadAsync_RejectsCorruptUnknownDuplicateAndNonCanonicalDocuments()
    {
        var persistence = new FakePersistence { Content = "{not-json" };
        var store = new AuditMutationJournalStore(persistence, new FixedTimeProvider());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());

        var valid = Create(store, "mode.set");
        persistence.Content = JsonSerializer.Serialize(new
        {
            schemaVersion = valid.SchemaVersion,
            transactionId = valid.TransactionId,
            eventName = valid.EventName,
            callerSid = valid.CallerSid,
            source = valid.Source,
            preparedAtUtc = valid.PreparedAtUtc,
            stage = "prepared",
            previous = valid.Previous,
            current = valid.Current,
            unexpected = true,
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());

        persistence.Content = persistence.Content.Replace(
            "\"schemaVersion\":1",
            "\"schemaVersion\":1,\"schemaVersion\":1",
            StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());

        persistence.Content = persistence.Content.Replace(
            "\"schemaVersion\":1,\"schemaVersion\":1",
            "\"SchemaVersion\":1",
            StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    private static AuditMutationJournal Create(AuditMutationJournalStore store, string eventName) =>
        store.Create(
            eventName,
            "S-1-5-21-111-222-333-1001",
            "settings",
            new { value = "before" },
            new { value = "after" });

    private sealed class FakePersistence : IAuditMutationJournalPersistence
    {
        public string? Content { get; set; }
        public bool FailWrites { get; set; }
        public bool FailDeletes { get; set; }

        public Task<string?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Content);
        }

        public Task WriteAsync(string json, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailWrites)
            {
                throw new IOException("Injected audit-mutation write failure.");
            }

            Content = json;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailDeletes)
            {
                throw new IOException("Injected audit-mutation delete failure.");
            }

            Content = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 9, 5, 3, 0, 0, TimeSpan.Zero);
    }

    private sealed class OffsetTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 9, 5, 11, 0, 0, TimeSpan.FromHours(8));
    }

    private sealed class FileFixture : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(), $"v12-audit-mutation-{Guid.NewGuid():N}");

        public FileFixture()
        {
            Paths = new ServicePaths(directory);
        }

        public ServicePaths Paths { get; }

        public AuditMutationJournalStore CreateStore() =>
            new(Paths, new FixedTimeProvider());

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
