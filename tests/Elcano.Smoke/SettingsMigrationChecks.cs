using Elcano.Core;

internal static class SettingsMigrationChecks
{
    public static void Run(string directory)
    {
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            Console.WriteLine("PASS " + message);
        }
        var root = Path.Combine(directory, "settings-migration");
        AppDataPaths.MigrateLegacySettings(root);
        Check(!Directory.Exists(root), "Una instalación nueva no crea configuración vacía al migrar");
        var legacy = Path.Combine(root, "MinioDesk");
        var destination = Path.Combine(root, "Elcano");
        var profile = new ConnectionProfile { Name = "Anterior", Endpoint = "https://s3.example.com", AccessKey = "test-key", SecretKey = "test-secret" };
        new ProfileStore(legacy).Save([profile]);
        File.WriteAllText(Path.Combine(legacy, "appearance.json"), "{\"Dark\":true}");
        new ClientPreferencesStore(legacy).Save(new(false));
        File.WriteAllText(Path.Combine(legacy, "unrelated.txt"), "No migrar");
        var originals = Directory.GetFiles(legacy).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);
        AppDataPaths.MigrateLegacySettings(root);
        Check(new ProfileStore(destination).Load().Single() == profile, "La migración conserva perfiles y credenciales cifradas con DPAPI");
        Check(File.ReadAllText(Path.Combine(destination, "appearance.json")) == "{\"Dark\":true}" && !new ClientPreferencesStore(destination).Load().PrefetchEnabled,
            "La migración conserva el tema y la preferencia de precarga");
        Check(!File.Exists(Path.Combine(destination, "unrelated.txt")), "La migración copia solo archivos de configuración conocidos");
        new ClientPreferencesStore(destination).Save(new(true));
        File.WriteAllText(Path.Combine(destination, "connections.json"), "[]");
        AppDataPaths.MigrateLegacySettings(root);
        Check(new ClientPreferencesStore(destination).Load().PrefetchEnabled && new ProfileStore(destination).Load().Count == 0,
            "Volver a abrir no sobrescribe las preferencias ni restaura conexiones eliminadas");
        Check(originals.All(pair => File.ReadAllBytes(Path.Combine(legacy, pair.Key!)).SequenceEqual(pair.Value)),
            "Los archivos originales permanecen intactos para volver a la versión anterior");
        Check(!Directory.EnumerateFiles(destination, "*.tmp").Any(), "La migración no deja archivos temporales");
    }
}
