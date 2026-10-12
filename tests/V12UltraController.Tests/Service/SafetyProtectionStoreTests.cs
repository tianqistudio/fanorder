using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed class SafetyProtectionStoreTests
{
    [Theory]
    [InlineData("{")]
    [InlineData("{\"SchemaVersion\":1}")]
    [InlineData("{\"SchemaVersion\":1,\"Disabled\":true,\"Disabled\":false,\"DisabledAt\":null,\"CallerSid\":null}")]
    [InlineData("{\"SchemaVersion\":1,\"Disabled\":true,\"DisabledAt\":null,\"CallerSid\":null}")]
    [InlineData("{\"SchemaVersion\":2,\"Disabled\":false,\"DisabledAt\":null,\"CallerSid\":null}")]
    [InlineData("{\"SchemaVersion\":1,\"Disabled\":false,\"DisabledAt\":\"2026-09-07T00:00:00Z\",\"CallerSid\":null}")]
    public async Task IncompleteCorruptOrAmbiguousSafetyIntentCannotDefaultToEnabled(string raw)
    {
        var directory = Path.Combine(Path.GetTempPath(), "v12-safety-marker-" + Guid.NewGuid().ToString("N"));
        var paths = new ServicePaths(directory);
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(paths.SafetyProtectionPath, raw);
            await Assert.ThrowsAnyAsync<Exception>(() => new SafetyProtectionStore(paths).LoadAsync(CancellationToken.None));
            Assert.Equal(raw, await File.ReadAllTextAsync(paths.SafetyProtectionPath));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task SafetyIntentHasIndependentAtomicRoundTripAndMissingDefaultsToNone()
    {
        var directory = Path.Combine(Path.GetTempPath(), "v12-safety-marker-" + Guid.NewGuid().ToString("N"));
        var paths = new ServicePaths(directory);
        var store = new SafetyProtectionStore(paths);
        try
        {
            Assert.Null(await store.LoadAsync(CancellationToken.None));
            var intent = new SafetyProtectionIntent(1, true, DateTimeOffset.UtcNow, "fake-owner");
            await store.SaveAsync(intent, CancellationToken.None);
            Assert.Equal(intent, await store.LoadAsync(CancellationToken.None));
            Assert.False(File.Exists(paths.ConfigurationPath));
            Assert.False(File.Exists(paths.AuditMutationPendingPath));
            Assert.Single(Directory.GetFiles(directory));
            var resumed = new SafetyProtectionIntent(1, false, null, "fake-owner");
            await store.SaveAsync(resumed, CancellationToken.None);
            Assert.Equal(resumed, await store.LoadAsync(CancellationToken.None));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
