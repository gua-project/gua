using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Gua.Core;

namespace Gua.Testing;

public sealed partial class GuaWebSocketContext : IGuaContext, IGuaClockContext, IGuaAsyncClockContext, IGuaWorldContext, IDisposable
{
    private static readonly TimeSpan MinimumClockPauseResponseTimeout = TimeSpan.FromSeconds(11);
    private readonly Uri uri;
    private readonly TimeSpan requestTimeout;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private ClientWebSocket? socket;
    private long connectionGeneration;
    private readonly List<GuaActionEvent> bufferedActionEvents = new();
    private int nextId = 1;
    private bool disposed;
    public GuaWebSocketContext(string bridgeUrl, TimeSpan? requestTimeout = null) { uri = new Uri(bridgeUrl); this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5); }

    public void WaitUntilAvailable(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout; Exception? last = null;
        do { try { GetUiTreeJson(); return; } catch (Exception error) { last = error; Thread.Sleep(100); } } while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"Timed out connecting to Gua bridge at {uri}.", last);
    }
    public string GetUiTreeJson() => Raw(new { type = "get_ui_tree" });
    public GuaRemoteTree GetRemoteTree() => Request<GuaRemoteTree>(new { type = "get_ui_tree" });
    public GuaClockStatus GetClockStatus() => ToClockStatus(Request<RemoteClockStatus>(new { type = "get_clock" }));
    public GuaClockResult InstallClock(TimeSpan? initialTime = null, TimeSpan? step = null)
    { Request<RemoteClockStatus>(ClockCommand("clock_install", "initialTimeMs", (initialTime ?? TimeSpan.Zero).TotalMilliseconds, step)); return GuaClockResult.Ok; }
    public async Task<GuaClockResult> InstallClockAsync(TimeSpan? initialTime = null, TimeSpan? step = null, CancellationToken cancellationToken = default)
    {
        await RequestAsync<RemoteClockStatus>(ClockCommand("clock_install", "initialTimeMs", (initialTime ?? TimeSpan.Zero).TotalMilliseconds, step), cancellationToken).ConfigureAwait(false);
        return GuaClockResult.Ok;
    }
    public GuaClockResult PauseClock() { Request<RemoteClockStatus>(new { type = "clock_pause" }, responseTimeout: ClockPauseResponseTimeout); return GuaClockResult.Ok; }
    public async Task<GuaClockResult> PauseClockAsync(CancellationToken cancellationToken = default)
    {
        await RequestAsync<RemoteClockStatus>(new { type = "clock_pause" }, cancellationToken, responseTimeout: ClockPauseResponseTimeout).ConfigureAwait(false);
        return GuaClockResult.Ok;
    }
    public GuaClockResult RunClockFor(TimeSpan duration, TimeSpan? step = null)
    {
        var status = Request<RemoteClockStatus>(ClockCommand("clock_run_for", "durationMs", duration.TotalMilliseconds, step));
        if (status.CompletionSessionEpoch is not { } completionEpoch || status.CompletionAfterFrameSequence is not { } completionFrame ||
            status.OperationSequence is not { } operationSequence)
            throw new NotSupportedException("The connected runtime does not provide correlated clock completion.");
        var deadline = DateTime.UtcNow + requestTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var context = GetContextStatus();
            if (context.SessionEpoch != completionEpoch) throw new InvalidOperationException("stale_session");
            if (status.CompletedOperationSequence >= operationSequence && context.FrameSequence > completionFrame) return GuaClockResult.Ok;
            Thread.Sleep(5);
            status = Request<RemoteClockStatus>(new { type = "get_clock" });
        }
        throw new TimeoutException("Timed out waiting for Gua clock run_for host completion.");
    }
    public async Task<GuaClockResult> RunClockForAsync(TimeSpan duration, TimeSpan? step = null, CancellationToken cancellationToken = default)
    {
        var status = await RequestAsync<RemoteClockStatus>(ClockCommand("clock_run_for", "durationMs", duration.TotalMilliseconds, step), cancellationToken).ConfigureAwait(false);
        if (status.CompletionSessionEpoch is not { } completionEpoch || status.CompletionAfterFrameSequence is not { } completionFrame ||
            status.OperationSequence is not { } operationSequence)
            throw new NotSupportedException("The connected runtime does not provide correlated clock completion.");
        var deadline = DateTime.UtcNow + requestTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = await RequestAsync<Status>(new { type = "get_context_status" }, cancellationToken).ConfigureAwait(false);
            if (context.SessionEpoch != completionEpoch) throw new InvalidOperationException("stale_session");
            if (status.CompletedOperationSequence >= operationSequence && context.FrameSequence > completionFrame) return GuaClockResult.Ok;
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            status = await RequestAsync<RemoteClockStatus>(new { type = "get_clock" }, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("Timed out waiting for Gua clock run_for host completion.");
    }
    public GuaClockResult ResumeClock() { Request<RemoteClockStatus>(new { type = "clock_resume" }); return GuaClockResult.Ok; }
    public async Task<GuaClockResult> ResumeClockAsync(CancellationToken cancellationToken = default)
    {
        await RequestAsync<RemoteClockStatus>(new { type = "clock_resume" }, cancellationToken).ConfigureAwait(false);
        return GuaClockResult.Ok;
    }
    private static GuaClockStatus ToClockStatus(RemoteClockStatus value) => new(value.Installed,
        string.Equals(value.State, "paused", StringComparison.Ordinal), value.NowMs, value.DefaultStepMs, value.PendingMs, value.Generation);
    private static Dictionary<string, object> ClockCommand(string type, string valueName, double value, TimeSpan? step)
    { var command = new Dictionary<string, object> { ["type"] = type, [valueName] = value }; if (step.HasValue) command["stepMs"] = step.Value.TotalMilliseconds; return command; }
    public string GetDiagnosticsJson() => Raw(new { type = "get_diagnostics" });
    public GuaVersion GetVersion() => GuaVersion.Parse(Raw(new { type = "get_version" }));
    public string GetScreenshotJson() => Raw(new { type = "get_screenshot" });
    public GuaCapturedScreenshot CaptureScreenshot(TimeSpan timeout, ulong? afterFrameSequence = null)
    {
        try { return GuaCapturedScreenshot.Parse(Raw(new { type = "capture_screenshot", afterFrameSequence, timeoutMs = Math.Max(1, (int)timeout.TotalMilliseconds) }, timeout + requestTimeout)); }
        catch (InvalidOperationException error) when (error.Message.Contains("headless")) { throw new GuaRemoteScreenshotException(GuaRemoteScreenshotError.Headless, "Viewport capture is unavailable in headless mode.", error); }
        catch (InvalidOperationException error) when (error.Message.Contains("rendering_disabled")) { throw new GuaRemoteScreenshotException(GuaRemoteScreenshotError.RenderingDisabled, "Viewport rendering is disabled.", error); }
        catch (InvalidOperationException error) when (error.Message.Contains("unsupported")) { throw new GuaRemoteScreenshotException(GuaRemoteScreenshotError.Unsupported, "The connected adapter does not support viewport capture.", error); }
        catch (InvalidOperationException error) when (error.Message.Contains("timed out")) { throw new GuaRemoteScreenshotException(GuaRemoteScreenshotError.Timeout, $"Viewport capture timed out after {timeout:g}.", error); }
        catch (InvalidOperationException error) when (error.Message.Contains("stale_session")) { throw new GuaRemoteScreenshotException(GuaRemoteScreenshotError.StaleSession, "The screenshot request belongs to a stale session.", error); }
        catch (OperationCanceledException error) { throw new GuaRemoteScreenshotException(GuaRemoteScreenshotError.Timeout, $"Viewport capture timed out after {timeout:g}.", error); }
    }
    public GuaNodeState GetNodeState(string id) { var node = Tree().Nodes.FirstOrDefault(n => n.Id == id) ?? throw new InvalidOperationException($"Gua node not found: {id}"); return new(node.Visible, node.Enabled); }
    public string FindNodeById(string id) => Tree().Nodes.FirstOrDefault(n => n.Id == id)?.Id ?? throw new InvalidOperationException($"Gua node not found by id: {id}");
    public string FindNodeByRole(string role, string? name = null) => Tree().Nodes.FirstOrDefault(n => n.Role == role && (name == null || n.Label == name))?.Id ?? throw new InvalidOperationException($"Gua node not found by role: {role}, {name}");
    public string FindNodeByText(string text) => Tree().Nodes.FirstOrDefault(n => n.Text == text || n.Label == text)?.Id ?? throw new InvalidOperationException($"Gua node not found by text: {text}");
    public GuaQueryResult Query(GuaSelector selector)
    {
        if (selector == null) throw new ArgumentNullException(nameof(selector));
        return Request<GuaQueryResult>(new { type = "query_nodes", selectorId = selector.Id, idMatch = (int)selector.IdMatch, role = selector.Role, roleMatch = (int)selector.RoleMatch,
            name = selector.Name, nameMatch = (int)selector.NameMatch, text = selector.Text, textMatch = (int)selector.TextMatch, parentId = selector.ParentId,
            directChild = selector.DirectChild ? 1 : 0, visible = (int)selector.Visible, enabled = (int)selector.Enabled });
    }
    public bool EnqueueClick(string id) { Request<object?>(new { type = "click_node", nodeId = id }, true); return true; }
    public GuaActionError EnqueueAction(GuaActionRequest request, out ulong requestId)
    {
        var type = request.Action switch { GuaActionType.Click => "click_node", GuaActionType.Focus => "focus_node", GuaActionType.SetValue => "set_value", GuaActionType.SetChecked => "set_checked", GuaActionType.Select => "select", GuaActionType.Scroll => "scroll", GuaActionType.PressKey => "press_key", _ => throw new ArgumentOutOfRangeException(nameof(request)) };
        try
        {
            var result = Request<ActionResult>(new { type, nodeId = request.NodeId, value = request.Value, deltaX = request.DeltaX, deltaY = request.DeltaY,
                @checked = request.BoolValue, key = request.Key, modifiers = request.Modifiers, sensitive = request.Sensitive, scrollUnit = request.ScrollUnit });
            requestId = result.RequestId; return GuaActionError.None;
        }
        catch (InvalidOperationException error)
        {
            requestId = 0; if (error.Message.Contains("node_not_found")) return GuaActionError.NodeNotFound; if (error.Message.Contains("hidden")) return GuaActionError.Hidden;
            if (error.Message.Contains("disabled")) return GuaActionError.Disabled; if (error.Message.Contains("unsupported")) return GuaActionError.Unsupported;
            if (error.Message.Contains("invalid_value")) return GuaActionError.InvalidValue; return GuaActionError.InvalidArgument;
        }
    }
    public bool TryPollActionEvent(out GuaActionEvent e)
    {
        if (bufferedActionEvents.Count != 0) { e = bufferedActionEvents[0]; bufferedActionEvents.RemoveAt(0); return true; }
        return Poll(null, out e);
    }
    public bool TryPollActionEvent(ulong requestId, out GuaActionEvent e)
    {
        if (requestId == 0) throw new ArgumentOutOfRangeException(nameof(requestId));
        var index = bufferedActionEvents.FindIndex(candidate => candidate.RequestId == requestId);
        if (index >= 0) { e = bufferedActionEvents[index]; bufferedActionEvents.RemoveAt(index); return true; }
        return Poll(requestId, out e);
    }
    private bool Poll(ulong? requestId, out GuaActionEvent e)
    {
        var result = Request<EventResult?>(requestId.HasValue ? new { type = "poll_events", requestId = requestId.Value } : new { type = "poll_events" }, true);
        if (result == null) { e = default; return false; }
        e = new(result.RequestId, (GuaActionType)result.Action, result.Succeeded, (GuaActionError)result.Error, result.NodeId, result.Value, result.Sensitive, result.SessionEpoch, result.FrameSequence, result.Revision); return true;
    }
    public bool TryPollEvent(out GuaEvent e)
    {
        while (Poll(null, out var actionEvent))
        {
            if (actionEvent.Succeeded && actionEvent.Action is GuaActionType.Click or GuaActionType.Focus)
            {
                e = new GuaEvent(actionEvent.Action == GuaActionType.Click ? GuaEventType.Click : GuaEventType.Focus, actionEvent.NodeId);
                return true;
            }
            bufferedActionEvents.Add(actionEvent);
        }
        e = default;
        return false;
    }
    public GuaContextStatus GetContextStatus() { var s = Request<Status>(new { type = "get_context_status" }); return new(s.SessionEpoch, s.FrameSequence, s.Revision, s.NodeCount, s.PendingRequestCount, s.InFlightRequestCount, s.UnconsumedEventCount, s.LogCount, s.HasScreenshot, Action(s.FirstPendingAction), s.FirstPendingNodeId, Action(s.FirstEventAction), s.FirstEventNodeId) { WorldFrameSequence = s.WorldFrameSequence, WorldRevision = s.WorldRevision, WorldObjectCount = s.WorldObjectCount }; }
    public GuaResetReport Reset(GuaResetOptions? options = null)
    {
        options ??= new(); var epoch = options.ExpectedSessionEpoch ?? GetContextStatus().SessionEpoch;
        var r = Request<ResetResult>(new { type = "reset_context", expectedSessionEpoch = epoch, flags = (uint)options.Targets, flagsVersion = 2, strict = options.Strict });
        return new((GuaResetResult)r.Result, r.PreviousSessionEpoch, r.SessionEpoch, r.PendingRequestCount, r.InFlightRequestCount, r.UnconsumedEventCount,
            r.DiscardedNodeCount, r.DiscardedPendingRequestCount, r.DiscardedInFlightRequestCount, r.DiscardedEventCount, r.DiscardedLogCount, r.DiscardedScreenshot,
            Action(r.FirstPendingAction), r.FirstPendingNodeId, Action(r.FirstEventAction), r.FirstEventNodeId) { DiscardedWorldObjectCount = r.DiscardedWorldObjectCount };
    }
    private static GuaActionType? Action(int value) => value == 0 ? null : (GuaActionType)value;
    private GuaRemoteTree Tree() => GetRemoteTree();

    private TimeSpan ClockPauseResponseTimeout => requestTimeout > MinimumClockPauseResponseTimeout ? requestTimeout : MinimumClockPauseResponseTimeout;

    private T Request<T>(object command, bool allowNull = false, TimeSpan? responseTimeout = null)
    {
        requestGate.Wait();
        try
        {
            EnsureConnected(); var id = nextId++; var bytes = Envelope(id, command); Send(bytes);
            while (true)
            {
                using var document = JsonDocument.Parse(Receive(responseTimeout)); var root = document.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
                if (!root.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(root.GetProperty("error").GetString());
                var result = root.GetProperty("result").Deserialize<T>(JsonOptions); if (result == null && !allowNull) throw new InvalidOperationException("Gua bridge returned an empty result."); return result!;
            }
        }
        finally { requestGate.Release(); }
    }
    private async Task<T> RequestAsync<T>(object command, CancellationToken cancellationToken, bool allowNull = false, TimeSpan? responseTimeout = null)
    {
        await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false); var id = nextId++; var bytes = Envelope(id, command);
            using (var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                sendTimeout.CancelAfter(requestTimeout);
                await socket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, sendTimeout.Token).ConfigureAwait(false);
            }
            while (true)
            {
                using var document = JsonDocument.Parse(await ReceiveAsync(cancellationToken, responseTimeout).ConfigureAwait(false)); var root = document.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
                if (!root.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(root.GetProperty("error").GetString());
                var result = root.GetProperty("result").Deserialize<T>(JsonOptions); if (result == null && !allowNull) throw new InvalidOperationException("Gua bridge returned an empty result."); return result!;
            }
        }
        finally { requestGate.Release(); }
    }
    private string Raw(object command, TimeSpan? responseTimeout = null, long? observeGeneration = null,
        bool ignoreStaleObserve = false, Action<long>? onSuccess = null)
    {
        requestGate.Wait();
        try
        {
            if (observeGeneration.HasValue && (disposed || socket?.State != WebSocketState.Open || observeGeneration.Value != connectionGeneration)) {
                if (ignoreStaleObserve) return "null";
                throw new InvalidOperationException("Observe subscription belongs to an inactive connection.");
            }
            EnsureConnected(); var id = nextId++; Send(Envelope(id, command));
            while (true) { using var document = JsonDocument.Parse(Receive(responseTimeout)); var root = document.RootElement; if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue; if (!root.GetProperty("ok").GetBoolean()) throw new RemoteCommandRejectedException(root.GetProperty("error").GetString()); var result = root.GetProperty("result").GetRawText(); onSuccess?.Invoke(connectionGeneration); return result; }
        }
        finally { requestGate.Release(); }
    }
    private sealed class RemoteCommandRejectedException(string? message) : InvalidOperationException(message);
    private static byte[] Envelope(int id, object command)
    {
        using var source = JsonDocument.Parse(JsonSerializer.Serialize(command, command.GetType())); using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); writer.WriteNumber("id", id); foreach (var property in source.RootElement.EnumerateObject()) property.WriteTo(writer); writer.WriteEndObject(); }
        return stream.ToArray();
    }
    private void EnsureConnected()
    {
        if (disposed) throw new ObjectDisposedException(nameof(GuaWebSocketContext)); if (socket?.State == WebSocketState.Open) return;
        socket?.Dispose(); socket = new ClientWebSocket(); using var cts = new CancellationTokenSource(requestTimeout);
        try { socket.ConnectAsync(uri, cts.Token).GetAwaiter().GetResult(); connectionGeneration++; } catch { socket.Dispose(); socket = null; throw; }
    }
    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (disposed) throw new ObjectDisposedException(nameof(GuaWebSocketContext)); if (socket?.State == WebSocketState.Open) return;
        socket?.Dispose(); socket = new ClientWebSocket(); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(requestTimeout);
        try { await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false); connectionGeneration++; } catch { socket.Dispose(); socket = null; throw; }
    }
    private void Send(byte[] payload) { using var cts = new CancellationTokenSource(requestTimeout); socket!.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, cts.Token).GetAwaiter().GetResult(); }
    private string Receive(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? requestTimeout); var buffer = new byte[65536]; using var stream = new MemoryStream();
        while (true) { var result = socket!.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).GetAwaiter().GetResult(); if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("Gua bridge WebSocket closed."); stream.Write(buffer, 0, result.Count); if (result.EndOfMessage) return Encoding.UTF8.GetString(stream.ToArray()); }
    }
    private async Task<string> ReceiveAsync(CancellationToken cancellationToken, TimeSpan? responseTimeout = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(responseTimeout ?? requestTimeout); var buffer = new byte[65536]; using var stream = new MemoryStream();
        while (true) { var result = await socket!.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).ConfigureAwait(false); if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("Gua bridge WebSocket closed."); stream.Write(buffer, 0, result.Count); if (result.EndOfMessage) return Encoding.UTF8.GetString(stream.ToArray()); }
    }
    public void Dispose() { if (disposed) return; disposed = true; socket?.Dispose(); socket = null; }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record ActionResult(ulong RequestId);
    private sealed record RemoteClockStatus(bool Installed, string State, double NowMs, double DefaultStepMs, double PendingMs, ulong Generation,
        ulong CompletedOperationSequence = 0, ulong? OperationSequence = null,
        ulong? CompletionSessionEpoch = null, ulong? CompletionAfterFrameSequence = null);
    private sealed record EventResult(ulong RequestId, int Action, bool Succeeded, int Error, string NodeId, string Value, bool Sensitive, ulong SessionEpoch, ulong FrameSequence, ulong Revision);
    private sealed record Status(ulong SessionEpoch, ulong FrameSequence, ulong Revision, uint NodeCount, uint PendingRequestCount, uint InFlightRequestCount, uint UnconsumedEventCount, uint LogCount, bool HasScreenshot, int FirstPendingAction, string FirstPendingNodeId, int FirstEventAction, string FirstEventNodeId, ulong WorldFrameSequence = 0, ulong WorldRevision = 0, uint WorldObjectCount = 0);
    private sealed record ResetResult(int Result, ulong PreviousSessionEpoch, ulong SessionEpoch, uint PendingRequestCount, uint InFlightRequestCount, uint UnconsumedEventCount, uint DiscardedNodeCount, uint DiscardedPendingRequestCount, uint DiscardedInFlightRequestCount, uint DiscardedEventCount, uint DiscardedLogCount, bool DiscardedScreenshot, int FirstPendingAction, string FirstPendingNodeId, int FirstEventAction, string FirstEventNodeId, uint DiscardedWorldObjectCount = 0);
}
