using Amazon.S3.Model;
using DuckDB.NET.Data;
using MinioDesk.Core;
using System.Text.Json;

if (args.Length < 4) throw new ArgumentException("Uso: DIRECTORIO BUCKET REGION ENDPOINT [ENDPOINT...]");
var output = Path.GetFullPath(args[0]);
var bucket = args[1];
var region = args[2];
var endpoints = args[3..];
var access = Environment.GetEnvironmentVariable("MINIODESK_SAMPLE_ACCESS_KEY") ?? throw new ArgumentException("Falta MINIODESK_SAMPLE_ACCESS_KEY.");
var secret = Environment.GetEnvironmentVariable("MINIODESK_SAMPLE_SECRET_KEY") ?? throw new ArgumentException("Falta MINIODESK_SAMPLE_SECRET_KEY.");
var prefix = "muestras-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + "/";
Directory.CreateDirectory(output);
var s3 = new S3Service();
var parquet = new ParquetService(Path.Combine(output, "extensions"));
var manifest = new List<SampleManifest>();

// Deterministic synthetic data only; no personal data or production files are read.
foreach (var endpoint in endpoints)
{
    var profile = new ConnectionProfile { Name = "Ejemplos", Endpoint = endpoint, Region = region, AccessKey = access, SecretKey = secret };
    profile.Validate();
    using var client = S3Service.CreateClient(profile);
    var buckets = await client.ListBucketsAsync(new ListBucketsRequest());
    if (!(buckets.Buckets ?? []).Any(item => item.BucketName == bucket))
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });

    var origin = ParquetService.Literal(endpoint);
    var samples = new[]
    {
        new Sample("01_ventas_2026.parquet", 240, $"""
            SELECT i + 1 AS pedido_id, DATE '2026-01-01' + CAST(i % 90 AS INTEGER) AS fecha,
                CASE i % 4 WHEN 0 THEN 'México' WHEN 1 THEN 'Colombia' WHEN 2 THEN 'Perú' ELSE 'Chile' END AS pais,
                CASE i % 3 WHEN 0 THEN 'Teclado' WHEN 1 THEN 'Monitor' ELSE 'Mouse' END AS producto,
                CAST(i % 5 + 1 AS INTEGER) AS cantidad,
                CAST((i % 100 + 1) * 12.35 AS DECIMAL(12,2)) AS importe,
                i % 7 <> 0 AS pagado,
                CASE WHEN i % 9 = 0 THEN NULL ELSE 'Entrega programada' END AS observaciones,
                {origin} AS servidor_origen
            FROM range(240) t(i)
            """),
        new Sample("02_clientes_unicode.parquet", 30, $"""
            SELECT i + 1 AS cliente_id,
                CASE i % 5 WHEN 0 THEN 'Ana López' WHEN 1 THEN 'José Muñoz' WHEN 2 THEN 'Zoë Martin'
                    WHEN 3 THEN '李明' ELSE 'Sofía Núñez' END AS nombre,
                'cliente' || CAST(i + 1 AS VARCHAR) || '@example.test' AS email,
                CASE WHEN i % 4 = 0 THEN NULL ELSE 'Ciudad de México' END AS ciudad,
                DATE '2026-01-01' + CAST(i AS INTEGER) AS fecha_alta,
                {origin} AS servidor_origen
            FROM range(30) t(i)
            """),
        new Sample("03_tipos_y_nulos.parquet", 12, $"""
            SELECT i AS id, CAST(i * 3.1415 AS DECIMAL(12,4)) AS decimal_ejemplo,
                DATE '2026-10-01' + CAST(i AS INTEGER) AS fecha,
                TIMESTAMP '2026-10-01 08:30:00' + i * INTERVAL '1 hour' AS fecha_hora,
                CASE WHEN i % 3 = 0 THEN NULL ELSE i % 2 = 0 END AS activo,
                CASE WHEN i % 2 = 0 THEN NULL ELSE 'Texto con acentos: áéíóú ñ' END AS texto_opcional,
                [CAST(i AS INTEGER), CAST(i + 1 AS INTEGER)] AS lista,
                struct_pack(categoria := 'demo', prioridad := i % 3) AS detalle,
                {origin} AS servidor_origen
            FROM range(12) t(i)
            """),
        new Sample("04_eventos_100000.parquet", 100_000, $"""
            SELECT i AS evento_id, TIMESTAMP '2026-10-01 00:00:00' + i * INTERVAL '1 second' AS instante,
                'sensor-' || CAST(i % 20 AS VARCHAR) AS sensor,
                CAST(18 + (i % 150) / 10.0 AS DECIMAL(6,2)) AS temperatura,
                CASE WHEN i % 17 = 0 THEN 'advertencia' ELSE 'normal' END AS estado,
                {origin} AS servidor_origen
            FROM range(100000) t(i)
            """),
        new Sample("05_vacio_con_esquema.parquet", 0, $"""
            SELECT CAST(NULL AS BIGINT) AS id, CAST(NULL AS VARCHAR) AS descripcion,
                CAST(NULL AS DATE) AS fecha, CAST(NULL AS DECIMAL(10,2)) AS importe,
                {origin} AS servidor_origen WHERE false
            """)
    };

    var localDirectory = Path.Combine(output, new Uri(endpoint).Port.ToString());
    Directory.CreateDirectory(localDirectory);
    using var connection = new DuckDBConnection("Data Source=:memory:");
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SET memory_limit='256MB'; SET threads=2;";
    command.ExecuteNonQuery();
    foreach (var sample in samples)
    {
        var local = Path.Combine(localDirectory, sample.Name);
        command.CommandText = $"COPY ({sample.Query}) TO {ParquetService.Literal(local.Replace('\\', '/'))} (FORMAT PARQUET, COMPRESSION ZSTD);";
        command.ExecuteNonQuery();
        command.CommandText = $"SELECT count(*) FROM read_parquet({ParquetService.Literal(local.Replace('\\', '/'))});";
        if (Convert.ToInt64(command.ExecuteScalar()) != sample.RowCount) throw new InvalidDataException("Cantidad de filas incorrecta.");
        var key = prefix + sample.Name;
        await s3.UploadAsync(profile, bucket, key, local, null, CancellationToken.None);
        var preview = await parquet.PreviewRemoteAsync(profile, bucket, key, CancellationToken.None);
        if (preview.Rows.Rows.Count != Math.Min(200, sample.RowCount) || preview.Schema.Rows.Count == 0)
            throw new InvalidDataException("La vista previa remota no coincide con el ejemplo.");
        if (sample.RowCount > 0)
        {
            var originColumn = preview.Rows.Columns.Cast<System.Data.DataColumn>().Single(column => column.Caption == "servidor_origen");
            if (preview.Rows.Rows[0][originColumn].ToString() != endpoint)
                throw new InvalidDataException("La vista previa utilizó otro servidor.");
        }
        manifest.Add(new SampleManifest(endpoint, bucket, key, sample.RowCount, preview.Schema.Rows.Count, new FileInfo(local).Length));
        Console.WriteLine($"OK {endpoint} -> s3://{bucket}/{key} | {sample.RowCount:N0} filas | {preview.Schema.Rows.Count} columnas");
    }
}
var manifestPath = Path.Combine(output, "manifest.json");
File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Ejemplos conservados en el bucket {bucket}, carpeta {prefix}");
Console.WriteLine($"Manifiesto local: {manifestPath}");

internal sealed record Sample(string Name, int RowCount, string Query);
internal sealed record SampleManifest(string Endpoint, string Bucket, string Key, int Rows, int Columns, long Bytes);
