namespace Elcano.Core;

public sealed record SqlSuggestion(string Label, string InsertText, string Detail);

public static class SqlCompletion
{
    public static IReadOnlyList<SqlSuggestion> Suggest(IEnumerable<(string Name, string Type)> columns, string prefix)
    {
        var items = columns.Select(column => new SqlSuggestion(column.Name, ParquetQuery.QuoteColumn(column.Name), column.Type))
            .Append(new SqlSuggestion("archivo", "archivo", "Tabla del archivo abierto"))
            .Concat(new[] { "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "LIMIT", "COUNT", "SUM", "AVG", "DISTINCT", "AS", "AND", "OR", "IS NULL", "HAVING" }
                .Select(word => new SqlSuggestion(word, word, "SQL")));
        return items.Where(item => prefix.Length == 0 || item.Label.Contains(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.Detail == "SQL")
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase).Take(24).ToArray();
    }
}
