using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Elcano.Core;

internal static class FolderProtocolChecks
{
    public static async Task RunAsync(string directory)
    {
        using var server = new S3LoopbackServer();
        var profile = new ConnectionProfile { Name = "S3 simulado", Endpoint = server.Endpoint, AccessKey = "test-key", SecretKey = "test-secret" };
        var service = new FolderTransferService();
        for (var index = 0; index < 1003; index++) server.Objects[$"tree/sub/{index:D4}_México.txt"] = Encoding.UTF8.GetBytes("x");
        server.Objects["tree/"] = []; server.Objects["tree/vacía/"] = [];
        var contents = Encoding.UTF8.GetBytes("id,pais\n1,México\n"); server.Objects["tree/ventas.csv"] = contents;
        server.Objects["tree-extra/conservar.txt"] = [1]; server.Objects["tree"] = [2];
        using (var plan = await service.PrepareAsync(profile, "demo", "tree/", CancellationToken.None))
        {
            Check(plan.Objects == 1006 && plan.Files == 1004 && server.ListCalls >= 2, "Inventario recursivo con varias páginas S3");
            try { await service.DeleteAsync(profile with { Id = Guid.NewGuid() }, plan, null, CancellationToken.None); throw new Exception("Se reutilizó un inventario con otra conexión"); }
            catch (ArgumentException) { Check(server.Objects.Count == 1008, "El inventario no permite eliminar desde una conexión distinta"); }
            var path = Path.Combine(directory, "carpeta.zip");
            await service.DownloadZipAsync(profile, plan, path, null, CancellationToken.None);
            using (var archive = ZipFile.OpenRead(path))
            {
                Check(archive.Entries.Count(entry => !entry.FullName.EndsWith('/')) == 1004 && archive.GetEntry("tree/vacía/") is not null, "ZIP completo con subcarpetas vacías");
                using var stream = archive.GetEntry("tree/ventas.csv")!.Open(); using var copy = new MemoryStream(); await stream.CopyToAsync(copy);
                Check(copy.ToArray().SequenceEqual(contents) && archive.GetEntry("tree/sub/0000_México.txt") is not null, "ZIP íntegro y nombres Unicode");
                Check(archive.Entries.All(entry => entry.FullName.StartsWith("tree/", StringComparison.Ordinal)), "ZIP limitado a la carpeta seleccionada");
            }
            var cancelledZip = Path.Combine(directory, "cancelado.zip"); File.WriteAllText(cancelledZip, "anterior");
            using var downloadCancel = new CancellationTokenSource();
            try
            {
                await service.DownloadZipAsync(profile, plan, cancelledZip, new InlineProgress<FolderProgress>(value => { if (value.Bytes > 0) downloadCancel.Cancel(); }), downloadCancel.Token);
                throw new Exception("Se ignoró la cancelación del ZIP");
            }
            catch (OperationCanceledException) { }
            Check(File.ReadAllText(cancelledZip) == "anterior" && !Directory.GetFiles(directory, "*.part").Any(), "Cancelar ZIP conserva el destino anterior y limpia parciales");
            using var deleteCancel = new CancellationTokenSource();
            try
            {
                await service.DeleteAsync(profile, plan, new InlineProgress<FolderProgress>(value => { if (value.Completed >= 1000) deleteCancel.Cancel(); }), deleteCancel.Token);
                throw new Exception("Se ignoró la cancelación de eliminar carpeta");
            }
            catch (OperationCanceledException) { }
            Check(server.Objects.Keys.Count(key => key.StartsWith("tree/", StringComparison.Ordinal)) == 6, "Eliminar por lotes permite cancelar entre lotes");
            Check(server.DeleteBatches.All(size => size <= 1000), "Los lotes de eliminación respetan el máximo S3");
        }
        using (var remaining = await service.PrepareAsync(profile, "demo", "tree/", CancellationToken.None))
            await service.DeleteAsync(profile, remaining, null, CancellationToken.None);
        Check(!server.Objects.Keys.Any(key => key.StartsWith("tree/", StringComparison.Ordinal)) && server.Objects.ContainsKey("tree") && server.Objects.ContainsKey("tree-extra/conservar.txt"), "Eliminación completa conserva objetos y prefijos vecinos");
        server.Objects["partial/a.txt"] = [1]; server.Objects["partial/b.txt"] = [2]; server.Denied.Add("partial/b.txt");
        using (var partial = await service.PrepareAsync(profile, "demo", "partial/", CancellationToken.None))
        {
            try { await service.DeleteAsync(profile, partial, null, CancellationToken.None); throw new Exception("Se ignoró el error parcial S3"); }
            catch (FolderTransferException exception) { Check(exception.Message.Contains("parcial") && !server.Objects.ContainsKey("partial/a.txt") && server.Objects.ContainsKey("partial/b.txt"), "Errores parciales de DeleteObjects se informan sin afirmar éxito total"); }
        }
        server.Objects["unsafe/../escape.txt"] = [1];
        server.Objects["conflict/A.txt"] = [1]; server.Objects["conflict/a.txt"] = [2];
        foreach (var prefix in new[] { "unsafe/", "conflict/" })
        {
            using var plan = await service.PrepareAsync(profile, "demo", prefix, CancellationToken.None);
            var path = Path.Combine(directory, prefix[..^1] + ".zip");
            try { await service.DownloadZipAsync(profile, plan, path, null, CancellationToken.None); throw new Exception("Se permitió una ruta ZIP inválida"); }
            catch (FolderTransferException) { Check(!File.Exists(path) && !Directory.GetFiles(directory, "*.part").Any(), "ZIP rechaza rutas inseguras y colisiones de Windows sin dejar parciales"); }
        }
        Check(server.Errors.IsEmpty, "El protocolo HTTP S3 no produjo errores en el servidor de pruebas");
        Console.WriteLine("Pruebas de carpetas completadas con S3 simulado local; no sustituyen la integración con MinIO.");
    }
    private static void Check(bool condition, string text)
    { if (!condition) throw new InvalidOperationException(text); Console.WriteLine("PASS " + text); }

