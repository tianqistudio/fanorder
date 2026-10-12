using System.IO;
using TianqiStudio.DesktopShortcuts;
using V12UltraController.Settings;

internal static class ShortcutChecks
{
    internal static void Run(Action<bool, string> assert, string? outputRoot = null)
    {
        var ctrlAlt = ShortcutModifiers.Control | ShortcutModifiers.Alt;
        var show = new ShortcutChord(0x46, ctrlAlt);
        var profileChord = new ShortcutChord(0x50, ctrlAlt);
        var rejectedChord = new ShortcutChord(0x51, ctrlAlt);
        var unsavedChord = new ShortcutChord(0x52, ctrlAlt);
        var refreshChord = new ShortcutChord(0x74, ShortcutModifiers.None);
        var copyChord = new ShortcutChord(0x43, ShortcutModifiers.Control);
        var saveChord = new ShortcutChord(0x53, ShortcutModifiers.Control);
        ShortcutAction Action(string id, ShortcutScopePolicy policy, ShortcutChord? chord, bool global) =>
            new(id, id, "Synthetic", policy, new(chord, global), "Synthetic action; no device access.");
        var actions = new[]
        {
            Action("window.toggle", ShortcutScopePolicy.RequiredGlobal, show, true),
            Action("profile:stable-id", ShortcutScopePolicy.LocalOrGlobal, null, true),
            Action("fan.increase", ShortcutScopePolicy.LocalOrGlobal, null, true),
            Action("fan.off", ShortcutScopePolicy.LocalOrGlobal, null, false),
            Action("window.refresh", ShortcutScopePolicy.LocalOnly, refreshChord, false),
            Action("profile.save", ShortcutScopePolicy.LocalOnly, saveChord, false),
            Action("copy-collision", ShortcutScopePolicy.LocalOrGlobal, copyChord, false),
            Action("typing-collision", ShortcutScopePolicy.LocalOrGlobal, new(0x41, ShortcutModifiers.None), false),
        };
        var store = new MemoryShortcutStore();
        var registrar = new FakeHotkeyRegistrar();
        var manager = new ShortcutManager(store, registrar);
        var dispatched = new List<string>();
        var captured = new List<ShortcutChord?>();
        manager.ActionRequested += dispatched.Add;
        manager.ChordCaptured += captured.Add;
        manager.ReplaceActions(actions);
        assert(registrar.Registered.Count == 1 && registrar.Registered.Values.Single() == show,
            "shortcuts register only the default global window chord through fake registrar");
        assert(store.Document.Bindings.Count == 0, "loading shortcut defaults does not write preferences");
        var showRegistration = registrar.Registered.Keys.Single();
        registrar.Raise(showRegistration);
        assert(dispatched.SequenceEqual(new[] { "window.toggle" }), "fake global dispatch works independently of window visibility");
        assert(!manager.TryDispatchLocal(show), "global chord is not duplicated by local dispatch");
        assert(manager.TryDispatchLocal(refreshChord) && dispatched[^1] == "window.refresh", "local refresh dispatches");
        var count = dispatched.Count;
        assert(!manager.TryDispatchLocal(refreshChord, isRepeat: true) && dispatched.Count == count,
            "local key autorepeat is suppressed");
        manager.SuspendDispatch = true;
        registrar.Raise(showRegistration);
        assert(dispatched.Count == count && captured.Single() == show,
            "recording captures an already registered global chord without triggering its action");
        assert(!manager.TryDispatchLocal(refreshChord) && dispatched.Count == count,
            "recording pauses local shortcuts");
        manager.SuspendDispatch = false;
        assert(manager.TryDispatchLocal(refreshChord), "dispatch resumes after capture cancellation");

        assert(!manager.TryUpdate("profile:stable-id", new(show, false), out var conflict) && conflict!.Contains("冲突"),
            "duplicate chords are rejected across local and global scopes");
        assert(manager.Bindings["profile:stable-id"].Chord is null, "duplicate rejection retains the previous binding");
        assert(!manager.TryUpdate("window.toggle", new(show, false), out _), "required global action rejects window-only scope");
        assert(!manager.TryUpdate("window.refresh", new(new(0x74, ctrlAlt), true), out _), "local-only action rejects global scope");
        assert(!manager.TryUpdate("fan.increase", new(new(0x41, ShortcutModifiers.Shift), true), out _),
            "global shortcuts require Ctrl or Alt");
        assert(!manager.TryUpdate("fan.increase", new(new(0x41, ShortcutModifiers.Windows), false), out _), "Windows key bindings are rejected");
        assert(!manager.TryUpdate("fan.increase", new(new(0x7B, ctrlAlt), true), out _), "F12 is rejected");
        assert(!manager.TryUpdate("fan.increase", new(new(0x73, ShortcutModifiers.Alt), true), out _), "Alt+F4 is rejected");
        assert(!manager.TryUpdate("fan.increase", new(new(0x20, ShortcutModifiers.Alt), true), out _), "Alt+Space is rejected");
        assert(!manager.TryUpdate("fan.increase", new(new(0xA0, ctrlAlt), true), out _), "modifier-only virtual keys are rejected");
        assert(!manager.TryUpdate("fan.increase", new(new(0x2E, ctrlAlt), true), out _), "Delete cannot become a device shortcut");
        assert(manager.TryUpdate("profile:stable-id", new(profileChord, true), out _), "valid global profile binding persists");
        var profileRegistration = registrar.Registered.Single(item => item.Value == profileChord).Key;
        assert(store.Document.Version == 1 && store.Document.Bindings["profile:stable-id"].Chord == profileChord,
            "versioned persistence uses the stable profile action id");
        registrar.Unavailable.Add(rejectedChord);
        assert(!manager.TryUpdate("profile:stable-id", new(rejectedChord, true), out var rejected) && rejected!.Contains("原绑定"),
            "native registration failure reports failure and preserves previous binding");
        assert(manager.Bindings["profile:stable-id"].Chord == profileChord && registrar.Registered.ContainsKey(profileRegistration) &&
            store.Document.Bindings["profile:stable-id"].Chord == profileChord, "registration failure leaves memory, native registration and disk binding aligned");
        count = dispatched.Count;
        registrar.Raise(profileRegistration);
        assert(dispatched.Count == count + 1 && dispatched[^1] == "profile:stable-id", "previous binding still dispatches after replacement failure");

        store.FailSave = true;
        assert(!manager.TryUpdate("profile:stable-id", new(unsavedChord, true), out var failedSave) && failedSave!.Contains("保存"),
            "save failure is surfaced instead of claiming success");
        assert(!registrar.Registered.ContainsValue(unsavedChord) && registrar.Registered.ContainsKey(profileRegistration) &&
            manager.Bindings["profile:stable-id"].Chord == profileChord,
            "save failure unregisters only the staged registration and preserves the old shortcut");
        store.FailSave = false;
        assert(manager.TryUpdate("profile:stable-id", new(profileChord, false), out _) && !registrar.Registered.ContainsKey(profileRegistration),
            "changing to local scope releases the native registration");
        assert(manager.TryDispatchLocal(profileChord) && dispatched[^1] == "profile:stable-id", "changed local scope dispatches locally");
        assert(manager.TryUpdate("profile:stable-id", new(profileChord, true), out _), "local binding can return to global scope");
        manager.ReplaceActions(actions.Select(action => action.Id == "profile:stable-id" ? action with { Name = "Renamed profile" } : action));
        assert(manager.Bindings["profile:stable-id"].Chord == profileChord, "profile rename preserves binding by stable id");

        count = dispatched.Count;
        assert(!manager.TryDispatchLocal(copyChord, textInput: true) && dispatched.Count == count, "Ctrl+C remains an edit command inside text input");
        assert(!manager.TryDispatchLocal(new(0x41, ShortcutModifiers.None), textInput: true), "typing a letter cannot invoke a local device action");
        assert(manager.TryDispatchLocal(copyChord), "custom local chord remains available outside text input");
        assert(manager.TryDispatchLocal(refreshChord, textInput: true), "F5 remains available while editing text");
        assert(manager.TryDispatchLocal(saveChord, textInput: true) && dispatched[^1] == "profile.save", "Ctrl+S still saves the profile draft while editing");

        manager.ReplaceActions(actions.Where(action => action.Id != "profile:stable-id"));
        assert(!manager.Bindings.ContainsKey("profile:stable-id") && !registrar.Registered.ContainsValue(profileChord),
            "deleting a saved profile releases its shortcut immediately");
        assert(!store.Document.Bindings.ContainsKey("profile:stable-id"), "deleted profile binding is removed from persistence");
        count = dispatched.Count;
        registrar.Raise(profileRegistration);
        assert(dispatched.Count == count, "stale native ids cannot dispatch deleted profile actions");
        assert(manager.TryUpdate("window.toggle", new(null, true), out _) && !registrar.Registered.ContainsValue(show),
            "clearing a chord releases its global registration");
        manager.Dispose();
        assert(registrar.Disposed && registrar.Registered.Count == 0, "exit disposes and releases all fake native registrations");
        assert(!manager.TryDispatchLocal(refreshChord), "disposed shortcut manager cannot dispatch local actions");
        manager.Dispose();

        var unavailable = new FakeHotkeyRegistrar();
        unavailable.Unavailable.Add(show);
        using (var startup = new ShortcutManager(new MemoryShortcutStore(), unavailable))
        {
            startup.ReplaceActions(actions);
            assert(startup.Errors.ContainsKey("window.toggle") && unavailable.Registered.Count == 0,
                "startup conflict stays visible and is never represented as an active global binding");
        }
        var malformed = new MemoryShortcutStore();
        malformed.Save(new(1, new() { ["window.toggle"] = new(new(0x7B, ctrlAlt), true) }));
        using (var restored = new ShortcutManager(malformed, new FakeHotkeyRegistrar()))
        {
            restored.ReplaceActions(actions);
            assert(restored.Bindings["window.toggle"].Chord is null && restored.Errors.ContainsKey("window.toggle"),
                "invalid saved chord is disabled and surfaced rather than registered");
        }

        if (outputRoot is null) return;
        var storePath = Path.Combine(outputRoot, "shortcut-store-checks", "shortcuts.json");
        var localStore = new LocalShortcutStore(storePath);
        assert(localStore.Load().Bindings.Count == 0 && !File.Exists(storePath), "missing shortcut file reads defaults without creating user state");
        localStore.Save(new(1, new() { ["test"] = new(profileChord, true) }));
        assert(localStore.Load().Bindings["test"].Chord == profileChord, "isolated versioned shortcuts.json round trip succeeds");
        localStore.Save(new(1, new() { ["test"] = new(rejectedChord, false) }));
        assert(localStore.Load().Bindings["test"].Chord == rejectedChord &&
            Directory.GetFiles(Path.GetDirectoryName(storePath)!, "*.tmp").Length == 0,
            "atomic shortcut replacement leaves the complete latest document and no temporary file");
        var otherWindowStore = new LocalShortcutStore(storePath);
        otherWindowStore.Load();
        localStore.Save(new(1, new() { ["test"] = new(profileChord, true) }));
        var conflictRejected = false;
        try { otherWindowStore.Save(new(1, new() { ["test"] = new(unsavedChord, true) })); }
        catch (IOException) { conflictRejected = true; }
        assert(conflictRejected && localStore.Load().Bindings["test"].Chord == profileChord,
            "a second settings window cannot overwrite shortcuts changed since its last read");
        var staleRegistrar = new FakeHotkeyRegistrar();
        using (var staleManager = new ShortcutManager(otherWindowStore, staleRegistrar))
        {
            staleManager.ReplaceActions(actions);
            localStore.Save(new(1, new() { ["test"] = new(rejectedChord, false) }));
            assert(!staleManager.TryUpdate("window.toggle", new(unsavedChord, true), out var staleError) &&
                staleError!.Contains("其他窗口") && staleRegistrar.Registered.ContainsValue(show) &&
                !staleRegistrar.Registered.ContainsValue(unsavedChord),
                "concurrent save rejection rolls staged hotkey back and retains the previous live registration");
        }
        var uninitializedStore = new LocalShortcutStore(storePath);
        var uninitializedRejected = false;
        try { uninitializedStore.Save(new(1, new())); }
        catch (IOException) { uninitializedRejected = true; }
        assert(uninitializedRejected, "shortcut persistence refuses writes without a successful initial read");
        File.WriteAllText(storePath, "{broken json");
        using (var corrupted = new ShortcutManager(localStore, new FakeHotkeyRegistrar()))
        {
            corrupted.ReplaceActions(actions);
            assert(corrupted.StorageError is not null && File.ReadAllText(storePath) == "{broken json",
                "corrupted input is surfaced and retained untouched during initialization");
            assert(!corrupted.TryUpdate("window.toggle", new(unsavedChord, true), out _) &&
                File.ReadAllText(storePath) == "{broken json", "editing after a failed load cannot overwrite the malformed source file");
        }
        const string incompatibleDocument = "{\"Version\":2,\"Bindings\":{}}";
        File.WriteAllText(storePath, incompatibleDocument);
        using var incompatible = new ShortcutManager(new LocalShortcutStore(storePath), new FakeHotkeyRegistrar());
        incompatible.ReplaceActions(actions);
        assert(incompatible.StorageError is not null && !incompatible.TryUpdate("window.toggle", new(null, true), out _) &&
            File.ReadAllText(storePath) == incompatibleDocument, "future shortcut document versions remain intact even after an attempted edit");
    }
}
