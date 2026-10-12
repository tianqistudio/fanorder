using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace TianqiStudio.DesktopShortcuts;

[Flags]
public enum ShortcutModifiers { None = 0, Alt = 1, Control = 2, Shift = 4, Windows = 8 }
public enum ShortcutScopePolicy { RequiredGlobal, LocalOrGlobal, LocalOnly }
public sealed record ShortcutChord(int VirtualKey, ShortcutModifiers Modifiers);
public sealed record ShortcutBinding(ShortcutChord? Chord, bool Global);
public sealed record ShortcutAction(string Id, string Name, string Group, ShortcutScopePolicy Scope,
    ShortcutBinding DefaultBinding, string Help);
public sealed record ShortcutDocument(int Version, Dictionary<string, ShortcutBinding> Bindings);

public interface IShortcutStore
{
    ShortcutDocument Load();
    void Save(ShortcutDocument document);
}

public sealed class MemoryShortcutStore : IShortcutStore
{
    public ShortcutDocument Document { get; private set; } = new(1, new());
    public bool FailSave { get; set; }
    public ShortcutDocument Load() => new(Document.Version, new(Document.Bindings));
    public void Save(ShortcutDocument document)
    {
        if (FailSave) throw new IOException("Synthetic shortcut save failure.");
        Document = new(document.Version, new(document.Bindings));
    }
}

public sealed class LocalShortcutStore(string? filePath = null) : IShortcutStore
{
    private readonly string path = Path.GetFullPath(filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TianqiStudio", "DesktopShortcuts", "shortcuts.json"));
    private string? loadedFingerprint;
    private bool loaded;

    public ShortcutDocument Load()
    {
        loaded = false;
        var bytes = ReadBytes();
        var document = bytes is null ? new(1, new()) : JsonSerializer.Deserialize<ShortcutDocument>(bytes)
            ?? throw new InvalidDataException("快捷键文件为空。");
        ValidateDocument(document);
        loadedFingerprint = Fingerprint(bytes);
        loaded = true;
        return document;
    }

    public void Save(ShortcutDocument document)
    {
        if (!loaded) throw new IOException("快捷键文件未成功读取，不能覆盖；请修复文件后重新打开设置。");
        ValidateDocument(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > 256 * 1024) throw new InvalidDataException("快捷键文件超过允许大小。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!string.Equals(loadedFingerprint, Fingerprint(ReadBytes()), StringComparison.Ordinal))
            throw new IOException("快捷键已被其他窗口修改；请重新打开设置后再保存。");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            loadedFingerprint = Fingerprint(bytes);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private byte[]? ReadBytes()
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException("快捷键文件过大。");
        return File.ReadAllBytes(path);
    }

    private static string? Fingerprint(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes));

    private static void ValidateDocument(ShortcutDocument document)
    {
        if (document.Version != 1 || document.Bindings is null || document.Bindings.Count > 512 ||
            document.Bindings.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value is null))
            throw new InvalidDataException("快捷键文件版本或内容不受支持。");
    }
}

/// <summary>Builds an application-specific LocalAppData path without sharing another application's settings.</summary>
public static class ShortcutStorePaths
{
    public static string ForApplication(string publisher, string product, string fileName = "shortcuts.json")
    {
        if (string.IsNullOrWhiteSpace(publisher) || string.IsNullOrWhiteSpace(product) ||
            string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            publisher is "." or ".." || product is "." or ".." || fileName is "." or ".." ||
            publisher.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            product.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Publisher, product and fileName must be safe path components.");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            publisher, product, fileName);
    }
}

public interface IHotkeyRegistrar : IDisposable
{
    event Action<int>? Pressed;
    bool TryRegister(int id, ShortcutChord chord, out string? error);
    void Unregister(int id);
}

public sealed class FakeHotkeyRegistrar : IHotkeyRegistrar
{
    public event Action<int>? Pressed;
    public Dictionary<int, ShortcutChord> Registered { get; } = new();
    public HashSet<ShortcutChord> Unavailable { get; } = new();
    public bool Disposed { get; private set; }
    public bool TryRegister(int id, ShortcutChord chord, out string? error)
    {
        error = null;
        if (Disposed || Unavailable.Contains(chord) || Registered.ContainsValue(chord))
        { error = "组合已被其他程序占用。"; return false; }
        Registered.Add(id, chord);
        return true;
    }
    public void Raise(int id) => Pressed?.Invoke(id);
    public void Unregister(int id) => Registered.Remove(id);
    public void Dispose() { Registered.Clear(); Disposed = true; }
}

/// <summary>Contains no WPF, device, pipe or native calls. Registration and persistence are injectable.</summary>
public sealed class ShortcutManager : IDisposable
{
    private readonly IShortcutStore store;
    private readonly IHotkeyRegistrar registrar;
    private readonly Dictionary<string, ShortcutAction> actions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShortcutBinding> bindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> registrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> errors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShortcutBinding> loaded;
    private int nextId = 1;
    private bool disposed;
    public bool SuspendDispatch { get; set; }
    public string? StorageError { get; private set; }
    public IReadOnlyDictionary<string, ShortcutBinding> Bindings => bindings;
    public IReadOnlyDictionary<string, string> Errors => errors;
    public IReadOnlyCollection<ShortcutAction> Actions => actions.Values;
    public event Action<string>? ActionRequested;
    public event Action<ShortcutChord?>? ChordCaptured;

