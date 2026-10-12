using V12UltraController.Core.Status;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    private string? _pageServiceConnection;
    private bool _pageServiceDisconnected;
    private long _pageReadEpoch;
    private long _pageControlGeneration;

    private bool PageReadAvailable => _pageServiceConnection is not null && !_closing && !_closed && !_disableSafetyInProgress;

    private void ObservePageServiceConnection()
    {
        _pageServiceDisconnected = false;
        UpdatePageReadAvailability();
    }

    private void InvalidatePageServiceConnection()
    {
        _pageServiceDisconnected = true;
        UpdatePageReadAvailability();
    }

    private void UpdatePageReadAvailability()
    {
        if (ReadLightingButton is null) return;
        var connection = !_pageServiceDisconnected && !_closing && !_closed && _lastStatus is { } status &&
            (status.Software is null || status.Software.UiContractVersion == UiCompatibility.ContractVersion)
            ? $"{status.Software?.UiContractVersion}:{status.Software?.ServiceVersion ?? "legacy"}" : null;
        var changedConnection = !string.Equals(connection, _pageServiceConnection, StringComparison.Ordinal);
        if (changedConnection || _pageControlGeneration != _controlRequestGeneration)
        {
            _pageReadEpoch++;
            _pageControlGeneration = _controlRequestGeneration;
            _pageServiceConnection = connection;
            _lightingReadCancellation?.Cancel();
            _analysisReadCancellation?.Cancel();
            _curveReadCancellation?.Cancel();
            _curveReading = false;
            _lightingReadCancellation = _analysisReadCancellation = null;
            var lightingInterrupted = _lightingReading;
            var analysisInterrupted = _analysisReading;
            _lightingReading = _analysisReading = false;
            if (changedConnection)
            {
                _lightingAutoReadAttempted = false;
                _lightingSupported = false;
                _lightingBaseline = null;
                _lightingCacheReadAt = null;
                _lightingFeedback = connection is null ? "等待后台连接；灯光草稿已保留。" : "进入页面自动读取缓存。";
                _lightingError = null;
                RenderLightingPreview();
                advice = null;
                adviceUndo = null;
                _analysisError = null;
                AdviceGrid.ItemsSource = null;
                AdviceSummaryText.Text = connection is null ? "后台已断开；重新连接后可分析历史。" : "点击分析，查看记录与曲线建议。";
                UpdateAdviceSelection();
            }
            else
            {
                if (lightingInterrupted) _lightingFeedback = "读取已中断；草稿已保留，可刷新缓存。";
                if (analysisInterrupted) AdviceSummaryText.Text = "分析已中断，可重新分析；未修改曲线。";
            }
        }
        UpdateLightingAvailability();
        UpdateAdviceAvailability();
        UpdateCurveReadAvailability();
        if (CurrentPageId == "lighting" && PageReadAvailable && !_lightingAutoReadAttempted && !_lightingReading && !_lightingWriting)
            _ = ReadLightingCacheAsync();
    }

    private bool IsCurrentPageRead(long epoch, long controlGeneration) => PageReadAvailable &&
        epoch == _pageReadEpoch && controlGeneration == _controlRequestGeneration;
}
