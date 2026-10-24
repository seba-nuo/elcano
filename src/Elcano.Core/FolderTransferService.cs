using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;

namespace Elcano.Core;

public sealed record FolderObject(string Key, long Size);
public sealed record FolderProgress(long Completed, long Total, long Bytes, long TotalBytes, string Key)
{
    public double Percent => Total > 0 ? Math.Min(100, Completed * 100d / Total) : 100;
}
public sealed class FolderTransferException(string message) : Exception(message);

public sealed class FolderPlan : IDisposable
{
    private readonly string manifest;
    internal Guid ProfileId { get; }
    internal string Endpoint { get; }
    public string Bucket { get; }
    public string Prefix { get; }
    public long Objects { get; }
    public long Files { get; }
    public long Bytes { get; }
    private bool disposed;
    internal FolderPlan(string path, ConnectionProfile profile, string bucket, string prefix, long objects, long files, long bytes)
    { manifest = path; ProfileId = profile.Id; Endpoint = profile.Endpoint; Bucket = bucket; Prefix = prefix; Objects = objects; Files = files; Bytes = bytes; }
    internal void Validate(ConnectionProfile profile)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (profile.Id != ProfileId || profile.Endpoint != Endpoint) throw new ArgumentException("La carpeta pertenece a otra conexión.");
    }
    internal async IAsyncEnumerable<FolderObject> ReadAsync([EnumeratorCancellation] CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var reader = new StreamReader(new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, true), Encoding.UTF8);
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            token.ThrowIfCancellationRequested();
            var item = JsonSerializer.Deserialize<FolderObject>(line) ?? throw new InvalidDataException("No se pudo leer el inventario de la carpeta.");
            if (!item.Key.StartsWith(Prefix, StringComparison.Ordinal)) throw new InvalidDataException("El inventario contiene un objeto fuera de la carpeta.");
            yield return item;
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; File.Delete(manifest);
    }
}

