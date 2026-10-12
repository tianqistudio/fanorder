using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed class TakeoverTransitionJournalTests
{
    [Fact]
    public async Task Store_RoundTripsAndDeletesValidatedJournal()
    {
        var directory = CreateDirectory();
        try
        {
            var store = new TakeoverTransitionJournalStore(new ServicePaths(directory));
            var journal = CreateJournal();

            await store.SaveAsync(journal);
            Assert.True(TakeoverTransitionJournalStore.Equivalent(
                journal,
                Assert.IsType<TakeoverTransitionJournal>(await store.LoadAsync())));

            await store.DeleteExactAsync(journal);
            Assert.Null(await store.LoadAsync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Store_RejectsCorruptOrInconsistentJournal()
    {
        var directory = CreateDirectory();
        try
        {
            var paths = new ServicePaths(directory);
            var store = new TakeoverTransitionJournalStore(paths);
            await File.WriteAllTextAsync(paths.TransitionJournalPath, "{not-json");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());

            var inconsistent = CreateJournal() with
            {
                Operation = TakeoverTransitionOperation.Rollback,
                Stage = TakeoverTransitionStage.TaskDisabled,
            };
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(inconsistent));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(1, 7)]
    public async Task Store_AcceptsOperationSpecificTaskMutationBarrier(
        int operationValue,
        int stageValue)
    {
        var directory = CreateDirectory();
        try
        {
            var store = new TakeoverTransitionJournalStore(new ServicePaths(directory));
            var journal = CreateJournal() with
            {
                Operation = (TakeoverTransitionOperation)operationValue,
                Stage = (TakeoverTransitionStage)stageValue,
            };

            await store.SaveAsync(journal);

            Assert.True(TakeoverTransitionJournalStore.Equivalent(
                journal,
                Assert.IsType<TakeoverTransitionJournal>(await store.LoadAsync())));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Store_RejectsLegacySchemaOneWithoutDeletingIt()
    {
        var directory = CreateDirectory();
        try
        {
            var paths = new ServicePaths(directory);
            var store = new TakeoverTransitionJournalStore(paths);
            var legacy = CreateJournal() with { SchemaVersion = 1 };
            await File.WriteAllTextAsync(
                paths.TransitionJournalPath,
                System.Text.Json.JsonSerializer.Serialize(
                    legacy,
                    new System.Text.Json.JsonSerializerOptions(
                        System.Text.Json.JsonSerializerDefaults.Web)));

            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());

            Assert.True(File.Exists(paths.TransitionJournalPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Store_RejectsUnknownDuplicateAndWrongCaseProperties()
    {
        var directory = CreateDirectory();
        try
        {
            var paths = new ServicePaths(directory);
            var store = new TakeoverTransitionJournalStore(paths);
            var json = System.Text.Json.JsonSerializer.Serialize(
                CreateJournal(),
                new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web));

            var documents = new[]
            {
                json[..^1] + ",\"unknown\":true}",
                "{\"schemaVersion\":2," + json[1..],
                json.Replace("\"ownerSid\"", "\"OwnerSid\"", StringComparison.Ordinal),
            };

            foreach (var document in documents)
            {
                await File.WriteAllTextAsync(paths.TransitionJournalPath, document);
                await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Store_RejectsUnknownDuplicateWrongCaseMissingAndInvalidAuditProperties()
    {
        var directory = CreateDirectory();
        try
        {
            var paths = new ServicePaths(directory);
            var store = new TakeoverTransitionJournalStore(paths);
            var json = System.Text.Json.JsonSerializer.Serialize(
                CreateJournal(),
                new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web));

            const string eventProperty = "\"eventName\":\"commission.commit\"";
            const string callerProperty = "\"callerSid\":\"S-1-5-21-100-200-300-400\"";
            var documents = new[]
            {
                json.Replace(eventProperty, "\"unknown\":true," + eventProperty, StringComparison.Ordinal),
                json.Replace(callerProperty, "\"callerSid\":\"S-1-5-18\"," + callerProperty,
                    StringComparison.Ordinal),
                json.Replace("\"callerSid\"", "\"CallerSid\"", StringComparison.Ordinal),
                json.Replace(eventProperty + ",", string.Empty, StringComparison.Ordinal),
                json.Replace(eventProperty, "\"eventName\":null", StringComparison.Ordinal),
            };

            foreach (var document in documents)
            {
                await File.WriteAllTextAsync(paths.TransitionJournalPath, document);
                await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Store_RejectsOperationDriftBetweenResolvedOutcomeAndAuditAppend()
    {
        var directory = CreateDirectory();
        try
        {
            var store = new TakeoverTransitionJournalStore(new ServicePaths(directory));
            var resolved = CreateResolvedCommitJournal();
            await store.SaveAsync(resolved);

            await Assert.ThrowsAsync<InvalidDataException>(() => store.AdvanceAsync(
                resolved,
                resolved with
                {
                    Operation = TakeoverTransitionOperation.Rollback,
                    Stage = TakeoverTransitionStage.AuditAppended,
                }));

            Assert.True(TakeoverTransitionJournalStore.Equivalent(
                resolved,
                Assert.IsType<TakeoverTransitionJournal>(await store.LoadAsync())));

            var appended = await store.AdvanceAsync(
                resolved,
                resolved with { Stage = TakeoverTransitionStage.AuditAppended });
            Assert.Equal(TakeoverTransitionOperation.Commit, appended.Operation);
            Assert.Equal(TakeoverTransitionStage.AuditAppended, appended.Stage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Store_RejectsOversizedOrUnreadableExistingPathInsteadOfTreatingItAsMissing()
    {
        var directory = CreateDirectory();
        try
        {
            var paths = new ServicePaths(directory);
            var store = new TakeoverTransitionJournalStore(paths);
            await File.WriteAllTextAsync(paths.TransitionJournalPath, new string(' ', 16 * 1024 + 1));

            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());

            File.Delete(paths.TransitionJournalPath);
            Directory.CreateDirectory(paths.TransitionJournalPath);
            var exception = await Record.ExceptionAsync(() => store.LoadAsync());
            Assert.NotNull(exception);
            Assert.False(exception is FileNotFoundException);
            Assert.False(exception is DirectoryNotFoundException);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Store_DeleteExactRefusesDifferentTransactionAndVerifiesDeletion()
    {
        var directory = CreateDirectory();
        try
        {
            var store = new TakeoverTransitionJournalStore(new ServicePaths(directory));
            var journal = CreateJournal();
            await store.SaveAsync(journal);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => store.DeleteExactAsync(journal with { TransitionId = Guid.NewGuid() }));
            Assert.True(TakeoverTransitionJournalStore.Equivalent(
                journal,
                Assert.IsType<TakeoverTransitionJournal>(await store.LoadAsync())));

            await store.DeleteExactAsync(journal);
            Assert.Null(await store.LoadAsync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("bad-owner")]
    [InlineData("S-1-5-21-100\n200")]
    public async Task Store_RejectsInvalidOwnerIdentity(string ownerSid)
    {
        var directory = CreateDirectory();
        try
        {
            var store = new TakeoverTransitionJournalStore(new ServicePaths(directory));

            await Assert.ThrowsAsync<InvalidDataException>(
                () => store.SaveAsync(CreateJournal() with { OwnerSid = ownerSid }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static TakeoverTransitionJournal CreateJournal() => new(
        TakeoverTransitionJournal.CurrentSchemaVersion,
        Guid.NewGuid(),
        TakeoverTransitionOperation.Commit,
        TakeoverTransitionStage.Prepared,
        "S-1-5-21-100-200-300-400",
        new string('A', 64),
        TaskOriginallyEnabled: true,
        StartMythCoolRequested: false,
        DateTimeOffset.UtcNow,
        CreatePendingAudit("commission.commit"));

    private static TakeoverTransitionAudit CreatePendingAudit(string eventName)
    {
        var previous = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            commissioned = false,
            taskEnabled = true,
        });
        return new TakeoverTransitionAudit(
            eventName,
            "S-1-5-21-100-200-300-400",
            "test",
            TakeoverTransitionAuditResult.Pending,
            TakeoverTransitionAuditAction.Pending,
            previous,
            previous.Clone(),
            ResolvedByCallerSid: null,
            ResolvedBySource: null);
    }

    private static TakeoverTransitionJournal CreateResolvedCommitJournal()
    {
        var journal = CreateJournal();
        return journal with
        {
            Stage = TakeoverTransitionStage.OutcomeResolved,
            Audit = journal.Audit with
            {
                Result = TakeoverTransitionAuditResult.Success,
                Action = TakeoverTransitionAuditAction.CommitCompleted,
            },
        };
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"v12-journal-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
