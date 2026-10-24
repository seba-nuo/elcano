namespace Elcano.Core;

public static class AppDataPaths
{
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Elcano");

    public static void MigrateLegacySettings(string? localApplicationData = null)
    {
        var root = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var legacy = Path.Combine(root, "MinioDesk");
        var destination = Path.Combine(root, "Elcano");
        // Copy settings only; DPAPI remains bound to the same Windows user. Keep the originals for rollback.
        foreach (var name in new[] { "connections.json", "appearance.json", "preferences.json" })
        {
            var source = Path.Combine(legacy, name);
            var target = Path.Combine(destination, name);
            if (!File.Exists(source) || File.Exists(target)) continue;
            Directory.CreateDirectory(destination);
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temporary);
                try { File.Move(temporary, target); }
                catch (IOException) when (File.Exists(target)) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
