using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using V12UltraController.Core.Configuration;

namespace V12UltraController.Settings;

internal interface IControlProfileStore
{
    IReadOnlyList<ControlProfileSnapshot> Load();
    void Save(IReadOnlyList<ControlProfileSnapshot> profiles);
}

internal sealed class MemoryControlProfileStore : IControlProfileStore
{
    private IReadOnlyList<ControlProfileSnapshot> _profiles = [];

    public MemoryControlProfileStore(IReadOnlyList<ControlProfileSnapshot>? profiles = null)
    {
        if (profiles is not null) Save(profiles);
    }

    public IReadOnlyList<ControlProfileSnapshot> Load() => ControlProfileStoreData.Copy(_profiles);
    public void Save(IReadOnlyList<ControlProfileSnapshot> profiles)
    {
        ControlProfileStoreData.Validate(profiles);
        _profiles = ControlProfileStoreData.Copy(profiles);
    }
}

internal sealed class LocalControlProfileStore : IControlProfileStore
{
    private const int MaximumBytes = 1_048_576;
    private readonly string _path;
    private string? _loadedFingerprint;
    private bool _loaded;

    internal LocalControlProfileStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TianqiStudio", "V12UltraController", "profiles.json");

    public IReadOnlyList<ControlProfileSnapshot> Load()
    {
        var bytes = ReadBytes();
        var profiles = bytes is null ? [] : ReadDocument(bytes);
        _loadedFingerprint = Fingerprint(bytes);
        _loaded = true;
        return ControlProfileStoreData.Copy(profiles);
    }

    public void Save(IReadOnlyList<ControlProfileSnapshot> profiles)
    {
        if (!_loaded) throw new InvalidOperationException("请先读取本地方案；未读取的文件不能覆盖。");
        ControlProfileStoreData.Validate(profiles);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ProfileDocument(1, profiles), ControlProfileStoreData.JsonOptions);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("本地方案文件超过允许大小。");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        using var lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!string.Equals(_loadedFingerprint, Fingerprint(ReadBytes()), StringComparison.Ordinal))
            throw new IOException("本地方案已被其他窗口修改；请重新打开设置后再保存，当前草稿仍保留。");
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
            _loadedFingerprint = Fingerprint(bytes);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private byte[]? ReadBytes()
    {
        if (!File.Exists(_path)) return null;
        if (new FileInfo(_path).Length > MaximumBytes) throw new InvalidDataException("本地方案文件过大，已保留原文件。");
        return File.ReadAllBytes(_path);
    }

    private static IReadOnlyList<ControlProfileSnapshot> ReadDocument(byte[] bytes)
    {
        var document = JsonSerializer.Deserialize<ProfileDocument>(bytes, ControlProfileStoreData.JsonOptions)
            ?? throw new InvalidDataException("本地方案文件为空或格式无效，已保留原文件。");
        if (document.SchemaVersion != 1) throw new InvalidDataException("本地方案版本不兼容，已保留原文件。");
        ControlProfileStoreData.Validate(document.Profiles);
        return document.Profiles;
    }

    private static string? Fingerprint(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes));
    private sealed record ProfileDocument(int SchemaVersion, IReadOnlyList<ControlProfileSnapshot> Profiles);
}

internal static class ControlProfileStoreData
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    internal static ControlProfileSnapshot Copy(ControlProfileSnapshot profile) => profile with
    {
        Curve = new FanCurve(profile.Curve.Points.Select(point => point with { }).ToArray()),
        Lighting = profile.Lighting is { } lighting ? lighting with { } : null,
    };

    internal static IReadOnlyList<ControlProfileSnapshot> Copy(IReadOnlyList<ControlProfileSnapshot> profiles) =>
        Array.AsReadOnly(profiles.Select(Copy).ToArray());

    internal static void Validate(IReadOnlyList<ControlProfileSnapshot>? profiles)
    {
        if (profiles is null || profiles.Count > 128) throw new InvalidDataException("本地方案列表无效或超过 128 个。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (profile is null) throw new InvalidDataException("本地方案包含空记录。");
            profile.Validate();
            if (!ids.Add(profile.Id) || !names.Add(profile.Name.Trim()))
                throw new InvalidDataException("方案 ID 或名称重复，请使用不同名称。");
        }
    }
}
