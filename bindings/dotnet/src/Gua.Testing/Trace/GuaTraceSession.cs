using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gua.Core;

namespace Gua.Testing;

/// <summary>Framework-independent, bounded fact recorder. Never polls or operates a runtime.</summary>
public sealed partial class GuaTraceSession : IDisposable, IAsyncDisposable
{
    private sealed class Step
    {
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal readonly List<GuaTraceEvent> Events = new();
        internal readonly Dictionary<string, byte[]> Blobs = new();
        internal readonly HashSet<GuaTraceRequest> Requests = new();
        internal long Bytes;
        internal bool Ended;
        internal GuaTraceStepKind Kind;
        internal GuaTraceOutcome Outcome;
        internal bool ClientCompleted;
    }
    private sealed record Batch(GuaTraceEvent[] Events, Dictionary<string, byte[]> Blobs,
        GuaTraceManifest? Manifest, bool Replace, bool Discard, long Bytes,
        TaskCompletionSource<bool>? Completion = null);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly GuaTraceOptions _options;
    private readonly GuaTraceRedaction _redaction;
    private readonly LinkedList<Step> _steps = new();
    private readonly Dictionary<string, Step> _byId = new();
    private readonly Dictionary<GuaTraceRequest, string> _requests = new();
    private readonly HashSet<string> _issues = new(StringComparer.Ordinal);
    private readonly BlockingCollection<Batch> _queue;
    private readonly Task _writer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string _startedAt = DateTimeOffset.UtcNow.ToString("O");
    private long _sequence, _memoryBytes, _queuedBytes, _artifactBytes, _evicted, _dropped, _unfinishedSteps;
    private bool _stopped, _closed, _disposed, _outcomeSet;
    private GuaTraceOutcome _outcome;
    private Task<bool>? _pendingCheckpoint;
    public string TraceId { get; } = Guid.NewGuid().ToString("N");
    public string ArtifactPath { get; }

    public GuaTraceSession(GuaTraceOptions? options = null)
    {
        _options = options ?? new();
        if (_options.MaxSteps is < 1 or > 10000 || _options.MaxMemoryBytes < 1024 ||
            _options.MaxEventBytes < 256 || _options.MaxAttachmentBytes < 1 ||
            _options.MaxQueueItems is < 1 or > 65536 || _options.MaxArtifactBytes < 1024 ||
            _options.FlushTimeout <= TimeSpan.Zero || _options.FlushTimeout.TotalMilliseconds > int.MaxValue ||
            _options.Profile is not ("debug" or "player") ||
            !Enum.IsDefined(typeof(GuaTraceCaptureMode), _options.CaptureMode) ||
            !Enum.IsDefined(typeof(GuaTraceSavePolicy), _options.SavePolicy))
            throw new ArgumentException("Invalid Gua Trace options.", nameof(options));
        _redaction = new(_options.Secrets);
        ArtifactPath = Path.Combine(Path.GetFullPath(_options.OutputDirectory), TraceId);
        _queue = new(_options.MaxQueueItems);
        _writer = Task.Run(WriteLoop);
    }

