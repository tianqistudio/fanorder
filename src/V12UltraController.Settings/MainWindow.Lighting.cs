using System.Text.Json;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    private bool _lightingSupported;
    private LightingSettings? _lightingBaseline;
    private bool _homeLightingCacheUnavailable;
    private bool _renderingLighting;
    private LightingSettings? _lightingLastCleanDraft;
    private long _lightingDraftRevision;
    private long _lightingReadSequence;
    private bool _lightingReading;
    private bool _lightingWriting;
    private bool _lightingAutoReadAttempted;
    private CancellationTokenSource? _lightingReadCancellation;
    private DateTimeOffset? _lightingCacheReadAt;
    private string _lightingFeedback = "进入页面自动读取缓存。";
    private string? _lightingError;
    private bool HasUnsavedLightingDraft => (_lightingBaseline ?? _lightingLastCleanDraft) is { } clean && LightingDraft() != clean;
    private void UndoLighting_Click(object sender, RoutedEventArgs e)
    {
        if (_commandInProgress || _lightingBaseline is not { } saved) return;
        SetLightingDraft(saved);
        _lightingLastCleanDraft = saved;
        RenderLightingPreview();
        SetLightingFeedback("已撤销草稿修改；没有改变设备。", null);
    }

    private void SetLightingDraft(LightingSettings settings)
    {
        _renderingLighting = true;
        try
        {
            LightingEnabledCheckBox.IsChecked = settings.Enabled;
            LightingModeCombo.SelectedIndex = settings.Mode;
            LightingColorCombo.SelectedIndex = settings.Color;
            LightingBrightnessSlider.Value = settings.Brightness;
            LightingSpeedSlider.Value = settings.Speed;
        }
        finally { _renderingLighting = false; }
        RenderLightingPreview();
    }

    private LightingSettings LightingDraft() => new(LightingEnabledCheckBox.IsChecked == true,
        LightingModeCombo.SelectedIndex, LightingColorCombo.SelectedIndex,
        (int)LightingBrightnessSlider.Value, (int)LightingSpeedSlider.Value);

    private void LightingDraft_Changed(object sender, RoutedEventArgs e)
    {
        if (LightingSpeedSlider is null || LightingBrightnessSlider is null ||
            LightingColorCombo is null || LightingModeCombo is null || LightingDraftText is null) return;
        if (!_renderingLighting) _lightingDraftRevision++;
        RenderLightingPreview();
        if (!_renderingLighting && HasUnsavedLightingDraft)
            SetLightingFeedback("有未应用的修改。", null);
    }

    private void RenderLightingPreview()
    {
        var draft = LightingDraft();
        if (draft.Mode < 0 || draft.Color < 0) return;
        string[] colors = ["#FF596A", "#4AA8FF", "#4CD7A0", "#B58AFF", "#FFB454"];
        Brush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[draft.Color]));
        if (draft.Mode >= 2)
        {
            var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            for (var i = 0; i < colors.Length; i++)
                gradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(colors[i]), i / 4.0));
            brush = gradient;
        }
        LightingPreviewRing.Stroke = brush;
        LightingPreviewRing.Opacity = draft.Enabled ? 0.15 + 0.85 * draft.Brightness / 255.0 : 0;
        LightingPreviewLabel.Text = !draft.Enabled ? "灯光关闭 · 草稿" :
            new[] { "单色常亮", "单色呼吸", "多彩 · 01", "多彩 · 02" }[draft.Mode];
        LightingColorHint.Text = draft.Mode >= 2 ? "多彩模式不使用单色预设；切回时保留。" : "单色模式使用此颜色";
        LightingDraftText.Text = HasUnsavedLightingDraft ? "● 未应用的修改" : _lightingBaseline is null ? "尚未读取 · 仅本地草稿" :
            draft == _lightingBaseline ? "草稿与最近读取 / 确认值一致" : "● 未应用的修改";
    }

    private async void ReadLightingButton_Click(object sender, RoutedEventArgs e) => await ReadLightingCacheAsync();

    internal async Task ReadLightingCacheAsync()
    {
        if (!PageReadAvailable || _lightingReading || _lightingWriting) return;
        _lightingAutoReadAttempted = true;
        var epoch = _pageReadEpoch;
        var controlGeneration = _controlRequestGeneration;
        var sequence = ++_lightingReadSequence;
        var draftRevision = _lightingDraftRevision;
        var hadDraft = HasUnsavedLightingDraft;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        _lightingReadCancellation = cancellation;
        _lightingReading = true;
        _lightingError = null;
        UpdateLightingAvailability();
        try
        {
            // This operation returns the service cache only; it does not ask the
            // service to perform a HID read or use the global device-command gate.
            var response = await SendCheckedAsync(PipeOperations.LightingGet, new { }, cancellation.Token);
            if (!IsCurrentPageRead(epoch, controlGeneration) || sequence != _lightingReadSequence) return;
            if (response.Data is not { } data) throw new InvalidDataException("缺少灯光状态。");
            _lightingSupported = data.TryGetProperty("supported", out var supported) && supported.ValueKind == JsonValueKind.True;
            _lightingBaseline = null;
            _lightingCacheReadAt = data.TryGetProperty("lastSuccessfulRead", out var stamp) &&
                stamp.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(stamp.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var readAt) ? readAt : null;
            if (data.TryGetProperty("settings", out var value) && value.ValueKind == JsonValueKind.Object)
            {
                var settings = value.Deserialize<LightingSettings>(JsonOptions)
                    ?? throw new InvalidDataException("灯光状态无效。");
                settings.Validate();
                var retainDraft = hadDraft || draftRevision != _lightingDraftRevision;
                _lightingBaseline = settings;
                if (!retainDraft) SetLightingDraft(settings);
                _lightingLastCleanDraft = settings;
            }
            _homeLightingCacheUnavailable = _lightingBaseline is null;
            RenderLightingPreview();
            SetLightingFeedback(_lightingBaseline is null ? "后台暂无灯光缓存；当前为本地草稿。" :
                HasUnsavedLightingDraft ? "已刷新缓存；保留未应用的修改。" : "已读取后台缓存，尚未改变设备。", null);
        }
        catch (Exception exception) when (IsExpectedClientException(exception))
        {
            if (!IsCurrentPageRead(epoch, controlGeneration) || sequence != _lightingReadSequence) return;
            _lightingSupported = false;
            _lightingBaseline = null;
            SetLightingFeedback("读取失败；草稿已保留，可重试刷新。", ExplainException(exception));
        }
        finally
        {
            if (IsCurrentPageRead(epoch, controlGeneration) && sequence == _lightingReadSequence)
            {
                _lightingReading = false;
                _lightingReadCancellation = null;
                UpdateLightingAvailability();
            }
        }
    }

    private async void ApplyLightingButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(async () =>
        {
            if (_lightingReading) return;
            BeginLightingWrite();
            try
            {
            if (!_lightingSupported && !(ExplicitManualContext && ExplicitLightingSupported))
                throw new InvalidOperationException("请先读取灯光状态。");
            if (_currentMode == V12UltraController.Core.Control.ControlMode.Released || ExplicitManualContext && !ExplicitLightingSupported)
            {
                SetLightingFeedback("当前无法应用；草稿已保留。", LightingBlockedReason);
                return;
            }
            var settings = LightingDraft();
            settings.Validate();
            var response = await SendCheckedAsync(PipeOperations.LightingSet, settings);
            if (response.Data is not { } data || !data.TryGetProperty("readbackConfirmed", out var confirmed) ||
                confirmed.ValueKind != JsonValueKind.True)
                throw new InvalidDataException("后台未确认灯光回读；不能视为应用成功。");
            _lightingBaseline = settings;
            _lightingCacheReadAt = null;
            _homeLightingCacheUnavailable = false;
            _lightingLastCleanDraft = settings;
            RenderLightingPreview();
            SetLightingFeedback(LightingDraft() != settings ? "提交已确认；当前草稿另有未应用修改。" : "灯光已由设备确认。",
                null);
            }
            finally { EndLightingWrite(); }
        }, reportError: message => SetLightingFeedback("灯光操作未确认；草稿已保留。", message));

    private void InvalidateLightingCacheRead()
    {
        _lightingReadSequence++;
        _lightingReadCancellation?.Cancel();
        _lightingReadCancellation = null;
        _lightingReading = false;
        _lightingAutoReadAttempted = true;
    }

    private void BeginLightingWrite()
    {
        _lightingWriting = true;
        InvalidateLightingCacheRead();
        UpdateLightingAvailability();
    }

    private void EndLightingWrite()
    {
        _lightingWriting = false;
        UpdateLightingAvailability();
    }

    private void SetLightingFeedback(string message, string? error)
    {
        _lightingFeedback = message;
        _lightingError = error;
        RenderLightingPreview();
        UpdateLightingAvailability();
    }

    private void UpdateLightingAvailability()
    {
        if (ReadLightingButton is null || _closing || _closed) return;
        ReadLightingButton.IsEnabled = PageReadAvailable && !_lightingReading && !_lightingWriting;
        ReadLightingButton.Content = _lightingReading ? "正在刷新…" : _lightingError is not null ? "重试刷新" : "刷新缓存";
        ReadLightingButton.ToolTip = _lightingWriting ? "等待灯光操作确认。" :
            !PageReadAvailable ? "等待兼容后台；刷新不改变设备。" :
            _lightingError ?? "只读后台缓存；保留未应用草稿。";
        ApplyLightingButton.IsEnabled = !_lightingReading && !_lightingWriting && LightingOutputAvailable &&
            (_lightingSupported || ExplicitManualContext && ExplicitLightingSupported);
        ApplyLightingButton.ToolTip = _lightingReading ? "正在刷新缓存；可继续编辑。" :
            ApplyLightingButton.IsEnabled ? "应用草稿并由后台核验。" :
            !LightingOutputAvailable ? LightingBlockedReason : "请先读取后台缓存；失败可重试。";
        UndoLightingButton.IsEnabled = !_commandInProgress && !_lightingReading && !_lightingWriting && _lightingBaseline is not null && HasUnsavedLightingDraft;
        UndoLightingButton.ToolTip = _lightingBaseline is null ? "尚无已确认值；草稿已保留。" :
            !HasUnsavedLightingDraft ? "没有可撤销的修改。" : "恢复最近确认值，不改变设备。";
        LightingStatusText.Text = _lightingWriting ? "正在应用灯光，等待确认…" : _lightingReading ? "正在读取缓存；可继续编辑…" : _lightingFeedback;
        LightingStatusText.ToolTip = _lightingError ?? (_lightingCacheReadAt is { } stamp
            ? $"最近回读：{stamp.ToLocalTime():MM-dd HH:mm:ss}；不是实时状态。" : "回读时间未知；预览不代表当前设备。");
        LightingAvailabilityText.Text = ApplyLightingButton.IsEnabled ? "修改先保存在草稿；点击应用才发送。" :
            _lightingReading ? "刷新只读取后台缓存。" : ApplyLightingButton.ToolTip.ToString();
        LightingFeedbackText.Text = HasUnsavedLightingDraft ? "有未应用修改" : _lightingBaseline is null ? "本地草稿" : "已读取缓存";
        LightingFeedbackText.ToolTip = LightingStatusText.ToolTip;
    }
}
