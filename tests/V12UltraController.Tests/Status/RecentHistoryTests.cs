using System.Text.Json;
using V12UltraController.Core.Control;
using V12UltraController.Core.Status;

namespace V12UltraController.Tests.Status;

public sealed class RecentHistoryTests
{
    [Fact]
    public void MonitoringPauseBreaksAllChartLinesWithoutInventingSamples()
    {
        var now = DateTimeOffset.UtcNow;
        var history = new RecentHistory();
        history.Add(now, Status(now));
        history.Add(now.AddSeconds(2), Status(now.AddSeconds(2)));
        history.Add(now.AddMinutes(2), Status(now.AddMinutes(2)));
        Assert.Equal(3, history.Samples.Count);
        Assert.False(history.Samples[1].BreakBefore);
        Assert.True(history.Samples[2].BreakBefore);
        Assert.Equal(50, history.Samples[2].Temperature);
    }
    private static ControllerStatus Status(DateTimeOffset stamp) => new("Running", true, "service",
        new(true,"374A","B101","1602",true), new(ControlMode.Auto,60,59,true),
        new(50,[],50,true,stamp),new(false,FailSafeReason.None,null),stamp,stamp,new(false,null,null));

    [Fact]
    public void FreshStaleDisconnectedAndClockRollback()
    {
        var now = DateTimeOffset.UtcNow;
        var history = new RecentHistory();
        history.Add(now,Status(now));
        Assert.Equal(50,history.Samples[0].Temperature);
        Assert.Equal(59,history.Samples[0].Reported);
        history.Add(now.AddSeconds(11),Status(now));
        Assert.Null(history.Samples[1].Temperature);
        Assert.Null(history.Samples[1].Reported);
        Assert.Equal(60,history.Samples[1].Target);
        history.Add(now.AddSeconds(12),null);
        Assert.Null(history.Samples[2].Target);
        history.Add(now.AddMinutes(-1),null);
        Assert.Single(history.Samples);
    }

    [Fact]
    public void FreshTemperatureWithoutHidConnectionDoesNotInventFanHistoryOrRetainStaleServiceData()
    {
        var now = DateTimeOffset.UtcNow;
        var history = new RecentHistory();
        var status = Status(now) with
        {
            Device = Status(now).Device with { Connected = false },
            SafetyProtection = new(true, true, true, false, null, now),
        };
        history.Add(now, status);
        Assert.Equal(50, history.Samples[0].Temperature);
        Assert.Null(history.Samples[0].Target);
        Assert.Null(history.Samples[0].Reported);

        history.Add(now.AddSeconds(11), status);
        Assert.Null(history.Samples[1].Temperature);
        history.Add(now.AddSeconds(12), null);
        Assert.Null(history.Samples[2].Temperature);
        Assert.All(history.Samples, sample =>
        {
            Assert.Null(sample.Target);
            Assert.Null(sample.Reported);
        });
    }

    [Fact]
    public void TenMinuteWindowAndOneSecondLimit()
    {
        var now = DateTimeOffset.UtcNow;
        var history = new RecentHistory();
        for (var i=0;i<1500;i++) { history.Add(now.AddSeconds(i),Status(now.AddSeconds(i))); history.Add(now.AddSeconds(i),null); }
        Assert.Equal(601,history.Samples.Count);
        Assert.Equal(TimeSpan.FromMinutes(10),history.Samples[^1].Time-history.Samples[0].Time);
    }

    [Fact]
    public void OldStatusWithoutSoftwareMetadataStillDeserializes()
    {
        var status = Status(DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(status);
        var result = JsonSerializer.Deserialize<ControllerStatus>(json);
        Assert.NotNull(result);
        Assert.Null(result.Software);
    }
}
