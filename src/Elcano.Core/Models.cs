namespace Elcano.Core;

public sealed record ConnectionProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string Region { get; init; } = "us-east-1";
    public string Bucket { get; init; } = "";
    public string AccessKey { get; init; } = "";
    public string SecretKey { get; init; } = "";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Escribe un nombre para la conexión.");
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") ||
            uri.AbsolutePath != "/" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Usa una dirección como https://storage.ejemplo.com, sin rutas ni credenciales.");
        if (string.IsNullOrWhiteSpace(AccessKey) || string.IsNullOrWhiteSpace(SecretKey))
            throw new ArgumentException("Escribe la access key y la secret key.");
        if (string.IsNullOrWhiteSpace(Region)) throw new ArgumentException("Escribe la región del servidor.");
        if (Bucket.Contains('/') || Bucket.Contains('\\')) throw new ArgumentException("El bucket no debe contener rutas.");
    }
    public override string ToString() => Name;
}

public sealed record ObjectEntry(string Key, string Name, bool IsFolder, long Size, DateTime? Modified)
{
    public string Kind => IsFolder ? "Carpeta" : Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } extension ? extension : "Archivo";
    public string SizeLabel => IsFolder ? "—" : FormatSize(Size);
    public string ModifiedLabel => Modified?.ToLocalTime().ToString("dd MMM yyyy HH:mm") ?? "—";
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}

public sealed record ObjectPage(IReadOnlyList<ObjectEntry> Entries, string? NextToken);
public sealed record FolderTotals(long Files, long Folders, long Pages);
public sealed record TransferProgress(long Done, long Total)
{
    public double Percent => Total > 0 ? Math.Min(100, 100d * Done / Total) : 0;
}
