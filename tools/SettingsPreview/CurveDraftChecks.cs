using System.Reflection;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Ipc;
using V12UltraController.Settings;

internal static class CurveDraftChecks
{
    internal static void Run(Action<bool, string> assert)
    {
        var client = new DelayedCurveClient();
        var window = new MainWindow(client);
        var read = (Task)typeof(MainWindow).GetMethod("LoadCurveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { false })!;
        assert(!read.IsCompleted, "curve read can remain in flight without a UI window");
        window.CurvePoints[0].FanPercent = 35;
        client.Complete(new { points = new[] { new { temperatureCelsius = 0, fanPercent = 34 },
            new { temperatureCelsius = 90, fanPercent = 100 } } });
        read.GetAwaiter().GetResult();
        assert(window.CurvePoints[0].FanPercent == 35 && window.CurvePoints.Count == FanCurve.RemoteHostSafe.Points.Count,
            "in-flight curve read does not overwrite edits made while waiting");
        assert(window.CurveDraftChanged, "retained curve differs from the freshly read backend baseline");
        var apply = (Task)typeof(MainWindow).GetMethod("ApplyCurveDraftAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null)!;
        assert(!apply.IsCompleted && client.Request!.Operation == PipeOperations.CurveSet,
            "curve apply waits for the submitted snapshot response");
        assert(client.Request!.Payload.GetProperty("points")[0].GetProperty("fanPercent").GetByte() == 35,
            "submitted curve carries the value present when apply was invoked");
        window.CurvePoints[0].FanPercent = 33;
        client.Complete(new { saved = true });
        apply.GetAwaiter().GetResult();
        assert(window.CurvePoints[0].FanPercent == 33 && window.CurveDraftChanged,
            "late curve confirmation cannot mark newer edits as saved");
        window.CurvePoints[0].FanPercent = 35;
        assert(!window.CurveDraftChanged, "only the submitted curve is accepted as the saved baseline");
        var staleRead = (Task)typeof(MainWindow).GetMethod("LoadCurveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { true })!;
        var finishStaleRead = client.CaptureReply();
        assert(!(bool)typeof(MainWindow).GetField("_commandInProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!,
            "curve cache read does not occupy the global device-command state");
        var saveDuringRead = (Task)typeof(MainWindow).GetMethod("ApplyCurveDraftAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null)!;
        client.Complete(new { saved = true });
        saveDuringRead.GetAwaiter().GetResult();
        finishStaleRead(new { points = new[] { new { temperatureCelsius = 0, fanPercent = 34 },
            new { temperatureCelsius = 90, fanPercent = 100 } } });
        staleRead.GetAwaiter().GetResult();
        assert(window.CurvePoints[0].FanPercent == 35 && !window.CurveDraftChanged,
            "a cache response begun before save cannot replace the confirmed curve or its baseline");
        assert(!window.IsVisible, "curve race checks never show a native window");
    }

    private sealed class DelayedCurveClient : IPipeApiClient
    {
        private TaskCompletionSource<PipeResponse>? completion;
        internal PipeRequest? Request { get; private set; }
        public Task<PipeResponse> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (request.Operation is not (PipeOperations.CurveGet or PipeOperations.CurveSet))
                throw new InvalidOperationException("Unexpected request in isolated curve draft check.");
            Request = request;
            completion = new TaskCompletionSource<PipeResponse>();
            return completion.Task;
        }
        internal void Complete<T>(T payload) => completion!.SetResult(PipeResponse.Success(Request!.RequestId, payload));
        internal Action<object> CaptureReply()
        {
            var pending = completion!;
            var request = Request!;
            return payload => pending.SetResult(PipeResponse.Success(request.RequestId, payload));
        }
    }
}