    public ShortcutManager(IShortcutStore store, IHotkeyRegistrar registrar)
    {
        this.store = store;
        this.registrar = registrar;
        try
        {
            var document = store.Load();
            if (document.Version != 1 || document.Bindings is null)
                throw new InvalidDataException("快捷键文件版本不受支持。");
            loaded = document.Bindings;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            loaded = new();
            StorageError = "无法读取快捷键，已使用默认值；原文件保持不变。";
        }
        registrar.Pressed += OnPressed;
    }

    public void ReplaceActions(IEnumerable<ShortcutAction> nextActions)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var next = nextActions.ToDictionary(action => action.Id, StringComparer.Ordinal);
        var removed = actions.Keys.Where(id => !next.ContainsKey(id)).ToArray();
        foreach (var id in removed)
        {
            if (registrations.Remove(id, out var registration)) registrar.Unregister(registration);
            bindings.Remove(id); errors.Remove(id); actions.Remove(id); loaded.Remove(id);
        }
        foreach (var action in next.Values)
        {
            var existed = actions.ContainsKey(action.Id);
            actions[action.Id] = action;
            if (existed) continue;
            var requested = loaded.TryGetValue(action.Id, out var saved) && saved is not null
                ? saved : action.DefaultBinding;
            var error = Validate(action, requested);
            if (error is null && requested.Chord is not null && bindings.Any(item => item.Value.Chord == requested.Chord))
                error = "组合与另一动作重复，请重新录入。";
            if (error is not null)
            {
                bindings[action.Id] = new(null, action.Scope == ShortcutScopePolicy.RequiredGlobal ||
                    action.Scope == ShortcutScopePolicy.LocalOrGlobal && requested.Global);
                errors[action.Id] = error;
                continue;
            }
            bindings[action.Id] = requested;
            if (requested is { Global: true, Chord: { } chord })
            {
                var id = nextId++;
                if (registrar.TryRegister(id, chord, out error)) registrations.Add(action.Id, id);
                else errors[action.Id] = error ?? "全局快捷键注册失败；该快捷键尚未生效。";
            }
        }
        if (removed.Length != 0)
        {
            try { SaveBindings(bindings); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            { StorageError = "已停用删除方案的快捷键，但未能保存清理结果。"; }
        }
    }

    public bool TryUpdate(string actionId, ShortcutBinding candidate, out string? error)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!actions.TryGetValue(actionId, out var action))
        { error = "动作已不存在。"; return false; }
        error = Validate(action, candidate);
        if (error is null && candidate.Chord is not null)
        {
            var duplicate = bindings.FirstOrDefault(item => item.Key != actionId && item.Value.Chord == candidate.Chord);
            if (duplicate.Key is not null) error = $"与“{actions[duplicate.Key].Name}”冲突；原绑定保持不变。";
        }
        if (error is not null) { errors[actionId] = error; return false; }

