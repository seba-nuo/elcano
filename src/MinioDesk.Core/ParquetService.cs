using System.Data;
using System.IO.Compression;
using DuckDB.NET.Data;

namespace MinioDesk.Core;

public sealed record ParquetPreview(DataTable Rows, DataTable Schema, string Source);

public sealed class ParquetService
{
    private readonly string extensionDirectory;
    private readonly SemaphoreSlim gate = new(1, 1);
    public ParquetService(string? directory = null) => extensionDirectory = directory ?? Path.Combine(new ProfileStore().DirectoryPath, "extensions");

    public async Task<ParquetPreview> PreviewRemoteAsync(ConnectionProfile profile, string bucket, string key, CancellationToken token)
    {
        profile.Validate();
        if (key.IndexOfAny(['*', '?', '[', ']']) >= 0)
            throw new ArgumentException("Para previsualizar esta clave con caracteres de patrón, descarga el archivo y usa Abrir Parquet local.");
        // One query at a time; each isolated database has exactly one endpoint/secret.
        await gate.WaitAsync(token);
        try { return await Task.Run(() => ReadPreview(profile, bucket, key, token), token); }
        finally { gate.Release(); }
    }

    public async Task<ParquetPreview> PreviewLocalAsync(string path, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return await Task.Run(() => ReadPreview(null, "", Path.GetFullPath(path), token), token); }
        finally { gate.Release(); }
    }

    public string PrepareExtension()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        return LoadHttp(command, CancellationToken.None);
    }

    private DuckDBConnection Open()
    {
        var connection = new DuckDBConnection("Data Source=:memory:");
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SET memory_limit='256MB'; SET threads=2; SET preserve_insertion_order=false;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private string LoadHttp(DuckDBCommand command, CancellationToken token)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "extensions", "httpfs.duckdb_extension");
        if (File.Exists(bundled))
        {
            command.CommandText = $"LOAD {Literal(bundled.Replace('\\', '/'))};";
            command.ExecuteNonQuery();
            return bundled;
        }
        // Download with .NET and atomically rename with Windows APIs. Native INSTALL
        // can fail its rename step on synchronized Windows directories.
        command.CommandText = "PRAGMA platform;";
        var platform = (string)command.ExecuteScalar()!;
        var version = command.Connection!.ServerVersion;
        var cached = Path.Combine(extensionDirectory, version, platform, "httpfs.duckdb_extension");
        if (!File.Exists(cached)) DownloadExtensionAsync(version, platform, cached, token).GetAwaiter().GetResult();
        // DuckDB's loader verifies the official extension signature and version.
        command.CommandText = $"LOAD {Literal(cached.Replace('\\', '/'))};";
        command.ExecuteNonQuery();
        return cached;
    }

    private static async Task DownloadExtensionAsync(string version, string platform, string destination, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            await using (var source = await client.GetStreamAsync($"https://extensions.duckdb.org/{version}/{platform}/httpfs.duckdb_extension.gz", deadline.Token))
            await using (var gzip = new GZipStream(source, CompressionMode.Decompress))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
                await gzip.CopyToAsync(output, deadline.Token);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private ParquetPreview ReadPreview(ConnectionProfile? profile, string bucket, string key, CancellationToken token)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        using var registration = token.Register(command.Cancel);
        try
        {
            token.ThrowIfCancellationRequested();
            string source;
            if (profile is not null)
            {
                LoadHttp(command, token);
                command.CommandText = "SET http_timeout=30;";
                command.ExecuteNonQuery();
                token.ThrowIfCancellationRequested();
                var uri = new Uri(profile.Endpoint);
                command.CommandText = $"""
                    CREATE SECRET active_minio (
                        TYPE s3, PROVIDER config,
                        KEY_ID {Literal(profile.AccessKey)}, SECRET {Literal(profile.SecretKey)},
                        ENDPOINT {Literal(uri.Authority)}, REGION {Literal(profile.Region)},
                        URL_STYLE 'path', USE_SSL {(uri.Scheme == "https" ? "true" : "false")}
                    );
                    """;
                try { command.ExecuteNonQuery(); }
                catch { throw new InvalidOperationException("No se pudo configurar la conexión de lectura Parquet. Revisa endpoint, región y credenciales."); }
                source = $"s3://{bucket}/{key}";
            }
            else source = key.Replace('\\', '/');

            var scan = $"read_parquet({Literal(source)}, file_row_number=false)";
            command.CommandText = "SET enable_external_file_cache=false;";
            command.ExecuteNonQuery();
            command.CommandText = $"DESCRIBE SELECT * FROM {scan};";
            var schema = new DataTable();
            schema.Columns.Add("Columna"); schema.Columns.Add("Tipo"); schema.Columns.Add("Nulos");
            var names = new List<string>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var name = reader.GetString(0);
                    schema.Rows.Add(name, reader.GetString(1), reader.GetString(2));
                    if (names.Count < 64) names.Add(name);
                }
            }
            token.ThrowIfCancellationRequested();
            var projection = string.Join(", ", names.Select(name => $"left(CAST({Identifier(name)} AS VARCHAR), 512) AS {Identifier(name)}"));
            command.CommandText = $"SELECT {projection} FROM {scan} LIMIT 200;";
            command.UseStreamingMode = true;
            var rows = new DataTable();
            // Stable internal names avoid WPF binding issues with punctuation in Parquet column names.
            for (var index = 0; index < names.Count; index++) rows.Columns.Add($"c{index}", typeof(string)).Caption = names[index];
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    rows.Rows.Add(values);
                }
            }
            return new ParquetPreview(rows, schema, source);
        }
        catch when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }

    public static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
    private static string Identifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
