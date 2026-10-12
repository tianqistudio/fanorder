using System.Windows;
using System.Windows.Controls;
using System.Text.Json;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;

namespace V12UltraController.Settings;

public partial class MainWindow
{
    private string? _startupConnection;
    private bool _startupDisconnected;
    private long _startupEpoch;
    private long _startupOperation;
    private bool _startupAutoReadAttempted;
    private bool _startupReading;
    private bool _startupSaving;
    private bool _renderingStartup;
    private bool _startupDraftEdited;
    private bool _startupSaveUnconfirmed;
    private bool? _startupBaseline;
    private bool? _startupDraft;
    private string? _startupError;
    private CancellationTokenSource? _startupRequestLifetime;

    private bool StartupConnected => _startupConnection is not null && !_closing && !_closed;
    private bool StartupDirty => _startupDraft.HasValue &&
        (_startupDraftEdited || _startupSaveUnconfirmed || _startupBaseline.HasValue && _startupDraft != _startupBaseline);

    private void UpdateStartupSettingsAvailability()
    {
        if (StartupEnabledCheckBox is null) return;
        var connection = !_startupDisconnected && !_closing && !_closed &&
            _lastStatus?.Software is { UiContractVersion: UiCompatibility.ContractVersion } software
            ? $"{software.UiContractVersion}:{software.ServiceVersion}" : null;
        if (!string.Equals(connection, _startupConnection, StringComparison.Ordinal))
        {
            var preserveDraft = StartupDirty || _startupSaving;
            _startupEpoch++;
            _startupConnection = connection;
            _startupRequestLifetime?.Cancel();
            _startupRequestLifetime = null;
            _startupReading = _startupSaving = false;
            _startupBaseline = null;
            _startupAutoReadAttempted = false;
            _startupError = null;
            _startupDraftEdited = preserveDraft;
            if (!preserveDraft) _startupDraft = null;
        }
        RenderStartupSettings();
        UpdateStartupFanSettingsAvailability();
        if (CurrentPageId == "general" && StartupConnected && !_startupAutoReadAttempted && !_startupReading && !_startupSaving)
            _ = ReadStartupSettingsAsync();
    }

    private void InvalidateStartupConnection()
    {
        _startupDisconnected = true;
        UpdateStartupSettingsAvailability();
    }

    private async void ReadStartupButton_Click(object sender, RoutedEventArgs e) => await ReadStartupSettingsAsync();
    private async void SaveStartupButton_Click(object sender, RoutedEventArgs e) => await SaveStartupSettingsAsync();

