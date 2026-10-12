using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed class ServiceStopIntentTests
{
    [Theory]
    [InlineData("OnStop", 1)]
    [InlineData("OnShutdown", 2)]
    public void WindowsServiceLifetime_MarksReasonBeforeStoppingApplication(
        string callback,
        int expectedValue)
    {
        var expected = (ServiceStopReason)expectedValue;
        var intent = new ServiceStopIntent();
        var applicationLifetime = new CapturingApplicationLifetime(intent);
        using var lifetime = CreateLifetime(intent, applicationLifetime);

        InvokeCallback(lifetime, callback);

        Assert.Equal(expected, intent.Reason);
        Assert.Equal(expected, applicationLifetime.ReasonWhenStopApplicationWasCalled);
    }

    [Fact]
    public void FirstTerminalSignalWins()
    {
        var intent = new ServiceStopIntent();

        Assert.True(intent.TrySet(ServiceStopReason.ServiceStop));
        Assert.False(intent.TrySet(ServiceStopReason.SystemShutdown));
        Assert.Equal(ServiceStopReason.ServiceStop, intent.Reason);
    }

    [Fact]
    public async Task ConcurrentTerminalSignals_HaveExactlyOneWinner()
    {
        var intent = new ServiceStopIntent();
        var successfulWrites = 0;
        var tasks = Enumerable.Range(0, 64).Select(index => Task.Run(() =>
        {
            var reason = index % 2 == 0
                ? ServiceStopReason.ServiceStop
                : ServiceStopReason.SystemShutdown;
            if (intent.TrySet(reason))
            {
                Interlocked.Increment(ref successfulWrites);
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(1, successfulWrites);
        Assert.NotEqual(ServiceStopReason.Unknown, intent.Reason);
    }

    [Fact]
    public void UnknownCannotBePromotedToTerminalIntent()
    {
        var intent = new ServiceStopIntent();

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => intent.TrySet(ServiceStopReason.Unknown));
        Assert.Equal(ServiceStopReason.Unknown, intent.Reason);
    }

    [Fact]
    public void Replacement_PreservesWindowsServiceOptionsAndUnrelatedRegistrations()
    {
        var services = new ServiceCollection();
        var marker = new object();
        services.AddSingleton(marker);
        services.AddSingleton<ServiceStopIntent>();
        services.AddSingleton<IHostLifetime, FakeHostLifetime>();
        services.Configure<WindowsServiceLifetimeOptions>(
            options => options.ServiceName = ServiceConstants.ServiceName);

        V12WindowsServiceLifetimeRegistration.ReplaceRegisteredLifetime(services);

        var lifetime = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IHostLifetime));
        Assert.Equal(typeof(V12WindowsServiceLifetime), lifetime.ImplementationType);
        using var provider = services.BuildServiceProvider();
        Assert.Same(marker, provider.GetRequiredService<object>());
        Assert.Equal(
            ServiceConstants.ServiceName,
            provider.GetRequiredService<IOptions<WindowsServiceLifetimeOptions>>().Value.ServiceName);
    }

    private static V12WindowsServiceLifetime CreateLifetime(
        ServiceStopIntent intent,
        IHostApplicationLifetime applicationLifetime) => new(
        intent,
        new FakeHostEnvironment(),
        applicationLifetime,
        NullLoggerFactory.Instance,
        Options.Create(new HostOptions { ShutdownTimeout = TimeSpan.Zero }),
        Options.Create(new WindowsServiceLifetimeOptions { ServiceName = ServiceConstants.ServiceName }));

    private static void InvokeCallback(V12WindowsServiceLifetime lifetime, string methodName)
    {
        var method = typeof(V12WindowsServiceLifetime).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Could not locate {methodName}.");
        _ = method.Invoke(lifetime, null);
    }

    private sealed class CapturingApplicationLifetime(ServiceStopIntent intent) : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public ServiceStopReason ReasonWhenStopApplicationWasCalled { get; private set; } =
            ServiceStopReason.Unknown;

        public void StopApplication()
        {
            ReasonWhenStopApplicationWasCalled = intent.Reason;
            stopping.Cancel();
        }
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "V12UltraController.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
