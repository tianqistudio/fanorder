using System.IO;

namespace V12UltraController.Settings;

internal interface IAppearancePreference
{
    bool LoadClassic();
    void SaveClassic(bool classic);
}

internal sealed class LocalAppearancePreference : IAppearancePreference
{
    private readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TianqiStudio", "V12UltraController", "appearance.txt");
    public bool LoadClassic()
    {
        try { return File.Exists(path) && new FileInfo(path).Length <= 32 && File.ReadAllText(path) == "classic"; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }
    public void SaveClassic(bool classic)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, classic ? "classic" : "windows");
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
