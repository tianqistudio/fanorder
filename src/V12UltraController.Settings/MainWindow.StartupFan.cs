using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using V12UltraController.Core.Ipc;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    private bool _renderingStartupFan;
    private bool _startupFanReading;
    private bool _startupFanSaving;
    private bool _startupFanInputValid = true;
    private bool _startupFanDraftEdited;
    private bool _startupFanSaveUnconfirmed;
    private byte? _startupFanBaseline;
    private byte? _startupFanDraft;
    private string? _startupFanError;

    private bool StartupFanDirty => _startupFanDraft.HasValue &&
        (_startupFanDraftEdited || _startupFanSaveUnconfirmed ||
         _startupFanBaseline.HasValue && _startupFanDraft != _startupFanBaseline);

    private void UpdateStartupFanSettingsAvailability()
    {
        if (StartupFanPercentSlider is null || _closing || _closed) return;
        var connected = StartupConnected;
        if (!connected)
        {
            _startupFanBaseline = null;
            _startupFanReading = _startupFanSaving = false;
            _startupFanError = null;
            if (!StartupFanDirty) _startupFanDraft = null;
        }

        RenderStartupFanSettings();
    }

    private async Task ReadStartupFanSettingsAsync()
    {
        if (!StartupConnected || _startupFanReading || _startupFanSaving) return;
        _startupFanReading = true;
        _startupFanError = null;
        RenderStartupFanSettings();
        try
        {
            var response = await SendStartupRequestAsync(PipeOperations.StartupFanGet, new { }, _windowLifetime.Token);
            if (response.Data is not { } data ||
                !data.TryGetProperty("readbackConfirmed", out var confirmed) || confirmed.ValueKind != JsonValueKind.True ||
                !data.TryGetProperty("percent", out var percentElement) ||
                !percentElement.TryGetByte(out var percent) || percent is < 10 or > 100)
                throw new InvalidOperationException("后台未返回有效的启动转速，请刷新状态。");
            AcceptStartupFanReadback(percent);
        }
        catch (Exception exception) when (IsExpectedClientException(exception))
        {
            _startupFanError = "状态读取失败：" + ExplainException(exception);
        }
        finally
        {
            _startupFanReading = false;
            RenderStartupFanSettings();
        }
    }

    private async void SaveStartupFanButton_Click(object sender, RoutedEventArgs e) =>
        await SaveStartupFanSettingsAsync();

    internal async Task SaveStartupFanSettingsAsync()
    {
        if (!StartupConnected || _startupFanReading || _startupFanSaving || !StartupFanDirty ||
            !_startupFanInputValid || _startupFanDraft is not { } desired ||
            (!_startupFanBaseline.HasValue && !_startupFanSaveUnconfirmed)) return;

        _startupFanSaving = true;
        _startupFanError = null;
        RenderStartupFanSettings();
        try
        {
            var response = await SendStartupRequestAsync(
                PipeOperations.StartupFanSet,
                new { percent = desired },
                _windowLifetime.Token);
            if (response.Data is not { } data ||
                !data.TryGetProperty("readbackConfirmed", out var confirmed) || confirmed.ValueKind != JsonValueKind.True ||
                !data.TryGetProperty("percent", out var element) || !element.TryGetByte(out var actual) ||
                actual != desired)
                throw new InvalidOperationException("后台读回的启动转速与提交值不一致。");
            AcceptStartupFanReadback(actual);
            ActivityText.Text = $"启动起始转速 {actual}% 已保存。仅在后台启动且风扇未转动时使用。";
        }
        catch (Exception exception) when (IsExpectedClientException(exception))
        {
            _startupFanSaveUnconfirmed = true;
            _startupFanDraftEdited = true;
            _startupFanError = "保存未确认：" + ExplainException(exception);
        }
        finally
        {
            _startupFanSaving = false;
            RenderStartupFanSettings();
        }
    }

    private void StartupFanPercentSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_renderingStartupFan) return;
        var percent = (byte)Math.Clamp(Math.Round(e.NewValue), 10, 100);
        _startupFanDraft = percent;
        _startupFanDraftEdited = !_startupFanBaseline.HasValue || percent != _startupFanBaseline;
        _startupFanInputValid = true;
        RenderStartupFanSettings();
    }

    private void StartupFanPercentInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_renderingStartupFan) return;
        _startupFanInputValid = byte.TryParse(
            StartupFanPercentInput.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var percent) &&
            percent is >= 10 and <= 100;
        if (_startupFanInputValid)
        {
            _startupFanDraft = percent;
            _startupFanDraftEdited = !_startupFanBaseline.HasValue || percent != _startupFanBaseline;
            _renderingStartupFan = true;
            try { StartupFanPercentSlider.Value = percent; }
            finally { _renderingStartupFan = false; }
        }
        RenderStartupFanSettings();
    }

    private void StartupFanPercentInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (_startupFanInputValid && SaveStartupFanButton.IsEnabled) SaveStartupFanButton_Click(sender, e);
            else RenderStartupFanSettings();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_startupFanBaseline is { } baseline)
            {
                _startupFanDraft = baseline;
                _startupFanDraftEdited = false;
                _startupFanInputValid = true;
                RenderStartupFanSettings();
            }
            e.Handled = true;
        }
    }

    private void StartupFanPercentInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        RenderStartupFanSettings();

    private void AcceptStartupFanReadback(byte percent)
    {
        var retainDraft = StartupFanDirty;
        _startupFanBaseline = percent;
        if (!retainDraft) _startupFanDraft = percent;
        _startupFanDraftEdited = _startupFanDraft != percent;
        _startupFanSaveUnconfirmed = false;
        _startupFanInputValid = true;
        _startupFanError = null;
    }

    private void RenderStartupFanSettings()
    {
        if (StartupFanPercentSlider is null || _closing || _closed) return;
        var value = _startupFanDraft ?? _startupFanBaseline ?? V12UltraController.Core.Configuration.ControllerConfiguration.DefaultStartupFanPercent;
        _renderingStartupFan = true;
        try
        {
            StartupFanPercentSlider.Value = value;
            StartupFanPercentInput.Text = value.ToString(CultureInfo.InvariantCulture);
            StartupFanPercentText.Text = $"{value}%";
        }
        finally { _renderingStartupFan = false; }

        StartupFanPercentSlider.IsEnabled = StartupConnected && _startupFanBaseline.HasValue && !_startupFanReading && !_startupFanSaving;
        StartupFanPercentInput.IsEnabled = StartupConnected && _startupFanBaseline.HasValue && !_startupFanReading && !_startupFanSaving;
        SaveStartupFanButton.IsEnabled = StartupConnected && _startupFanInputValid && StartupFanDirty &&
            !_startupFanReading && !_startupFanSaving && (_startupFanBaseline.HasValue || _startupFanSaveUnconfirmed);
        SaveStartupFanButton.Content = _startupFanSaving ? "正在保存…" : _startupFanSaveUnconfirmed ? "重试保存" : "保存起始转速";
        StartupFanSettingText.Text = !StartupConnected ? "等待后台连接确认。" :
            _startupFanReading ? "正在确认启动转速…" :
            _startupFanSaving ? "正在保存，等待后台读回…" :
            _startupFanError is not null ? _startupFanError :
            !_startupFanBaseline.HasValue ? "尚未确认。" :
            !_startupFanInputValid ? "请输入 10–100 之间的整数。" :
            StartupFanDirty ? "有未保存的修改。" : $"已确认：{_startupFanBaseline.Value}%（默认 50%）";
        StartupFanPercentInput.ToolTip = "输入 10–100%；Enter 保存，Esc 恢复已确认值。";
        SaveStartupFanButton.ToolTip = _startupFanError ?? "仅保存未转动时的启动起始转速。";
    }
}
