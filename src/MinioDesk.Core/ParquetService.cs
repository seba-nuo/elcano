using System.Data;
using System.IO.Compression;
using DuckDB.NET.Data;
using DuckDB.NET.Native;

namespace MinioDesk.Core;

public sealed record ParquetPreview(DataTable Rows, DataTable Schema, string Source, string? Text = null, bool IsTruncated = false,
    DataTable? ResultSchema = null, bool RowsTruncated = false, bool CellsTruncated = false);

public sealed class ParquetService
{
    private readonly string extensionDirectory;
    private readonly SemaphoreSlim gate = new(1, 1);
    public ParquetService(string? directory = null) => extensionDirectory = directory ?? Path.Combine(new ProfileStore().DirectoryPath, "extensions");

    public async Task<ParquetPreview> PreviewRemoteAsync(ConnectionProfile profile, string bucket, string key, CancellationToken token)
        => await QueryRemoteAsync(profile, bucket, key, ParquetQuery.Default, token);

    public async Task<ParquetPreview> QueryRemoteAsync(ConnectionProfile profile, string bucket, string key, string query, CancellationToken token, IReadOnlyList<string>? columns = null)
    {
        profile.Validate();
        if (key.IndexOfAny(['*', '?', '[', ']']) >= 0)
            throw new ArgumentException("Para previsualizar esta clave con caracteres de patrón, descarga el archivo y usa Abrir archivo local.");
        // One query at a time; each isolated database has exactly one endpoint/secret.
        query = ParquetQuery.Normalize(query);
        await gate.WaitAsync(token);
        try { return await Task.Run(() => ReadPreview(profile, bucket, key, query, token, columns), token); }
        finally { gate.Release(); }
    }

    public async Task<ParquetPreview> PreviewLocalAsync(string path, CancellationToken token)
        => await QueryLocalAsync(path, ParquetQuery.Default, token);

    public async Task<ParquetPreview> QueryLocalAsync(string path, string query, CancellationToken token, IReadOnlyList<string>? columns = null)
    {
        query = ParquetQuery.Normalize(query);
        await gate.WaitAsync(token);
        try { return await Task.Run(() => ReadPreview(null, "", Path.GetFullPath(path), query, token, columns), token); }
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

    private ParquetPreview ReadPreview(ConnectionProfile? profile, string bucket, string key, string query, CancellationToken token, IReadOnlyList<string>? columns)
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

            var isCsv = Path.GetExtension(key).Equals(".csv", StringComparison.OrdinalIgnoreCase);
            var scan = isCsv
                ? $"read_csv({Literal(source)}, auto_detect=true, sample_size=2048, maximum_line_size=1048576, buffer_size=4194304, parallel=false)"
                : $"read_parquet({Literal(source)}, file_row_number=false)";
            command.CommandText = "SET enable_external_file_cache=false;";
            command.ExecuteNonQuery();
            command.CommandText = $"CREATE TEMP VIEW archivo AS SELECT * FROM {scan}; CREATE TEMP VIEW parquet AS SELECT * FROM archivo;";
            command.ExecuteNonQuery();
            var schema = Describe(command, "SELECT * FROM archivo", token);
            // Allow reading only the selected object/file after initializing its view.
            command.CommandText = $"SET allowed_paths=[{Literal(source)}]; SET enable_external_access=false; SET lock_configuration=true;";
            command.ExecuteNonQuery();
            token.ThrowIfCancellationRequested();
            try
            {
                using (var statements = ExtractSingle(connection, query)) { }
                var resultSchema = Describe(command, $"SELECT * FROM (\n{query}\n) AS result", token);
                var available = resultSchema.Rows.Cast<DataRow>().ToDictionary(row => (string)row[0], row => (string)row[1], StringComparer.Ordinal);
                var names = columns?.ToArray() ?? available.Keys.Take(64).ToArray();
                if (names.Length is < 1 or > 64 || names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(name => !available.ContainsKey(name)))
                    throw new ParquetQueryException("Elige entre 1 y 64 columnas del resultado actual.");
                var projection = string.Join(", ", names.Select(name => $"left(CAST({Identifier(name)} AS VARCHAR), 513) AS {Identifier(name)}"));
                command.CommandText = $"SELECT {projection} FROM (\n{query}\n) AS result LIMIT 201;";
                command.UseStreamingMode = true;
                var rows = new DataTable();
                for (var index = 0; index < names.Length; index++)
                {
                    var column = rows.Columns.Add($"c{index}", typeof(string)); column.Caption = names[index];
                    column.ExtendedProperties["DuckDB.Type"] = available[names[index]];
                }
                var rowsTruncated = false; var cellsTruncated = false;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (rows.Rows.Count == 200) { rowsTruncated = true; break; }
                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    for (var i = 0; i < values.Length; i++)
                        if (values[i] is string text && text.Length > 512)
                        {
                            var length = char.IsHighSurrogate(text[511]) ? 511 : 512;
                            values[i] = text[..length]; cellsTruncated = true;
                        }
                    rows.Rows.Add(values);
                }
                return new ParquetPreview(rows, schema, source, ResultSchema: resultSchema, RowsTruncated: rowsTruncated, CellsTruncated: cellsTruncated);
            }
            catch (DuckDBException exception) when (!token.IsCancellationRequested)
            {
                var message = exception.Message;
                if (profile is not null)
                {
                    message = message.Replace(profile.SecretKey, "[oculto]", StringComparison.Ordinal)
                        .Replace(profile.AccessKey, "[oculto]", StringComparison.Ordinal);
                }
                throw new ParquetQueryException(message.Length > 1600 ? message[..1600] : message);
            }
        }
        catch when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }

    private static DataTable Describe(DuckDBCommand command, string query, CancellationToken token)
    {
        command.CommandText = $"DESCRIBE {query};";
        var table = new DataTable();
        table.Columns.Add("Columna"); table.Columns.Add("Tipo"); table.Columns.Add("Nulos");
        using var reader = command.ExecuteReader();
        while (reader.Read()) { token.ThrowIfCancellationRequested(); table.Rows.Add(reader.GetString(0), reader.GetString(1), reader.GetString(2)); }
        return table;
    }

    private static DuckDBExtractedStatements ExtractSingle(DuckDBConnection connection, string sql)
    {
        var count = NativeMethods.ExtractStatements.DuckDBExtractStatements(connection.NativeConnection, sql, out var statements);
        if (count == 1) return statements;
        statements.Dispose();
        throw new ParquetQueryException("Usa una sola consulta SELECT o WITH con sintaxis válida.");
    }

    public static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
    private static string Identifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
