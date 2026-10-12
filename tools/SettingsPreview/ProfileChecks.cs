using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Controls;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;
using V12UltraController.Core.Hardware;
using V12UltraController.Core.Ipc;
using V12UltraController.Core.Status;
using V12UltraController.Settings;

internal static class ProfileChecks
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    internal static void Run(Action<bool, string> assert)
    {
        CheckStore(assert);
        CheckEditor(assert);
    }

    private static ControlProfileSnapshot Sample(string id = "profile-one", string name = "日常自动") =>
        new(id, name, ControlMode.Auto, 60, new FanCurve([new(0, 30), new(50, 60), new(90, 100)]),
            new LightingSettings(true, 1, 2, 180, 2));

    private static void CheckStore(Action<bool, string> assert)
    {
        var memory = new MemoryControlProfileStore([Sample()]);
        var copy = memory.Load()[0];
        assert(copy.Id == "profile-one" && !ReferenceEquals(copy.Curve, memory.Load()[0].Curve),
            "profile store returns independent saved snapshots");
        var invalidRejected = false;
        try { memory.Save([Sample(), Sample("different-id")]); }
        catch (InvalidDataException) { invalidRejected = true; }
        assert(invalidRejected && memory.Load().Count == 1, "duplicate profile names cannot replace saved data");

        // Test state lives only beside this offline build output, never under the
        // user's LocalApplicationData or the production preference directory.
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        var directory = Path.GetFullPath(Path.Combine(root, "profile-check-" + Guid.NewGuid().ToString("N")));
        if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test directory.");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "profiles.json");
            var store = new LocalControlProfileStore(path);
            assert(store.Load().Count == 0 && !File.Exists(path), "first profile read is empty and does not create a preference file");
            store.Save([Sample(), Sample("profile-two", "只控制风扇") with { Lighting = null }]);
            var reloaded = new LocalControlProfileStore(path).Load();
            assert(reloaded.Count == 2 && reloaded[0].Lighting?.Brightness == 180 && reloaded[1].Lighting is null,
                "versioned profile file round-trips curves and optional lighting");
            using (var document = JsonDocument.Parse(File.ReadAllBytes(path)))
                assert(document.RootElement.GetProperty("schemaVersion").GetInt32() == 1,
                    "profile file has explicit schema version");

            var contender = new LocalControlProfileStore(path);
            contender.Load();
            store.Save([Sample() with { Name = "已更新" }]);
            var conflictRejected = false;
            try { contender.Save([Sample() with { Name = "过期窗口" }]); }
            catch (IOException) { conflictRejected = true; }
            assert(conflictRejected && new LocalControlProfileStore(path).Load()[0].Name == "已更新",
                "stale profile window cannot overwrite a newer save");

            File.WriteAllText(path, "{broken-json");
            var corrupted = new LocalControlProfileStore(path);
            var loadRejected = false;
            try { corrupted.Load(); }
            catch (JsonException) { loadRejected = true; }
            var saveRejected = false;
            try { corrupted.Save([Sample()]); }
            catch (InvalidOperationException) { saveRejected = true; }
            assert(loadRejected && saveRejected && File.ReadAllText(path) == "{broken-json",
                "corrupt profile file is preserved and cannot be silently reset");
            File.WriteAllText(path, "{\"schemaVersion\":999,\"profiles\":[]}");
            var unknownVersion = false;
            try { new LocalControlProfileStore(path).Load(); }
            catch (InvalidDataException) { unknownVersion = true; }
            assert(unknownVersion && File.ReadAllText(path).Contains("999", StringComparison.Ordinal),
                "unknown profile schema is rejected without rewriting its file");
        }
        finally
        {
            var resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }

    private static void CheckEditor(Action<bool, string> assert)
    {
        var client = new ProfileFakeClient();
        var store = new MemoryControlProfileStore([Sample(), Sample("profile-two", "手动方案") with { Mode = ControlMode.Manual }]);
        var window = new MainWindow(client, profileStore: store);
        var broughtForward = 0;
        window.BringProfileConfirmationToFront = () => broughtForward++;
        window.ConfirmProfileAction = _ => true;
        Render(window, client.Status);
        var changes = 0;
        window.ProfilesChanged += () => changes++;
        assert(window.SavedProfiles.Count == 2 && client.Requests.Count == 0,
            "initializing saved profile editor uses injected storage and no IPC");
        window.NavigateToPage("profiles");
        var curve = Field<DataGrid>(window, "_profileCurveGrid");
        var first = ((IEnumerable<MainWindow.ProfileCurveRow>)curve.ItemsSource).First();
        first.FanPercent = 31;
        assert(Field<TextBlock>(window, "_profileDraftLabel").Text.Contains("未保存", StringComparison.Ordinal),
            "loaded profile curve edits are tracked as unsaved");
        window.NavigateToPage("lighting");
        window.NavigateToPage("profiles");
        assert(first.FanPercent == 31 && window.SavedProfiles[0].Curve.Points[0].FanPercent == 30,
            "navigation preserves profile drafts separately from saved snapshots");
        Field<ComboBox>(window, "_profileMode").SelectedIndex = 1;
        Field<Slider>(window, "_profileManual").Value = 43;
        window.ApplySavedProfileAsync("profile-one").GetAwaiter().GetResult();
        assert(client.Applied.Single().Profile.ManualSpeedPercent == 60 && client.Applied.Single().Profile.Mode == ControlMode.Auto,
            "profile apply sends only the saved snapshot while draft differs");
        assert(Field<Slider>(window, "_profileManual").Value == 43 && first.FanPercent == 31,
            "profile application and status refresh do not overwrite draft controls");
        window.SaveProfileDraftAsync().GetAwaiter().GetResult();
        assert(window.SavedProfiles.Single(profile => profile.Id == "profile-one").ManualSpeedPercent == 43 &&
            window.SavedProfiles.Single(profile => profile.Id == "profile-one").Curve.Points[0].FanPercent == 31 && changes == 1,
            "profile save commits independent draft and notifies shortcut catalog");

        window.RequestProfileName = _ => "新名称";
        Invoke(window, "RenameProfile");
        assert(window.SavedProfiles.Single(profile => profile.Id == "profile-one").Name == "新名称" && changes == 2,
            "profile rename keeps stable id and notifies shortcut catalog");
        Invoke(window, "DuplicateProfile");
        assert(window.SavedProfiles.Count == 2 && Field<TextBlock>(window, "_profileDraftLabel").Text.Contains("尚未保存", StringComparison.Ordinal),
            "duplicating a saved profile creates an unsaved independent draft");
        window.SaveProfileDraftAsync().GetAwaiter().GetResult();
        assert(window.SavedProfiles.Count == 3 && window.SavedProfiles.Select(profile => profile.Id).Distinct().Count() == 3,
            "saved profile copy receives a new stable id");
        var priorCalls = client.Applied.Count;
        Invoke(window, "DeleteProfile");
        assert(window.SavedProfiles.Count == 2 && client.Applied.Count == priorCalls,
            "deleting profile is local and does not alter backend control");

        Field<ComboBox>(window, "_profileMode").SelectedIndex = 0;
        first = ((IEnumerable<MainWindow.ProfileCurveRow>)curve.ItemsSource).First();
        first.FanPercent = 300;
        var oldCount = changes;
        window.SaveProfileDraftAsync().GetAwaiter().GetResult();
        assert(changes == oldCount && window.SavedProfiles.All(profile => profile.Curve.Points[0].FanPercent <= 100),
            "out-of-range curve draft cannot overwrite saved profile");
        first.FanPercent = 31;

        var manual = client.Status with { Fan = client.Status.Fan with { Mode = ControlMode.Manual, PoweredOn = true } };
        client.Status = manual with { Device = manual.Device with { Connected = false } };
        Render(window, client.Status);
        var before = client.Applied.Count;
        window.ApplySavedProfileAsync("profile-one").GetAwaiter().GetResult();
        assert(client.Applied.Count == before, "ordinary mode refuses applying to a disconnected device");
        client.Status = client.Status with { Fan = client.Status.Fan with { Mode = ControlMode.Released } };
        Render(window, client.Status);
        window.ConfirmProfileAction = _ => false;
        window.ApplySavedProfileAsync("profile-one").GetAwaiter().GetResult();
        assert(client.Applied.Count == before && broughtForward == 0 && Field<TextBlock>(window, "_profileResult").Text.Contains("纯手动"),
            "released mode requires an explicit puremanual selection before profile application");
        client.Status = client.Status with { Fan = client.Status.Fan with { Mode = ControlMode.PureManual } };
        Render(window, client.Status);
        window.ConfirmProfileAction = _ => true;
        typeof(MainWindow).GetField("trayPendingPercent", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, (byte?)71);
        window.ApplySavedProfileAsync("profile-one").GetAwaiter().GetResult();
        assert(client.Applied.Count == before + 1 && client.Applied[^1].ConfirmResumeControl,
            "confirmed puremanual mode can reenter protected control without a prior open HID connection");
        assert(typeof(MainWindow).GetField("trayPendingPercent", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is null,
            "profile application clears delayed tray speed requests");

        client.Status = manual with { Fan = manual.Fan with { PoweredOn = false, OffRequested = true } };
        Render(window, client.Status);
        string? confirmation = null;
        window.ConfirmProfileAction = message => { confirmation = message; return false; };
        before = client.Applied.Count;
        window.ApplySavedProfileAsync("profile-one").GetAwaiter().GetResult();
        assert(confirmation?.Contains("开启风扇", StringComparison.Ordinal) == true && client.Applied.Count == before,
            "applying from fan OFF clearly confirms fan startup before IPC");

        client.Status = manual;
        client.Outcome = ProfileApplyOutcome.Partial;
        Render(window, client.Status);
        window.ApplySavedProfileAsync("profile-one").GetAwaiter().GetResult();
        assert(Field<TextBlock>(window, "_profileResult").Text.Contains("未完整应用", StringComparison.Ordinal),
            "partial backend result is never presented as complete application");

        var emptyClient = new ProfileFakeClient();
        var emptyWindow = new MainWindow(emptyClient);
        Render(emptyWindow, emptyClient.Status);
        assert(emptyWindow.SavedProfiles.Count == 0, "first-run profile list has no fabricated presets");
        ((Task)Invoke(emptyWindow, "CreateProfileFromCurrentAsync")!).GetAwaiter().GetResult();
        assert(emptyWindow.SavedProfiles.Count == 0 && emptyClient.Requests.All(request => request.Operation is
            PipeOperations.StatusGet or PipeOperations.CurveGet or PipeOperations.LightingGet),
            "creating from current reads valid backend configuration without writes or implicit save");
        emptyWindow.SaveProfileDraftAsync().GetAwaiter().GetResult();
        assert(emptyWindow.SavedProfiles.Single().Curve.Points[0].FanPercent == 35 &&
            emptyWindow.SavedProfiles.Single().Lighting?.Color == 3,
            "new profile captures observed curve and lighting cache rather than UI defaults");
    }

    private static void Render(MainWindow window, ControllerStatus status) =>
        typeof(MainWindow).GetMethod("RenderStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [status]);
    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static object? Invoke(MainWindow window, string name) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);

    private sealed class ProfileFakeClient : IPipeApiClient
    {
        internal ControllerStatus Status { get; set; } = new("Running", true, "synthetic",
            new(true, "374A", "B101", "1602", true), new(ControlMode.Auto, 60, 60, true, 60, true),
            new(45, [40], 45, true, DateTimeOffset.UtcNow), new(false, FailSafeReason.None, null),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new(false, null, null),
            Software: new("test", UiCompatibility.ContractVersion, true, ProfileApplySupported: true));
        internal List<PipeRequest> Requests { get; } = [];
        internal List<ProfileApplyRequest> Applied { get; } = [];
        internal ProfileApplyOutcome Outcome { get; set; } = ProfileApplyOutcome.Completed;

        public Task<PipeResponse> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            object result = request.Operation switch
            {
                PipeOperations.StatusGet => Status,
                PipeOperations.CurveGet => new FanCurve([new(0, 35), new(60, 70), new(90, 100)]),
                PipeOperations.LightingGet => new { supported = true, settings = new LightingSettings(true, 1, 3, 150, 2) },
                PipeOperations.ProfileApply => Apply(request),
                _ => throw new InvalidOperationException("Unexpected fake-only operation: " + request.Operation),
            };
            return Task.FromResult(new PipeResponse(PipeProtocol.ApiVersion, request.RequestId, true,
                JsonSerializer.SerializeToElement(result, JsonOptions), null, DateTimeOffset.UtcNow));
        }

        private ProfileApplyResult Apply(PipeRequest request)
        {
            var payload = request.Payload.Deserialize<ProfileApplyRequest>(JsonOptions)!;
            Applied.Add(payload);
            var application = new ProfileApplicationStatus(request.RequestId, payload.Profile.Id, payload.Profile.Name,
                ProfileApplyStage.Finished, Outcome, ProfileConfigurationState.Saved, ProfileFanState.Confirmed,
                payload.Profile.Lighting is null ? ProfileLightingState.NotRequested : ProfileLightingState.Confirmed,
                Outcome == ProfileApplyOutcome.Completed ? null : "合成灯光验证失败", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            Status = Status with { Device = Status.Device with { Connected = true },
                Fan = Status.Fan with { Mode = payload.Profile.Mode, PoweredOn = true, OffRequested = false },
                ProfileApplication = application };
            return new ProfileApplyResult(application, Status);
        }
    }
}
