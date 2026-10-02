using System.Runtime.InteropServices;
using System.Text;
using Gua.Core;

namespace Gua.Runtime;

public enum GuaScreenshotAvailability
{
    Available = 1,
    Headless = -1,
    RenderingDisabled = -2,
    Unsupported = -3,
    StaleSession = -4,
}

public readonly record struct GuaScreenshotRequest(ulong RequestId, ulong SessionEpoch, ulong AfterFrameSequence);

public sealed partial class GuaRuntime : IDisposable
{
    private nint _handle;

    public GuaRuntime()
    {
        try {
            _handle = Native.gua_runtime_create();
            if (_handle == 0) throw new InvalidOperationException("Failed to create a Gua runtime.");
            _observations = new GuaContext(Native.gua_runtime_borrow_context(_handle));
            Clock = new GuaRuntimeClock(this);
        } catch (Exception error) {
            if (_handle != 0) { Native.gua_runtime_destroy(_handle); _handle = 0; }
            if (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                throw new InvalidOperationException("Failed to load the native Gua runtime. Ensure gua.dll and gua_runtime.dll match the current platform and architecture.", error);
            throw;
        }
    }

    public GuaRuntimeClock Clock { get; }
    internal nint Handle { get { ThrowIfDisposed(); return _handle; } }

    public bool InspectorBridgeRunning { get { ThrowIfDisposed(); return Native.gua_runtime_inspector_bridge_running(_handle) != 0; } }
    public string InspectorBridgeUrl { get { ThrowIfDisposed(); return ReadUtf8(Native.gua_runtime_inspector_bridge_url(_handle)); } }

    public bool StartInspectorBridge(int port = 8765)
    {
        ThrowIfDisposed();
        if (port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return Native.gua_runtime_start_inspector_bridge(_handle, port) != 0;
    }

    public void StopInspectorBridge() { ThrowIfDisposed(); Native.gua_runtime_stop_inspector_bridge(_handle); }

    public void SetAdapterVersion(string adapter, string version)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(adapter)) throw new ArgumentException("Adapter name is required.", nameof(adapter));
        if (adapter.Any(ch => ch is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'))
            throw new ArgumentException("Adapter name must contain only lowercase ASCII letters, digits, and underscores.", nameof(adapter));
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Adapter version is required.", nameof(version));
        Native.gua_runtime_set_adapter_version(_handle, adapter, version);
    }

    public void SetGodotPluginVersion(string version) { ThrowIfDisposed(); Native.gua_runtime_set_godot_plugin_version(_handle, version ?? string.Empty); }
    /// <summary>Advertises virtual_clock_v1 after an adapter has installed an unscaled clock pump.</summary>
    public void EnableVirtualClockAdapter() { ThrowIfDisposed(); Native.gua_runtime_set_virtual_clock_enabled(_handle, 1); }
    public bool EnqueueClick(string id) { ThrowIfDisposed(); return Native.gua_runtime_enqueue_click(_handle, id) != 0; }
    public bool ConsumeClickRequest(string id) { ThrowIfDisposed(); return Native.gua_runtime_consume_click_request(_handle, id) != 0; }
    public bool EmitClick(string id) { ThrowIfDisposed(); return Native.gua_runtime_emit_click(_handle, id) != 0; }
    public unsafe string? FindNodeByRole(string role, string label)
    {
        ThrowIfDisposed(); var buffer = stackalloc byte[128];
        return Native.gua_runtime_find_node_by_role(_handle, role, label, buffer, 128) == 0 ? null : Utf8(buffer, 128);
    }
    public unsafe bool TryPollEvent(out GuaEvent e)
    {
        ThrowIfDisposed(); Native.LegacyEvent native;
        if (Native.gua_runtime_poll_event(_handle, &native) == 0) { e = default; return false; }
        e = new GuaEvent((GuaEventType)native.Type, Utf8(native.NodeId, 128)); return true;
    }

    public void BeginFrame(string screen) { ThrowIfDisposed(); Native.gua_runtime_begin_frame(_handle, screen ?? string.Empty); }
    public void EndFrame() {
        var observations = Observations;
        lock (observations.ObserveGate) { ThrowIfDisposed(); observations.SampleObservations(true); if (_handle != 0) Native.gua_runtime_end_frame(_handle); }
    }

    public void RegisterNode(GuaNodeDescriptor descriptor)
    {
        ThrowIfDisposed();
        if (descriptor is null) throw new ArgumentNullException(nameof(descriptor));
        var known = GuaNodeKnownState.None;
        if (descriptor.ParentId is not null) known |= GuaNodeKnownState.ParentId;
        if (descriptor.Text is not null) known |= GuaNodeKnownState.Text;
        if (descriptor.Value is not null) known |= GuaNodeKnownState.Value;
        if (descriptor.Focused.HasValue) known |= GuaNodeKnownState.Focused;
        if (descriptor.Hovered.HasValue) known |= GuaNodeKnownState.Hovered;
        if (descriptor.Pressed.HasValue) known |= GuaNodeKnownState.Pressed;
        if (descriptor.Checked.HasValue) known |= GuaNodeKnownState.Checked;
        if (descriptor.Selected.HasValue) known |= GuaNodeKnownState.Selected;
        if (descriptor.CaretPosition.HasValue) known |= GuaNodeKnownState.CaretPosition;
        if (descriptor.SelectionStart.HasValue && descriptor.SelectionEnd.HasValue) known |= GuaNodeKnownState.Selection;
        if (descriptor.ScrollX.HasValue && descriptor.ScrollY.HasValue) known |= GuaNodeKnownState.Scroll;
        if (descriptor.ScrollMaxX.HasValue && descriptor.ScrollMaxY.HasValue) known |= GuaNodeKnownState.ScrollMax;
        if (descriptor.RangeValue.HasValue) known |= GuaNodeKnownState.RangeValue;
        if (descriptor.RangeMin.HasValue) known |= GuaNodeKnownState.RangeMin;
        if (descriptor.RangeMax.HasValue) known |= GuaNodeKnownState.RangeMax;
        if (descriptor.SelectedIndex.HasValue) known |= GuaNodeKnownState.SelectedIndex;

        var values = new[] { descriptor.Id, descriptor.ParentId, descriptor.Role, descriptor.Label, descriptor.Text, descriptor.Value };
        var pointers = values.Select<string?, nint>(value => value is null ? 0 : Marshal.StringToCoTaskMemUTF8(value)).ToArray();
        using var policy = new RuntimeAgentPolicy(descriptor.AgentPolicy);
        try
        {
            var node = new Native.NodeV3
            {
                StructSize = (uint)Marshal.SizeOf<Native.NodeV3>(),
                Base = new Native.NodeV2
                {
                    StructSize = (uint)Marshal.SizeOf<Native.NodeV2>(), KnownMask = (ulong)known,
                    Id = pointers[0], ParentId = pointers[1], Role = pointers[2], Label = pointers[3], Text = pointers[4], Value = pointers[5],
                    Bounds = descriptor.Bounds, Visible = descriptor.Visible ? 1 : 0, Enabled = descriptor.Enabled ? 1 : 0,
                    Focused = descriptor.Focused == true ? 1 : 0, Hovered = descriptor.Hovered == true ? 1 : 0,
                    Pressed = descriptor.Pressed == true ? 1 : 0, Checked = descriptor.Checked == true ? 1 : 0, Selected = descriptor.Selected == true ? 1 : 0,
                },
                CaretPosition = descriptor.CaretPosition ?? 0, SelectionStart = descriptor.SelectionStart ?? 0, SelectionEnd = descriptor.SelectionEnd ?? 0,
                ScrollX = descriptor.ScrollX ?? 0, ScrollY = descriptor.ScrollY ?? 0, ScrollMaxX = descriptor.ScrollMaxX ?? 0, ScrollMaxY = descriptor.ScrollMaxY ?? 0,
                RangeValue = descriptor.RangeValue ?? 0, RangeMin = descriptor.RangeMin ?? 0, RangeMax = descriptor.RangeMax ?? 0,
                SelectedIndex = descriptor.SelectedIndex ?? -1,
            };
            var secured = new Native.NodeV4 { StructSize = (uint)Marshal.SizeOf<Native.NodeV4>(), Base = node, AgentPolicy = policy.Value };
            if (Native.gua_runtime_register_node_v4(_handle, in secured) == 0)
                throw new InvalidOperationException($"Failed to register Gua runtime node '{descriptor.Id}'.");
        }
        finally { foreach (var pointer in pointers.Where(pointer => pointer != 0)) Marshal.FreeCoTaskMem(pointer); }
    }

    public unsafe bool TryConsumeAction(GuaActionType action, string? nodeId, out GuaActionRequest request)
    {
        ThrowIfDisposed();
        var native = new Native.ActionRequest { StructSize = (uint)sizeof(Native.ActionRequest) };
        if (Native.gua_runtime_consume_action_request(_handle, (int)action, nodeId, ref native) == 0) { request = default; return false; }
        byte* node = native.NodeId;
        byte* value = native.Value;
        byte* key = native.Key;
        var observationProfile = (GuaObservationProfile)Native.gua_runtime_get_action_request_observation_profile(_handle, native.RequestId);
        request = new GuaActionRequest((GuaActionType)native.Action, Utf8(node, 128), Utf8(value, 256), native.DeltaX, native.DeltaY,
            native.BoolValue != 0, Utf8(key, 64), native.Modifiers, native.Sensitive != 0, native.ScrollUnit, native.RequestId,
            observationProfile);
        return true;
    }

    public GuaActionError EnqueueAction(GuaActionRequest request, out ulong requestId)
        => EnqueueAction(request, playerProjection: false, out requestId);

    public GuaActionError EnqueuePlayerAction(GuaActionRequest request, out ulong requestId)
        => EnqueueAction(request, playerProjection: true, out requestId);

    private GuaActionError EnqueueAction(GuaActionRequest request, bool playerProjection, out ulong requestId)
    {
        ThrowIfDisposed();
        nint node = request.NodeId is null ? 0 : Marshal.StringToCoTaskMemUTF8(request.NodeId);
        nint value = request.Value is null ? 0 : Marshal.StringToCoTaskMemUTF8(request.Value);
        nint key = request.Key is null ? 0 : Marshal.StringToCoTaskMemUTF8(request.Key);
        try
        {
            var descriptor = new Native.ActionDescriptor
            {
                StructSize = (uint)Marshal.SizeOf<Native.ActionDescriptor>(), Action = (int)request.Action,
                NodeId = node, Value = value, DeltaX = request.DeltaX, DeltaY = request.DeltaY,
                BoolValue = request.BoolValue ? 1 : 0, Key = key, Modifiers = request.Modifiers,
                Sensitive = request.Sensitive ? 1 : 0, ScrollUnit = request.ScrollUnit,
            };
            var result = playerProjection
                ? Native.gua_runtime_enqueue_player_action(_handle, in descriptor, out requestId)
                : Native.gua_runtime_enqueue_action(_handle, in descriptor, out requestId);
            return result == 1 ? GuaActionError.None : (GuaActionError)result;
        }
        finally
        {
            if (node != 0) Marshal.FreeCoTaskMem(node);
            if (value != 0) Marshal.FreeCoTaskMem(value);
            if (key != 0) Marshal.FreeCoTaskMem(key);
        }
    }

    public GuaActionCancelResult CancelAction(ulong requestId)
    {
        ThrowIfDisposed();
        if (requestId == 0) throw new ArgumentOutOfRangeException(nameof(requestId));
        return (GuaActionCancelResult)Native.gua_runtime_cancel_action_request(_handle, requestId);
    }

    public unsafe bool TryPollActionEvent(ulong requestId, out GuaActionEvent e)
    {
        ThrowIfDisposed();
        if (requestId == 0) throw new ArgumentOutOfRangeException(nameof(requestId));
        var native = new Native.ActionEventV3
        {
            StructSize = (uint)sizeof(Native.ActionEventV3),
            Base = new Native.ActionEventV2 { StructSize = (uint)sizeof(Native.ActionEventV2) },
        };
        if (Native.gua_runtime_poll_event_v3_for_request(_handle, requestId, ref native) == 0)
        {
            e = default;
            return false;
        }
        byte* node = native.Base.NodeId;
        byte* value = native.Base.Value;
        e = new GuaActionEvent(native.Base.RequestId, (GuaActionType)native.Base.Action,
            native.Base.Status == 1, (GuaActionError)native.Base.ErrorCode,
            Utf8(node, 128), native.Base.Sensitive != 0 ? string.Empty : Utf8(value, 256), native.Base.Sensitive != 0,
            native.SessionEpoch, native.FrameSequence, native.Revision);
        return true;
    }

    public void EmitActionResult(GuaActionRequest request, bool succeeded, GuaActionError error = GuaActionError.None, string? value = null)
    {
        ThrowIfDisposed();
        nint node = request.NodeId is null ? default : (nint)Marshal.StringToCoTaskMemUTF8(request.NodeId);
        nint output = value is null ? default : (nint)Marshal.StringToCoTaskMemUTF8(request.Sensitive ? string.Empty : value);
        try
        {
            var result = new Native.ActionResult
            {
                StructSize = (uint)Marshal.SizeOf<Native.ActionResult>(), RequestId = request.RequestId, Action = (int)request.Action,
                Status = succeeded ? 1 : 2, ErrorCode = succeeded ? 0 : (int)error, NodeId = node, Value = output, Sensitive = request.Sensitive ? 1 : 0,
            };
            if (Native.gua_runtime_emit_action_result(_handle, in result) == 0)
                throw new InvalidOperationException(
                    $"Failed to emit Gua action result {request.RequestId} (action={(int)request.Action}, node='{request.NodeId ?? "<null>"}', structSize={result.StructSize}).");
        }
        finally { if (node != 0) Marshal.FreeCoTaskMem(node); if (output != 0) Marshal.FreeCoTaskMem(output); }
    }

    public bool TryConsumeScreenshotRequest(out GuaScreenshotRequest request)
    {
        ThrowIfDisposed();
        var native = new Native.ScreenshotRequest { StructSize = (uint)Marshal.SizeOf<Native.ScreenshotRequest>() };
        if (Native.gua_runtime_consume_screenshot_request(_handle, ref native) == 0) { request = default; return false; }
        request = new GuaScreenshotRequest(native.RequestId, native.SessionEpoch, native.AfterFrameSequence);
        return true;
    }

    public void CompleteScreenshot(GuaScreenshotRequest request, GuaScreenshotAvailability availability, string? dataUri = null, int width = 0, int height = 0)
    {
        TryCompleteScreenshot(request, availability, dataUri, width, height);
    }

    public bool TryCompleteScreenshot(GuaScreenshotRequest request, GuaScreenshotAvailability availability, string? dataUri = null, int width = 0, int height = 0)
    {
        ThrowIfDisposed();
        return Native.gua_runtime_complete_screenshot_request(
            _handle, request.RequestId, (int)availability, dataUri ?? string.Empty, width, height) != 0;
    }

    public void AddLog(int level, string message) { ThrowIfDisposed(); Native.gua_runtime_add_log(_handle, level, message); }
    public string GetUiTreeJson() { ThrowIfDisposed(); return CopyUiTree(_handle); }
    public string GetPlayerUiTreeJson() { ThrowIfDisposed(); return CopyJson(_handle, JsonSource.PlayerUiTree); }
    public string GetVersionJson() { ThrowIfDisposed(); return CopyVersion(_handle); }
    /// <summary>Read-only, redacted Debug diagnostics, including the bounded native lifecycle journal.</summary>
    public string GetDiagnosticsJson() { ThrowIfDisposed(); return Observations.GetDiagnosticsJson(); }

    public void Dispose()
    {
        // Observation tokens and getters synchronize against runtime destruction.
        if (_observations is { } observations) { lock (observations.ObserveGate) DisposeCore(); }
        else DisposeCore();
    }
    private void DisposeCore()
    {
        if (_handle == 0) return;
        try { ShutdownGameInputHost(); }
        finally
        {
            _observations?.Dispose();
            Native.gua_runtime_destroy(_handle);
            _handle = 0;
        }
    }

    private void ThrowIfDisposed() { if (_handle == 0) throw new ObjectDisposedException(nameof(GuaRuntime)); }
    private static string ReadUtf8(nint pointer) => Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
    private static unsafe string Utf8(byte* pointer, int capacity) { var length = 0; while (length < capacity && pointer[length] != 0) length++; return Encoding.UTF8.GetString(pointer, length); }
    private static unsafe string CopyUiTree(nint handle) => CopyJson(handle, JsonSource.UiTree);
    private static unsafe string CopyVersion(nint handle) => CopyJson(handle, JsonSource.Version);
    private static unsafe string CopyJson(nint handle, JsonSource source)
    {
        var requiredSize = CopyJson(source, handle, null, 0);
        if (requiredSize <= 0) throw new InvalidOperationException("Native Gua returned an invalid JSON size.");
        while (true)
        {
            var bytes = new byte[requiredSize];
            fixed (byte* pointer = bytes)
            {
                var actualSize = CopyJson(source, handle, pointer, bytes.Length);
                if (actualSize <= 0) throw new InvalidOperationException("Native Gua returned an invalid JSON size.");
                if (actualSize <= bytes.Length) return Encoding.UTF8.GetString(bytes, 0, actualSize - 1);
                requiredSize = actualSize;
            }
        }
    }
    private static unsafe int CopyJson(JsonSource source, nint handle, byte* buffer, int bufferSize) => source switch
    {
        JsonSource.UiTree => Native.gua_runtime_copy_ui_tree_json(handle, buffer, bufferSize),
        JsonSource.PlayerUiTree => Native.gua_runtime_copy_player_ui_tree_json(handle, buffer, bufferSize),
        JsonSource.Version => Native.gua_runtime_copy_version_json(handle, buffer, bufferSize),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };
    private enum JsonSource { UiTree, PlayerUiTree, Version }
}
