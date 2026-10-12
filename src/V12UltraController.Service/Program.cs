using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Security;
using V12UltraController.Core.Thermal;
using V12UltraController.Service;

var builder = Host.CreateApplicationBuilder(args);
var servicePaths = new ServicePaths();
var writerLease = new SingleWriterLease(servicePaths);
builder.Services.AddSingleton<ServiceStopIntent>();
builder.Services.AddWindowsService(options => options.ServiceName = ServiceConstants.ServiceName);
builder.Services.ReplaceIfRunningAsWindowsService();
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(5);
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(servicePaths);
builder.Logging.AddRollingJsonFile();
builder.Services.AddSingleton<JsonlAuditLog>();
builder.Services.AddSingleton<ServiceRuntimeStateStore>();
builder.Services.AddSingleton<ISafetyProtectionStore, SafetyProtectionStore>();
builder.Services.AddSingleton<IDeviceStateCacheStore, DeviceStateCacheStore>();
builder.Services.AddSingleton<TakeoverTransitionJournalStore>();
builder.Services.AddSingleton<AuditMutationJournalStore>();
builder.Services.AddSingleton<SafetyOperationAuditJournalStore>();
builder.Services.AddSingleton<IForcedUninstallRecoveryMarkerStore,
    ForcedUninstallRecoveryMarkerStore>();
builder.Services.AddSingleton<IInstallerEnrollmentReader, WindowsRegistryInstallerEnrollmentReader>();
builder.Services.AddSingleton<ISecurePipeFactory, SecurePipeFactory>();
builder.Services.AddSingleton<IMythCoolConflictDetector, MythCoolConflictDetector>();
// The coordinator deliberately retains this OS lease when HID ownership is poisoned.
// Registering a pre-created instance keeps provider teardown from calling Dispose and
// releasing the cross-process lease while an unbounded native call may still own HID.
builder.Services.AddSingleton(writerLease);
builder.Services.AddSingleton<ISchtasksProcessRunner, WindowsSchtasksProcessRunner>();
builder.Services.AddSingleton<IMythCoolScheduledTaskManager, MythCoolScheduledTaskManager>();
builder.Services.AddSingleton<ControlPolicy>();
builder.Services.AddSingleton<IV12DeviceLocator, WindowsV12DeviceLocator>();
// The coordinator owns the thermal source and disposes it through the bounded hardware
// dispatcher. Registering a pre-created instance prevents the container from invoking an
// unbounded direct DisposeAsync after a native hardware-library stall.
builder.Services.AddSingleton<IThermalSource>(new LibreHardwareMonitorThermalSource());
// HID and hardware-monitor calls must never share a worker. A cancellation-insensitive
// LibreHardwareMonitor update must not prevent the service from scheduling ON-at-100 on HID.
builder.Services.AddSingleton<HidIoDispatcher>();
builder.Services.AddSingleton<ThermalIoDispatcher>();
builder.Services.AddSingleton<IConfigurationStore>(provider =>
{
    var paths = provider.GetRequiredService<ServicePaths>();
    return new AtomicJsonConfigurationStore(paths.ConfigurationPath);
});
builder.Services.AddSingleton<ControllerCoordinator>();
builder.Services.AddSingleton<ThermalHistoryRecorder>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ThermalHistoryRecorder>());
builder.Services.AddSingleton<PipeRequestHandler>();
builder.Services.AddSingleton<IProcessTerminator, ProcessTerminator>();
builder.Services.AddSingleton<FatalServiceFailureHandler>();
builder.Services.AddHostedService<ControllerLoopWorker>();
builder.Services.AddHostedService<PipeServerWorker>();
builder.Services.AddHostedService<ControllerWatchdogWorker>();

var host = builder.Build();
try
{
    await host.RunAsync().ConfigureAwait(false);
}
catch (OperationCanceledException) when (
    host.Services.GetRequiredService<IHostApplicationLifetime>()
        .ApplicationStopping.IsCancellationRequested)
{
    // Normal SCM/console lifetime cancellation.
}
catch (Exception exception)
{
    Environment.ExitCode = 7;
    Console.Error.WriteLine($"V12 Ultra Controller terminated unexpectedly: {exception.GetType().Name}");
}
finally
{
    // Keep the process-owned instance rooted through host teardown. The OS closes it at
    // process exit if a poisoned shutdown intentionally refused to release it.
    GC.KeepAlive(writerLease);
}

return Environment.ExitCode;
