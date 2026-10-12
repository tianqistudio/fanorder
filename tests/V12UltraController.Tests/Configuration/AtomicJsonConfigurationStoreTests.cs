using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using System.Text.Json.Nodes;

namespace V12UltraController.Tests.Configuration;

public sealed class AtomicJsonConfigurationStoreTests
{
    [Fact]
    public async Task MissingFile_ReturnsSafeUncommissionedDefault()
    {
        using var directory = new TemporaryDirectory();
        using var store = new AtomicJsonConfigurationStore(Path.Combine(directory.Path, "controller.json"));

        var configuration = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(configuration.Commissioned);
        Assert.Equal(ControlMode.Auto, configuration.Mode);
        Assert.Equal(100, configuration.ManualSpeedPercent);
        Assert.Equal(36, configuration.Curve.Interpolate(30));
        Assert.Equal(ControllerConfiguration.CurrentSchemaVersion, configuration.SchemaVersion);
        Assert.Equal(SystemShutdownFanBehavior.KeepAt100, configuration.SystemShutdownFanBehavior);
        Assert.Null(configuration.ShutdownOffQualification);
    }

    [Fact]
    public async Task ExistingButUnreadableConfigurationPath_IsNotTreatedAsMissing()
    {
        using var directory = new TemporaryDirectory();
        // Opening a directory as a regular file fails deterministically on Windows. This
        // exercises the same branch as an ACL-denied file without weakening the test
        // process's own temporary-directory permissions.
        using var store = new AtomicJsonConfigurationStore(directory.Path);

        var exception = await Assert.ThrowsAsync<ConfigurationValidationException>(
            () => store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Unable to load", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionOneConfiguration_MigratesToKeepAt100WithoutMintingQualification()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "controller.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "commissioned": true,
              "mode": "manual",
              "manualSpeedPercent": 66,
              "curve": {
                "points": [
                  { "temperatureCelsius": 0, "fanPercent": 36 },
                  { "temperatureCelsius": 100, "fanPercent": 100 }
                ]
              }
            }
            """,
            TestContext.Current.CancellationToken);
        using var store = new AtomicJsonConfigurationStore(path);

        var configuration = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ControllerConfiguration.CurrentSchemaVersion, configuration.SchemaVersion);
        Assert.True(configuration.Commissioned);
        Assert.Equal(ControlMode.Manual, configuration.Mode);
        Assert.Equal(66, configuration.ManualSpeedPercent);
        Assert.Equal(SystemShutdownFanBehavior.KeepAt100, configuration.SystemShutdownFanBehavior);
        Assert.Null(configuration.ShutdownOffQualification);
    }

    [Fact]
    public async Task VersionOneConfiguration_RejectsFieldsThatBelongOnlyToCurrentSchema()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "controller.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "commissioned": false,
              "mode": "auto",
              "manualSpeedPercent": 100,
              "curve": {
                "points": [
                  { "temperatureCelsius": 0, "fanPercent": 36 },
                  { "temperatureCelsius": 100, "fanPercent": 100 }
                ]
              },
              "systemShutdownFanBehavior": "turnOffOnSystemShutdown"
            }
            """,
            TestContext.Current.CancellationToken);
        using var store = new AtomicJsonConfigurationStore(path);

