using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using V12UltraController.Core.Status;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    private readonly RecentHistory _history = new();
    private DateTimeOffset _historyRenderTime;

    private void RenderHistory(ControllerStatus? status)
    {
        UpdateAdviceAvailability();
        var now = DateTimeOffset.UtcNow;
        _historyRenderTime = now;
        _history.Add(now, status);
        Geometry Draw(Func<HistorySample, double?> value, double maximum)
        {
            var geometry = new StreamGeometry();
            using var context = geometry.Open();
            var active = false;
            foreach (var sample in _history.Samples)
            {
                if (sample.BreakBefore) active = false;
                if (value(sample) is not { } number) { active = false; continue; }
                var point = new Point(Math.Clamp(600 - (now - sample.Time).TotalSeconds, 0, 600),
                    120 - 120 * number / maximum);
                if (!active) { context.BeginFigure(point, false, false); active = true; }
                else context.LineTo(point, true, false);
            }
            return geometry;
        }
        TemperatureHistoryPath.Data = Draw(sample => sample.Temperature, 120);
        TargetHistoryPath.Data = Draw(sample => sample.Target, 100);
        ReportedHistoryPath.Data = Draw(sample => sample.Reported, 100);
        var validTemperature = _history.Samples.Count(sample => sample.Temperature.HasValue);
        var validReadback = _history.Samples.Count(sample => sample.Reported.HasValue);
        HistorySampleText.Text = $"有效温度 {validTemperature} 点 · 有效回读 {validReadback} 点";
        HistorySampleText.ToolTip = "显示本窗口最近 10 分钟；缺失或过期留空。";
        var latest = _history.Samples.LastOrDefault();
        TrendTemperatureValueText.Text = latest?.Temperature is { } temperature ? $"{temperature:0.#}°C" : "—";
        TrendTargetValueText.Text = latest?.Target is { } target ? $"{target:0}%" : "—";
        TrendReportedValueText.Text = latest?.Reported is { } readback ? $"{readback:0}%" : "—";
        TrendFreshnessText.Text = validTemperature == 0 && validReadback == 0
            ? "尚无有效温度或设备回读；未知值留空，目标不代表实测转速。"
            : latest?.Temperature is null || latest.Reported is null
                ? "部分数据不可用；空白表示缺失或过期。"
                : $"观测至 {now.ToLocalTime():HH:mm:ss}；悬浮图表查看时间和值。";
    }

    private void HistoryChart_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        var x = Math.Clamp(e.GetPosition(canvas).X, 0, 600);
        var requestedTime = _historyRenderTime - TimeSpan.FromSeconds(600 - x);
        if (canvas.ToolTip is not ToolTip tooltip)
        {
            tooltip = new ToolTip { Content = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 280 } };
            canvas.ToolTip = tooltip;
            ToolTipService.SetInitialShowDelay(canvas, 80);
            ToolTipService.SetShowDuration(canvas, 30000);
        }
        if (tooltip.Content is TextBlock text)
            text.Text = DescribeHistoryAt(requestedTime, canvas.Name == "TemperatureHistoryCanvas");
    }

    private void HistoryChart_MouseLeave(object sender, MouseEventArgs e)
    {
        // Let WPF own opening/closing; the stable tooltip is reused on the next
        // hover and never reparents a help-popup element.
    }

    internal string DescribeHistoryAt(DateTimeOffset time, bool temperatureOnly)
    {
        var nearest = _history.Samples.MinBy(sample => Math.Abs((sample.Time - time).TotalSeconds));
        if (nearest is null || Math.Abs((nearest.Time - time).TotalSeconds) > 5)
            return $"{time.ToLocalTime():HH:mm:ss}\n此时间段没有采样；不补推数值。";
        var heading = nearest.Time.ToLocalTime().ToString("HH:mm:ss");
        if (temperatureOnly)
            return nearest.Temperature is { } temperature ? $"{heading}\n控制温度 {temperature:0.#}°C" :
                $"{heading}\n温度无有效读数。";
        var target = nearest.Target is { } targetValue ? $"{targetValue:0}%" : "未知";
        var readback = nearest.Reported is { } reportedValue ? $"{reportedValue:0}%" : "未知或已过期";
        return $"{heading}\n目标 {target}\n设备回读 {readback}\n百分比不是实测 RPM。";
    }
}
