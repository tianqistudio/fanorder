using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    private CurveAdvice? advice;
    private (FanCurve Before, FanCurve Applied)? adviceUndo;
    private bool _analysisReading;
    private bool _analysisWriting;
    private long _analysisReadSequence;
    private CancellationTokenSource? _analysisReadCancellation;
    private string? _analysisError;

    private void UpdateAdviceAvailability()
    {
        if (AnalyzeHistoryButton is null || _closing || _closed) return;
        AnalyzeHistoryButton.IsEnabled = PageReadAvailable && !_analysisReading && !_analysisWriting;
        AnalyzeHistoryButton.Content = _analysisReading ? "正在分析…" : _analysisError is not null ? "重新分析" : "分析历史";
        ApplyAdviceButton.IsEnabled = !_analysisReading && StateMutationAvailable && advice is { StorageWarning: null } a &&
            DateTimeOffset.UtcNow - a.GeneratedAt < TimeSpan.FromMinutes(30) &&
            a.Rows.Any(r => r.Before != r.After);
        UndoAdviceButton.IsEnabled = !_analysisReading && StateMutationAvailable && adviceUndo is not null;
        ExportAdviceButton.IsEnabled = advice is not null;
        AnalyzeHistoryButton.ToolTip = _analysisReading ? "正在分析；其他页面仍可用。" :
            !PageReadAvailable ? "先连接兼容后台。" : _analysisError ?? "分析本地历史；不改风扇设置。";
        ApplyAdviceButton.ToolTip = !StateMutationAvailable ? ControlBlockedReason : advice is null ? "请先分析历史，生成可应用的建议。" :
            advice.StorageWarning is not null ? "历史存储有问题；处理后重试分析。" :
            DateTimeOffset.UtcNow - advice.GeneratedAt >= TimeSpan.FromMinutes(30) ? "建议已过期；请重新分析。" :
            !advice.Rows.Any(row => row.Before != row.After) ? "建议与当前曲线相同，无需应用。" :
            SafetyProtectionDisabled ? "保存建议曲线和自动模式；自动调节仍暂停。" : "保存建议曲线并切到自动模式。";
        ApplyAdviceButton.Content = SafetyProtectionDisabled ? "保存建议（自动暂停）" : "保存并选择自动模式";
        UndoAdviceButton.Content = "恢复上一曲线";
        UndoAdviceButton.ToolTip = adviceUndo is null ? "本次尚未应用建议，没有可撤销的曲线。" :
            !StateMutationAvailable ? ControlBlockedReason : SafetyProtectionDisabled ? "恢复上一条曲线；自动调节仍暂停。" : "恢复上一条曲线。";
        ExportAdviceButton.ToolTip = advice is null ? "请先分析历史，再导出结果。" : "导出当前分析结果，不改设备设置。";
    }

    private async void AnalyzeHistoryButton_Click(object sender, RoutedEventArgs e) => await AnalyzeHistoryAsync();

    internal async Task AnalyzeHistoryAsync()
    {
        if (!PageReadAvailable || _analysisReading || _analysisWriting) return;
        var epoch = _pageReadEpoch;
        var generation = _controlRequestGeneration;
        var sequence = ++_analysisReadSequence;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        _analysisReadCancellation = cancellation;
        _analysisReading = true;
        _analysisError = null;
        AdviceSummaryText.Text = "正在分析已有记录；未改变曲线。";
        UpdateAdviceAvailability();
        try
        {
            var response = await SendCheckedAsync(PipeOperations.HistoryAnalyze, new { }, cancellation.Token);
            if (!IsCurrentPageRead(epoch, generation) || sequence != _analysisReadSequence) return;
            advice = response.Data!.Value.Deserialize<CurveAdvice>(JsonOptions) ?? throw new InvalidDataException("历史分析响应为空。");
            if (advice.Rows is null || advice.Rows.Any(row => row is null) || advice.Baseline is null || advice.Suggested is null)
                throw new InvalidDataException("历史分析缺少有效曲线或建议行。");
            AdviceGrid.ItemsSource = advice.Rows;
            AdviceGrid.SelectedIndex = advice.Rows.Count > 0 ? 0 : -1;
            var changed = advice.Rows.Count(row => row.Before != row.After);
            var maximumDelta = advice.Rows.Count == 0 ? 0 : advice.Rows.Max(row => Math.Abs(row.After - row.Before));
            AdviceSummaryText.Text = advice.StorageWarning is not null ? "历史记录有存储问题；建议暂不可保存。" :
                advice.EligibleSamples == 0 ? "没有可用于建议的有效自动模式记录；曲线未改动。" :
                changed == 0 ? $"已分析 {advice.EligibleSamples} 个有效分钟样本，无需修改曲线。" :
                $"建议调整 {changed} 个温度点，最大变化 {maximumDelta} 个百分点。有效样本 {advice.EligibleSamples} 分钟。";
            AdviceSummaryText.ToolTip = $"分析于 {advice.GeneratedAt.ToLocalTime():MM-dd HH:mm:ss}\n记录 {advice.RecordedSamples} 分钟；有效自动记录 {advice.EligibleSamples} 分钟。\n{advice.Note}\n{advice.StorageWarning}";
            UpdateAdviceSelection();
        }
        catch (Exception exception) when (IsExpectedClientException(exception))
        {
            if (!IsCurrentPageRead(epoch, generation) || sequence != _analysisReadSequence) return;
            advice = null;
            AdviceGrid.ItemsSource = null;
            SetAdviceError(ExplainException(exception));
            UpdateAdviceSelection();
        }
        finally
        {
            if (IsCurrentPageRead(epoch, generation) && sequence == _analysisReadSequence)
            {
                _analysisReading = false;
                _analysisReadCancellation = null;
                UpdateAdviceAvailability();
            }
        }
    }

    private async void ApplyAdviceButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(async () =>
        {
            if (_analysisReading) return;
            _analysisWriting = true;
            UpdateAdviceAvailability();
            try
            {
            var selected = advice ?? throw new InvalidOperationException("请先分析历史。");
            if (selected.StorageWarning is not null || DateTimeOffset.UtcNow - selected.GeneratedAt >= TimeSpan.FromMinutes(30))
                throw new InvalidOperationException("历史不可用或建议已过期，请重新分析。");
            await SendCheckedAsync(PipeOperations.CurveAdviceApply, new { expected = selected.Baseline, proposed = selected.Suggested });
            adviceUndo = (selected.Baseline, selected.Suggested);
            advice = null;
            AdviceGrid.ItemsSource = null;
            AdviceSummaryText.Text = SafetyProtectionDisabled ? "建议曲线与自动模式已保存；自动调节仍暂停，可撤销本次曲线。" :
                "建议曲线已保存并选择自动模式；实际转速以后台状态为准，可撤销本次曲线。";
            _analysisError = null;
            UpdateAdviceSelection();
            UpdateAdviceAvailability();
            }
            finally { _analysisWriting = false; UpdateAdviceAvailability(); }
        }, refreshAfterSuccess: true, reportError: SetAdviceError);

    private async void UndoAdviceButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(async () =>
        {
            if (_analysisReading) return;
            _analysisWriting = true;
            UpdateAdviceAvailability();
            try
            {
            var previous = adviceUndo ?? throw new InvalidOperationException("没有本次可撤销的曲线。");
            await SendCheckedAsync(PipeOperations.CurveAdviceApply, new { expected = previous.Applied, proposed = previous.Before });
            adviceUndo = null;
            advice = null;
            AdviceGrid.ItemsSource = null;
            AdviceSummaryText.Text = SafetyProtectionDisabled ? "已恢复上一条曲线；自动模式保持暂停。" : "已恢复上一条曲线；保持自动模式。";
            _analysisError = null;
            UpdateAdviceSelection();
            UpdateAdviceAvailability();
            }
            finally { _analysisWriting = false; UpdateAdviceAvailability(); }
        }, refreshAfterSuccess: true, reportError: SetAdviceError);

    private void SetAdviceError(string message)
    {
        _analysisError = message;
        AdviceSummaryText.Text = "本次操作未确认；可重新分析后再试。";
        AdviceSummaryText.ToolTip = message;
        UpdateAdviceAvailability();
    }

    private void AdviceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAdviceSelection();

    private void UpdateAdviceSelection()
    {
        if (AdviceSelectionTitleText is null || AdviceSelectionDetailText is null) return;
        if (AdviceGrid.SelectedItem is CurveAdviceRow row)
        {
            AdviceSelectionTitleText.Text = $"{row.TemperatureCelsius:0.#}°C · {row.Before}% → {row.After}%";
            AdviceSelectionDetailText.Text = $"{row.Samples} 个分钟样本。{row.Reason}";
        }
        else
        {
            AdviceSelectionTitleText.Text = "选择一个温度点查看依据";
            AdviceSelectionDetailText.Text = "分析完成后，可在表格中选择一行。";
        }
    }

    private void ExportAdviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (advice is null) return;
        var dialog = new SaveFileDialog { Filter = "JSON 分析结果|*.json", FileName = "v12-curve-advice.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(advice, new JsonSerializerOptions(JsonOptions) { WriteIndented = true })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetAdviceError("导出失败：" + ex.Message); }
    }
}
