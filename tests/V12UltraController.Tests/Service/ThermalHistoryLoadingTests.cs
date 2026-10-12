using System.Globalization;
using System.Text;
using System.Text.Json;
using V12UltraController.Core.Status;
using V12UltraController.Service;

namespace V12UltraController.Tests.Service;

public sealed partial class ControllerCoordinatorTests
{
    [Fact]
    public async Task ThermalHistoryLoaderRetainsNewestSamplesAcrossReverseOrderedDaysAndRows()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var observations = Enumerable.Range(0, 12000).Select(index => new ThermalObservation(
            now.AddMinutes(-1).AddSeconds(-30 * index), 50, 60, true, false, "synthetic-curve")).ToArray();
        var originals = new Dictionary<string, byte[]>();
        // Create newest files first and put each file's newest observations first. Retention
        // must depend on timestamps, regardless of filesystem enumeration and append order.
        foreach (var day in observations.GroupBy(sample => sample.Time.UtcDateTime.Date))
        {
            var path = await WriteThermalHistoryInputAsync(fixture, new DateTimeOffset(day.Key),
                string.Concat(day.Select(sample => JsonSerializer.Serialize(sample) + "\n")));
            originals[path] = await File.ReadAllBytesAsync(path);
        }

        var loaded = await ThermalHistoryRecorder.LoadHistoryAsync(
            ThermalHistoryInputDirectory(fixture), now, CancellationToken.None);

        Assert.False(loaded.Healthy);
        Assert.Equal(11000, loaded.Samples.Length);
        Assert.Equal(observations.Take(11000).Reverse(), loaded.Samples);
        foreach (var (path, original) in originals)
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        AssertThermalHistoryDidNotUseHardware(fixture);
    }

    [Fact]
    public async Task ThermalHistoryLoaderFiltersDenseExpiredRowsAndUsesInclusiveFixedTimeBoundaries()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var earliest = now.AddDays(-7);
        var observations = new[]
        {
            new ThermalObservation(now.AddTicks(1), 50, 60, true, false, "future"),
            new ThermalObservation(now, 50, 60, true, false, "newest"),
            new ThermalObservation(earliest.AddTicks(-1), 50, 60, true, false, "expired"),
            new ThermalObservation(earliest, 50, 60, true, false, "oldest"),
        };
        var content = new StringBuilder();
        // Missing Time deserializes as an expired default value. It must never accumulate
        // in the bounded staging collection before the time-window filter runs.
        for (var index = 0; index < 100000; index++) content.Append("{}\n");
        foreach (var observation in observations)
            content.Append(JsonSerializer.Serialize(observation)).Append('\n');
        var path = await WriteThermalHistoryInputAsync(fixture, now, content.ToString());
        var original = await File.ReadAllBytesAsync(path);

        var loaded = await ThermalHistoryRecorder.LoadHistoryAsync(
            ThermalHistoryInputDirectory(fixture), now, CancellationToken.None);

        Assert.True(loaded.Healthy);
        Assert.Equal(new[] { observations[3], observations[1] }, loaded.Samples);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        AssertThermalHistoryDidNotUseHardware(fixture);
    }

    [Fact]
    public async Task ThermalHistoryLoaderKeepsValidRowsAroundCorruptionAndPreservesInput()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var first = new ThermalObservation(now.AddMinutes(-2), 50, 60, true, false, "first");
        var second = first with { Time = now.AddMinutes(-1), CurveId = "second" };
        var content = JsonSerializer.Serialize(first) + "\n{incomplete\nnull\n" +
            JsonSerializer.Serialize(second) + "\n";
        var path = await WriteThermalHistoryInputAsync(fixture, now, content);
        var original = await File.ReadAllBytesAsync(path);

        var loaded = await ThermalHistoryRecorder.LoadHistoryAsync(
            ThermalHistoryInputDirectory(fixture), now, CancellationToken.None);

        Assert.False(loaded.Healthy);
        Assert.Equal(new[] { first, second }, loaded.Samples);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        AssertThermalHistoryDidNotUseHardware(fixture);
    }

    [Fact]
    public async Task ThermalHistoryLoaderCancellationPreservesInputAndDoesNotUseHardware()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var path = await WriteThermalHistoryInputAsync(fixture, now, "{}\n");
        var original = await File.ReadAllBytesAsync(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ThermalHistoryRecorder.LoadHistoryAsync(
            ThermalHistoryInputDirectory(fixture), now, cancellation.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        AssertThermalHistoryDidNotUseHardware(fixture);
    }

    [Fact]
    public async Task ThermalHistoryLoaderRejectsOversizedOpenedFileWithoutChangingInput()
    {
        await using var fixture = await CoordinatorFixture.CreateAsync(commissioned: false);
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var path = await WriteThermalHistoryInputAsync(fixture, now, string.Empty);
        var original = new byte[2 * 1024 * 1024 + 1];
        await File.WriteAllBytesAsync(path, original);

        await Assert.ThrowsAsync<InvalidDataException>(() => ThermalHistoryRecorder.LoadHistoryAsync(
            ThermalHistoryInputDirectory(fixture), now, CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        AssertThermalHistoryDidNotUseHardware(fixture);
    }

    private static string ThermalHistoryInputDirectory(CoordinatorFixture fixture) =>
        Path.Combine(fixture.Paths.DataDirectory, "thermal-history-v1");

    private static async Task<string> WriteThermalHistoryInputAsync(
        CoordinatorFixture fixture, DateTimeOffset day, string content)
    {
        var directory = ThermalHistoryInputDirectory(fixture);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory,
            day.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private static void AssertThermalHistoryDidNotUseHardware(CoordinatorFixture fixture)
    {
        Assert.Equal(0, fixture.Locator.OpenCalls);
        Assert.Equal(0, fixture.Device.ReadCalls);
        Assert.Empty(fixture.Device.Writes);
        Assert.Equal(0, fixture.Device.ShutdownOffWrites);
    }
}
