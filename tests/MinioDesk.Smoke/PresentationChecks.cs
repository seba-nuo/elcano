using System.Data;
using System.Text.Json;
using DuckDB.NET.Data;
using MinioDesk.Core;

internal static class PresentationChecks
{
    public static async Task RunAsync(string directory)
    {
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }
        foreach (var name in new[] { "total-ventas", "total ventas", "país.origen", "dos__guiones", "col\"umna" })
        {
            var quoted = ParquetQuery.QuoteColumn(name); var sql = "SELECT " + quoted + " FROM archivo";
            foreach (var caret in new[] { 9, 7 + quoted.Length - 1, 7 + quoted.Length })
            {
                var span = SqlCompletionSpan.At(sql, caret);
                Check(sql[..span.Start] + quoted + sql[span.End..] == sql, "Autocompletado reemplaza el identificador completo: " + name);
            }
        }
        var incomplete = "SELECT \"total-v"; var pending = SqlCompletionSpan.At(incomplete, incomplete.Length);
        Check(pending.Start == 7 && pending.Prefix == "total-v", "Autocompletado reconoce nombres entre comillas sin cerrar");
        foreach (var sql in new[] { "SELECT 'texto'", "SELECT 1 -- comentario", "SELECT /* comentario */ 1", "SELECT $$texto$$" })
            Check(!SqlCompletionSpan.At(sql, sql.IndexOf(sql.Contains("comentario") ? "comentario" : "texto", StringComparison.Ordinal) + 2).CanComplete, "No ofrece columnas dentro de cadenas o comentarios");
        foreach (var sql in new[] { "SELECT 'texto", "SELECT 1 -- comentario", "SELECT /* comentario", "SELECT $$texto" })
            Check(!SqlCompletionSpan.At(sql, sql.Length).CanComplete, "No autocompleta al final de una cadena o comentario sin cerrar");
        Check(PreviewRowComparer.CompareValues("2", "10", "BIGINT") < 0 && PreviewRowComparer.CompareValues("100", "10", "BIGINT") > 0, "Orden numérico correcto: 2, 10, 100");
        Check(PreviewRowComparer.CompareValues("99999999999999999999999999999999999999.01", "99999999999999999999999999999999999999.02", "DECIMAL(38,2)") < 0, "Orden decimal sin pérdida de precisión");
        Check(PreviewRowComparer.CompareValues(DBNull.Value, "", "VARCHAR") < 0 && PreviewRowComparer.CompareValues("-2.1", "-2.01", "DECIMAL(8,2)") < 0, "Orden distingue NULL, vacío y decimales negativos");
        var path = Path.Combine(directory, "wide.parquet");
        using (var connection = new DuckDBConnection("Data Source=:memory:"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = $"COPY (SELECT i AS id, repeat('x', 600) AS largo, NULL::VARCHAR AS nulo, '' AS vacio, {string.Join(",", Enumerable.Range(0, 70).Select(i => $"i AS \"col-{i}\""))} FROM range(300) t(i)) TO {ParquetService.Literal(path.Replace('\\', '/'))} (FORMAT PARQUET);";
            command.ExecuteNonQuery();
        }
        var service = new ParquetService(); var preview = await service.QueryLocalAsync(path, "SELECT * FROM archivo", CancellationToken.None);
        Check(preview.Rows.Rows.Count == 200 && preview.Rows.Columns.Count == 64 && preview.ResultSchema!.Rows.Count == 74 && preview.RowsTruncated && preview.CellsTruncated, "Vista informa límites de filas, columnas y celdas recortadas");
        Check(preview.Rows.Rows[0][2] is DBNull && (string)preview.Rows.Rows[0][3] == "", "El resultado conserva NULL distinto de texto vacío");
        var chosen = await service.QueryLocalAsync(path, "SELECT * FROM archivo", CancellationToken.None, ["col-69", "id"]);
        Check(chosen.Rows.Columns.Count == 2 && chosen.Rows.Columns[0].Caption == "col-69" && chosen.Rows.Columns[1].ExtendedProperties["DuckDB.Type"]?.ToString() == "BIGINT", "Se pueden elegir columnas posteriores a la 64 y conservar sus tipos");
        try { await service.QueryLocalAsync(path, "SELECT * FROM archivo", CancellationToken.None, ["no-existe"]); throw new Exception("Se aceptó una columna inexistente"); }
        catch (ParquetQueryException) { Check(true, "Selector valida columnas del resultado actual"); }
        var logs = new DiagnosticLog(Path.Combine(directory, "logs"));
        var detail = logs.Record(new InvalidOperationException("SECRET-DO-NOT-LOG SELECT clave FROM privado"), "Test");
        var stored = File.ReadAllText(Path.Combine(logs.DirectoryPath, "diagnostics.jsonl"));
        Check(!stored.Contains("SECRET") && !stored.Contains("SELECT") && !detail.Display.Contains("privado") && stored.Contains(detail.Id), "Diagnóstico conserva referencia y tipo sin mensajes, consultas ni secretos");
        JsonDocument.Parse(stored);
        var preferences = new ClientPreferencesStore(Path.Combine(directory, "settings")); preferences.Save(new(false));
        Check(!preferences.Load().PrefetchEnabled, "La preferencia de precarga persiste");
    }
}
