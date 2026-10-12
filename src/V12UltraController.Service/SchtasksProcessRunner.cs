using System.Diagnostics;
using System.Text;

namespace V12UltraController.Service;

internal sealed record SchtasksProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal enum SchtasksCommandKind
{
    Query,
    Mutation,
}

internal interface ISchtasksProcessRunner
{
    Task<SchtasksProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        SchtasksCommandKind kind,
        CancellationToken cancellationToken);
}

internal sealed class ScheduledTaskCommandOwnershipUncertainException : IOException
{
    public ScheduledTaskCommandOwnershipUncertainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class WindowsSchtasksProcessRunner : ISchtasksProcessRunner
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TerminationConfirmationTimeout = TimeSpan.FromSeconds(2);

    public async Task<SchtasksProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        SchtasksCommandKind kind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "schtasks.exe"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start schtasks.exe.");
        }

        // These reads deliberately have no caller token. On cancellation we first terminate
        // and confirm the process, then drain the now-closed pipes with an independent bound.
        // A caller token must never leave schtasks alive merely because pipe draining stopped.
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        // A mutating command has crossed a durable write-ahead journal barrier before it
        // reaches this method. Once the child starts, caller cancellation is deliberately not
        // linked: the manager must wait for a definite process result and task readback. Query
        // commands remain caller-cancellable because they cannot change Task Scheduler state.
        using var commandDeadline = kind == SchtasksCommandKind.Mutation
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        commandDeadline.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(commandDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            var exitConfirmed = TryHasExited(process);
            if (!exitConfirmed)
            {
                exitConfirmed = await TryKillAndConfirmExitAsync(process).ConfigureAwait(false);
            }

            if (kind == SchtasksCommandKind.Mutation)
            {
                ObserveEventually(standardOutput);
                ObserveEventually(standardError);
                throw new ScheduledTaskCommandOwnershipUncertainException(
                    exitConfirmed
                        ? "A mutating schtasks.exe command exceeded its deadline. The child exited, " +
                          "but an already-submitted Task Scheduler RPC cannot be proven absent."
                        : "A mutating schtasks.exe command exceeded its deadline and the child " +
                          "process could not be proven terminated.",
                    exception);
            }

            if (!exitConfirmed)
            {
                ObserveEventually(standardOutput);
                ObserveEventually(standardError);
                throw new IOException(
                    "A read-only schtasks.exe query could not be proven terminated.",
                    exception);
            }

            // Exit is now proven. Drain redirected streams before preserving the original
            // cancellation/timeout meaning. No later task mutation can arrive from this child.
            _ = await DrainAfterExitAsync(standardOutput, standardError).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "The schtasks.exe command was cancelled after its process exit was confirmed.",
                    exception,
                    cancellationToken);
            }

            throw new TimeoutException("schtasks.exe did not finish within 15 seconds.", exception);
        }

        var output = await DrainAfterExitAsync(standardOutput, standardError).ConfigureAwait(false);
        return new SchtasksProcessResult(process.ExitCode, output.StandardOutput, output.StandardError);
    }

    private static async Task<bool> TryKillAndConfirmExitAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (TryHasExited(process))
        {
            return true;
        }
        catch
        {
            return TryHasExited(process);
        }

        using var confirmationDeadline =
            new CancellationTokenSource(TerminationConfirmationTimeout);
        try
        {
            await process.WaitForExitAsync(confirmationDeadline.Token).ConfigureAwait(false);
            return TryHasExited(process);
        }
        catch (OperationCanceledException)
        {
            return TryHasExited(process);
        }
        catch
        {
            return TryHasExited(process);
        }
    }

    private static bool TryHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(string StandardOutput, string StandardError)> DrainAfterExitAsync(
        Task<string> standardOutput,
        Task<string> standardError)
    {
        using var drainDeadline = new CancellationTokenSource(TerminationConfirmationTimeout);
        await Task.WhenAll(standardOutput, standardError)
            .WaitAsync(drainDeadline.Token)
            .ConfigureAwait(false);
        return (await standardOutput.ConfigureAwait(false), await standardError.ConfigureAwait(false));
    }

    private static void ObserveEventually(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
