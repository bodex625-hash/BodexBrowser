using System.Text.Json;

namespace HBProxy.Windows;

public sealed class ProfileSettingsStore
{
    private readonly string _filePath;

    public ProfileSettingsStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bodex", "HBProxy");
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "profile.json");
    }

    public SavedProfile Load()
    {
        if (!File.Exists(_filePath))
            return new SavedProfile("", 1080, true);

        try
        {
            return JsonSerializer.Deserialize<SavedProfile>(File.ReadAllText(_filePath))
                ?? new SavedProfile("", 1080, true);
        }
        catch (JsonException)
        {
            return new SavedProfile("", 1080, true);
        }
    }

    public void Save(string serverAddress, int port, bool enableUdp)
    {
        var profile = new SavedProfile(serverAddress, port, enableUdp);
        var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _filePath, true);
    }
}

public sealed record SavedProfile(string ServerAddress, int Port, bool EnableUdp);
