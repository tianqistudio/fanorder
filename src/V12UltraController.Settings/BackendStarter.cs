using System.Diagnostics;
using System.IO;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Settings;

internal interface IBackendStarter
{
    Task StartAsync(CancellationToken cancellationToken);
}

internal sealed class WindowsBackendStarter : IBackendStarter
{
    // No user-supplied executable, service name, arguments or startup mode.
    internal static ProcessStartInfo CreateStartInfo()
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var info = new ProcessStartInfo(Path.Combine(systemDirectory, "sc.exe"))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = systemDirectory,
        };
        info.ArgumentList.Add("start");
        info.ArgumentList.Add(PipeProtocol.ServiceName);
        return info;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(CreateStartInfo())
            ?? throw new IOException("Windows 未创建后台启动请求。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        // 1056: already running. A successful start request is not pipe readiness.
        if (process.ExitCode is not (0 or 1056))
            throw new IOException($"Windows 启动后台失败（{process.ExitCode}）；请确认程序已完整安装，服务没有被禁用。");
    }
}
