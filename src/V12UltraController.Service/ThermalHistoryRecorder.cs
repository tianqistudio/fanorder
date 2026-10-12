using System.Globalization;
using System.Text.Json;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Status;

namespace V12UltraController.Service;

/// <summary>Advisory-only worker. Never opens hardware or modifies controller policy.</summary>
internal sealed class ThermalHistoryRecorder(ControllerCoordinator coordinator, ServicePaths paths) : BackgroundService
{
    private const int MaximumSamples = 11000;
    private const long MaximumFileBytes = 2 * 1024 * 1024;
    private const string DegradedHistoryWarning = "部分历史损坏或过量，建议仅供预览；请导出排查后再应用。";
    private readonly object sync = new();
    private readonly List<ThermalObservation> samples = [];
    private string? warning = "历史记录正在初始化。";
    private readonly string directory = Path.Combine(paths.DataDirectory, "thermal-history-v1");

    public CurveAdvice Analyze(FanCurve baseline)
    {
        ThermalObservation[] copy;
        string? currentWarning;
        lock (sync) { copy = samples.ToArray(); currentWarning = warning; }
        return CurveAdvisor.Analyze(copy, baseline, DateTimeOffset.UtcNow) with { StorageWarning = currentWarning };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Isolate disk I/O from the control loop and its gate/watchdog.
        await Task.Yield();
        var loadHealthy = true;
        try
        {
            AssertPlain(paths.DataDirectory);
            Directory.CreateDirectory(directory);
            AssertPlain(directory);
            var loaded = await LoadHistoryAsync(directory, DateTimeOffset.UtcNow, stoppingToken);
            loadHealthy = loaded.Healthy;
            lock (sync)
            {
                samples.AddRange(loaded.Samples);
                warning = loadHealthy ? null : DegradedHistoryWarning;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            lock (sync) warning = "本地历史不可用，未影响风扇控制：" + ex.GetType().Name;
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var sample = CurveAdvisor.Capture(now, coordinator.GetStatus(), coordinator.GetCurve());
                AssertPlain(directory);
                var path = Path.Combine(directory, now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
                if (File.Exists(path))
                {
                    AssertPlain(path);
                    if (new FileInfo(path).Length > MaximumFileBytes) throw new IOException("History quota reached.");
                }
                await File.AppendAllTextAsync(path, JsonSerializer.Serialize(sample) + Environment.NewLine, stoppingToken);
                lock (sync)
                {
                    samples.Add(sample);
                    samples.RemoveAll(s => s.Time < now.AddDays(-7) || s.Time > now);
                    if (samples.Count > MaximumSamples) samples.RemoveRange(0, samples.Count - MaximumSamples);
                    warning = loadHealthy ? null : DegradedHistoryWarning;
                }
                foreach (var old in Directory.EnumerateFiles(directory, "*.jsonl"))
                    if (TryDate(old, out var date) && date < now.UtcDateTime.Date.AddDays(-7))
                    { AssertPlain(old); File.Delete(old); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { lock (sync) warning = "历史记录写入失败，建议仅供预览：" + ex.GetType().Name; }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal static async Task<(ThermalObservation[] Samples, bool Healthy)> LoadHistoryAsync(
        string directory, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var earliest = now.AddDays(-7);
        var firstDay = earliest.UtcDateTime.Date;
        var lastDay = now.UtcDateTime.Date;
        var healthy = true;
        long sequence = 0;
        // Keep the newest observations independently of filesystem enumeration and line order.
        // Negative sequence retains the first occurrence when timestamps are exactly equal.
        var retained = new PriorityQueue<ThermalObservation, (DateTimeOffset Time, long Order)>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryDate(file, out var day) || day < firstDay || day > lastDay) continue;
            AssertPlain(file);
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan));
            if (reader.BaseStream.Length > MaximumFileBytes)
                throw new InvalidDataException("History file too large.");
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var sample = JsonSerializer.Deserialize<ThermalObservation>(line);
                    if (sample is null) { healthy = false; continue; }
                    if (sample.Time < earliest || sample.Time > now) continue;
                    var priority = (sample.Time, -sequence++);
                    if (retained.Count < MaximumSamples) retained.Enqueue(sample, priority);
                    else
                    {
                        retained.EnqueueDequeue(sample, priority);
                        healthy = false;
                    }
                }
                catch (JsonException) { healthy = false; }
            }
        }
        // Publish only a complete bounded snapshot; requests during startup see no partial history.
        return (retained.UnorderedItems.OrderBy(item => item.Priority.Time)
            .ThenByDescending(item => item.Priority.Order).Select(item => item.Element).ToArray(), healthy);
    }

    private static bool TryDate(string path, out DateTime date) => DateTime.TryParseExact(
        Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    private static void AssertPlain(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("History reparse path refused.");
    }
}