public sealed class FolderTransferService
{
    public static void ValidatePrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix) || !prefix.EndsWith('/') || prefix.Trim('/').Length == 0)
            throw new ArgumentException("Selecciona una carpeta concreta; esta operación no admite la raíz del bucket.");
    }

    public async Task<FolderPlan> PrepareAsync(ConnectionProfile profile, string bucket, string prefix, CancellationToken token)
    {
        ValidatePrefix(prefix); token.ThrowIfCancellationRequested();
        var manifest = Path.Combine(Path.GetTempPath(), $"elcano-folder-{Guid.NewGuid():N}.jsonl");
        try
        {
            long objects = 0, files = 0, bytes = 0;
            using var client = S3Service.CreateClient(profile);
            await using (var writer = new StreamWriter(new FileStream(manifest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, true), new UTF8Encoding(false)))
            {
                string? continuation = null;
                do
                {
                    var page = await client.ListObjectsV2Async(new ListObjectsV2Request
                    { BucketName = bucket, Prefix = prefix, MaxKeys = 1000, ContinuationToken = continuation }, token).ConfigureAwait(false);
                    foreach (var item in page.S3Objects ?? [])
                    {
                        token.ThrowIfCancellationRequested();
                        if (!item.Key.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("El servidor devolvió un objeto fuera de la carpeta seleccionada.");
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new FolderObject(item.Key, item.Size ?? 0)).AsMemory(), token).ConfigureAwait(false);
                        objects++; if (!item.Key.EndsWith('/')) files++; bytes += item.Size ?? 0;
                    }
                    var next = page.IsTruncated == true ? page.NextContinuationToken : null;
                    if (page.IsTruncated == true && (string.IsNullOrEmpty(next) || next == continuation)) throw new InvalidDataException("No se pudo continuar el inventario de la carpeta.");
                    continuation = next;
                } while (continuation is not null);
            }
            token.ThrowIfCancellationRequested();
            return new FolderPlan(manifest, profile, bucket, prefix, objects, files, bytes);
        }
        catch { File.Delete(manifest); throw; }
    }

    public async Task DownloadZipAsync(ConnectionProfile profile, FolderPlan plan, string destination, IProgress<FolderProgress>? progress, CancellationToken token)
    {
        plan.Validate(profile); token.ThrowIfCancellationRequested();
        var rootName = plan.Prefix[..^1].Split('/')[^1];
        ValidateZipName(rootName);
        var temporary = destination + $".{Guid.NewGuid():N}.part";
        try
        {
            using var client = S3Service.CreateClient(profile);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            await using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
            {
                var paths = new ZipPaths(archive);
                paths.Directory(rootName);
                long completed = 0, bytes = 0;
                var buffer = new byte[65536];
                await foreach (var item in plan.ReadAsync(token).ConfigureAwait(false))
                {
                    var relative = item.Key[plan.Prefix.Length..];
                    if (relative.Length == 0) { completed++; continue; }
                    var directory = item.Key.EndsWith('/');
                    var path = rootName + "/" + (directory ? relative[..^1] : relative);
                    ValidateZipName(path);
                    if (directory) paths.Directory(path);
                    else
                    {
                        paths.File(path);
                        using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = plan.Bucket, Key = item.Key }, token).ConfigureAwait(false);
                        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
                        await using var target = entry.Open();
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        int read;
                        while ((read = await response.ResponseStream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                        {
                            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); bytes += read;
                            if (clock.ElapsedMilliseconds >= 100)
                            { progress?.Report(new FolderProgress(completed, plan.Objects, bytes, plan.Bytes, item.Key)); clock.Restart(); }
                        }
                    }
                    completed++; progress?.Report(new FolderProgress(completed, plan.Objects, bytes, plan.Bytes, item.Key));
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
            progress?.Report(new FolderProgress(plan.Objects, plan.Objects, plan.Bytes, plan.Bytes, ""));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task DeleteAsync(ConnectionProfile profile, FolderPlan plan, IProgress<FolderProgress>? progress, CancellationToken token)
    {
        plan.Validate(profile); token.ThrowIfCancellationRequested();
        using var client = S3Service.CreateClient(profile);
        var batch = new List<KeyVersion>(1000); long completed = 0;
        async Task DeleteBatch()
        {
            token.ThrowIfCancellationRequested();
            DeleteObjectsResponse response;
            try
            {
                response = await client.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = plan.Bucket, Objects = batch, Quiet = true }, token).ConfigureAwait(false);
            }
            catch (DeleteObjectsException exception) { response = exception.Response; }
            var errors = response.DeleteErrors?.Count ?? 0;
            completed += batch.Count - errors;
            progress?.Report(new FolderProgress(completed, plan.Objects, 0, plan.Bytes, batch[^1].Key));
            if (errors > 0) throw new FolderTransferException($"Eliminación parcial: {completed:N0} objetos eliminados y {errors:N0} rechazados por el servidor. Revisa los permisos o la retención y actualiza la carpeta.");
            batch.Clear();
        }
        await foreach (var item in plan.ReadAsync(token).ConfigureAwait(false))
        {
            batch.Add(new KeyVersion { Key = item.Key });
            if (batch.Count == 1000) await DeleteBatch().ConfigureAwait(false);
        }
        if (batch.Count > 0) await DeleteBatch().ConfigureAwait(false);
    }

    public static void ValidateZipName(string path)
    {
        foreach (var part in path.Split('/'))
        {
            var stem = part.Split('.')[0];
            var reserved = stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³'));
            if (part.Length == 0 || part.Length > 255 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || reserved ||
                part.Any(character => character < 32 || "<>:\"\\|?*".Contains(character)))
                throw new FolderTransferException("La carpeta contiene nombres que no pueden representarse de forma segura en Windows. Descarga esos archivos individualmente con otro nombre.");
        }
    }

    private sealed class ZipPaths(ZipArchive archive)
    {
        private readonly HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> directories = new(StringComparer.OrdinalIgnoreCase);
        public void Directory(string path)
        {
            var parent = path.LastIndexOf('/');
            if (parent >= 0) Directory(path[..parent]);
            if (files.Contains(path)) throw Conflict();
            if (directories.TryGetValue(path, out var original)) { if (original != path) throw Conflict(); return; }
            directories.Add(path, path); archive.CreateEntry(path + "/");
        }
        public void File(string path)
        {
            var parent = path.LastIndexOf('/');
            if (parent >= 0) Directory(path[..parent]);
            if (directories.ContainsKey(path) || !files.Add(path)) throw Conflict();
        }
        private static FolderTransferException Conflict() => new("Hay rutas incompatibles o nombres que solo difieren en mayúsculas. No se generó un ZIP para evitar sobrescribir archivos al extraerlo en Windows.");
    }
}