        var previous = bindings[actionId];
        var oldId = registrations.GetValueOrDefault(actionId);
        var sameNative = oldId != 0 && previous.Global && candidate.Global && previous.Chord == candidate.Chord;
        var newId = 0;
        if (!sameNative && candidate is { Global: true, Chord: { } chord })
        {
            newId = nextId++;
            if (!registrar.TryRegister(newId, chord, out error))
            {
                error = (error ?? "注册失败。") + " 原绑定保持不变。";
                errors[actionId] = error;
                return false;
            }
        }
        var next = new Dictionary<string, ShortcutBinding>(bindings) { [actionId] = candidate };
        try { SaveBindings(next); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (newId != 0) registrar.Unregister(newId);
            error = "无法保存快捷键；原绑定保持不变。" + exception.Message;
            errors[actionId] = error;
            return false;
        }
        if (!sameNative && oldId != 0) { registrar.Unregister(oldId); registrations.Remove(actionId); }
        if (newId != 0) registrations[actionId] = newId;
        bindings[actionId] = candidate;
        errors.Remove(actionId);
        StorageError = null;
        return true;
    }

    public bool TryDispatchLocal(ShortcutChord chord, bool isRepeat = false, bool textInput = false)
    {
        if (disposed || SuspendDispatch || isRepeat) return false;
        if (textInput && ShouldPreserveTextInput(chord)) return false;
        var match = bindings.FirstOrDefault(item => !item.Value.Global && item.Value.Chord == chord);
        if (match.Key is null) return false;
        ActionRequested?.Invoke(match.Key);
        return true;
    }

    public static bool ShouldPreserveTextInput(ShortcutChord chord)
    {
        // Keep typing, selection and standard edit/navigation commands in the editor.
        // The app's Ctrl+S, Ctrl+1/2/3 and function keys remain available.
        if ((chord.Modifiers & (ShortcutModifiers.Alt | ShortcutModifiers.Windows)) != 0) return false;
        if (chord.VirtualKey is >= 0x70 and <= 0x7A) return false;
        if (chord.Modifiers.HasFlag(ShortcutModifiers.Control) && chord.VirtualKey is 0x53 or 0x31 or 0x32 or 0x33)
            return false;
        return true;
    }

    private void OnPressed(int id)
    {
        if (disposed) return;
        var match = registrations.FirstOrDefault(item => item.Value == id);
        if (SuspendDispatch)
        {
            // RegisterHotKey can consume this key before WPF sees PreviewKeyDown.
            // Offer it to the recorder without ever executing the registered action.
            if (match.Key is not null) ChordCaptured?.Invoke(bindings[match.Key].Chord);
            return;
        }
        if (match.Key is not null) ActionRequested?.Invoke(match.Key);
    }

    private void SaveBindings(Dictionary<string, ShortcutBinding> source) => store.Save(new(1, new(source)));

    public static string? Validate(ShortcutAction action, ShortcutBinding binding)
    {
        if (action.Scope == ShortcutScopePolicy.RequiredGlobal && !binding.Global)
            return "此动作需要全局快捷键。";
        if (action.Scope == ShortcutScopePolicy.LocalOnly && binding.Global)
            return "此动作仅支持窗口内快捷键。";
        if (binding.Chord is not { } chord) return null;
        var key = chord.VirtualKey;
        var modifiers = chord.Modifiers;
        if ((modifiers & ~(ShortcutModifiers.Alt | ShortcutModifiers.Control | ShortcutModifiers.Shift)) != 0)
            return "不支持 Windows 键及未知修饰键。";
        if (key == 0x7B) return "F12 为系统保留按键，请使用其他组合。";
        if (!(key is >= 0x20 and <= 0x2D or >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or
            >= 0x60 and <= 0x7A or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2))
            return "此按键不可绑定；Esc 取消，Delete 清除。";
        if ((modifiers.HasFlag(ShortcutModifiers.Alt) && key is 0x73 or 0x20) ||
            (modifiers.HasFlag(ShortcutModifiers.Control) && modifiers.HasFlag(ShortcutModifiers.Alt) && key == 0x2E))
            return "此组合由 Windows 保留。";
        if (binding.Global && (modifiers & (ShortcutModifiers.Control | ShortcutModifiers.Alt)) == 0)
            return "全局快捷键至少需要 Ctrl 或 Alt。";
        return null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        registrar.Pressed -= OnPressed;
        foreach (var id in registrations.Values) registrar.Unregister(id);
        registrations.Clear();
        registrar.Dispose();
    }
}
