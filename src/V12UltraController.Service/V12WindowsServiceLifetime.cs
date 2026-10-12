using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace V12UltraController.Service;

internal sealed class V12WindowsServiceLifetime(
    ServiceStopIntent stopIntent,
    IHostEnvironment environment,
    IHostApplicationLifetime applicationLifetime,
    ILoggerFactory loggerFactory,
    IOptions<HostOptions> hostOptions,
    IOptions<WindowsServiceLifetimeOptions> windowsServiceOptions)
    : WindowsServiceLifetime(
        environment,
        applicationLifetime,
        loggerFactory,
        hostOptions,
        windowsServiceOptions)
{
    protected override void OnStop()
    {
        stopIntent.TrySet(ServiceStopReason.ServiceStop);
        base.OnStop();
    }

    protected override void OnShutdown()
    {
        stopIntent.TrySet(ServiceStopReason.SystemShutdown);
        base.OnShutdown();
    }
}

internal static class V12WindowsServiceLifetimeRegistration
{
    public static void ReplaceIfRunningAsWindowsService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (WindowsServiceHelpers.IsWindowsService())
        {
            ReplaceRegisteredLifetime(services);
        }
    }

    internal static void ReplaceRegisteredLifetime(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.RemoveAll<IHostLifetime>();
        services.AddSingleton<IHostLifetime, V12WindowsServiceLifetime>();
    }
}
