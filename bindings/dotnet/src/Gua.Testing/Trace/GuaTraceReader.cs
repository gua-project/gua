using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gua.Testing;

/// <summary>Bounded offline reader. Does not follow URLs, links, recordings or arbitrary attachment paths.</summary>
public static class GuaTraceReader
{
    private static readonly Regex BlobName = new(@"^(snapshots|attachments)/[0-9a-f]{64}\.json$", RegexOptions.CultureInvariant);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static bool Id(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void Require(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object || names.Any(name => !root.TryGetProperty(name, out _))) throw new JsonException();
    }
    public static GuaTraceReadResult Read(string directory, long maxBytes = 256L * 1024 * 1024,
        int maxRecords = 100000, int maxRecordBytes = 4 * 1024 * 1024)
    {
        if (maxBytes < 1024 || maxRecords < 1 || maxRecordBytes < 256) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        directory = Path.GetFullPath(directory); CheckNoLinks(directory);
        long budget = maxBytes + 65536;
        var manifestPath = Path.Combine(directory, "manifest.json");
        CheckNoLinks(manifestPath);
        var manifestBytes = ReadBounded(manifestPath, Math.Min(budget, 65536)); budget -= manifestBytes.Length;
        GuaTraceManifest manifest;
        try
        {
            using var document = JsonDocument.Parse(StrictUtf8.GetString(manifestBytes));
            Require(document.RootElement, "schemaVersion", "traceId", "captureMode", "savePolicy", "profile", "primaryOutcome",
                "finalized", "collectionClock", "startedAt", "lastSequence", "quality");
            Require(document.RootElement.GetProperty("quality"), "detailStopped", "evictedSteps", "droppedEvents", "issues");
            manifest = document.RootElement.Deserialize<GuaTraceManifest>(GuaTraceJson.Options)
                ?? throw new JsonException();
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException) { throw new InvalidDataException("Invalid Trace manifest."); }
        if (manifest.SchemaVersion != 1 || manifest.Quality is null || manifest.Quality.Issues is null ||
            !Id(manifest.TraceId) || manifest.CollectionClock is null || manifest.StartedAt is null ||
            manifest.LastSequence is < 0 or > 9007199254740991 || manifest.Quality.EvictedSteps < 0 ||
            manifest.Quality.DroppedEvents < 0 || manifest.Quality.Issues.Count > 64 || manifest.Quality.Issues.Any(s => s is null) ||
            manifest.CaptureMode is not ("recent" or "streaming") ||
            manifest.SavePolicy is not ("onFailure" or "always") || manifest.Profile is not ("debug" or "player") ||
            manifest.PrimaryOutcome is not ("unknown" or "passed" or "failed" or "interrupted"))
            throw new InvalidDataException("Unsupported or invalid Trace manifest.");
        var issues = new List<string>();
        if (!manifest.Finalized) issues.Add("unfinalized");
        var events = new List<GuaTraceEvent>();
        var blobs = new Dictionary<string, JsonElement>();
        var path = Path.Combine(directory, "events.jsonl"); CheckNoLinks(path);
        if (!File.Exists(path)) { issues.Add("events-missing"); return new(manifest, events, blobs, issues); }
        long previous = 0;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var line = new MemoryStream())
        {
            int b;
            while ((b = stream.ReadByte()) != -1)
            {
                if (--budget < 0 || line.Length >= maxRecordBytes || events.Count >= maxRecords)
                { issues.Add("reader-limit"); break; }
                if (b != '\n') { line.WriteByte((byte)b); continue; }
                try
                {
                    using var document = JsonDocument.Parse(StrictUtf8.GetString(line.ToArray()));
                    Require(document.RootElement, "schemaVersion", "traceId", "sequence", "eventId", "stepId", "type", "collectedMilliseconds", "data");
                    var record = document.RootElement.Deserialize<GuaTraceEvent>(GuaTraceJson.Options);
                    if (record is null || record.SchemaVersion != 1 || record.TraceId != manifest.TraceId ||
                        record.Sequence <= previous || record.Sequence > 9007199254740991 || string.IsNullOrEmpty(record.EventId) ||
                        !Id(record.StepId) || string.IsNullOrEmpty(record.Type) || record.Type.Length > 64 ||
                        record.Data.ValueKind == JsonValueKind.Undefined ||
                        record.CollectedMilliseconds < 0 || double.IsInfinity(record.CollectedMilliseconds) ||
                        double.IsNaN(record.CollectedMilliseconds))
                        throw new JsonException();
                    events.Add(record); previous = record.Sequence;
                }
                catch (Exception error) when (error is JsonException or DecoderFallbackException) { issues.Add("invalid-record"); break; }
                line.SetLength(0);
            }
            if (line.Length > 0 && !issues.Contains("invalid-record") && !issues.Contains("reader-limit")) issues.Add("incomplete-tail");
        }
        if (manifest.Finalized && previous != manifest.LastSequence) issues.Add("sequence-incomplete");
        var observations = new HashSet<string>(events.Where(e => e.Type == "observation" && e.Data.ValueKind == JsonValueKind.Object)
            .Select(e => e.Data.TryGetProperty("observationId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString()! : ""), StringComparer.Ordinal);
        foreach (var e in events)
        {
            if (e.Type == "assertion.evaluation" && e.Data.ValueKind == JsonValueKind.Object &&
                e.Data.TryGetProperty("observations", out var refs) && refs.ValueKind == JsonValueKind.Array &&
                refs.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.String || !observations.Contains(r.GetString()!)))
                issues.Add("observation-outside-retention");
            if (e.Type is not ("observation" or "attachment") || e.Data.ValueKind != JsonValueKind.Object ||
                !e.Data.TryGetProperty("blob", out var blob) || blob.ValueKind == JsonValueKind.Null) continue;
            if (blob.ValueKind != JsonValueKind.String) { issues.Add("invalid-blob-reference"); continue; }
            var relative = blob.GetString()!;
            if (blobs.ContainsKey(relative)) continue;
            try
            {
                var content = ReadBounded(ResolveBlob(directory, relative), Math.Min(budget, maxRecordBytes));
                budget -= content.Length;
                using var hash = SHA256.Create();
                var expected = BitConverter.ToString(hash.ComputeHash(content)).Replace("-", "").ToLowerInvariant();
                if (Path.GetFileNameWithoutExtension(relative) != expected) { issues.Add("blob-hash-mismatch"); continue; }
                using var doc = JsonDocument.Parse(StrictUtf8.GetString(content));
                blobs.Add(relative, doc.RootElement.Clone());
            }
            catch { issues.Add("blob-unavailable"); }
        }
        return new(manifest, events, blobs, issues.Distinct().ToArray());
    }
    private static byte[] ReadBounded(string path, long limit)
    {
        CheckNoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > limit || stream.Length > int.MaxValue) throw new InvalidDataException("Trace reader byte limit.");
        var data = new byte[(int)stream.Length];
        var offset = 0;
        while (offset < data.Length)
        {
            var read = stream.Read(data, offset, data.Length - offset);
            if (read == 0) throw new InvalidDataException("Truncated Trace file.");
            offset += read;
        }
        return data;
    }
    internal static string ResolveBlob(string directory, string relative)
    {
        if (!BlobName.IsMatch(relative)) throw new InvalidDataException("Invalid Trace blob reference.");
        var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        CheckNoLinks(path); return path;
    }
    internal static void CheckNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Trace paths cannot traverse links.");
    }
}
