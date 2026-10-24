using System.Text;
using System.Text.RegularExpressions;

namespace Elcano.Core;

public sealed class ParquetQueryException(string message) : Exception(message);

public static class ParquetQuery
{
    public const string Default = "SELECT * FROM archivo LIMIT 200";

    // Preserve quoted text, strip comments and accept exactly one SELECT/WITH.
    // Execution is also wrapped as a subquery and DuckDB restricts file access.
    public static string Normalize(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new ParquetQueryException("Escribe una consulta SELECT sobre archivo.");
        if (sql.Length > 20_000) throw new ParquetQueryException("La consulta supera los 20 000 caracteres.");
        var output = new StringBuilder();
        var ended = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                output.Append('\n'); continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var depth = 1; i += 2;
                while (i < sql.Length && depth > 0)
                {
                    if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*') { depth++; i += 2; }
                    else if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                if (depth != 0) throw new ParquetQueryException("El comentario SQL no está cerrado.");
                i--; output.Append(' '); continue;
            }
            if (char.IsWhiteSpace(c)) { output.Append(c); continue; }
            if (ended) throw new ParquetQueryException("Ejecuta una sola consulta SELECT o WITH a la vez.");
            if (c == ';') { ended = true; continue; }
            if (c is '\'' or '"')
            {
                var quote = c; var closed = false; output.Append(c);
                while (++i < sql.Length)
                {
                    output.Append(sql[i]);
                    if (sql[i] != quote) continue;
                    if (i + 1 < sql.Length && sql[i + 1] == quote) { output.Append(sql[++i]); continue; }
                    closed = true; break;
                }
                if (!closed) throw new ParquetQueryException("Hay una cadena o un nombre de columna sin cerrar.");
                continue;
            }
            if (c == '$')
            {
                var match = Regex.Match(sql[i..], @"^\$[A-Za-z_0-9]*\$");
                if (match.Success)
                {
                    var end = sql.IndexOf(match.Value, i + match.Length, StringComparison.Ordinal);
                    if (end < 0) throw new ParquetQueryException("La cadena SQL no está cerrada.");
                    output.Append(sql.AsSpan(i, end + match.Length - i)); i = end + match.Length - 1; continue;
                }
            }
            output.Append(c);
        }
        var result = output.ToString().Trim();
        if (!Regex.IsMatch(result, @"\A(SELECT|WITH)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new ParquetQueryException("Usa SELECT o WITH. El archivo está disponible como tabla archivo.");
        return result;
    }

    public static string QuoteColumn(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}