    public GuaTraceStatus Status { get { lock (_gate) return StatusUnsafe(); } }
    private GuaTraceStatus StatusUnsafe() => new(_stopped, _evicted, _dropped, _issues.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    private void Stop(string reason) { _stopped = true; _issues.Add(reason); }
    private GuaTraceManifest Manifest(bool finalized) => new(1, TraceId,
        _options.CaptureMode == GuaTraceCaptureMode.Recent ? "recent" : "streaming",
        _options.SavePolicy == GuaTraceSavePolicy.OnFailure ? "onFailure" : "always", _options.Profile,
        _outcome.ToString().ToLowerInvariant(), finalized, "collector-monotonic:" + TraceId, _startedAt,
        _sequence, StatusUnsafe());

    /// <summary>Returns an empty ID when recording is unavailable; does not fail the caller's operation.</summary>
    public string BeginStep(GuaTraceStepKind kind, string label, GuaTraceRequest? request = null,
        string? parentStepId = null, string? sourceFile = null, int? sourceLine = null)
    {
        lock (_gate)
        {
            if (_closed || _stopped) { _dropped++; return ""; }
            if (request is not null && _requests.TryGetValue(request, out var existing)) return existing;
            if (request is not null && !ValidRequest(request)) { _issues.Add("invalid-request"); return ""; }
            while (_steps.Count >= _options.MaxSteps)
            {
                var old = _steps.First!.Value;
                _steps.RemoveFirst(); _byId.Remove(old.Id); _memoryBytes -= old.Bytes; _evicted++;
                foreach (var key in old.Requests) _requests.Remove(key);
            }
            var step = new Step { Kind = kind }; _steps.AddLast(step); _byId.Add(step.Id, step);
            if (request is not null) { step.Requests.Add(request); _requests.Add(request, step.Id); }
            var location = sourceFile?.Replace('\\', '/').Split('/').Last();
            if (AppendUnsafe(step, "step.begin", GuaTraceJson.Element(new { kind, label, request, parentStepId,
                source = location is null ? null : new { file = location, line = sourceLine } })))
                _unfinishedSteps++;
            return step.Id;
        }
    }

    public bool Correlate(string stepId, GuaTraceRequest request)
    {
        lock (_gate)
        {
            if (_closed || !_byId.TryGetValue(stepId, out var step) || !ValidRequest(request)) return false;
            if (_requests.TryGetValue(request, out var existing)) return existing == stepId;
            if (step.Requests.Count >= 64) { Stop("correlation-limit"); return false; }
            if (!AppendUnsafe(step, "request.correlated", GuaTraceJson.Element(request))) return false;
            step.Requests.Add(request); _requests.Add(request, stepId); return true;
        }
    }
    private static bool ValidRequest(GuaTraceRequest request) =>
        !string.IsNullOrEmpty(request.SourceId) && request.SourceId.Length <= 128 &&
        ulong.TryParse(request.SessionEpoch, out _) && ulong.TryParse(request.RequestId, out _);

    /// <summary>Append native phases to an existing client step, including late completion.</summary>
    public bool RecordRequest(GuaTraceRequest request, string phase, JsonElement data)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(request, out var id)) { _issues.Add("request-outside-correlation-window"); return false; }
            return Record(id, "request.phase", GuaTraceJson.Element(new { request, phase, result = data }));
        }
    }
    public bool Record(string stepId, string type, JsonElement data, bool sensitive = false)
    {
        lock (_gate)
        {
            if (_closed || !_byId.TryGetValue(stepId, out var step)) { _dropped++; _issues.Add("step-outside-retention"); return false; }
            if (string.IsNullOrEmpty(type) || type.Length > 64 || type.Any(c => !char.IsLetterOrDigit(c) && c != '.' && c != '-'))
            { _issues.Add("invalid-event-type"); return false; }
            var recorded = AppendUnsafe(step, type, data, sensitive);
            if (recorded && type == "request.completion") step.ClientCompleted = true;
            return recorded;
        }
    }
    public void EndStep(string stepId, GuaTraceOutcome outcome)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(stepId, out var step) || step.Ended || _closed) return;
            if (!Enum.IsDefined(typeof(GuaTraceOutcome), outcome)) { _issues.Add("invalid-step-outcome"); return; }
            if (AppendUnsafe(step, "step.end", GuaTraceJson.Element(new { outcome })))
            {
                step.Ended = true; step.Outcome = outcome; _unfinishedSteps--;
                if (outcome is GuaTraceOutcome.Unknown or GuaTraceOutcome.Interrupted)
                    _issues.Add("uncertain-step-outcome");
            }
        }
    }
    public string Mark(string label)
    {
        var id = BeginStep(GuaTraceStepKind.Mark, label); EndStep(id, GuaTraceOutcome.Passed); return id;
    }
    public bool Annotate(string stepId, string name, GuaValue value, bool sensitive = false)
    {
        if (sensitive) return Record(stepId, "annotation", GuaTraceJson.Element(new { name, value = "[redacted]" }));
        // GuaValue remains the native-backed source of truth; Trace does not reimplement Value validation.
        using var doc = JsonDocument.Parse(value.ToJson());
        return Record(stepId, "annotation", GuaTraceJson.Element(new { name, value = doc.RootElement }));
    }
    public bool Evaluate(string stepId, string target, string @operator, GuaValue? expected, GuaValue? actual,
        string truth, string role, IReadOnlyList<string> observations, GuaTraceOutcome callerOutcome,
        bool sensitive = false)
    {
        if (truth is not ("true" or "false" or "unknown")) return false;
        using var e = JsonDocument.Parse(sensitive || expected is null ? "null" : expected.ToJson());
        using var a = JsonDocument.Parse(sensitive || actual is null ? "null" : actual.ToJson());
        return Record(stepId, "assertion.evaluation", GuaTraceJson.Element(new { target, @operator,
            expected = expected is null ? (JsonElement?)null : e.RootElement,
            actual = actual is null ? (JsonElement?)null : a.RootElement, truth, role, observations, callerOutcome }), sensitive);
    }

    /// <summary>Only received changes are recorded. Missing before/after is absence, never a dummy null Value.</summary>
    public bool Change(string stepId, string target, string change, GuaTraceHost host,
        GuaValue? before, GuaValue? after, string continuity = "unverified", bool sensitive = false)
    {
        if (change is not ("added" or "removed" or "changed") ||
            (change == "added" && (before is not null || after is null)) ||
            (change == "removed" && (before is null || after is not null)) ||
            (change == "changed" && (before is null || after is null))) return false;
        using var b = JsonDocument.Parse(sensitive || before is null ? "null" : before.ToJson());
        using var a = JsonDocument.Parse(sensitive || after is null ? "null" : after.ToJson());
        return Record(stepId, "observation.change", GuaTraceJson.Element(new { target, change, host, continuity,
            before = before is null ? (JsonElement?)null : b.RootElement,
            after = after is null ? (JsonElement?)null : a.RootElement }), sensitive);
    }

    /// <summary>Snapshot content is deduplicated after masking, independently of the observation ID.</summary>
    public string Observe(string stepId, string channel, string reason, string availability,
        GuaTraceHost host, JsonElement? snapshot = null, string continuity = "unverified", bool sensitive = false)
    {
        lock (_gate)
        {
            if (_closed || _stopped || !_byId.TryGetValue(stepId, out var step)) return "";
            var observationId = Guid.NewGuid().ToString("N");
            var blobs = new Dictionary<string, byte[]>();
            string? blob = null;
            if (snapshot.HasValue)
            {
                var bytes = _redaction.Json(snapshot.Value, sensitive);
                if (bytes.Length > _options.MaxAttachmentBytes) { Stop("snapshot-limit"); return ""; }
                blob = "snapshots/" + Hash(bytes) + ".json"; blobs.Add(blob, bytes);
            }
            return AppendUnsafe(step, "observation", GuaTraceJson.Element(new { observationId, channel, reason,
                availability, host, continuity, blob }), blobs: blobs) ? observationId : "";
        }
    }
    /// <summary>JSON-only attachment boundary; screenshots require a separate caller-side pixel policy.</summary>
    public bool Attach(string stepId, string schema, JsonElement content, bool sensitive = false)
    {
        lock (_gate)
        {
            if (_closed || _stopped || !_byId.TryGetValue(stepId, out var step)) return false;
            var bytes = _redaction.Json(content, sensitive);
            if (bytes.Length > _options.MaxAttachmentBytes) { Stop("attachment-limit"); return false; }
            var path = "attachments/" + Hash(bytes) + ".json";
            return AppendUnsafe(step, "attachment", GuaTraceJson.Element(new { schema, blob = path }),
                blobs: new() { [path] = bytes });
        }
    }
    private static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
    private bool AppendUnsafe(Step step, string type, JsonElement data, bool sensitive = false,
        Dictionary<string, byte[]>? blobs = null)
    {
        if (_stopped || _closed) { _dropped++; return false; }
        try
        {
            var cleanBytes = _redaction.Json(data, sensitive);
            if (cleanBytes.Length > _options.MaxEventBytes) { Stop("event-limit"); _dropped++; return false; }
            using var clean = JsonDocument.Parse(cleanBytes);
            var next = _sequence + 1;
            var record = new GuaTraceEvent(1, TraceId, next, "e" + next, step.Id,
                _redaction.Text(type), _clock.Elapsed.TotalMilliseconds, clean.RootElement.Clone());
            var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(record, GuaTraceJson.Options)) + 1L;
            blobs ??= new();
            bytes += blobs.Values.Sum(b => (long)b.Length);
            if (_options.CaptureMode == GuaTraceCaptureMode.Streaming)
            {
                if (_artifactBytes + bytes > _options.MaxArtifactBytes) { Stop("artifact-limit"); _dropped++; return false; }
                if (_queuedBytes + bytes > _options.MaxMemoryBytes) { Stop("queue-byte-limit"); _dropped++; return false; }
                _queuedBytes += bytes;
                if (!_queue.TryAdd(new(new[] { record }, blobs, null, false, false, bytes)))
                { _queuedBytes -= bytes; Stop("queue-limit"); _dropped++; return false; }
                _artifactBytes += bytes;
            }
            else
            {
                if (_memoryBytes + bytes > _options.MaxMemoryBytes || _memoryBytes + bytes > _options.MaxArtifactBytes)
                { Stop("retained-byte-limit"); _dropped++; return false; }
                step.Events.Add(record);
                foreach (var pair in blobs) step.Blobs[pair.Key] = pair.Value;
                step.Bytes += bytes; _memoryBytes += bytes;
            }
            _sequence = next; return true;
        }
        catch { Stop("capture-failed"); _dropped++; return false; }
    }

    /// <summary>Freeze the caller's primary result independently of later cleanup and late events.</summary>
    public bool SetPrimaryOutcome(GuaTraceOutcome outcome)
    {
        lock (_gate)
        {
            if (_closed || _outcomeSet || !Enum.IsDefined(typeof(GuaTraceOutcome), outcome)) return false;
            _outcome = outcome; _outcomeSet = true; return true;
        }
    }
    public Task<bool> FlushAsync() => CheckpointAsync(false);
    public async Task<bool> CompleteAsync(GuaTraceOutcome outcome)
    {
        var elapsed = Stopwatch.StartNew();
        SetPrimaryOutcome(outcome);
        await StopLifecycleAsync().ConfigureAwait(false);
        var remaining = _options.FlushTimeout > elapsed.Elapsed ? _options.FlushTimeout - elapsed.Elapsed : TimeSpan.Zero;
        return await CheckpointAsync(true, remaining).ConfigureAwait(false);
    }
    private async Task<bool> CheckpointAsync(bool final, TimeSpan? timeout = null)
    {
        var limit = timeout ?? _options.FlushTimeout;
        var elapsed = Stopwatch.StartNew();
        if (!await _flushGate.WaitAsync(limit).ConfigureAwait(false))
        { lock (_gate) Stop("flush-timeout"); return false; }
        TimeSpan Remaining() => limit > elapsed.Elapsed ? limit - elapsed.Elapsed : TimeSpan.Zero;
        try
        {
            Batch batch;
            lock (_gate)
            {
                if (_closed) return !_issues.Contains("write-failed") && !_issues.Contains("flush-timeout");
                // A timed-out writer still owns its checkpoint; never accumulate retained-window copies.
                if (_pendingCheckpoint is { IsCompleted: false }) { Stop("flush-pending"); return false; }
                if (final)
                {
                    // Retain uncertainty even when its beginning has left the bounded correlation window.
                    if (_unfinishedSteps > 0) _issues.Add("unfinished-steps");
                    _closed = true;
                }
                var discard = final && _outcome == GuaTraceOutcome.Passed &&
                    _options.SavePolicy == GuaTraceSavePolicy.OnFailure && !_stopped && _issues.Count == 0;
                var events = _options.CaptureMode == GuaTraceCaptureMode.Recent
                    ? _steps.SelectMany(s => s.Events).OrderBy(e => e.Sequence).ToArray() : Array.Empty<GuaTraceEvent>();
                var blobs = new Dictionary<string, byte[]>();
                if (_options.CaptureMode == GuaTraceCaptureMode.Recent)
                    foreach (var step in _steps) foreach (var pair in step.Blobs) blobs[pair.Key] = pair.Value;
                batch = new(events, blobs, Manifest(final), _options.CaptureMode == GuaTraceCaptureMode.Recent,
                    discard, 0, new(TaskCreationOptions.RunContinuationsAsynchronously));
                _pendingCheckpoint = batch.Completion!.Task;
            }
            var accepted = await Task.Run(() => _queue.TryAdd(batch, Remaining())).ConfigureAwait(false);
            if (!accepted) { batch.Completion!.TrySetResult(false); lock (_gate) Stop("flush-timeout"); return false; }
            var winner = await Task.WhenAny(batch.Completion!.Task, Task.Delay(Remaining())).ConfigureAwait(false);
            if (winner != batch.Completion.Task) { lock (_gate) Stop("flush-timeout"); return false; }
            return await batch.Completion.Task.ConfigureAwait(false);
        }
        catch { lock (_gate) Stop("flush-failed"); return false; }
        finally { _flushGate.Release(); }
    }

    private void WriteLoop()
    {
        foreach (var batch in _queue.GetConsumingEnumerable())
        {
            var success = true;
            try
            {
                bool discard; lock (_gate) discard = batch.Discard && !_stopped && _issues.Count == 0;
                if (discard)
                {
                    // Only remove our own artifact, after rejecting links throughout the owned subtree.
                    if (Directory.Exists(ArtifactPath))
                    {
                        GuaTraceReader.CheckNoLinks(ArtifactPath);
                        foreach (var entry in Directory.EnumerateFileSystemEntries(ArtifactPath, "*", SearchOption.AllDirectories))
                            GuaTraceReader.CheckNoLinks(entry);
                        Directory.Delete(ArtifactPath, true);
                    }
                }
                else
                {
                    GuaTraceReader.CheckNoLinks(ArtifactPath);
                    Directory.CreateDirectory(ArtifactPath);
                    if (!File.Exists(Path.Combine(ArtifactPath, "manifest.json")))
                    {
                        GuaTraceManifest initial; lock (_gate) initial = Manifest(false);
                        WriteManifest(initial);
                    }
                    foreach (var pair in batch.Blobs)
                    {
                        var path = GuaTraceReader.ResolveBlob(ArtifactPath, pair.Key);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllBytes(path, pair.Value);
                    }
                    var eventsPath = Path.Combine(ArtifactPath, "events.jsonl");
                    GuaTraceReader.CheckNoLinks(eventsPath);
                    using (var stream = new FileStream(eventsPath, batch.Replace ? FileMode.Create : FileMode.Append,
                        FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" })
                        foreach (var e in batch.Events) writer.WriteLine(JsonSerializer.Serialize(e, GuaTraceJson.Options));
                    if (batch.Replace)
                    {
                        // Remove only obsolete, hash-named blobs from earlier explicit recent checkpoints.
                        foreach (var folder in new[] { "snapshots", "attachments" })
                        {
                            var directory = Path.Combine(ArtifactPath, folder);
                            if (!Directory.Exists(directory)) continue;
                            GuaTraceReader.CheckNoLinks(directory);
                            foreach (var path in Directory.EnumerateFiles(directory))
                            {
                                var relative = folder + "/" + Path.GetFileName(path);
                                if (!batch.Blobs.ContainsKey(relative)) File.Delete(GuaTraceReader.ResolveBlob(ArtifactPath, relative));
                            }
                        }
                    }
                    if (batch.Manifest is not null)
                    {
                        // Include asynchronous writer failures which occurred after the checkpoint was queued.
                        GuaTraceStatus quality; lock (_gate) quality = StatusUnsafe();
                        WriteManifest(batch.Manifest with { Quality = quality });
                    }
                }
            }
            catch { success = false; lock (_gate) Stop("write-failed"); }
            finally
            {
                lock (_gate) _queuedBytes -= batch.Bytes;
                lock (_gate) success = success && !_issues.Contains("write-failed") && !_issues.Contains("flush-timeout");
                batch.Completion?.TrySetResult(success);
            }
        }
    }
    private void WriteManifest(GuaTraceManifest manifest)
    {
        var path = Path.Combine(ArtifactPath, "manifest.json");
        GuaTraceReader.CheckNoLinks(path);
        var temporary = Path.Combine(ArtifactPath, "manifest.tmp");
        GuaTraceReader.CheckNoLinks(temporary);
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, GuaTraceJson.Options), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        // Always pass the flush gate: _closed can be set before another finalizer has queued its batch.
        await CompleteAsync(GuaTraceOutcome.Interrupted).ConfigureAwait(false);
        _queue.CompleteAdding();
        // A blocked filesystem cannot make runtime disposal wait forever.
        await Task.WhenAny(_writer, Task.Delay(_options.FlushTimeout)).ConfigureAwait(false);
    }
}
