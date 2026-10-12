using TianqiStudio.DesktopShortcuts;
using Xunit;

namespace TianqiStudio.DesktopShortcuts.Tests;

public sealed class ShortcutManagerTests
{
    private static ShortcutAction Action(
        string id,
        ShortcutScopePolicy scope = ShortcutScopePolicy.LocalOrGlobal,
        ShortcutChord? chord = null,
        bool global = false) =>
        new(id, id, "test", scope, new(chord, global), "test action");

    [Fact]
    public void ReplaceActionsLoadsDefaultsAndRegistersGlobalBinding()
    {
        var store = new MemoryShortcutStore();
        using var registrar = new FakeHotkeyRegistrar();
        using var manager = new ShortcutManager(store, registrar);

        manager.ReplaceActions([Action("show", ShortcutScopePolicy.RequiredGlobal,
            new(0x46, ShortcutModifiers.Control | ShortcutModifiers.Alt), true)]);

        Assert.Equal(new ShortcutChord(0x46, ShortcutModifiers.Control | ShortcutModifiers.Alt),
            manager.Bindings["show"].Chord);
        Assert.Single(registrar.Registered);
        Assert.Empty(manager.Errors);
    }

    [Fact]
    public void DuplicateBindingsAreRejectedWithoutChangingExistingBinding()
    {
        using var manager = new ShortcutManager(new MemoryShortcutStore(), new FakeHotkeyRegistrar());
        manager.ReplaceActions([
            Action("one", chord: new(0x41, ShortcutModifiers.Control), global: false),
            Action("two"),
        ]);

        var changed = manager.TryUpdate("two",
            new(new(0x41, ShortcutModifiers.Control), false), out var error);

        Assert.False(changed);
        Assert.Contains("冲突", error);
        Assert.Null(manager.Bindings["two"].Chord);
    }

    [Fact]
    public void FailedNativeRegistrationKeepsPreviousBinding()
    {
        var registrar = new FakeHotkeyRegistrar();
        using var manager = new ShortcutManager(new MemoryShortcutStore(), registrar);
        var first = new ShortcutChord(0x41, ShortcutModifiers.Control | ShortcutModifiers.Alt);
        var second = new ShortcutChord(0x42, ShortcutModifiers.Control | ShortcutModifiers.Alt);
        manager.ReplaceActions([Action("one", chord: first, global: true)]);
        registrar.Unavailable.Add(second);

        var changed = manager.TryUpdate("one", new(second, true), out var error);

        Assert.False(changed);
        Assert.Contains("占用", error);
        Assert.Equal(first, manager.Bindings["one"].Chord);
        Assert.Single(registrar.Registered);
    }

    [Fact]
    public void FailedSaveUnregistersNewBindingAndKeepsOldBinding()
    {
        var store = new MemoryShortcutStore();
        using var registrar = new FakeHotkeyRegistrar();
        using var manager = new ShortcutManager(store, registrar);
        var first = new ShortcutChord(0x41, ShortcutModifiers.Control | ShortcutModifiers.Alt);
        var second = new ShortcutChord(0x42, ShortcutModifiers.Control | ShortcutModifiers.Alt);
        manager.ReplaceActions([Action("one", chord: first, global: true)]);
        store.FailSave = true;

        var changed = manager.TryUpdate("one", new(second, true), out var error);

        Assert.False(changed);
        Assert.Contains("保存", error);
        Assert.Equal(first, manager.Bindings["one"].Chord);
        Assert.Single(registrar.Registered);
        Assert.DoesNotContain(second, registrar.Registered.Values);
    }

    [Fact]
    public void LocalDispatchHonorsSuspensionRepeatAndTextInputRules()
    {
        using var manager = new ShortcutManager(new MemoryShortcutStore(), new FakeHotkeyRegistrar());
        var chord = new ShortcutChord(0x41, ShortcutModifiers.None);
        manager.ReplaceActions([Action("local", chord: chord, global: false)]);
        var calls = 0;
        manager.ActionRequested += _ => calls++;

        Assert.True(manager.TryDispatchLocal(chord));
        Assert.False(manager.TryDispatchLocal(chord, isRepeat: true));
        Assert.False(manager.TryDispatchLocal(chord, textInput: true));
        manager.SuspendDispatch = true;
        Assert.False(manager.TryDispatchLocal(chord));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void DisposeUnregistersAllNativeBindings()
    {
        var registrar = new FakeHotkeyRegistrar();
        var manager = new ShortcutManager(new MemoryShortcutStore(), registrar);
        manager.ReplaceActions([Action("one", chord: new(0x41, ShortcutModifiers.Control), global: true)]);

        manager.Dispose();

        Assert.Empty(registrar.Registered);
        Assert.True(registrar.Disposed);
    }

    [Fact]
    public void StorePathIsApplicationSpecificAndRejectsUnsafeFileName()
    {
        var path = ShortcutStorePaths.ForApplication("ExamplePublisher", "ExampleProduct");

        Assert.EndsWith(Path.Combine("ExamplePublisher", "ExampleProduct", "shortcuts.json"), path);
        Assert.Throws<ArgumentException>(() =>
            ShortcutStorePaths.ForApplication("ExamplePublisher", "ExampleProduct", "..\\other.json"));
        Assert.Throws<ArgumentException>(() =>
            ShortcutStorePaths.ForApplication("ExamplePublisher", "ExampleProduct", "bad*name.json"));
        Assert.Throws<ArgumentException>(() =>
            ShortcutStorePaths.ForApplication("..", "ExampleProduct"));
        Assert.Throws<ArgumentException>(() =>
            ShortcutStorePaths.ForApplication("ExamplePublisher", "ExampleProduct", ".."));
    }

    [Fact]
    public void LocalStoreDetectsConcurrentChange()
    {
        var root = Path.Combine(Path.GetTempPath(), "desktop-shortcuts-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "shortcuts.json");
        try
        {
            var first = new LocalShortcutStore(path);
            _ = first.Load();
            Directory.CreateDirectory(root);
            File.WriteAllText(path, "{\"Version\":1,\"Bindings\":{\"other\":{\"Chord\":null,\"Global\":false}}}");

            Assert.Throws<IOException>(() => first.Save(new(1, new())));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
