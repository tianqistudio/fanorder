using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed class SafetyOperationAuditJournalStoreTests
{
    [Fact]
    public async Task FileStore_UsesExactInjectedPathAndRoundTripsBomlessJson()
    {
        using var fixture = new FileFixture();
        var store = fixture.CreateStore();
        var journal = CreateSessionJournal(store);

        await store.PrepareAsync(journal);

        Assert.True(File.Exists(fixture.Path));
        var bytes = await File.ReadAllBytesAsync(fixture.Path);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        var loaded = await fixture.CreateStore().LoadAsync();
        AssertJournalEqual(journal, Assert.IsType<SafetyOperationAuditJournal>(loaded));
    }

    [Fact]
    public async Task CrashSnapshots_EveryStageReloadsAndOnlyAuditAppendedCanDelete()
    {
        using var fixture = new FileFixture();
        var prepared = CreateHidJournal(fixture.CreateStore());
        await fixture.CreateStore().PrepareAsync(prepared);
        Assert.Equal(
            SafetyOperationAuditStage.Prepared,
            (await fixture.CreateStore().LoadAsync())!.Stage);

        var effectInProgress = await fixture.CreateStore().MarkEffectInProgressAsync(
            (await fixture.CreateStore().LoadAsync())!);
        Assert.Equal(
            SafetyOperationAuditStage.EffectInProgress,
            (await fixture.CreateStore().LoadAsync())!.Stage);

        var resolved = await fixture.CreateStore().ResolveAsync(
            effectInProgress,
            SafetyOperationAuditOutcome.Compensated,
            new { poweredOn = true, fanPercent = 100, readbackConfirmed = true });
        var afterResolveCrash = (await fixture.CreateStore().LoadAsync())!;
        Assert.Equal(SafetyOperationAuditStage.OutcomeResolved, afterResolveCrash.Stage);
        Assert.Equal(SafetyOperationAuditOutcome.Compensated, afterResolveCrash.Outcome);
        Assert.Equal(100, afterResolveCrash.Observed.GetProperty("fanPercent").GetInt32());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.CreateStore().DeleteExactAsync(resolved));
        var auditAppended = await fixture.CreateStore().MarkAuditAppendedAsync(afterResolveCrash);
        Assert.Equal(
            SafetyOperationAuditStage.AuditAppended,
            (await fixture.CreateStore().LoadAsync())!.Stage);

        await fixture.CreateStore().DeleteExactAsync(auditAppended);
        Assert.Null(await fixture.CreateStore().LoadAsync());
    }

    [Theory]
    [InlineData((int)SafetyOperationAuditOutcome.Success)]
    [InlineData((int)SafetyOperationAuditOutcome.NotApplied)]
    [InlineData((int)SafetyOperationAuditOutcome.Compensated)]
    [InlineData((int)SafetyOperationAuditOutcome.Indeterminate)]
    public async Task Resolve_SupportsEveryTerminalOutcome(int outcomeValue)
    {
        var outcome = (SafetyOperationAuditOutcome)outcomeValue;
        var persistence = new FakePersistence();
        var store = CreateStore(persistence);
        var prepared = CreateSessionJournal(store);
        await store.PrepareAsync(prepared);
        var effect = await store.MarkEffectInProgressAsync(prepared);

        var resolved = await store.ResolveAsync(
            effect,
            outcome,
            new { result = outcome.ToString() });

        Assert.Equal(SafetyOperationAuditStage.OutcomeResolved, resolved.Stage);
        Assert.Equal(outcome, resolved.Outcome);
        Assert.Equal(JsonValueKind.Object, resolved.Observed.ValueKind);
        AssertJournalEqual(resolved, (await store.LoadAsync())!);
    }

    [Fact]
    public async Task StartupRecovery_CanClassifyPreparedIntentAsNotAppliedWithoutPublishingEffectInProgress()
    {
        var persistence = new FakePersistence();
        var store = CreateStore(persistence);
        var prepared = CreateSessionJournal(store);
        await store.PrepareAsync(prepared);

        var resolved = await store.ResolvePreparedAsNotAppliedAsync(
            prepared,
            new { recovery = "service-startup", effectApplied = false });

        Assert.Equal(SafetyOperationAuditStage.OutcomeResolved, resolved.Stage);
        Assert.Equal(SafetyOperationAuditOutcome.NotApplied, resolved.Outcome);
        Assert.False(resolved.Observed.GetProperty("effectApplied").GetBoolean());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.ResolvePreparedAsNotAppliedAsync(resolved, new { effectApplied = false }));
    }

    [Theory]
    [InlineData((int)SafetyOperationKind.SessionOnly, false)]
    [InlineData((int)SafetyOperationKind.HidWithOn100Recovery, true)]
    public void Create_AcceptsOnlyRecoveryFlagThatMatchesKind(
        int kindValue,
        bool requiresOn100Recovery)
    {
        var kind = (SafetyOperationKind)kindValue;
        var store = CreateStore(new FakePersistence());

        var valid = store.Create(
            "commission.example",
            "S-1-5-18",
            "service",
            kind,
            new { stage = "before" },
            new { stage = "after" },
            requiresOn100Recovery);
        Assert.Equal(kind, valid.Kind);
        Assert.Equal(requiresOn100Recovery, valid.RequiresOn100Recovery);

        Assert.Throws<InvalidDataException>(() => store.Create(
            "commission.example",
            "S-1-5-18",
            "service",
            kind,
            new { stage = "before" },
            new { stage = "after" },
            !requiresOn100Recovery));
    }

    [Fact]
    public void JournalValidation_ObservedExistsExactlyAfterResolution()
    {
        var store = CreateStore(new FakePersistence());
        var prepared = CreateSessionJournal(store);
        var objectSnapshot = JsonSerializer.SerializeToElement(new { result = "known" });
        var nullSnapshot = JsonSerializer.SerializeToElement<object?>(null);

        Assert.Throws<InvalidDataException>(() =>
            (prepared with { Observed = objectSnapshot }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (prepared with
            {
                Stage = SafetyOperationAuditStage.OutcomeResolved,
                Outcome = SafetyOperationAuditOutcome.Success,
                Observed = nullSnapshot,
            }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (prepared with
            {
                Stage = SafetyOperationAuditStage.OutcomeResolved,
                Observed = objectSnapshot,
            }).Validate());
    }

    [Fact]
    public async Task Prepare_AllowsExactlyOneOutstandingTransactionEvenConcurrently()
    {
        var persistence = new FakePersistence();
        var store = CreateStore(persistence);
        var first = CreateSessionJournal(store, "session.first");
        var second = CreateSessionJournal(store, "session.second");

        var attempts = await Task.WhenAll(AttemptPrepareAsync(first), AttemptPrepareAsync(second));

        Assert.Equal(1, attempts.Count(static succeeded => succeeded));
        var loaded = Assert.IsType<SafetyOperationAuditJournal>(await store.LoadAsync());
        Assert.Contains(loaded.TransactionId, new[] { first.TransactionId, second.TransactionId });
        return;

        async Task<bool> AttemptPrepareAsync(SafetyOperationAuditJournal journal)
        {
            try
            {
                await store.PrepareAsync(journal);
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }
    }

    [Fact]
    public async Task StageMethods_RejectSkipsReplaysAndWrongStructuralCas()
    {
        var persistence = new FakePersistence();
        var store = CreateStore(persistence);
        var prepared = CreateSessionJournal(store);
        await store.PrepareAsync(prepared);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ResolveAsync(
            prepared,
            SafetyOperationAuditOutcome.Success,
            new { state = "done" }));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.MarkAuditAppendedAsync(prepared));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.DeleteExactAsync(prepared));

        var wrongExpected = prepared with { Source = "other" };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.MarkEffectInProgressAsync(wrongExpected));

        var effect = await store.MarkEffectInProgressAsync(prepared);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.MarkEffectInProgressAsync(effect));
        var resolved = await store.ResolveAsync(
            effect,
            SafetyOperationAuditOutcome.Success,
            new { state = "done" });
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ResolveAsync(
            resolved,
            SafetyOperationAuditOutcome.Indeterminate,
            new { state = "changed" }));

        var wrongResolved = resolved with
        {
            Observed = JsonSerializer.SerializeToElement(new { state = "tampered" }),
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.MarkAuditAppendedAsync(wrongResolved));
        AssertJournalEqual(resolved, (await store.LoadAsync())!);
    }

    [Fact]
    public async Task CreateAndResolve_ReusePrivacyBoundedSnapshotPolicy()
    {
        var store = CreateStore(new FakePersistence());
        Assert.Throws<InvalidDataException>(() => store.Create(
            "session.sensitive",
            "S-1-5-18",
            "service",
            SafetyOperationKind.SessionOnly,
            new { serialNumber = "secret" },
            new { state = "after" },
            requiresOn100Recovery: false));

        var effect = CreateSessionJournal(store) with
        {
            Stage = SafetyOperationAuditStage.EffectInProgress,
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ResolveAsync(
            effect,
            SafetyOperationAuditOutcome.Success,
            new { rawPayload = "secret" }));
    }

    [Fact]
    public void Create_NormalizesPreparedTimeToUtcAndRequiresSidLikeCaller()
    {
        var store = new SafetyOperationAuditJournalStore(
            new FakePersistence(),
            new OffsetTimeProvider());

        var journal = CreateSessionJournal(store);

        Assert.Equal(TimeSpan.Zero, journal.PreparedAtUtc.Offset);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 5, 3, 0, 0, TimeSpan.Zero),
            journal.PreparedAtUtc);
        Assert.Throws<InvalidDataException>(() => store.Create(
            "session.invalid-caller",
            "not-a-sid",
            "service",
            SafetyOperationKind.SessionOnly,
            new { state = "before" },
            new { state = "after" },
            requiresOn100Recovery: false));
    }

    [Fact]
    public async Task Load_RejectsBadUnknownDuplicateMissingMisCasedAndNonCanonicalJson()
    {
        var persistence = new FakePersistence();
        var store = CreateStore(persistence);
        var prepared = CreateSessionJournal(store);
        await store.PrepareAsync(prepared);
        var canonical = persistence.Content!;

        var malformedDocuments = new List<string>
        {
            "{not-json",
            canonical[..^1] + ",\"unexpected\":true}",
            canonical.Replace(
                "\"schemaVersion\":1",
                "\"schemaVersion\":1,\"schemaVersion\":1",
                StringComparison.Ordinal),
            canonical.Replace("\"eventName\"", "\"EventName\"", StringComparison.Ordinal),
            RemoveProperty(canonical, "outcome"),
            canonical.Replace("\"stage\":\"prepared\"", "\"stage\":0", StringComparison.Ordinal),
            canonical.Replace(
                "\"stage\":\"prepared\"",
                "\"stage\":\"Prepared\"",
                StringComparison.Ordinal),
            canonical.Replace(
                "\"kind\":\"sessionOnly\"",
                "\"kind\":\"futureKind\"",
                StringComparison.Ordinal),
            canonical.Replace(
                "\"requiresOn100Recovery\":false",
                "\"requiresOn100Recovery\":true",
                StringComparison.Ordinal),
            canonical.Replace(
                "\"observed\":null",
                "\"observed\":{\"claimed\":true}",
                StringComparison.Ordinal),
            canonical.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal),
        };

        foreach (var malformed in malformedDocuments)
        {
            persistence.Content = malformed;
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        }

        persistence.Content = new string('x', SafetyOperationAuditJournalStore.MaximumDocumentBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task Load_RejectsResolvedJournalWithoutObservedSnapshot()
    {
        var persistence = new FakePersistence();
        var store = CreateStore(persistence);
        var prepared = CreateSessionJournal(store);
        await store.PrepareAsync(prepared);
        var effect = await store.MarkEffectInProgressAsync(prepared);
        _ = await store.ResolveAsync(
            effect,
            SafetyOperationAuditOutcome.NotApplied,
            new { state = "before" });
        persistence.Content = persistence.Content!.Replace(
            "\"observed\":{\"state\":\"before\"}",
            "\"observed\":null",
            StringComparison.Ordinal);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task WriteCutPoints_PreserveTheActuallyDurableStageForRecovery()
    {
        var beforePersistence = new FakePersistence
        {
            NextWriteFailure = PersistenceFailureCut.BeforeEffect,
        };
        var beforeStore = CreateStore(beforePersistence);
        await Assert.ThrowsAsync<IOException>(() =>
            beforeStore.PrepareAsync(CreateSessionJournal(beforeStore)));
        Assert.Null(await beforeStore.LoadAsync());

        var afterPersistence = new FakePersistence
        {
            NextWriteFailure = PersistenceFailureCut.AfterEffect,
        };
        var afterStore = CreateStore(afterPersistence);
        await Assert.ThrowsAsync<IOException>(() =>
            afterStore.PrepareAsync(CreateSessionJournal(afterStore)));
        Assert.Equal(SafetyOperationAuditStage.Prepared, (await afterStore.LoadAsync())!.Stage);

        var advancePersistence = new FakePersistence();
        var advanceStore = CreateStore(advancePersistence);
        var prepared = CreateSessionJournal(advanceStore);
        await advanceStore.PrepareAsync(prepared);
        advancePersistence.NextWriteFailure = PersistenceFailureCut.BeforeEffect;
        await Assert.ThrowsAsync<IOException>(() =>
            advanceStore.MarkEffectInProgressAsync(prepared));
        Assert.Equal(SafetyOperationAuditStage.Prepared, (await advanceStore.LoadAsync())!.Stage);

        advancePersistence.NextWriteFailure = PersistenceFailureCut.AfterEffect;
        await Assert.ThrowsAsync<IOException>(() =>
            advanceStore.MarkEffectInProgressAsync(prepared));
        Assert.Equal(
            SafetyOperationAuditStage.EffectInProgress,
            (await advanceStore.LoadAsync())!.Stage);
    }

    [Fact]
    public async Task DeleteCutPointsAndNoOpDeleteRemainTruthfullyRecoverable()
    {
        var beforePersistence = new FakePersistence();
        var beforeStore = CreateStore(beforePersistence);
        var beforeAppended = await ReachAuditAppendedAsync(beforeStore);
        beforePersistence.NextDeleteFailure = PersistenceFailureCut.BeforeEffect;
        await Assert.ThrowsAsync<IOException>(() =>
            beforeStore.DeleteExactAsync(beforeAppended));
        AssertJournalEqual(beforeAppended, (await beforeStore.LoadAsync())!);

        var afterPersistence = new FakePersistence();
        var afterStore = CreateStore(afterPersistence);
        var afterAppended = await ReachAuditAppendedAsync(afterStore);
        afterPersistence.NextDeleteFailure = PersistenceFailureCut.AfterEffect;
        await Assert.ThrowsAsync<IOException>(() =>
            afterStore.DeleteExactAsync(afterAppended));
        Assert.Null(await afterStore.LoadAsync());

        var noOpPersistence = new FakePersistence { IgnoreDeletes = true };
        var noOpStore = CreateStore(noOpPersistence);
        var noOpAppended = await ReachAuditAppendedAsync(noOpStore);
        await Assert.ThrowsAsync<IOException>(() =>
            noOpStore.DeleteExactAsync(noOpAppended));
        AssertJournalEqual(noOpAppended, (await noOpStore.LoadAsync())!);
    }

    [Fact]
    public async Task SuccessfulWriteWithMismatchedReadbackIsRejected()
    {
        var persistence = new FakePersistence { CorruptWrites = true };
        var store = CreateStore(persistence);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.PrepareAsync(CreateSessionJournal(store)));
    }

    private static async Task<SafetyOperationAuditJournal> ReachAuditAppendedAsync(
        SafetyOperationAuditJournalStore store)
    {
        var prepared = CreateSessionJournal(store);
        await store.PrepareAsync(prepared);
        var effect = await store.MarkEffectInProgressAsync(prepared);
        var resolved = await store.ResolveAsync(
            effect,
            SafetyOperationAuditOutcome.Success,
            new { state = "after" });
        return await store.MarkAuditAppendedAsync(resolved);
    }

    private static SafetyOperationAuditJournal CreateSessionJournal(
        SafetyOperationAuditJournalStore store,
        string eventName = "session.operation") =>
        store.Create(
            eventName,
            "S-1-5-21-111-222-333-1001",
            "settings",
            SafetyOperationKind.SessionOnly,
            new { sessionState = "before" },
            new { sessionState = "after" },
            requiresOn100Recovery: false);

    private static SafetyOperationAuditJournal CreateHidJournal(
        SafetyOperationAuditJournalStore store) =>
        store.Create(
            "shutdown.qualify",
            "S-1-5-21-111-222-333-1001",
            "settings",
            SafetyOperationKind.HidWithOn100Recovery,
            new { poweredOn = true, fanPercent = 100 },
            new { poweredOn = false, recoveryPercent = 100 },
            requiresOn100Recovery: true);

    private static SafetyOperationAuditJournalStore CreateStore(FakePersistence persistence) =>
        new(persistence, new FixedTimeProvider());

    private static string RemoveProperty(string json, string propertyName)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        Assert.True(node.Remove(propertyName));
        return node.ToJsonString();
    }

    private static void AssertJournalEqual(
        SafetyOperationAuditJournal expected,
        SafetyOperationAuditJournal actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.TransactionId, actual.TransactionId);
        Assert.Equal(expected.EventName, actual.EventName);
        Assert.Equal(expected.CallerSid, actual.CallerSid);
        Assert.Equal(expected.Source, actual.Source);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.PreparedAtUtc, actual.PreparedAtUtc);
        Assert.True(JsonElement.DeepEquals(expected.Previous, actual.Previous));
        Assert.True(JsonElement.DeepEquals(expected.Intended, actual.Intended));
        Assert.True(JsonElement.DeepEquals(expected.Observed, actual.Observed));
        Assert.Equal(expected.RequiresOn100Recovery, actual.RequiresOn100Recovery);
        Assert.Equal(expected.Stage, actual.Stage);
        Assert.Equal(expected.Outcome, actual.Outcome);
    }

    private enum PersistenceFailureCut
    {
        None,
        BeforeEffect,
        AfterEffect,
    }

    private sealed class FakePersistence : ISafetyOperationAuditJournalPersistence
    {
        private readonly object sync = new();
        private string? content;

        public string? Content
        {
            get
            {
                lock (sync)
                {
                    return content;
                }
            }
            set
            {
                lock (sync)
                {
                    content = value;
                }
            }
        }

        public PersistenceFailureCut NextWriteFailure { get; set; }

        public PersistenceFailureCut NextDeleteFailure { get; set; }

        public bool IgnoreDeletes { get; set; }

        public bool CorruptWrites { get; set; }

        public Task<string?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Content);
        }

        public Task WriteAsync(string json, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cut = NextWriteFailure;
            NextWriteFailure = PersistenceFailureCut.None;
            if (cut == PersistenceFailureCut.BeforeEffect)
            {
                throw new IOException("Injected safety-operation write failure before effect.");
            }

            Content = CorruptWrites
                ? json.Replace("\"source\":\"settings\"", "\"source\":\"corrupted\"", StringComparison.Ordinal)
                : json;
            if (cut == PersistenceFailureCut.AfterEffect)
            {
                throw new IOException("Injected safety-operation write failure after effect.");
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cut = NextDeleteFailure;
            NextDeleteFailure = PersistenceFailureCut.None;
            if (cut == PersistenceFailureCut.BeforeEffect)
            {
                throw new IOException("Injected safety-operation delete failure before effect.");
            }

            if (!IgnoreDeletes)
            {
                Content = null;
            }

            if (cut == PersistenceFailureCut.AfterEffect)
            {
                throw new IOException("Injected safety-operation delete failure after effect.");
            }

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
        private readonly string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"v12-safety-operation-audit-{Guid.NewGuid():N}");

        public FileFixture()
        {
            Path = System.IO.Path.Combine(directory, "safety-operation.pending.json");
        }

        public string Path { get; }

        public SafetyOperationAuditJournalStore CreateStore() =>
            new(Path, new FixedTimeProvider());

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
