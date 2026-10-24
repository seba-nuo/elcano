using System.Text.Json;

namespace MinioDesk.Core;

public sealed record ClientPreferences(bool PrefetchEnabled = true);
public sealed class ClientPreferencesStore(string? directory = null)
{
    private readonly string path = Path.Combine(directory ?? new ProfileStore().DirectoryPath, "preferences.json");
    public ClientPreferences Load() => File.Exists(path) ? JsonSerializer.Deserialize<ClientPreferences>(File.ReadAllText(path)) ?? new() : new();
    public void Save(ClientPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(preferences)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
