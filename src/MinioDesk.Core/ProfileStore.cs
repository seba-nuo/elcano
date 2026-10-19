using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MinioDesk.Core;

public sealed class ProfileStore
{
    public string DirectoryPath { get; }
    private string FilePath => Path.Combine(DirectoryPath, "connections.json");
    public ProfileStore(string? directory = null) => DirectoryPath = directory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MinioDesk");

    public IReadOnlyList<ConnectionProfile> Load()
    {
        if (!File.Exists(FilePath)) return [];
        var stored = JsonSerializer.Deserialize<List<StoredProfile>>(File.ReadAllText(FilePath))
            ?? throw new InvalidDataException("El archivo de conexiones está vacío o dañado.");
        return stored.Select(item =>
        {
            var clear = ProtectedData.Unprotect(Convert.FromBase64String(item.Credentials), null, DataProtectionScope.CurrentUser);
            try
            {
                var keys = JsonSerializer.Deserialize<Keys>(clear) ?? throw new InvalidDataException("Credenciales inválidas.");
                return new ConnectionProfile { Id = item.Id, Name = item.Name, Endpoint = item.Endpoint,
                    Region = item.Region, Bucket = item.Bucket, AccessKey = keys.AccessKey, SecretKey = keys.SecretKey };
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }).ToArray();
    }

    public void Save(IEnumerable<ConnectionProfile> profiles)
    {
        var stored = profiles.Select(profile =>
        {
            profile.Validate();
            var clear = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Keys(profile.AccessKey, profile.SecretKey)));
            try
            {
                var encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
                return new StoredProfile(profile.Id, profile.Name, profile.Endpoint, profile.Region, profile.Bucket, Convert.ToBase64String(encrypted));
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }).ToArray();
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }

    private sealed record Keys(string AccessKey, string SecretKey);
    private sealed record StoredProfile(Guid Id, string Name, string Endpoint, string Region, string Bucket, string Credentials);
}
