using Amazon.S3.Model;
using DuckDB.NET.Data;
using MinioDesk.Core;
using System.Security.Cryptography;

if (args.Length < 2) throw new ArgumentException("Uso: --local|--integration|--prepare-extensions DIRECTORIO");
var directory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(directory);
var parquet = new ParquetService(Path.Combine(directory, "extensions"));
if (args[0] == "--prepare-extensions")
{
    var extensionPath = parquet.PrepareExtension();
    File.WriteAllText(Path.Combine(directory, "extension-path.txt"), extensionPath);
    Console.WriteLine("Extensión httpfs preparada.");
    return;
}
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    Console.WriteLine("PASS " + description);
}

var profile = new ConnectionProfile { Name = "Prueba", Endpoint = "http://localhost:9000", AccessKey = "test-key", SecretKey = "test-secret-'á" };
var store = new ProfileStore(Path.Combine(directory, "profiles"));
store.Save([profile]);
Check(store.Load().Single() == profile, "Perfiles: cifrado DPAPI y lectura sin pérdida");
var storedText = File.ReadAllText(Path.Combine(store.DirectoryPath, "connections.json"));
Check(!storedText.Contains(profile.SecretKey) && !storedText.Contains(profile.AccessKey), "Las credenciales no aparecen en texto claro");
try { (profile with { Endpoint = "http://localhost:9000/path" }).Validate(); throw new Exception("Se aceptó una ruta en el endpoint"); }
catch (ArgumentException) { Check(true, "Validación de endpoint"); }

var file = Path.Combine(directory, "demo's.parquet");
using (var connection = new DuckDBConnection("Data Source=:memory:"))
{
    connection.Open(); using var command = connection.CreateCommand();
    command.CommandText = $"COPY (SELECT i AS id, 'México' AS \"país.origen\", repeat('x', 900) AS detalle FROM range(1000) t(i)) TO {ParquetService.Literal(file.Replace('\\', '/'))} (FORMAT PARQUET);";
    command.ExecuteNonQuery();
}
var localPreview = await parquet.PreviewLocalAsync(file, CancellationToken.None);
Check(localPreview.Rows.Rows.Count == 200 && localPreview.Schema.Rows.Count == 3, "Parquet local: 200 filas y esquema");
Check(localPreview.Rows.Columns[1].Caption == "país.origen" && localPreview.Rows.Rows[0][1].ToString() == "México", "Nombres especiales y Unicode");
Check(localPreview.Rows.Rows[0][2].ToString()!.Length == 512, "Celdas limitadas para la vista previa");
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try { await parquet.PreviewLocalAsync(file, cancellation.Token); throw new Exception("Se ignoró la cancelación"); }
    catch (OperationCanceledException) { Check(true, "Cancelación de vista previa"); }
}
if (args[0] != "--integration") return;

var access = Environment.GetEnvironmentVariable("MINIODESK_TEST_ACCESS_KEY") ?? throw new Exception("Falta MINIODESK_TEST_ACCESS_KEY");
var secret = Environment.GetEnvironmentVariable("MINIODESK_TEST_SECRET_KEY") ?? throw new Exception("Falta MINIODESK_TEST_SECRET_KEY");
var bucket = "miniodesk-smoke-" + Guid.NewGuid().ToString("N");
var s3 = new S3Service();
foreach (var endpoint in new[] { "http://localhost:9000", "http://localhost:9100" })
{
    var active = profile with { Endpoint = endpoint, AccessKey = access, SecretKey = secret };
    using var client = S3Service.CreateClient(active);
    await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
    try
    {
        var key = "datos/demo's.parquet";
        await s3.UploadAsync(active, bucket, key, file, null, CancellationToken.None);
        var listing = await s3.ListAsync(active, bucket, "", null, CancellationToken.None);
        Check(listing.Entries.Single().IsFolder, $"{endpoint}: navegación por prefijos");
        var buckets = await s3.ListBucketsAsync(active with { Bucket = bucket }, CancellationToken.None);
        Check(buckets.Single() == bucket, $"{endpoint}: perfil con bucket fijo");
        var remotePreview = await parquet.PreviewRemoteAsync(active, bucket, key, CancellationToken.None);
        Check(remotePreview.Rows.Rows.Count == 200 && remotePreview.Rows.Rows[0][1].ToString() == "México", $"{endpoint}: Parquet remoto por httpfs");
        var download = Path.Combine(directory, "download.parquet");
        await s3.DownloadAsync(active, bucket, key, download, null, CancellationToken.None);
        Check(SHA256.HashData(File.ReadAllBytes(file)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(download))), $"{endpoint}: subida y descarga íntegras");

        // Force a multipart upload without allocating the file in RAM.
        var largePath = Path.Combine(directory, "multipart.bin");
        using (var large = File.Create(largePath)) large.SetLength(17 * 1024 * 1024);
        await s3.UploadAsync(active, bucket, "multipart.bin", largePath, null, CancellationToken.None);
        var metadata = await client.GetObjectMetadataAsync(bucket, "multipart.bin");
        Check(metadata.ContentLength == 17 * 1024 * 1024, $"{endpoint}: carga multipart");
        await Parallel.ForEachAsync(Enumerable.Range(0, 205), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (index, token) =>
            await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = $"pages/{index:D3}.txt", ContentBody = "x" }, token));
        var first = await s3.ListAsync(active, bucket, "pages/", null, CancellationToken.None);
        var second = await s3.ListAsync(active, bucket, "pages/", first.NextToken, CancellationToken.None);
        Check(first.Entries.Count == 200 && first.NextToken is not null && second.Entries.Count == 5 && second.NextToken is null,
            $"{endpoint}: paginación 200 + 5 sin duplicados");
        var cancelledPath = Path.Combine(directory, "cancelled.bin");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await s3.DownloadAsync(active, bucket, key, cancelledPath, null, cancelled.Token); }
        catch (OperationCanceledException) { }
        Check(!File.Exists(cancelledPath) && !Directory.GetFiles(directory, "*.part").Any(), $"{endpoint}: cancelación sin archivos parciales");
    }
    finally
    {
        // Only clean the unique bucket created above by this test.
        string? continuation = null;
        do
        {
            var objects = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, ContinuationToken = continuation });
            continuation = objects.IsTruncated == true ? objects.NextContinuationToken : null;
            foreach (var item in objects.S3Objects ?? []) await client.DeleteObjectAsync(bucket, item.Key);
        } while (continuation is not null);
        await client.DeleteBucketAsync(bucket);
    }
}
Console.WriteLine("Pruebas de integración terminadas en los dos servidores.");
