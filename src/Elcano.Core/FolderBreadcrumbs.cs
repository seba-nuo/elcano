namespace Elcano.Core;

public sealed record FolderCrumb(string Name, string Prefix, bool IsOverflow = false, bool IsCurrent = false)
{
    public string Hint => IsOverflow ? "Ver todos los niveles" : Prefix.Length == 0 ? Name : Prefix;
}

public static class FolderBreadcrumbs
{
    public static IReadOnlyList<FolderCrumb> Build(string? bucket, string prefix)
    {
        if (bucket is null) return [];
        var crumbs = new List<FolderCrumb> { new(bucket, "") };
        var start = 0;
        for (var index = 0; index < prefix.Length; index++)
        {
            if (prefix[index] != '/') continue;
            var name = prefix[start..index];
            crumbs.Add(new FolderCrumb(name.Length == 0 ? "(vacío)" : name, prefix[..(index + 1)]));
            start = index + 1;
        }
        crumbs[^1] = crumbs[^1] with { IsCurrent = true };
        return crumbs;
    }
    public static IReadOnlyList<FolderCrumb> Compact(IReadOnlyList<FolderCrumb> crumbs)
        => crumbs.Count <= 4 ? crumbs : [crumbs[0], new FolderCrumb("…", "", true), crumbs[^2], crumbs[^1]];
}
