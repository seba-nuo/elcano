using Amazon.S3.Model;
using DuckDB.NET.Data;
using Elcano.Core;
using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;

if (args.Length < 2) throw new ArgumentException("Uso: --local|--integration|--prepare-extensions DIRECTORIO");
var directory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(directory);
if (args[0] == "--folder-protocol") { await FolderProtocolChecks.RunAsync(directory); return; }
var parquet = new ParquetService(Path.Combine(directory, "extensions"));
if (args[0] == "--prepare-extensions")
{
    var extensionPath = parquet.PrepareExtension();
    File.WriteAllText(Path.Combine(directory, "extension-path.txt"), extensionPath);
    Console.WriteLine("Extensión httpfs preparada.");
    return;
}
SettingsMigrationChecks.Run(directory);
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
var filtered = await parquet.QueryLocalAsync(file, "SELECT id, \"país.origen\" FROM parquet WHERE id >= 995 ORDER BY id DESC; -- filtro", CancellationToken.None);
Check(filtered.Rows.Rows.Count == 5 && filtered.Rows.Rows[0][0].ToString() == "999", "SQL: filtros, orden, selección de columnas y comentario final");
var grouped = await parquet.QueryLocalAsync(file, "WITH datos AS (SELECT * FROM parquet WHERE id < 10) SELECT count(*) AS total, sum(id) AS suma FROM datos", CancellationToken.None);
Check(grouped.Rows.Rows.Count == 1 && grouped.Rows.Rows[0][0].ToString() == "10" && grouped.Rows.Rows[0][1].ToString() == "45", "SQL: CTE y agregaciones sobre el archivo completo");
Check(grouped.Schema.Rows.Count == 3 && grouped.Rows.Columns[0].Caption == "total", "El esquema original y las columnas del resultado se mantienen separados");
var quoted = await parquet.QueryLocalAsync(file, "SELECT ';' AS punto, $$hola; mundo$$ AS texto FROM parquet LIMIT 1", CancellationToken.None);
Check(quoted.Rows.Rows[0][1].ToString() == "hola; mundo", "SQL: punto y coma dentro de literales");
foreach (var invalid in new[] { "DELETE FROM parquet", "SELECT * FROM parquet; DROP VIEW parquet", "SELECT inexistente FROM parquet", "WITH x AS (DELETE FROM parquet RETURNING *) SELECT * FROM x" })
{
    try { await parquet.QueryLocalAsync(file, invalid, CancellationToken.None); throw new Exception("Se aceptó una consulta inválida"); }
    catch (ParquetQueryException) { Check(true, "SQL inválido o de escritura rechazado"); }
}
var otherFile = Path.Combine(directory, "privado.txt");
File.WriteAllText(otherFile, "dato privado");
try
{
    await parquet.QueryLocalAsync(file, $"SELECT * FROM read_text({ParquetService.Literal(otherFile.Replace('\\', '/'))})", CancellationToken.None);
    throw new Exception("Se permitió leer un archivo ajeno a la consulta");
}
catch (ParquetQueryException) { Check(true, "Las consultas no pueden leer otros archivos del equipo"); }
var afterError = await parquet.QueryLocalAsync(file, "SELECT count(*) AS total FROM parquet", CancellationToken.None);
Check(afterError.Rows.Rows[0][0].ToString() == "1000", "Se puede ejecutar otra consulta después de un error");
var suggestions = SqlCompletion.Suggest([("país.origen", "VARCHAR"), ("total ventas", "DECIMAL"), ("id", "BIGINT")], "pa");
Check(suggestions.Any(item => item.Label == "país.origen" && item.InsertText == "\"país.origen\""), "Autocompletado: Unicode y nombres con puntuación");
Check(SqlCompletion.Suggest([("total ventas", "DECIMAL")], "ventas").Single(item => item.Detail == "DECIMAL").InsertText == "\"total ventas\"", "Autocompletado: columnas con espacios y búsqueda parcial");
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try { await parquet.PreviewLocalAsync(file, cancellation.Token); throw new Exception("Se ignoró la cancelación"); }
    catch (OperationCanceledException) { Check(true, "Cancelación de vista previa"); }
}
using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
{
    try
    {
        await parquet.QueryLocalAsync(file, "SELECT sum(sin(i)) FROM range(1000000000) t(i)", cancellation.Token);
        throw new Exception("Se ignoró la cancelación de una consulta en ejecución");
    }
    catch (OperationCanceledException) { Check(true, "Una consulta SQL en ejecución se puede cancelar"); }
}
Check((await parquet.QueryLocalAsync(file, "SELECT count(*) FROM parquet", CancellationToken.None)).Rows.Rows[0][0].ToString() == "1000",
    "Se puede consultar después de cancelar una consulta SQL");
