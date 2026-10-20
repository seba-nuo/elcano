using System.Data;
using System.Text;
using Amazon.S3.Model;

namespace MinioDesk.Core;

public sealed class FilePreviewService(string? extensionDirectory = null)
{
    public const int MaxTextCharacters = 131_072;
    private readonly ParquetService tables = new(extensionDirectory);
    public static bool IsSupported(string path) => Path.GetExtension(path).ToLowerInvariant() is ".parquet" or ".csv" or ".txt";
    public static bool IsText(string path) => Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase);

    public async Task<ParquetPreview> PreviewRemoteAsync(ConnectionProfile profile, string bucket, string key, CancellationToken token)
    {
        ValidateExtension(key);
        if (!IsText(key)) return await tables.PreviewRemoteAsync(profile, bucket, key, token);
        using var client = S3Service.CreateClient(profile);
        using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, token);
        return await ReadTextAsync(response.ResponseStream, $"s3://{bucket}/{key}", token);
    }

    public async Task<ParquetPreview> PreviewLocalAsync(string path, CancellationToken token)
    {
        ValidateExtension(path);
        if (!IsText(path)) return await tables.PreviewLocalAsync(path, token);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return await ReadTextAsync(stream, path, token);
    }

    public Task<ParquetPreview> QueryRemoteAsync(ConnectionProfile profile, string bucket, string key, string sql, CancellationToken token)
    {
        ValidateTabular(key);
        return tables.QueryRemoteAsync(profile, bucket, key, sql, token);
    }
    public Task<ParquetPreview> QueryLocalAsync(string path, string sql, CancellationToken token)
    {
        ValidateTabular(path);
        return tables.QueryLocalAsync(path, sql, token);
    }
    private static void ValidateExtension(string path)
    {
        if (!IsSupported(path)) throw new ArgumentException("Abre un archivo Parquet, CSV o TXT.");
    }
    private static void ValidateTabular(string path)
    {
        ValidateExtension(path);
        if (IsText(path)) throw new ArgumentException("Las consultas SQL están disponibles para Parquet y CSV.");
    }
    private static async Task<ParquetPreview> ReadTextAsync(Stream stream, string source, CancellationToken token)
    {
        // Read a bounded prefix, never materialize the full object in memory.
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var characters = new char[MaxTextCharacters + 1];
        var read = 0;
        while (read < characters.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = await reader.ReadAsync(characters.AsMemory(read), token);
            if (count == 0) break;
            read += count;
        }
        token.ThrowIfCancellationRequested();
        var length = Math.Min(read, MaxTextCharacters);
        if (length > 0 && char.IsHighSurrogate(characters[length - 1])) length--;
        return new ParquetPreview(new DataTable(), new DataTable(), source, new string(characters, 0, length), read > MaxTextCharacters);
    }
}