    private sealed class S3LoopbackServer : IDisposable
    {
        private static readonly XNamespace Ns = "http://s3.amazonaws.com/doc/2006-03-01/";
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task accepting;
        public ConcurrentDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Denied { get; } = new(StringComparer.Ordinal);
        public ConcurrentBag<int> DeleteBatches { get; } = [];
        public ConcurrentQueue<Exception> Errors { get; } = new();
        public int ListCalls;
        public string Endpoint { get; }
        public S3LoopbackServer()
        {
            listener.Start(); Endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            accepting = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); _ = Task.Run(() => HandleAsync(client)); } }
            catch (Exception) when (stop.IsCancellationRequested) { }
        }
        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream(); using var input = new BufferedStream(stream, 16384);
                    var request = ReadLine(input).Split(' '); var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string line;
                    while ((line = ReadLine(input)).Length > 0) { var colon = line.IndexOf(':'); headers[line[..colon]] = line[(colon + 1)..].Trim(); }
                    if (headers.TryGetValue("Expect", out var expect) && expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"));
                    var length = headers.TryGetValue("Content-Length", out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : 0;
                    if (length > 2 * 1024 * 1024) throw new InvalidDataException("Solicitud de prueba demasiado grande");
                    var body = new byte[length]; await input.ReadExactlyAsync(body, stop.Token);
                    var uri = new Uri(Endpoint + request[1]);
                    var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2))
                        .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => part.Length > 1 ? Uri.UnescapeDataString(part[1]) : "");
                    if (request[0] == "GET" && query.ContainsKey("list-type"))
                    {
                        Interlocked.Increment(ref ListCalls);
                        var prefix = query.GetValueOrDefault("prefix", ""); var offset = int.Parse(query.GetValueOrDefault("continuation-token", "0"), CultureInfo.InvariantCulture);
                        var maximum = int.Parse(query.GetValueOrDefault("max-keys", "1000"), CultureInfo.InvariantCulture);
                        var keys = Objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(key => key, StringComparer.Ordinal).ToArray();
                        var page = keys.Skip(offset).Take(maximum).ToArray(); var truncated = offset + page.Length < keys.Length;
                        var xml = new XElement(Ns + "ListBucketResult", new XElement(Ns + "Name", "demo"), new XElement(Ns + "Prefix", prefix),
                            new XElement(Ns + "KeyCount", page.Length), new XElement(Ns + "MaxKeys", maximum), new XElement(Ns + "IsTruncated", truncated),
                            truncated ? new XElement(Ns + "NextContinuationToken", offset + page.Length) : null,
                            page.Select(key => new XElement(Ns + "Contents", new XElement(Ns + "Key", key), new XElement(Ns + "Size", Objects[key].Length),
                                new XElement(Ns + "LastModified", "2026-01-01T00:00:00.000Z"), new XElement(Ns + "ETag", "\"test\""))));
                        await RespondAsync(stream, Encoding.UTF8.GetBytes(xml.ToString()), "application/xml");
                    }
                    else if (request[0] == "POST" && query.ContainsKey("delete"))
                    {
                        var xml = XDocument.Parse(Encoding.UTF8.GetString(body));
                        var keys = xml.Descendants().Where(element => element.Name.LocalName == "Object").Select(element => element.Elements().Single(child => child.Name.LocalName == "Key").Value).ToArray();
                        DeleteBatches.Add(keys.Length);
                        var errors = new List<XElement>();
                        foreach (var key in keys)
                        {
                            if (Denied.Contains(key)) errors.Add(new XElement(Ns + "Error", new XElement(Ns + "Key", key), new XElement(Ns + "Code", "AccessDenied"), new XElement(Ns + "Message", "denegado en prueba")));
                            else Objects.TryRemove(key, out _);
                        }
                        await RespondAsync(stream, Encoding.UTF8.GetBytes(new XElement(Ns + "DeleteResult", errors).ToString()), "application/xml");
                    }
                    else
                    {
                        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'); var slash = path.IndexOf('/'); var key = slash >= 0 ? path[(slash + 1)..] : "";
                        if (Objects.TryGetValue(key, out var data)) await RespondAsync(stream, data, "application/octet-stream");
                        else await RespondAsync(stream, Encoding.UTF8.GetBytes("<Error><Code>NoSuchKey</Code></Error>"), "application/xml", 404);
                    }
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
                catch (Exception exception) { Errors.Enqueue(exception); }
            }
        }
        private static string ReadLine(Stream input)
        {
            var line = new StringBuilder(); int character;
            while ((character = input.ReadByte()) != -1)
            {
                if (character == '\n') return line.ToString().TrimEnd('\r');
                line.Append((char)character); if (line.Length > 65536) throw new InvalidDataException("Cabecera demasiado larga");
            }
            throw new IOException("Conexión cerrada");
        }
        private static async Task RespondAsync(Stream stream, byte[] data, string contentType, int status = 200)
        {
            var header = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: {contentType}\r\nContent-Length: {data.Length}\r\nConnection: close\r\nx-amz-request-id: test\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header)); await stream.WriteAsync(data);
        }
        public void Dispose() { stop.Cancel(); listener.Stop(); accepting.GetAwaiter().GetResult(); stop.Dispose(); }
    }
}