var files = new FilePreviewService(Path.Combine(directory, "extensions"));
var csvPath = Path.Combine(directory, "ventas.csv");
await File.WriteAllTextAsync(csvPath, "id;nombre;importe\r\n1;\"José; Muñoz\";12.5\r\n2;\"Texto con\nvarias líneas\";20.5\r\n", new UTF8Encoding(true));
var csvPreview = await files.PreviewLocalAsync(csvPath, CancellationToken.None);
Check(csvPreview.Rows.Rows.Count == 2 && csvPreview.Schema.Rows.Count == 3 && csvPreview.Rows.Rows[0][1].ToString() == "José; Muñoz", "CSV: separador automático, BOM, Unicode y campos entre comillas");
Check(csvPreview.Rows.Rows[1][1].ToString()!.Contains('\n'), "CSV: saltos de línea dentro de un campo");
var csvQuery = await files.QueryLocalAsync(csvPath, "SELECT sum(importe) AS total FROM archivo", CancellationToken.None);
Check(Convert.ToDouble(csvQuery.Rows.Rows[0][0]) == 33, "CSV: consultas SQL sobre tipos detectados");
var plainCsvPath = Path.Combine(directory, "sin-cabecera.csv");
await File.WriteAllTextAsync(plainCsvPath, "1,10\n2,20\n3,30\n");
Check((await files.PreviewLocalAsync(plainCsvPath, CancellationToken.None)).Rows.Rows.Count == 3, "CSV sin cabecera: no se pierde la primera fila");
var txtPath = Path.Combine(directory, "notas.txt");
const string sampleText = "Texto de prueba: áéíóú, ñ y 李明.\r\nSegunda línea.\n\tCon tabulación.";
await File.WriteAllTextAsync(txtPath, sampleText, Encoding.Unicode);
Check((await files.PreviewLocalAsync(txtPath, CancellationToken.None)).Text == sampleText, "TXT: BOM UTF-16, Unicode, tabulaciones y saltos preservados");
var emptyTextPath = Path.Combine(directory, "vacio.txt");
await File.WriteAllTextAsync(emptyTextPath, "");
Check((await files.PreviewLocalAsync(emptyTextPath, CancellationToken.None)).Text == "", "TXT vacío: vista válida");
var longTextPath = Path.Combine(directory, "largo.txt");
await File.WriteAllTextAsync(longTextPath, new string('x', FilePreviewService.MaxTextCharacters + 1000));
var longText = await files.PreviewLocalAsync(longTextPath, CancellationToken.None);
Check(longText.IsTruncated && longText.Text!.Length == FilePreviewService.MaxTextCharacters, "TXT: lectura limitada sin cargar el archivo completo");
var cache = new PreviewCache<string>(value => value.Length, maxBytes: 10, maxEntries: 2);
cache.Put("a", "aaaa"); cache.Put("b", "bbbb");
Check(cache.TryGet("a", out _), "Caché: una vista abierta se puede recuperar");
cache.Put("c", "cccc");
Check(!cache.TryGet("b", out _) && cache.TryGet("a", out _) && cache.Bytes <= 10, "Caché: conserva las vistas recientes dentro del presupuesto");
cache.Put("c", "12345678901");
Check(!cache.TryGet("c", out _), "Caché: no retiene una vista que supera el presupuesto");
cache.Clear(); Check(cache.Count == 0 && cache.Bytes == 0, "Caché: invalidación completa");
ExplorerCacheChecks.Run();
await PresentationChecks.RunAsync(directory);
var crumbs = FolderBreadcrumbs.Build("datos", "ventas/2026/México/octubre/día 20/");
Check(crumbs.Count == 6 && crumbs[^1].Prefix == "ventas/2026/México/octubre/día 20/" && crumbs[0].Prefix == "", "Rutas: segmentos Unicode, espacios y acceso a la raíz");
var compact = FolderBreadcrumbs.Compact(crumbs);
Check(compact.Count == 4 && compact[1].IsOverflow && compact[^1] == crumbs[^1], "Rutas profundas: resumen conserva los últimos niveles y acceso a antecesores");
Check(FolderBreadcrumbs.Build("datos", "uno//tres/")[2].Prefix == "uno//", "Rutas: se conservan segmentos vacíos de las claves S3");
var folders = new FolderTransferService();
foreach (var invalidPrefix in new[] { "", "/", "////", "sin-barra-final" })
{
    try { await folders.PrepareAsync(profile, "demo", invalidPrefix, CancellationToken.None); throw new Exception("Se aceptó una operación de carpeta sobre una raíz o prefijo incompleto"); }
    catch (ArgumentException) { Check(true, "Carpetas: se rechazan raíces y prefijos sin límite de carpeta"); }
}
foreach (var invalidZipName in new[] { "../escape.txt", "/absoluto.txt", "ruta\\escape.txt", "C:/escape.txt", "NUL.txt", "COM¹.txt", "CONOUT$", "ruta/archivo.", "ruta//archivo.txt" })
{
    try { FolderTransferService.ValidateZipName(invalidZipName); throw new Exception("Se aceptó una ruta ZIP incompatible"); }
    catch (FolderTransferException) { Check(true, "ZIP: se rechazan rutas inseguras o incompatibles con Windows"); }
}
FolderTransferService.ValidateZipName("carpeta/México/李明.csv");
Check(true, "ZIP: se admiten nombres Unicode y subcarpetas");
if (args[0] != "--integration") return;

