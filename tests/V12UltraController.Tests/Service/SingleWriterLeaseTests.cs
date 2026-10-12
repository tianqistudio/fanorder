using Microsoft.Extensions.DependencyInjection;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed class SingleWriterLeaseTests
{
    [Fact]
    public async Task Lease_IsExclusiveAndCanBeReleasedAfterAsyncContinuation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"v12-lease-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "device-writer.lock");
        try
        {
            using var first = new SingleWriterLease(path);
            using var second = new SingleWriterLease(path);

            Assert.True(first.TryAcquire());
            Assert.False(second.TryAcquire());

            await Task.Yield();
            first.Release();

            Assert.True(second.TryAcquire());
            second.Release();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void PreCreatedLease_IsNotDisposedByServiceProviderTeardown()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"v12-lease-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "device-writer.lock");
        try
        {
            using var processOwned = new SingleWriterLease(path);
            using var competitor = new SingleWriterLease(path);
            var services = new ServiceCollection();
            services.AddSingleton(processOwned);
            var provider = services.BuildServiceProvider();

            Assert.Same(processOwned, provider.GetRequiredService<SingleWriterLease>());
            Assert.True(processOwned.TryAcquire());
            provider.Dispose();

            Assert.True(processOwned.IsHeld);
            Assert.False(competitor.TryAcquire());
            processOwned.Release();
            Assert.True(competitor.TryAcquire());
            competitor.Release();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