    private void StartupSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_renderingStartup || StartupEnabledCheckBox is null || StartupEnabledCheckBox.IsChecked is not { } value) return;
        _startupDraft = value;
        _startupDraftEdited = !_startupBaseline.HasValue || value != _startupBaseline;
        RenderStartupSettings();
    }

    internal async Task ReadStartupSettingsAsync()
    {
        if (!StartupConnected || _startupReading || _startupSaving) return;
        _startupAutoReadAttempted = true;
        var epoch = _startupEpoch;
        var operation = ++_startupOperation;
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        _startupRequestLifetime = requestLifetime;
        _startupReading = true;
        _startupError = null;
        RenderStartupSettings();
        try
        {
            var response = await SendStartupRequestAsync(PipeOperations.StartupGet, new { }, requestLifetime.Token);
            if (!IsCurrentStartupRequest(epoch, operation)) return;
            AcceptStartupReadback(ReadConfirmedStartupValue(response));
            if (response.Data is { } data &&
                data.TryGetProperty("startupFanPercent", out var startupPercentElement) &&
                startupPercentElement.TryGetByte(out var startupPercent) && startupPercent is >= 10 and <= 100)
            {
                AcceptStartupFanReadback(startupPercent);
            }
        }
        catch (Exception exception) when (IsExpectedClientException(exception))
        {
            if (!IsCurrentStartupRequest(epoch, operation)) return;
            _startupBaseline = null;
            if (!StartupDirty) _startupDraft = null;
            _startupError = "状态读取失败：" + ExplainException(exception);
        }
        finally
        {
            if (IsCurrentStartupRequest(epoch, operation))
            {
                _startupReading = false;
                _startupRequestLifetime = null;
                RenderStartupSettings();
            }
        }
    }

    internal async Task SaveStartupSettingsAsync()
    {
        if (!StartupConnected || _startupReading || _startupSaving || !StartupDirty ||
            _startupDraft is not { } desired || (!_startupBaseline.HasValue && !_startupSaveUnconfirmed)) return;
        var epoch = _startupEpoch;
        var operation = ++_startupOperation;
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        _startupRequestLifetime = requestLifetime;
        _startupSaving = true;
        _startupError = null;
        RenderStartupSettings();
        try
        {
            // Capture the deliberate selection while this setting is locked;
            // unrelated fan controls remain independent of this request.
            var response = await SendStartupRequestAsync(PipeOperations.StartupSet, new { enabled = desired }, requestLifetime.Token);
            if (!IsCurrentStartupRequest(epoch, operation)) return;
            var confirmed = ReadConfirmedStartupValue(response);
            if (confirmed != desired) throw new InvalidOperationException("系统读回与提交选择不一致，保存结果未确认。");
            AcceptStartupReadback(confirmed);
        }
        catch (Exception exception) when (IsExpectedClientException(exception))
        {
            if (!IsCurrentStartupRequest(epoch, operation)) return;
            _startupBaseline = null;
            _startupSaveUnconfirmed = true;
            _startupDraftEdited = true;
            _startupError = "保存未确认：" + ExplainException(exception);
        }
        finally
        {
            if (IsCurrentStartupRequest(epoch, operation))
            {
                _startupSaving = false;
                _startupRequestLifetime = null;
                RenderStartupSettings();
            }
        }
    }

    private bool IsCurrentStartupRequest(long epoch, long operation) => StartupConnected &&
        epoch == _startupEpoch && operation == _startupOperation;

    private void AcceptStartupReadback(bool enabled)
    {
        var retainDraft = StartupDirty;
        _startupBaseline = enabled;
        if (!retainDraft) _startupDraft = enabled;
        _startupDraftEdited = _startupDraft != enabled;
        _startupSaveUnconfirmed = false;
        _startupError = null;
    }

    private async Task<PipeResponse> SendStartupRequestAsync<T>(string operation, T payload, CancellationToken cancellationToken)
    {
        // Startup is independent of fan-control priority, hardware barriers and
        // its busy state. Its own connection epoch rejects stale replies.
        var request = PipeRequest.Create(operation, payload);
        var response = await _client.SendAsync(request, PipeTimeout, cancellationToken);
        if (response.ApiVersion != PipeProtocol.ApiVersion)
            throw new InvalidOperationException("后台接口版本不兼容。");
        if (!string.Equals(request.RequestId, response.RequestId, StringComparison.Ordinal))
            throw new InvalidOperationException("后台响应标识不匹配。");
        if (!response.Ok) throw new PipeApiException(response.Error?.Code ?? PipeErrorCodes.InternalError,
            response.Error?.Message ?? "后台未确认开机启动设置。");
        if (response.Data is null) throw new InvalidOperationException("后台响应缺少开机启动状态。");
        return response;
    }

    private static bool ReadConfirmedStartupValue(PipeResponse response)
    {
        if (response.Data is not { } data ||
            !data.TryGetProperty("readbackConfirmed", out var confirmed) || confirmed.ValueKind != JsonValueKind.True ||
            !data.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException("后台未返回有效的系统确认值，请刷新状态。");
        return enabled.GetBoolean();
    }

    private void RenderStartupSettings()
    {
        if (StartupEnabledCheckBox is null || _closing || _closed) return;
        _renderingStartup = true;
        try { StartupEnabledCheckBox.IsChecked = _startupDraft; }
        finally { _renderingStartup = false; }
        StartupEnabledCheckBox.IsEnabled = StartupConnected && (_startupBaseline.HasValue || StartupDirty) && !_startupReading && !_startupSaving;
        ReadStartupButton.IsEnabled = StartupConnected && !_startupReading && !_startupSaving;
        SaveStartupButton.IsEnabled = StartupConnected && !_startupReading && !_startupSaving && StartupDirty &&
            (_startupBaseline.HasValue || _startupSaveUnconfirmed);
        ReadStartupButton.Content = _startupReading ? "正在刷新…" : _startupError is not null ? "重试刷新" : "刷新状态";
        SaveStartupButton.Content = _startupSaving ? "正在保存…" : _startupSaveUnconfirmed ? "重试保存" : "保存修改";
        StartupSettingText.Text = !StartupConnected ? _startupDraftEdited ? "等待连接确认，未保存选择已保留。" :
                _lastStatus is not null ? "等待兼容的后台确认状态。" : "等待连接后台，连接后自动确认。" :
            _startupReading ? StartupDirty ? "正在确认系统状态，保留你的修改…" : "正在确认系统状态…" :
            _startupSaving ? "正在保存，等待系统读回确认…" :
            _startupError is not null ? _startupSaveUnconfirmed ? "保存结果未确认，选择已保留。" : "暂时无法读取状态，请重试。" :
            !_startupBaseline.HasValue ? "进入此页后自动确认状态。" :
            StartupDirty ? "有未保存的修改。" : _startupBaseline == true ? "系统已确认：下次开机自动运行后台" : "系统已确认：下次开机不自动运行后台";
        StartupSettingText.ToolTip = _startupError ?? (_startupBaseline.HasValue
            ? $"最近确认：{(_startupBaseline == true ? "开机自动运行" : "开机不自动运行")}；点击保存才提交。" : "尚未确认；未勾选不代表关闭。" );
        ReadStartupButton.ToolTip = !StartupConnected ? "连接兼容的后台后会自动读取。" :
            _startupError ?? "重新确认设置；保留未保存选择。";
        SaveStartupButton.ToolTip = !StartupConnected ? "等待后台连接确认；未保存选择会保留。" :
            _startupReading ? "等待系统状态确认完成。" : _startupSaving ? "等待本次保存的系统读回确认。" :
            _startupSaveUnconfirmed ? "上次未确认；可重试保存或刷新确认。" :
            !_startupBaseline.HasValue ? "等待自动确认；失败可重试刷新。" :
            !StartupDirty ? "当前没有未保存的修改。" : "只保存开机启动，不改风扇或安全保护。";
        StartupEnabledCheckBox.ToolTip = StartupConnected && _startupBaseline.HasValue
            ? "选择随 Windows 开机运行后台，改后点击保存。" : "系统状态尚未确认；已有未保存选择会保留。";
        ToolTipService.SetShowOnDisabled(StartupEnabledCheckBox, true);
        RenderStartupFanSettings();
    }
}
