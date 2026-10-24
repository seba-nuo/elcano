using MinioDesk.Core;

internal static class ExplorerCacheChecks
{
    public static void Run()
    {
        var profile = new ConnectionProfile { Endpoint = "http://localhost:9000" };
        var location = ExplorerLocation.For(profile, "datos", "a/");
        var page = new ObjectPage([new("a/uno.txt", "uno.txt", false, 42, null), new("a/sub/", "sub", true, 0, null)], null);
        var cache = new ExplorerCache(); var generation = cache.Generation;
        cache.StorePage(location, null, page, generation);
        Check(cache.TryGetPage(location, null, out var found) && ReferenceEquals(found, page), "Explorador: reutiliza un listado sin volver a obtenerlo");
        Check(cache.TryGetTotals(location, out var totals) && totals == new FolderTotals(1, 1, 1), "Explorador: reutiliza totales de una carpeta completa");
        foreach (var other in new[] { location with { ProfileId = Guid.NewGuid() }, location with { Endpoint = "http://localhost:9100" },
            location with { Region = "otra" }, location with { Bucket = "otro" }, location with { Prefix = "b/" } })
            Check(!cache.TryGetPage(other, null, out _), "Explorador: aísla conexión, endpoint, región, bucket y carpeta");
        var second = page with { NextToken = "tercera" };
        cache.StorePage(location, "segunda", second, generation);
        Check(cache.TryGetPage(location, "segunda", out var paged) && ReferenceEquals(paged, second) && cache.TryGetPage(location, null, out found) && ReferenceEquals(found, page),
            "Explorador: cada token de paginación conserva su propio listado");
        var multiPage = location with { Prefix = "muchos/" };
        cache.StorePage(multiPage, null, new ObjectPage(page.Entries, "segunda"), generation);
        Check(!cache.TryGetTotals(multiPage, out _), "Explorador: una primera página incompleta no inventa totales");
        cache.StoreTotals(multiPage, new FolderTotals(450, 0, 3), generation);
        Check(cache.TryGetTotals(multiPage, out totals) && totals.Pages == 3 && cache.TryGetPage(multiPage, null, out _), "Explorador: guarda los recuentos sin perder la primera página");
        cache.Clear();
        Check(cache.Count == 0 && cache.Bytes == 0 && !cache.StorePage(location, null, page, generation), "Explorador: invalidar descarta respuestas de listados anteriores");
        cache.StoreTotals(location, new FolderTotals(999, 0, 5), generation);
        Check(!cache.TryGetTotals(location, out _), "Explorador: un recuento antiguo no repuebla una caché invalidada");
        var small = new ExplorerCache(maxBytes: 100_000, maxEntries: 2);
        small.StorePage(location, null, page, small.Generation); small.StorePage(multiPage, null, page, small.Generation);
        small.TryGetPage(location, null, out _); small.StorePage(location with { Prefix = "tercera/" }, null, page, small.Generation);
        Check(small.Count == 2 && !small.TryGetPage(multiPage, null, out _) && small.TryGetPage(location, null, out _), "Explorador: descarta las páginas menos recientes al alcanzar el límite");
        var limited = new ExplorerCache(maxBytes: 4096);
        var large = new ObjectPage([new(new string('x', 5000), "grande", false, 1, null)], null);
        limited.StorePage(location, null, large, limited.Generation);
        Check(limited.Count == 0 && limited.Bytes <= 4096, "Explorador: un listado demasiado grande no supera el presupuesto de memoria");
    }
    private static void Check(bool condition, string description)
    { if (!condition) throw new InvalidOperationException(description); Console.WriteLine("PASS " + description); }
}
