using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;
using V12UltraController.Settings;

internal static class PageInteractionChecks
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };

    internal static int Run(Action<bool, string> assert, string output)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { Check(assert); }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
        return 0;
    }

    private static void Check(Action<bool, string> assert)
    {
        var freshClient = new PageClient(); var freshWindow = Create(freshClient);
        assert(freshClient.LightReads == 0 && !Text(freshWindow, "LightingFeedbackText").Contains("未应用") &&
            Text(freshWindow, "LightingDraftText").Contains("本地草稿"),
            "a new lighting editor without a baseline is an unchanged local draft rather than a false dirty device state");
        Brightness(freshWindow).Value = 77; freshWindow.NavigateToPage("lighting");
        assert(Brightness(freshWindow).Value == 77 && Text(freshWindow, "LightingFeedbackText").Contains("未应用"),
            "edits before the first lighting cache read survive and remain marked as unapplied");

        var homeClient = new PageClient(); var homeWindow = Create(homeClient); homeWindow.NavigateToPage("lighting");
        Wait((Task)typeof(MainWindow).GetMethod("SetHomeLightingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(homeWindow, [false])!);
        assert(((CheckBox)homeWindow.FindName("LightingEnabledCheckBox")).IsChecked == true && !homeClient.Lighting.Enabled &&
            Text(homeWindow, "LightingFeedbackText").Contains("未应用") && Button(homeWindow, "UndoLightingButton").IsEnabled,
            "home lighting OFF keeps the ON editor draft but marks its difference from the latest confirmed baseline");
        var operationsBeforeUndo = homeClient.Operations.Count; Event(homeWindow, "UndoLighting_Click");
        assert(((CheckBox)homeWindow.FindName("LightingEnabledCheckBox")).IsChecked == false &&
            !Text(homeWindow, "LightingFeedbackText").Contains("未应用") && homeClient.Operations.Count == operationsBeforeUndo,
            "undo after a home lighting action restores its confirmed baseline locally without another device request");

        var client = new PageClient(); var window = Create(client);
        Set(window, "_commandInProgress", true);
        window.NavigateToPage("lighting");
        assert(client.LightReads == 1 && (bool)Field(window, "_commandInProgress")!,
            "lighting page reads its cache without acquiring or clearing another command's busy state");
        Set(window, "_commandInProgress", false); Invoke(window, "UpdateControlButtonsForBusyState");
        for (var i = 0; i < 3; i++) { window.NavigateToPage("fan"); window.NavigateToPage("lighting"); Render(window, client.Status); }
        assert(client.LightReads == 1, "lighting automatic cache read occurs once per connection without navigation or polling repeats");
        Brightness(window).Value = 199; client.Lighting = client.Lighting with { Brightness = 60 };
        Wait(window.ReadLightingCacheAsync());
        assert(Brightness(window).Value == 199 && Text(window, "LightingStatusText").Contains("保留"),
            "explicit lighting refresh preserves an already-dirty editor and updates its baseline only");

        var delayedClient = new PageClient { DelayLightRead = true }; var delayed = Create(delayedClient);
        delayed.NavigateToPage("lighting"); Brightness(delayed).Value = 211;
        assert(!(bool)Field(delayed, "_commandInProgress")! && Brightness(delayed).IsEnabled,
            "pending lighting cache read leaves the global UI and local draft editor available");
        delayedClient.CompleteLightRead(73); Pump();
        assert(Brightness(delayed).Value == 211 && Text(delayed, "LightingDraftText").Contains("未应用"),
            "edits made after a cache read starts survive its late response");

        var failClient = new PageClient { FailLightRead = true }; var failed = Create(failClient);
        failed.NavigateToPage("lighting");
        assert(Text(failed, "LightingStatusText").Contains("读取失败") && Button(failed, "ReadLightingButton").IsEnabled,
            "cache failure stays inside the lighting card and offers an enabled retry");
        for (var i = 0; i < 3; i++) { Render(failed, failClient.Status); failed.NavigateToPage("fan"); failed.NavigateToPage("lighting"); }
        assert(failClient.LightReads == 1, "failed lighting cache read does not turn into periodic or navigation retry loops");
        Wait(failed.ReadLightingCacheAsync());
        assert(failClient.LightReads == 2 && Text(failed, "LightingStatusText").Contains("已读取"),
            "explicit lighting retry can confirm cached settings without a device write");

        var staleClient = new PageClient { DelayLightRead = true }; var stale = Create(staleClient);
        stale.NavigateToPage("lighting"); Disconnect(stale);
        staleClient.Lighting = staleClient.Lighting with { Brightness = 66 }; Render(stale, staleClient.Status);
        assert(staleClient.LightReads == 2 && Brightness(stale).Value == 66,
            "reconnection reads a new lighting baseline on the current page");
        staleClient.CompleteLightRead(22); Pump();
        assert(Brightness(stale).Value == 66, "pre-disconnect lighting response cannot overwrite the new connection baseline");
        Brightness(stale).Value = 190; Disconnect(stale); Render(stale, staleClient.Status);
        assert(Brightness(stale).Value == 190, "lighting draft survives disconnect and automatic reconnection reads");

        foreach (var boundary in new[] { "priority", "version", "close" })
        {
            var boundaryClient = new PageClient { DelayLightRead = true }; var boundaryWindow = Create(boundaryClient);
            boundaryWindow.NavigateToPage("lighting"); Brightness(boundaryWindow).Value = 187;
            if (boundary == "priority") { Set(boundaryWindow, "_controlRequestGeneration", 1L); Invoke(boundaryWindow, "UpdatePageReadAvailability"); }
            else if (boundary == "version") Render(boundaryWindow, boundaryClient.Status with
                { Software = boundaryClient.Status.Software! with { UiContractVersion = 999 } });
            else { Set(boundaryWindow, "_closing", true); Invoke(boundaryWindow, "InvalidatePageServiceConnection"); }
            boundaryClient.CompleteLightRead(11); Pump();
            assert(Brightness(boundaryWindow).Value == 187 && Field(boundaryWindow, "_lightingBaseline") is null,
                $"{boundary} invalidation rejects late lighting confirmation without replacing the draft");
        }

        client.FailLightWrite = true;
        Event(window, "ApplyLightingButton_Click");
        assert(Text(window, "LightingStatusText").Contains("未确认") && Brightness(window).Value == 199,
            "failed explicit lighting write reports locally and retains its draft");
        Event(window, "ApplyLightingButton_Click");
        assert(client.LightWrites.Count == 2 && client.LightWrites[^1].Brightness == 199 && Text(window, "LightingStatusText").Contains("设备确认"),
            "explicit lighting retry submits the saved draft and requires readback confirmation");
        client.DelayLightWrite = true; Brightness(window).Value = 219;
        var beforeWriteReadCount = client.LightReads;
        Event(window, "ApplyLightingButton_Click");
        Wait(window.ReadLightingCacheAsync());
        assert(!Button(window, "ReadLightingButton").IsEnabled && client.LightReads == beforeWriteReadCount,
            "pending explicit lighting write blocks only its own cache refresh to prevent stale baseline races");
        Brightness(window).Value = 249;
        client.CompleteLightWrite(); Pump();
        assert(Brightness(window).Value == 249 && client.LightWrites[^1].Brightness == 219 &&
            Text(window, "LightingStatusText").Contains("另有未应用"),
            "lighting write confirmation records its captured payload while preserving edits made during the write");

        var shortcutClient = new PageClient { DelayLightRead = true }; var shortcutWindow = Create(shortcutClient);
        shortcutWindow.NavigateToPage("lighting");
        Invoke(shortcutWindow, "BeginLightingWrite");
        shortcutClient.CompleteLightRead(33); Pump();
        assert(Field(shortcutWindow, "_lightingBaseline") is null && !Button(shortcutWindow, "ReadLightingButton").IsEnabled,
            "home lighting write scope invalidates an outstanding cache read and prevents a new read");
        Invoke(shortcutWindow, "EndLightingWrite");

        var analysisClient = new PageClient { DelayAnalysis = true }; var analysisWindow = Create(analysisClient);
        var analysisTask = analysisWindow.AnalyzeHistoryAsync();
        assert(!analysisTask.IsCompleted && !(bool)Field(analysisWindow, "_commandInProgress")! &&
            Button(analysisWindow, "ApplyManualSpeedButton").IsEnabled,
            "history analysis is locally busy while fan controls remain available");
        analysisClient.CompleteAnalysis(); Wait(analysisTask);
        assert(Text(analysisWindow, "AdviceSummaryText").Contains("调整 1") &&
            Text(analysisWindow, "AdviceSelectionDetailText").Contains("synthetic reason"),
            "analysis shows a change summary and selected-row reasoning without row help buttons");
        assert(Button(analysisWindow, "ApplyAdviceButton").Content.ToString()!.Contains("暂停"),
            "saving advice makes the paused automatic configuration explicit");
        analysisClient.DelayAdviceWrite = true;
        var priorAnalysisReads = analysisClient.Operations.Count(operation => operation == PipeOperations.HistoryAnalyze);
        Event(analysisWindow, "ApplyAdviceButton_Click");
        Wait(analysisWindow.AnalyzeHistoryAsync());
        assert(!Button(analysisWindow, "AnalyzeHistoryButton").IsEnabled &&
            analysisClient.Operations.Count(operation => operation == PipeOperations.HistoryAnalyze) == priorAnalysisReads,
            "saving an analysis recommendation prevents a new same-card analysis from racing its result");
        analysisClient.CompleteAdviceWrite(); Pump();
        assert(Text(analysisWindow, "AdviceSummaryText").Contains("仍暂停") && analysisClient.AdviceWrites == 1,
            "advice saves through the existing command path and does not claim protection resumed");

        var staleAnalysisClient = new PageClient { DelayAnalysis = true }; var staleAnalysis = Create(staleAnalysisClient);
        var staleAnalysisTask = staleAnalysis.AnalyzeHistoryAsync(); Disconnect(staleAnalysis);
        Render(staleAnalysis, staleAnalysisClient.Status); staleAnalysisClient.CompleteAnalysis(); Wait(staleAnalysisTask);
        assert(Field(staleAnalysis, "advice") is null && !Button(staleAnalysis, "ApplyAdviceButton").IsEnabled,
            "an old analysis response cannot revive an applicable recommendation after disconnect");
        staleAnalysisClient.FailAnalysis = true; Wait(staleAnalysis.AnalyzeHistoryAsync());
        assert(Text(staleAnalysis, "AdviceSummaryText").Contains("未确认") && Button(staleAnalysis, "AnalyzeHistoryButton").IsEnabled,
            "analysis failure remains inline and can be explicitly retried");

        var history = (RecentHistory)Field(window, "_history")!;
        var now = DateTimeOffset.UtcNow;
        var old = client.Status with { Thermal = client.Status.Thermal with { LastUpdated = now.AddSeconds(-30) }, LastSuccessfulRead = now.AddSeconds(-30) };
        history.Add(now.AddSeconds(-30), old); history.Add(now.AddSeconds(-15), null);
        Render(window, client.Status with { Thermal = client.Status.Thermal with { LastUpdated = now }, LastSuccessfulRead = now });
        assert(window.DescribeHistoryAt(now.AddSeconds(-15), true).Contains("无有效读数") &&
            window.DescribeHistoryAt(now.AddSeconds(-7), false).Contains("没有采样"),
            "trend hover reports missing samples and gaps instead of interpolating unobserved values");
        assert(window.DescribeHistoryAt(now, false).Contains("不是实测 RPM") && Text(window, "TrendReportedValueText") == "59%",
            "trend hover distinguishes target and fresh percentage readback from physical RPM");
        assert(delayedClient.Operations.All(operation => operation == PipeOperations.LightingGet) &&
            failClient.Operations.All(operation => operation == PipeOperations.LightingGet),
            "automatic lighting reads and retries issue cache reads only with no HID-related mutation request");
    }

    private static MainWindow Create(PageClient client) { var window = new MainWindow(client); Render(window, client.Status); return window; }
    private static void Render(MainWindow window, ControllerStatus status)
    {
        typeof(MainWindow).GetMethod("RenderStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [status]);
        typeof(MainWindow).GetMethod("SetConnectionState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [true, "connected"]);
    }
    private static void Disconnect(MainWindow window)
    {
        Set(window, "_lastStatus", null);
        typeof(MainWindow).GetMethod("SetConnectionState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [false, "disconnected"]);
    }
    private static object? Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
    private static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static void Invoke(MainWindow window, string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    private static void Event(MainWindow window, string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
    private static Button Button(MainWindow window, string name) => (Button)window.FindName(name);
    private static Slider Brightness(MainWindow window) => (Slider)window.FindName("LightingBrightnessSlider");
    private static string Text(MainWindow window, string name) => ((TextBlock)window.FindName(name)).Text;
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    private static void Wait(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) Pump();
        if (!task.IsCompleted) throw new InvalidOperationException("Synthetic page request did not complete.");
        task.GetAwaiter().GetResult();
    }

    private sealed class PageClient : IPipeApiClient
    {
        private static readonly FanCurve Curve = new([new(0, 30), new(60, 60), new(90, 100)]);
        public ControllerStatus Status { get; set; } = new("SafetyProtectionDisabled", true, "synthetic",
            new(true, "374A", "B101", "1602", true), new(ControlMode.Manual, 59, 59, true, 59, true),
            new(57, [54], 57, true, DateTimeOffset.UtcNow), new(false, FailSafeReason.None, null), DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, new(false, null, null), Software: new("synthetic", UiCompatibility.ContractVersion, true, true, true, true),
            SafetyProtection: new(true, true, true, false, null, DateTimeOffset.UtcNow));
        public LightingSettings Lighting { get; set; } = new(true, 0, 1, 128, 1);
        public List<string> Operations { get; } = [];
        public List<LightingSettings> LightWrites { get; } = [];
        public int LightReads { get; private set; }
        public int AdviceWrites { get; private set; }
        public bool DelayLightRead { get; set; }
        public bool FailLightRead { get; set; }
        public bool FailLightWrite { get; set; }
        public bool DelayLightWrite { get; set; }
        public bool DelayAnalysis { get; set; }
        public bool FailAnalysis { get; set; }
        public bool DelayAdviceWrite { get; set; }
        private (PipeRequest Request, TaskCompletionSource<PipeResponse> Completion)? pendingLight;
        private (PipeRequest Request, TaskCompletionSource<PipeResponse> Completion)? pendingAnalysis;
        private (PipeRequest Request, LightingSettings Settings, TaskCompletionSource<PipeResponse> Completion)? pendingLightWrite;
        private (PipeRequest Request, TaskCompletionSource<PipeResponse> Completion)? pendingAdviceWrite;
        public Task<PipeResponse> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Operations.Add(request.Operation);
            if (request.Operation == PipeOperations.LightingGet)
            {
                LightReads++;
                if (DelayLightRead) { DelayLightRead = false; var completion = new TaskCompletionSource<PipeResponse>(); pendingLight = (request, completion); return completion.Task; }
                if (FailLightRead) { FailLightRead = false; return Task.FromException<PipeResponse>(new System.IO.IOException("synthetic cache failure")); }
                return Task.FromResult(LightResponse(request, Lighting));
            }
            if (request.Operation == PipeOperations.LightingSet)
            {
                var settings = request.Payload.Deserialize<LightingSettings>(JsonOptions)!; LightWrites.Add(settings);
                if (FailLightWrite) { FailLightWrite = false; return Task.FromException<PipeResponse>(new System.IO.IOException("synthetic write failure")); }
                if (DelayLightWrite) { DelayLightWrite = false; var completion = new TaskCompletionSource<PipeResponse>(); pendingLightWrite = (request, settings, completion); return completion.Task; }
                Lighting = settings; return Task.FromResult(PipeResponse.Success(request.RequestId, new { readbackConfirmed = true }));
            }
            if (request.Operation == PipeOperations.HistoryAnalyze)
            {
                if (DelayAnalysis) { DelayAnalysis = false; var completion = new TaskCompletionSource<PipeResponse>(); pendingAnalysis = (request, completion); return completion.Task; }
                if (FailAnalysis) { FailAnalysis = false; return Task.FromException<PipeResponse>(new System.IO.IOException("synthetic analysis failure")); }
                return Task.FromResult(AdviceResponse(request));
            }
            if (request.Operation == PipeOperations.CurveAdviceApply)
            {
                AdviceWrites++;
                if (DelayAdviceWrite) { DelayAdviceWrite = false; var completion = new TaskCompletionSource<PipeResponse>(); pendingAdviceWrite = (request, completion); return completion.Task; }
                Status = Status with { Fan = Status.Fan with { Mode = ControlMode.Auto } }; return Task.FromResult(PipeResponse.Success(request.RequestId, Status));
            }
            if (request.Operation == PipeOperations.StatusGet) return Task.FromResult(PipeResponse.Success(request.RequestId, Status));
            if (request.Operation == PipeOperations.CurveGet) return Task.FromResult(PipeResponse.Success(request.RequestId, Curve));
            throw new InvalidOperationException("Unexpected fake operation: " + request.Operation);
        }
        public void CompleteLightRead(int brightness)
        { var pending = pendingLight!.Value; pendingLight = null; pending.Completion.SetResult(LightResponse(pending.Request, Lighting with { Brightness = brightness })); }
        public void CompleteAnalysis()
        { var pending = pendingAnalysis!.Value; pendingAnalysis = null; pending.Completion.SetResult(AdviceResponse(pending.Request)); }
        public void CompleteLightWrite()
        {
            var pending = pendingLightWrite!.Value; pendingLightWrite = null; Lighting = pending.Settings;
            pending.Completion.SetResult(PipeResponse.Success(pending.Request.RequestId, new { readbackConfirmed = true }));
        }
        public void CompleteAdviceWrite()
        {
            var pending = pendingAdviceWrite!.Value; pendingAdviceWrite = null;
            Status = Status with { Fan = Status.Fan with { Mode = ControlMode.Auto } };
            pending.Completion.SetResult(PipeResponse.Success(pending.Request.RequestId, Status));
        }
        private static PipeResponse LightResponse(PipeRequest request, LightingSettings settings) => PipeResponse.Success(request.RequestId,
            new { supported = true, settings, lastSuccessfulRead = DateTimeOffset.UtcNow });
        private static PipeResponse AdviceResponse(PipeRequest request) => PipeResponse.Success(request.RequestId,
            new CurveAdvice(DateTimeOffset.UtcNow, 90, 60, Curve, new([new(0, 28), new(60, 60), new(90, 100)]),
                [new(0, 30, 28, 60, "synthetic reason"), new(90, 100, 100, 0, "fixed upper point")], "synthetic only"));
    }
}
