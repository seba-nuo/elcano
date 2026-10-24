using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.S3;

namespace MinioDesk.Core;

public sealed record DiagnosticEntry(string Id, DateTimeOffset Time, string Operation, string ExceptionType, string? Code, int? HttpStatus, string[] Frames)
{
    public string Display => $"Referencia: {Id}\nFecha: {Time:O}\nOperación: {Operation}\nTipo: {ExceptionType}\nCódigo: {Code ?? "—"}\nHTTP: {HttpStatus?.ToString() ?? "—"}\n" + string.Join("\n", Frames);
}
public sealed class DiagnosticLog(string? directory = null)
{
    private static readonly object Gate = new();
    public string DirectoryPath { get; } = directory ?? Path.Combine(new ProfileStore().DirectoryPath, "logs");
    public DiagnosticEntry Record(Exception exception, string operation)
    {
        // Only metadata is allowed: no exception messages, SQL, URLs, credentials, filenames or parameter values.
        static string Safe(string value) => Regex.IsMatch(value, @"\A[A-Za-z0-9_.+`<>-]{1,160}\z") ? value : "omitido";
        var s3 = exception as AmazonS3Exception;
        var entry = new DiagnosticEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, Safe(operation), Safe(exception.GetType().FullName ?? "Exception"),
            s3?.ErrorCode is { } code ? Safe(code) : null, s3 is null ? null : (int)s3.StatusCode,
            new StackTrace(exception, false).GetFrames().Take(12).Select(frame => frame.GetMethod()).Where(method => method is not null)
                .Select(method => Safe((method!.DeclaringType?.FullName ?? "") + "." + method.Name)).ToArray());
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath); var path = Path.Combine(DirectoryPath, "diagnostics.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
        }
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException) { }
        return entry;
    }
}
