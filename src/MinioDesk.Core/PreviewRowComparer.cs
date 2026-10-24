using System.Collections;
using System.Data;
using System.Globalization;
using System.Numerics;

namespace MinioDesk.Core;

public sealed class PreviewRowComparer(string column, string type, bool descending) : IComparer
{
    public int Compare(object? x, object? y)
    {
        var left = ((DataRowView)x!)[column]; var right = ((DataRowView)y!)[column];
        var result = CompareValues(left, right, type);
        return descending ? -Math.Sign(result) : result;
    }
    public static int CompareValues(object? left, object? right, string type)
    {
        if (left is null or DBNull) return right is null or DBNull ? 0 : -1;
        if (right is null or DBNull) return 1;
        var a = left.ToString()!; var b = right.ToString()!; type = type.ToUpperInvariant();
        if (type.Contains("INT", StringComparison.Ordinal) && !type.Contains('[', StringComparison.Ordinal) &&
            BigInteger.TryParse(a, CultureInfo.InvariantCulture, out var ai) && BigInteger.TryParse(b, CultureInfo.InvariantCulture, out var bi)) return ai.CompareTo(bi);
        if (type.StartsWith("DECIMAL", StringComparison.Ordinal) && TryDecimal(a, out var av, out var scaleA) && TryDecimal(b, out var bv, out var scaleB))
            return (av * BigInteger.Pow(10, Math.Max(0, scaleB - scaleA))).CompareTo(bv * BigInteger.Pow(10, Math.Max(0, scaleA - scaleB)));
        if (type is "DOUBLE" or "FLOAT" or "REAL" && double.TryParse(a, CultureInfo.InvariantCulture, out var ad) && double.TryParse(b, CultureInfo.InvariantCulture, out var bd)) return ad.CompareTo(bd);
        if ((type == "DATE" || type.StartsWith("TIMESTAMP", StringComparison.Ordinal)) &&
            DateTimeOffset.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) && DateTimeOffset.TryParse(b, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var bt)) return at.CompareTo(bt);
        return StringComparer.Ordinal.Compare(a, b);
    }
    private static bool TryDecimal(string text, out BigInteger value, out int scale)
    {
        var dot = text.IndexOf('.'); scale = dot < 0 ? 0 : text.Length - dot - 1;
        return BigInteger.TryParse(dot < 0 ? text : text.Remove(dot, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
