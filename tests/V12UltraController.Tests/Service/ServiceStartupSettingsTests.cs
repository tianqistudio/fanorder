using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed class ServiceStartupSettingsTests
{
    private static readonly PipePeerIdentity Peer = new("S-1-5-21-111-222-333-1001", "Settings");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ToggleOnlyChangesStartupRegistrationAndConfirmsReadback(bool enabled)
    {
        var registration = new FakeRegistration { Enabled = !enabled };
        var audit = new FakeAudit();
        var settings = new ServiceStartupSettings(registration, audit);
        var result = await settings.SetAsync(enabled, Peer, TestContext.Current.CancellationToken);
        Assert.Equal(enabled, result.Enabled);
        Assert.True(result.ReadbackConfirmed);
        Assert.Equal(new[] { enabled }, registration.Writes);
        Assert.Equal(new[] { "intent", "success" }, audit.Results);
        Assert.Equal(result.Enabled, settings.Read().Enabled);
    }

    [Fact]
    public async Task IntentAuditFailureDoesNotChangeSystemSetting()
    {
        var registration = new FakeRegistration();
        var settings = new ServiceStartupSettings(registration, new FakeAudit { FailResult = "intent" });
        await Assert.ThrowsAsync<IOException>(() => settings.SetAsync(true, Peer, CancellationToken.None));
        Assert.Empty(registration.Writes);
    }

    [Fact]
    public async Task CompletionAuditFailureRestoresPreviousSetting()
    {
        var registration = new FakeRegistration();
        var audit = new FakeAudit { FailResult = "success" };
        var settings = new ServiceStartupSettings(registration, audit);
        await Assert.ThrowsAsync<IOException>(() => settings.SetAsync(true, Peer, CancellationToken.None));
        Assert.False(registration.Enabled);
        Assert.Equal(new[] { true, false }, registration.Writes);
        Assert.Contains("compensated", audit.Results);
    }

    [Fact]
    public async Task NoOpMakesNoWriteOrAudit()
    {
        var registration = new FakeRegistration();
        var audit = new FakeAudit();
        var result = await new ServiceStartupSettings(registration, audit).SetAsync(false, Peer, CancellationToken.None);
        Assert.False(result.Changed);
        Assert.Empty(registration.Writes);
        Assert.Empty(audit.Results);
    }

    [Fact]
    public async Task IgnoredSystemWriteNeverReturnsSuccess()
    {
        var registration = new FakeRegistration { IgnoreWrites = true };
        var audit = new FakeAudit();
        await Assert.ThrowsAsync<IOException>(() => new ServiceStartupSettings(registration, audit)
            .SetAsync(true, Peer, CancellationToken.None));
        Assert.False(registration.Enabled);
        Assert.DoesNotContain("success", audit.Results);
        Assert.Contains("compensated", audit.Results);
    }

    [Fact]
    public async Task PreCancelledRequestHasNoSideEffects()
    {
        var registration = new FakeRegistration();
        var audit = new FakeAudit();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ServiceStartupSettings(registration, audit)
            .SetAsync(true, Peer, cancellation.Token));
        Assert.Empty(registration.Writes);
        Assert.Empty(audit.Results);
    }

    private sealed class FakeRegistration : IServiceStartupRegistration
    {
        public bool Enabled { get; set; }
        public bool IgnoreWrites { get; init; }
        public List<bool> Writes { get; } = [];
        public bool ReadEnabled() => Enabled;
        public void WriteEnabled(bool enabled) { Writes.Add(enabled); if (!IgnoreWrites) Enabled = enabled; }
    }

    private sealed class FakeAudit() : JsonlAuditLog(new ServicePaths(Path.Combine(Path.GetTempPath(), "v12-unused-fake-startup-audit")), TimeProvider.System)
    {
        public string? FailResult { get; init; }
        public List<string> Results { get; } = [];
        public override Task WriteAsync(string eventName, string result, string? callerSid = null, string? source = null,
            object? details = null, CancellationToken cancellationToken = default, Guid? transactionId = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("startup.set", eventName);
            Assert.Equal(Peer.CallerSid, callerSid);
            Assert.NotNull(transactionId);
            if (result == FailResult) throw new IOException("Synthetic audit failure");
            Results.Add(result);
            return Task.CompletedTask;
        }
    }
}