var access = Environment.GetEnvironmentVariable("ELCANO_TEST_ACCESS_KEY") ?? throw new Exception("Falta ELCANO_TEST_ACCESS_KEY");
var secret = Environment.GetEnvironmentVariable("ELCANO_TEST_SECRET_KEY") ?? throw new Exception("Falta ELCANO_TEST_SECRET_KEY");
var bucket = "elcano-smoke-" + Guid.NewGuid().ToString("N");
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
        var remoteQuery = await parquet.QueryRemoteAsync(active, bucket, key, "SELECT count(*) AS total, max(id) AS maximo FROM parquet", CancellationToken.None);
        Check(remoteQuery.Rows.Rows[0][0].ToString() == "1000" && remoteQuery.Rows.Rows[0][1].ToString() == "999", $"{endpoint}: consultas SQL sobre Parquet remoto completo");
        await s3.UploadAsync(active, bucket, "formatos/ventas.csv", csvPath, null, CancellationToken.None);
        await s3.UploadAsync(active, bucket, "formatos/notas.txt", txtPath, null, CancellationToken.None);
        var remoteCsv = await files.QueryRemoteAsync(active, bucket, "formatos/ventas.csv", "SELECT sum(importe) FROM archivo", CancellationToken.None);
        Check(Convert.ToDouble(remoteCsv.Rows.Rows[0][0]) == 33, $"{endpoint}: CSV remoto con consulta SQL");
        Check((await files.PreviewRemoteAsync(active, bucket, "formatos/notas.txt", CancellationToken.None)).Text == sampleText, $"{endpoint}: TXT remoto por streaming");
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
        var totals = await s3.CountFolderAsync(active, bucket, "pages/", CancellationToken.None);
        Check(totals.Files == 205 && totals.Folders == 0 && totals.Pages == 2, $"{endpoint}: total de archivos y páginas del nivel actual");
        var rootTotals = await s3.CountFolderAsync(active, bucket, "", CancellationToken.None);
        Check(rootTotals.Files == 1 && rootTotals.Folders == 3 && rootTotals.Pages == 1, $"{endpoint}: el recuento distingue archivos directos y subcarpetas sin recorrer sus contenidos");
        var cancelledPath = Path.Combine(directory, "cancelled.bin");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await s3.DownloadAsync(active, bucket, key, cancelledPath, null, cancelled.Token); }
        catch (OperationCanceledException) { }
        Check(!File.Exists(cancelledPath) && !Directory.GetFiles(directory, "*.part").Any(), $"{endpoint}: cancelación sin archivos parciales");
        await s3.DeleteAsync(active, bucket, key, CancellationToken.None);
        Check((await s3.ListAsync(active, bucket, "datos/", null, CancellationToken.None)).Entries.Count == 0, $"{endpoint}: eliminación de un archivo individual");
        Check((await s3.ListAsync(active, bucket, "pages/", null, CancellationToken.None)).Entries.Count == 200, $"{endpoint}: eliminar un archivo conserva los demás objetos");
        try { await s3.DeleteAsync(active, bucket, "pages/", CancellationToken.None); throw new Exception("Se permitió eliminar un prefijo"); }
        catch (ArgumentException) { Check(true, $"{endpoint}: no se eliminan carpetas desde la acción de archivo"); }

        await Parallel.ForEachAsync(Enumerable.Range(0, 1003), new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (index, token) =>
            await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = $"tree/sub/{index:D4}_México.txt", ContentBody = "x" }, token));
        await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "tree/", ContentBody = "" });
        await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "tree/vacía/", ContentBody = "" });
        await s3.UploadAsync(active, bucket, "tree/ventas.csv", csvPath, null, CancellationToken.None);
        await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "tree-extra/no-tocar.txt", ContentBody = "conservar" });
        // MinIO cannot reliably list an object and a directory with the exact same name.
        // The simulated S3 tests cover that namespace collision separately.
        await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "tree.txt", ContentBody = "conservar" });
        using (var plan = await folders.PrepareAsync(active, bucket, "tree/", CancellationToken.None))
        {
            Check(plan.Files == 1004 && plan.Objects == 1006, $"{endpoint}: inventario recursivo paginado de más de mil objetos (archivos={plan.Files}, objetos={plan.Objects})");
            var zipPath = Path.Combine(directory, "carpeta.zip");
            await folders.DownloadZipAsync(active, plan, zipPath, null, CancellationToken.None);
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                Check(archive.Entries.Count(entry => !entry.FullName.EndsWith('/')) == 1004 && archive.GetEntry("tree/vacía/") is not null && archive.GetEntry("tree/sub/0000_México.txt") is not null,
                    $"{endpoint}: ZIP conserva todos los archivos, subcarpetas vacías y nombres Unicode");
                using var zipped = archive.GetEntry("tree/ventas.csv")!.Open();
                Check(SHA256.HashData(zipped).SequenceEqual(SHA256.HashData(File.ReadAllBytes(csvPath))), $"{endpoint}: contenido ZIP íntegro");
                Check(!archive.Entries.Any(entry => entry.FullName.StartsWith("tree-extra")), $"{endpoint}: la descarga respeta el límite exacto del prefijo");
            }
            var cancelZip = Path.Combine(directory, "cancel-folder.zip"); File.WriteAllText(cancelZip, "conservar archivo anterior");
            using var downloadCancel = new CancellationTokenSource();
            try
            {
                await folders.DownloadZipAsync(active, plan, cancelZip, new InlineProgress<FolderProgress>(value => { if (value.Bytes > 0) downloadCancel.Cancel(); }), downloadCancel.Token);
                throw new Exception("Se ignoró la cancelación de la descarga de carpeta");
            }
            catch (OperationCanceledException) { }
            Check(File.ReadAllText(cancelZip) == "conservar archivo anterior" && !Directory.GetFiles(directory, "*.part").Any(), $"{endpoint}: cancelar ZIP conserva el destino anterior y limpia los parciales");
            using var deleteCancel = new CancellationTokenSource();
            try
            {
                await folders.DeleteAsync(active, plan, new InlineProgress<FolderProgress>(value => { if (value.Completed >= 1000) deleteCancel.Cancel(); }), deleteCancel.Token);
                throw new Exception("Se ignoró la cancelación de la eliminación recursiva");
            }
            catch (OperationCanceledException) { }
            using var remaining = await folders.PrepareAsync(active, bucket, "tree/", CancellationToken.None);
            Check(remaining.Objects == 6, $"{endpoint}: cancelación entre lotes conserva los objetos aún no eliminados");
            await folders.DeleteAsync(active, remaining, null, CancellationToken.None);
        }
        using (var empty = await folders.PrepareAsync(active, bucket, "tree/", CancellationToken.None))
            Check(empty.Objects == 0, $"{endpoint}: eliminación de toda la carpeta y sus marcadores");
        Check((await client.GetObjectMetadataAsync(bucket, "tree-extra/no-tocar.txt")).ContentLength == 9 && (await client.GetObjectMetadataAsync(bucket, "tree.txt")).ContentLength == 9,
            $"{endpoint}: eliminar una carpeta conserva archivos y prefijos vecinos");
        await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "unsafe/NUL.txt", ContentBody = "x" });
        using (var unsafePlan = await folders.PrepareAsync(active, bucket, "unsafe/", CancellationToken.None))
        {
            var unsafeZip = Path.Combine(directory, "unsafe.zip");
            try { await folders.DownloadZipAsync(active, unsafePlan, unsafeZip, null, CancellationToken.None); throw new Exception("Se permitió una entrada ZIP con un nombre reservado de Windows"); }
            catch (FolderTransferException) { Check(!File.Exists(unsafeZip) && !Directory.GetFiles(directory, "*.part").Any(), $"{endpoint}: ZIP inseguro rechazado sin dejar un archivo parcial"); }
            await folders.DeleteAsync(active, unsafePlan, null, CancellationToken.None);
        }
        await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "conflict/A.txt", ContentBody = "A" });
        await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "conflict/a.txt", ContentBody = "a" });
        using (var conflictPlan = await folders.PrepareAsync(active, bucket, "conflict/", CancellationToken.None))
        {
            try { await folders.DownloadZipAsync(active, conflictPlan, Path.Combine(directory, "conflict.zip"), null, CancellationToken.None); throw new Exception("Se ignoró una colisión de nombres de Windows"); }
            catch (FolderTransferException) { Check(true, $"{endpoint}: se rechazan colisiones de mayúsculas sin perder archivos"); }
            await folders.DeleteAsync(active, conflictPlan, null, CancellationToken.None);
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Fallo de integración en {endpoint}: {exception}");
        throw;
    }
    finally
    {
        // Only clean the unique bucket created above by this test.
        while (true)
        {
            var objects = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
            if (objects.S3Objects is not { Count: > 0 }) break;
            foreach (var item in objects.S3Objects ?? []) await client.DeleteObjectAsync(bucket, item.Key);
        }
        await client.DeleteBucketAsync(bucket);
    }
}
Console.WriteLine("Pruebas de integración terminadas en los dos servidores.");

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