        _ = await Assert.ThrowsAsync<ConfigurationValidationException>(
            () => store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VersionTwoTurnOffConfiguration_MissingOrIncompleteQualificationIsRejected()
    {
        string[] invalidDocuments =
        [
            """
            {
              "schemaVersion": 2,
              "systemShutdownFanBehavior": "turnOffOnSystemShutdown"
            }
            """,
            """
            {
              "schemaVersion": 2,
              "systemShutdownFanBehavior": "turnOffOnSystemShutdown",
              "shutdownOffQualification": {}
            }
            """,
            """
            {
              "schemaVersion": 2,
              "systemShutdownFanBehavior": "turnOffOnSystemShutdown",
              "shutdownOffQualification": {
                "protocolRevision": 1,
                "vendorId": 14154,
                "productId": 45313,
                "firmwareVersion": 5634,
                "usagePage": 65280,
                "usage": 1,
                "featureReportLength": 9
              }
            }
            """,
        ];

        using var directory = new TemporaryDirectory();
        for (var index = 0; index < invalidDocuments.Length; index++)
        {
            var path = Path.Combine(directory.Path, $"controller-{index}.json");
            await File.WriteAllTextAsync(
                path, invalidDocuments[index], TestContext.Current.CancellationToken);
            using var store = new AtomicJsonConfigurationStore(path);

            _ = await Assert.ThrowsAsync<ConfigurationValidationException>(
                () => store.LoadAsync(TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("{ \"commissioned\": true }")]
    [InlineData("{ \"SchemaVersion\": 2, \"commissioned\": true }")]
    [InlineData("{ \"schemaVerzion\": 2, \"commissioned\": true }")]
    [InlineData("{ \"schemaVersion\": null, \"commissioned\": true }")]
    [InlineData("{ \"schemaVersion\": \"2\", \"commissioned\": true }")]
    [InlineData("{ \"schemaVersion\": 1.5, \"commissioned\": true }")]
    public async Task SchemaVersion_MissingMisspelledNullStringOrFractional_IsRejected(string json)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "controller.json");
        await File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
        using var store = new AtomicJsonConfigurationStore(path);

        _ = await Assert.ThrowsAsync<ConfigurationValidationException>(
            () => store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public async Task UnsupportedSchemaVersion_IsRejectedBeforeConfigurationIsTrusted(int schemaVersion)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "controller.json");
        await File.WriteAllTextAsync(
            path,
            $$"""
            {
              "schemaVersion": {{schemaVersion}},
              "commissioned": true
            }
            """,
            TestContext.Current.CancellationToken);
        using var store = new AtomicJsonConfigurationStore(path);

        var exception = await Assert.ThrowsAsync<ConfigurationValidationException>(
            () => store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Unsupported configuration schema", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateSchemaVersion_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "controller.json");
        await File.WriteAllTextAsync(
            path,
            "{ \"schemaVersion\": 1, \"schemaVersion\": 2 }",
            TestContext.Current.CancellationToken);
        using var store = new AtomicJsonConfigurationStore(path);

        _ = await Assert.ThrowsAsync<ConfigurationValidationException>(
            () => store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExistingConfiguration_RejectsUnknownDuplicateWrongCaseAndMissingTopLevelProperties()
    {
        using var directory = new TemporaryDirectory();
        var canonicalPath = Path.Combine(directory.Path, "canonical.json");
        using (var canonicalStore = new AtomicJsonConfigurationStore(canonicalPath))
        {
            await canonicalStore.SaveAsync(
                ControllerConfiguration.CreateDefault(),
                TestContext.Current.CancellationToken);
        }

        var canonical = await File.ReadAllTextAsync(canonicalPath, TestContext.Current.CancellationToken);
        var documents = new[]
        {
            canonical.TrimEnd()[..^1] + ",\n  \"unknown\": true\n}",
            canonical.Replace(
                "\"commissioned\": false",
                "\"commissioned\": true,\n  \"commissioned\": false",
                StringComparison.Ordinal),
            canonical.Replace("\"manualSpeedPercent\"", "\"ManualSpeedPercent\"", StringComparison.Ordinal),
            canonical.Replace("\"manualSpeedPercent\": 100,", string.Empty, StringComparison.Ordinal),
        };

        await AssertDocumentsRejectedAsync(directory.Path, documents);
    }

    [Fact]
    public async Task ExistingConfiguration_RejectsUnknownDuplicateWrongCaseAndInvalidCurveMembers()
    {
        using var directory = new TemporaryDirectory();
        var canonicalPath = Path.Combine(directory.Path, "canonical.json");
        using (var canonicalStore = new AtomicJsonConfigurationStore(canonicalPath))
        {
            await canonicalStore.SaveAsync(
                ControllerConfiguration.CreateDefault(),
                TestContext.Current.CancellationToken);
        }

        var canonical = await File.ReadAllTextAsync(canonicalPath, TestContext.Current.CancellationToken);
        var documents = new[]
        {
            canonical.Replace(
                "\"points\": [",
                "\"unknown\": true,\n    \"points\": [",
                StringComparison.Ordinal),
            canonical.Replace(
                "\"points\": [",
                "\"points\": [],\n    \"points\": [",
                StringComparison.Ordinal),
            canonical.Replace("\"points\"", "\"Points\"", StringComparison.Ordinal),
            canonical.Replace(
                "\"temperatureCelsius\": 0,",
                "\"temperatureCelsius\": 1,\n        \"temperatureCelsius\": 0,",
                StringComparison.Ordinal),
            canonical.Replace(
                "\"temperatureCelsius\": 0",
                "\"TemperatureCelsius\": 0",
                StringComparison.Ordinal),
            canonical.Replace(
                "\"fanPercent\": 36",
                "\"fanPercent\": null",
                StringComparison.Ordinal),
        };

        await AssertDocumentsRejectedAsync(directory.Path, documents);
    }

    [Fact]
    public async Task ExistingConfiguration_RejectsNullWrongPrimitiveTypesAndNonCanonicalEnums()
    {
        using var directory = new TemporaryDirectory();
        var canonicalPath = Path.Combine(directory.Path, "canonical.json");
        using (var canonicalStore = new AtomicJsonConfigurationStore(canonicalPath))
        {
            await canonicalStore.SaveAsync(
                ControllerConfiguration.CreateDefault(),
                TestContext.Current.CancellationToken);
        }

        var canonical = await File.ReadAllTextAsync(canonicalPath, TestContext.Current.CancellationToken);
        var documents = new[]
        {
            MutateDocument(canonical, root => root["commissioned"] = null),
            MutateDocument(canonical, root => root["mode"] = 0),
            MutateDocument(canonical, root => root["mode"] = "Auto"),
            MutateDocument(canonical, root => root["manualSpeedPercent"] = "100"),
            MutateDocument(canonical, root => root["curve"] = null),
            MutateDocument(canonical, root => root["systemShutdownFanBehavior"] = 0),
            MutateDocument(canonical, root => root["systemShutdownFanBehavior"] = "KeepAt100"),
            MutateDocument(canonical, root => root["curve"]!["points"] = null),
            MutateDocument(canonical, root => root["curve"]!["points"]!.AsArray()[0] = null),
        };

        await AssertDocumentsRejectedAsync(directory.Path, documents);
    }

    [Fact]
    public async Task ExistingConfiguration_RejectsUnknownDuplicateWrongCaseAndInvalidShutdownQualificationMembers()
    {
        using var directory = new TemporaryDirectory();
        var canonicalPath = Path.Combine(directory.Path, "canonical-qualified.json");
        using (var canonicalStore = new AtomicJsonConfigurationStore(canonicalPath))
        {
            await canonicalStore.SaveAsync(
                CreateQualifiedConfiguration(),
                TestContext.Current.CancellationToken);
        }

        var canonical = await File.ReadAllTextAsync(canonicalPath, TestContext.Current.CancellationToken);
        var documents = new[]
        {
            canonical.Replace(
                "\"protocolRevision\": 1,",
                "\"unknown\": true,\n    \"protocolRevision\": 1,",
                StringComparison.Ordinal),
            canonical.Replace(
                "\"protocolRevision\": 1,",
                "\"protocolRevision\": 2,\n    \"protocolRevision\": 1,",
                StringComparison.Ordinal),
            canonical.Replace(
                "\"protocolRevision\"",
                "\"ProtocolRevision\"",
                StringComparison.Ordinal),
            canonical.Replace(
                "\"shutdownOffReadbackConfirmed\": true",
                "\"shutdownOffReadbackConfirmed\": null",
                StringComparison.Ordinal),
        };

        await AssertDocumentsRejectedAsync(directory.Path, documents);
    }

    [Fact]
    public async Task ExistingConfiguration_RejectsInvalidShutdownQualificationContainerAndPrimitiveTypes()
    {
        using var directory = new TemporaryDirectory();
        var canonicalPath = Path.Combine(directory.Path, "canonical-qualified.json");
        using (var canonicalStore = new AtomicJsonConfigurationStore(canonicalPath))
        {
            await canonicalStore.SaveAsync(
                CreateQualifiedConfiguration(),
                TestContext.Current.CancellationToken);
        }

        var canonical = await File.ReadAllTextAsync(canonicalPath, TestContext.Current.CancellationToken);
        var documents = new[]
        {
            MutateDocument(canonical, root => root["shutdownOffQualification"] = new JsonArray()),
            MutateDocument(canonical,
                root => root["shutdownOffQualification"]!["vendorId"] = "14154"),
            MutateDocument(canonical,
                root => root["shutdownOffQualification"]!["shutdownOffReadbackConfirmed"] = "true"),
            MutateDocument(canonical,
                root => root["shutdownOffQualification"]!["qualifiedAtUtc"] = 0),
        };

        await AssertDocumentsRejectedAsync(directory.Path, documents);
    }

    [Fact]
    public async Task QualifiedShutdownConfiguration_RoundTripsItsExactEvidence()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "controller.json");
        using var store = new AtomicJsonConfigurationStore(path);
        var expected = CreateQualifiedConfiguration();

        await store.SaveAsync(expected, TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected.SystemShutdownFanBehavior, loaded.SystemShutdownFanBehavior);
        Assert.Equal(expected.ShutdownOffQualification, loaded.ShutdownOffQualification);
    }

    [Fact]
    public async Task OversizedConfiguration_IsRejectedBeforeJsonParsing()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "controller.json");
        await File.WriteAllTextAsync(
            path,
            new string(' ', (64 * 1024) + 1),
            TestContext.Current.CancellationToken);
        using var store = new AtomicJsonConfigurationStore(path);

        var exception = await Assert.ThrowsAsync<ConfigurationValidationException>(
            () => store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Contains("between 1 and 65536 bytes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAndAtomicallyReplacesExistingConfiguration()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "controller.json");
        using var store = new AtomicJsonConfigurationStore(path);
        var first = new ControllerConfiguration
        {
            Commissioned = true,
            Mode = ControlMode.Manual,
            ManualSpeedPercent = 66,
            Curve = new FanCurve([new(0, 20), new(100, 100)]),
        };
        await store.SaveAsync(first, TestContext.Current.CancellationToken);

        var second = first with { ManualSpeedPercent = 77 };
        await store.SaveAsync(second, TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(loaded.Commissioned);
        Assert.Equal(ControlMode.Manual, loaded.Mode);
        Assert.Equal(77, loaded.ManualSpeedPercent);
        Assert.Equal(60, loaded.Curve.Interpolate(50));
        Assert.Contains("\"mode\": \"manual\"", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task PowerLossBeforeReplace_AbandonedPartialTempCannotReplaceLastDurableConfiguration()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "controller.json");
        using var store = new AtomicJsonConfigurationStore(path);
        await store.SaveAsync(
            ControllerConfiguration.CreateDefault() with { ManualSpeedPercent = 73 },
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, ".controller.json.interrupted.tmp"),
            "{\"schemaVersion\":2,\"manualSpeedPercent\":",
            TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(73, loaded.ManualSpeedPercent);
        Assert.Single(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task CommitFailure_PreservesOldConfigurationAndCleansPreparedTemp()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "controller.json");
        using (var initialStore = new AtomicJsonConfigurationStore(path))
        {
            await initialStore.SaveAsync(
                ControllerConfiguration.CreateDefault() with { ManualSpeedPercent = 64 },
                TestContext.Current.CancellationToken);
        }

        using (var failingStore = new AtomicJsonConfigurationStore(path, new ThrowingCommitter()))
        {
            _ = await Assert.ThrowsAsync<IOException>(() => failingStore.SaveAsync(
                ControllerConfiguration.CreateDefault() with { ManualSpeedPercent = 88 },
                TestContext.Current.CancellationToken));
        }

        using var readStore = new AtomicJsonConfigurationStore(path);
        var loaded = await readStore.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(64, loaded.ManualSpeedPercent);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task CleanupFailure_ReleasesGateAndPreservesOldConfiguration()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "controller.json");
        using (var initialStore = new AtomicJsonConfigurationStore(path))
        {
            await initialStore.SaveAsync(
                ControllerConfiguration.CreateDefault() with { ManualSpeedPercent = 64 },
                TestContext.Current.CancellationToken);
        }

        using var committer = new LockingFailOnceCommitter();
        using var store = new AtomicJsonConfigurationStore(path, committer);
        _ = await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(
            ControllerConfiguration.CreateDefault() with { ManualSpeedPercent = 88 },
            TestContext.Current.CancellationToken));

        // A real Windows sharing violation in cleanup must not strand the store's
        // semaphore. Release only the fake external handle before retrying the same store.
        Assert.NotNull(committer.FailedTemporaryPath);
        Assert.True(File.Exists(committer.FailedTemporaryPath));
        committer.ReleaseTemporaryFile();
        using var retryTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        retryTimeout.CancelAfter(TimeSpan.FromSeconds(5));

        var original = await store.LoadAsync(retryTimeout.Token);
        Assert.Equal(64, original.ManualSpeedPercent);
        await store.SaveAsync(original with { ManualSpeedPercent = 77 }, retryTimeout.Token);
        var updated = await store.LoadAsync(retryTimeout.Token);
        Assert.Equal(77, updated.ManualSpeedPercent);

        File.Delete(committer.FailedTemporaryPath);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task CorruptOrInvalidConfiguration_IsReportedInsteadOfSilentlyTrusted()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "controller.json");
        await File.WriteAllTextAsync(path, "{ this is not json", TestContext.Current.CancellationToken);
        using var store = new AtomicJsonConfigurationStore(path);

        _ = await Assert.ThrowsAsync<ConfigurationValidationException>(
            () => store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidConfiguration_IsRejectedBeforeAnyFileIsWritten()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "controller.json");
        using var store = new AtomicJsonConfigurationStore(path);
        var invalid = new ControllerConfiguration { ManualSpeedPercent = 1 };

        _ = await Assert.ThrowsAsync<ConfigurationValidationException>(
            () => store.SaveAsync(invalid, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    private static ControllerConfiguration CreateQualifiedConfiguration() =>
        ControllerConfiguration.CreateDefault() with
        {
            SystemShutdownFanBehavior = SystemShutdownFanBehavior.TurnOffOnSystemShutdown,
            ShutdownOffQualification = new SystemShutdownOffQualification
            {
                ProtocolRevision = SystemShutdownOffQualification.CurrentProtocolRevision,
                VendorId = 0x374A,
                ProductId = 0xB101,
                FirmwareVersion = 0x1602,
                UsagePage = 0xFF00,
                Usage = 0x0001,
                FeatureReportLength = 9,
                ShutdownOffReadbackConfirmed = true,
                StartupOnAt100ReadbackConfirmed = true,
                LightingStatePreserved = true,
                QualifiedAtUtc = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero),
            },
        };

    private static async Task AssertDocumentsRejectedAsync(
        string directory,
        IReadOnlyList<string> documents)
    {
        for (var index = 0; index < documents.Count; index++)
        {
            var path = Path.Combine(directory, $"rejected-{index}.json");
            await File.WriteAllTextAsync(path, documents[index], TestContext.Current.CancellationToken);
            using var store = new AtomicJsonConfigurationStore(path);

            _ = await Assert.ThrowsAsync<ConfigurationValidationException>(
                () => store.LoadAsync(TestContext.Current.CancellationToken));
        }
    }

    private static string MutateDocument(string json, Action<JsonObject> mutation)
    {
        var root = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidDataException("The canonical test configuration was not an object.");
        mutation(root);
        return root.ToJsonString();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"V12UltraController.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class ThrowingCommitter : IAtomicConfigurationCommitter
    {
        public void Commit(string temporaryPath, string destinationPath) =>
            throw new IOException("Injected atomic replacement failure.");
    }

    private sealed class LockingFailOnceCommitter : IAtomicConfigurationCommitter, IDisposable
    {
        private FileStream? lockedTemporaryFile;
        public string? FailedTemporaryPath { get; private set; }

        public void Commit(string temporaryPath, string destinationPath)
        {
            if (FailedTemporaryPath is null)
            {
                lockedTemporaryFile = new FileStream(
                    temporaryPath, FileMode.Open, FileAccess.Read, FileShare.None);
                FailedTemporaryPath = temporaryPath;
                throw new IOException("Injected atomic replacement failure with a locked temporary file.");
            }

            FileSystemAtomicConfigurationCommitter.Instance.Commit(temporaryPath, destinationPath);
        }

        public void ReleaseTemporaryFile()
        {
            lockedTemporaryFile?.Dispose();
            lockedTemporaryFile = null;
        }

        public void Dispose() => ReleaseTemporaryFile();
    }
}
