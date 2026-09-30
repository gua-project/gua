using System.Runtime.InteropServices;

namespace Gua.Core;

public sealed partial class GuaContext : IGuaContext, IGuaClockContext, IDisposable
{
    private nint _handle;
    private GuaClock? _clock;

    public GuaContext()
    {
        try
        {
            _handle = Native.gua_create_context();
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(Native.NativeLoadErrorMessage(ex), ex);
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidOperationException(Native.NativeLoadErrorMessage(ex), ex);
        }

        if (_handle == 0)
        {
            throw new InvalidOperationException("Failed to create a Gua context.");
        }
    }

    public void BeginFrame(string screen)
    {
        ThrowIfDisposed();
        Native.gua_begin_frame(_handle, screen);
    }

    public void EndFrame()
    {
        ThrowIfDisposed();
        lock (ObserveGate) { ThrowIfDisposed(); SampleObservations(true); if (!ObserveDisposed) Native.gua_end_frame(ObserveHandle); }
    }

    public void RegisterNode(
        string id,
        string role,
        string label,
        GuaBounds bounds,
        bool visible = true,
        bool enabled = true)
    {
        ThrowIfDisposed();
        Native.gua_register_node(
            _handle,
            id,
            role,
            label,
            bounds,
            visible ? 1 : 0,
            enabled ? 1 : 0);
    }

    public void RegisterNode(GuaNodeDescriptor descriptor)
    {
        Guard.NotNull(descriptor, nameof(descriptor));
        ThrowIfDisposed();

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

        var strings = new[] { descriptor.Id, descriptor.ParentId, descriptor.Role, descriptor.Label, descriptor.Text, descriptor.Value };
        var pointers = strings.Select<string?, nint>(value => value is null ? default : System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(value)).ToArray();
        using var policy = new NativeAgentPolicy(descriptor.AgentPolicy);
        try
        {
            var native = new Native.GuaNativeNodeDescriptorV2
            {
                StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.GuaNativeNodeDescriptorV2>(),
                KnownMask = (ulong)known,
                Id = pointers[0],
                ParentId = pointers[1],
                Role = pointers[2],
                Label = pointers[3],
                Text = pointers[4],
                Value = pointers[5],
                Bounds = descriptor.Bounds,
                Visible = descriptor.Visible ? 1 : 0,
                Enabled = descriptor.Enabled ? 1 : 0,
                Focused = descriptor.Focused == true ? 1 : 0,
                Hovered = descriptor.Hovered == true ? 1 : 0,
                Pressed = descriptor.Pressed == true ? 1 : 0,
                Checked = descriptor.Checked == true ? 1 : 0,
                Selected = descriptor.Selected == true ? 1 : 0,
            };
            var detailed = new Native.GuaNativeNodeDescriptorV3
            {
                StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.GuaNativeNodeDescriptorV3>(),
                Base = native,
                CaretPosition = descriptor.CaretPosition ?? 0, SelectionStart = descriptor.SelectionStart ?? 0, SelectionEnd = descriptor.SelectionEnd ?? 0,
                ScrollX = descriptor.ScrollX ?? 0, ScrollY = descriptor.ScrollY ?? 0, ScrollMaxX = descriptor.ScrollMaxX ?? 0, ScrollMaxY = descriptor.ScrollMaxY ?? 0,
                RangeValue = descriptor.RangeValue ?? 0, RangeMin = descriptor.RangeMin ?? 0, RangeMax = descriptor.RangeMax ?? 0,
                SelectedIndex = descriptor.SelectedIndex ?? -1,
            };
            var secured = new Native.GuaNativeNodeDescriptorV4
            {
                StructSize = (uint)Marshal.SizeOf<Native.GuaNativeNodeDescriptorV4>(), Base = detailed, AgentPolicy = policy.Value,
            };
            if (Native.gua_register_node_v4(_handle, in secured) == 0)
            {
                throw new InvalidOperationException($"Failed to register Gua v2 node: {descriptor.Id}");
            }
        }
        finally
        {
            foreach (var pointer in pointers.Where(pointer => pointer != 0))
            {
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    public string GetUiTreeJson()
    {
        ThrowIfDisposed();
        return ReadCopiedJson(JsonSource.UiTree, _handle);
    }

    public string GetUiTreeJson(GuaObservationProfile profile) => ReadProfileJson(profile, diagnostics: false);

    public string GetDiagnosticsJson()
    {
        ThrowIfDisposed();
        return ReadCopiedJson(JsonSource.Diagnostics, _handle);
    }

    public string GetDiagnosticsJson(GuaObservationProfile profile) => ReadProfileJson(profile, diagnostics: true);

    private unsafe string ReadProfileJson(GuaObservationProfile profile, bool diagnostics)
    {
        ThrowIfDisposed();
        int Copy(byte* buffer, int size) => diagnostics
            ? Native.gua_copy_diagnostics_json_for_profile(_handle, (int)profile, buffer, size)
            : Native.gua_copy_ui_tree_json_for_profile(_handle, (int)profile, buffer, size);
        var required = Copy(null, 0);
        if (required <= 0) throw new ArgumentOutOfRangeException(nameof(profile));
        while (true) {
            var bytes = new byte[required];
            fixed (byte* pointer = bytes) {
                var actual = Copy(pointer, bytes.Length);
                if (actual <= bytes.Length) return System.Text.Encoding.UTF8.GetString(bytes, 0, actual - 1);
                required = actual;
            }
        }
    }

    public GuaVersion GetVersion()
    {
        ThrowIfDisposed();
        return GuaVersion.Parse(ReadCopiedJson(JsonSource.Version, _handle));
    }

    public GuaClock Clock
    {
        get
        {
            ThrowIfDisposed();
            return _clock ??= new GuaClock(this, registerWithContext: false);
        }
    }

    internal void RegisterClock(GuaClock clock)
    {
        ThrowIfDisposed();
        if (_clock is not null && !ReferenceEquals(_clock, clock))
            throw new InvalidOperationException("A GuaContext can own only one GuaClock. Reuse context.Clock instead of constructing another clock.");
        _clock = clock;
    }

    public GuaClockResult InstallClock(TimeSpan? initialTime = null, TimeSpan? step = null)
    { ThrowIfDisposed(); return (GuaClockResult)Native.gua_clock_install(_handle,
        initialTime?.TotalMilliseconds ?? 0.0, step?.TotalMilliseconds ?? 1000.0 / 60.0); }
    public GuaClockResult PauseClock() { ThrowIfDisposed(); return (GuaClockResult)Native.gua_clock_pause(_handle); }
    public GuaClockResult RunClockFor(TimeSpan duration, TimeSpan? step = null)
    { ThrowIfDisposed(); return (GuaClockResult)Native.gua_clock_run_for(_handle, duration.TotalMilliseconds,
        step?.TotalMilliseconds ?? GetClockStatus().DefaultStepMilliseconds); }
    public GuaClockResult ResumeClock() { ThrowIfDisposed(); return (GuaClockResult)Native.gua_clock_resume(_handle); }
    public GuaClockStatus GetClockStatus()
    {
        ThrowIfDisposed(); var value = new Native.GuaNativeClockStatus { StructSize = (uint)Marshal.SizeOf<Native.GuaNativeClockStatus>() };
        if (Native.gua_clock_get_status(_handle, ref value) == 0) throw new InvalidOperationException("Failed to read Gua clock status.");
        return new(value.Installed != 0, value.Paused != 0, value.NowMs, value.DefaultStepMs, value.PendingMs, value.Generation);
    }
    public bool TryConsumeClockStep(out GuaClockStep step)
    {
        ThrowIfDisposed(); var value = new Native.GuaNativeClockStep { StructSize = (uint)Marshal.SizeOf<Native.GuaNativeClockStep>() };
        if (Native.gua_clock_consume_step(_handle, ref value) == 0) { step = default; return false; }
        step = new(new GuaClockDelta(value.DeltaMs), value.FinalStep != 0, value.Generation); return true;
    }

    public void ConfigureDiagnostics(uint historyLimit, string environmentJson = "{}")
    {
        ThrowIfDisposed();
        if (Native.gua_set_diagnostics_history_limit(_handle, historyLimit) == 0 ||
            Native.gua_set_diagnostics_environment_json(_handle, environmentJson) == 0)
        {
            throw new ArgumentException("Invalid Gua diagnostics configuration.", nameof(environmentJson));
        }
    }

    public unsafe GuaContextStatus GetContextStatus()
    {
        ThrowIfDisposed();
        Native.GuaNativeContextStatus status = default;
        status.StructSize = (uint)sizeof(Native.GuaNativeContextStatus);
        if (Native.gua_get_context_status(_handle, &status) == 0)
        {
            throw new InvalidOperationException("Failed to inspect the Gua context.");
        }

        return new GuaContextStatus(
            status.SessionEpoch, status.FrameSequence, status.Revision, status.NodeCount,
            status.PendingRequestCount, status.InFlightRequestCount, status.UnconsumedEventCount,
            status.LogCount, status.HasScreenshot != 0, ActionOrNull(status.FirstPendingAction),
            Marshal.PtrToStringUTF8((nint)status.FirstPendingNodeId) ?? string.Empty,
            ActionOrNull(status.FirstEventAction),
            Marshal.PtrToStringUTF8((nint)status.FirstEventNodeId) ?? string.Empty)
        { WorldFrameSequence = status.WorldFrameSequence, WorldRevision = status.WorldRevision, WorldObjectCount = status.WorldObjectCount };
    }

    public GuaResetReport Reset(GuaResetOptions? options = null) { lock (ObserveGate) return ResetCore(options); }

    private unsafe GuaResetReport ResetCore(GuaResetOptions? options)
    {
        ThrowIfDisposed();
        options ??= new GuaResetOptions();
        var nativeOptions = new Native.GuaNativeResetOptions
        {
            StructSize = (uint)sizeof(Native.GuaNativeResetOptions),
            Flags = (uint)options.Targets,
            Strict = options.Strict ? 1 : 0,
            ExpectedSessionEpoch = options.ExpectedSessionEpoch ?? 0,
            FlagsVersion = 2,
        };
        Native.GuaNativeResetReport report = default;
        report.StructSize = (uint)sizeof(Native.GuaNativeResetReport);
        var result = Native.gua_reset_context(_handle, in nativeOptions, &report);
        if (result == (int)GuaResetResult.InvalidArgument)
        {
            throw new ArgumentException("Invalid Gua reset options.", nameof(options));
        }
        if (result == (int)GuaResetResult.Succeeded && _clock is not null)
        {
            _clock.ObserveStatus(GetClockStatus());
        }
        return new GuaResetReport(
            (GuaResetResult)result, report.PreviousSessionEpoch, report.SessionEpoch,
            report.PendingRequestCount, report.InFlightRequestCount, report.UnconsumedEventCount,
            report.DiscardedNodeCount, report.DiscardedPendingRequestCount, report.DiscardedInFlightRequestCount,
            report.DiscardedEventCount, report.DiscardedLogCount, report.DiscardedScreenshot != 0,
            ActionOrNull(report.FirstPendingAction), Marshal.PtrToStringUTF8((nint)report.FirstPendingNodeId) ?? string.Empty,
            ActionOrNull(report.FirstEventAction), Marshal.PtrToStringUTF8((nint)report.FirstEventNodeId) ?? string.Empty)
        { DiscardedWorldObjectCount = report.DiscardedWorldObjectCount };
    }

    private static GuaActionType? ActionOrNull(int action) => action == 0 ? null : (GuaActionType)action;

    public void AddLog(GuaLogLevel level, string message)
    {
        ThrowIfDisposed();
        Native.gua_add_log(_handle, (int)level, message);
    }

    public string GetLogsJson()
    {
        ThrowIfDisposed();
        return ReadCopiedJson(JsonSource.Logs, _handle);
    }

    public void SetScreenshot(string dataUri, int width, int height)
    {
        ThrowIfDisposed();
        Native.gua_set_screenshot(_handle, dataUri, width, height);
    }

    public string GetScreenshotJson()
    {
        ThrowIfDisposed();
        return ReadCopiedJson(JsonSource.Screenshot, _handle);
    }

    public GuaNodeState GetNodeState(string id)
    {
        ThrowIfDisposed();
        if (Native.gua_get_node_state(_handle, id, out var state) == 0)
        {
            throw new InvalidOperationException($"Gua node not found: {id}");
        }
        return state;
    }

    public GuaNodeStateV2 GetNodeStateV2(string id)
    {
        ThrowIfDisposed();
        unsafe
        {
            Native.GuaNativeNodeStateV2 state = default;
            state.StructSize = (uint)sizeof(Native.GuaNativeNodeStateV2);
            if (Native.gua_get_node_state_v2(_handle, id, &state) == 0)
            {
                throw new InvalidOperationException($"Gua node not found: {id}");
            }

            var known = (GuaNodeKnownState)state.KnownMask;
            string? Read(byte* value, GuaNodeKnownState flag) => known.HasFlag(flag)
                ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)value) ?? string.Empty
                : null;
            bool? ReadBool(int value, GuaNodeKnownState flag) => known.HasFlag(flag) ? value != 0 : null;

            return new GuaNodeStateV2(
                known,
                state.Visible != 0,
                state.Enabled != 0,
                Read(state.ParentId, GuaNodeKnownState.ParentId),
                Read(state.Text, GuaNodeKnownState.Text),
                Read(state.Value, GuaNodeKnownState.Value),
                ReadBool(state.Focused, GuaNodeKnownState.Focused),
                ReadBool(state.Hovered, GuaNodeKnownState.Hovered),
                ReadBool(state.Pressed, GuaNodeKnownState.Pressed),
                ReadBool(state.Checked, GuaNodeKnownState.Checked),
                ReadBool(state.Selected, GuaNodeKnownState.Selected));
        }
    }

    public string FindNodeById(string id)
    {
        ThrowIfDisposed();
        unsafe
        {
            var buffer = stackalloc byte[Native.NodeIdBufferSize];
            if (Native.gua_find_node_by_id(_handle, id, buffer, Native.NodeIdBufferSize) == 0)
            {
                throw new InvalidOperationException($"Gua node not found by id: {id}");
            }

            return ReadUtf8NodeId(buffer);
        }
    }

    public string FindNodeByRole(string role, string? name = null)
    {
        ThrowIfDisposed();
        unsafe
        {
            var buffer = stackalloc byte[Native.NodeIdBufferSize];
            if (Native.gua_find_node_by_role(_handle, role, name, buffer, Native.NodeIdBufferSize) == 0)
            {
                throw new InvalidOperationException(name is null
                    ? $"Gua node not found by role: {role}"
                    : $"Gua node not found by role and name: {role}, {name}");
            }

            return ReadUtf8NodeId(buffer);
        }
    }

    public string FindNodeByText(string text)
    {
        ThrowIfDisposed();
        unsafe
        {
            var buffer = stackalloc byte[Native.NodeIdBufferSize];
            if (Native.gua_find_node_by_text(_handle, text, buffer, Native.NodeIdBufferSize) == 0)
            {
                throw new InvalidOperationException($"Gua node not found by text: {text}");
            }

            return ReadUtf8NodeId(buffer);
        }
    }

    public GuaQueryResult Query(GuaSelector selector)
    {
        Guard.NotNull(selector, nameof(selector));
        ThrowIfDisposed();
        var values = new[] { selector.Id, selector.Role, selector.Name, selector.Text, selector.ParentId };
        var pointers = values.Select<string?, nint>(value => value is null ? default : System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(value)).ToArray();
        try
        {
            var native = new Native.GuaNativeSelectorV1
            {
                StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.GuaNativeSelectorV1>(),
                Id = pointers[0], IdMatch = (int)selector.IdMatch,
                Role = pointers[1], RoleMatch = (int)selector.RoleMatch,
                Name = pointers[2], NameMatch = (int)selector.NameMatch,
                Text = pointers[3], TextMatch = (int)selector.TextMatch,
                ParentId = pointers[4], DirectChild = selector.DirectChild ? 1 : 0,
                Visible = (int)selector.Visible, Enabled = (int)selector.Enabled,
            };
            unsafe
            {
                var required = Native.gua_query_nodes_json(_handle, in native, null, 0);
                var buffer = new byte[required];
                fixed (byte* pointer = buffer)
                {
                    Native.gua_query_nodes_json(_handle, in native, pointer, buffer.Length);
                    var json = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)pointer)
                        ?? throw new InvalidOperationException("Native Gua returned invalid selector JSON.");
                    return System.Text.Json.JsonSerializer.Deserialize<GuaQueryResult>(json, QueryJsonOptions)
                        ?? throw new InvalidOperationException("Native Gua returned an empty selector result.");
                }
            }
        }
        finally
        {
            foreach (var pointer in pointers.Where(pointer => pointer != 0))
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pointer);
        }
    }

    public bool EnqueueClick(string id)
    {
        ThrowIfDisposed();
        return Native.gua_enqueue_click(_handle, id) != 0;
    }

    public GuaActionError EnqueueAction(GuaActionRequest request, out ulong requestId)
    {
        ThrowIfDisposed();
        var nodeId = Marshal.StringToCoTaskMemUTF8(request.NodeId);
        var value = Marshal.StringToCoTaskMemUTF8(request.Value);
        var key = Marshal.StringToCoTaskMemUTF8(request.Key);
        try
        {
            var descriptor = new Native.GuaNativeActionRequestDescriptor
            {
                StructSize = (uint)Marshal.SizeOf<Native.GuaNativeActionRequestDescriptor>(),
                Action = (int)request.Action,
                NodeId = nodeId,
                Value = value,
                DeltaX = request.DeltaX,
                DeltaY = request.DeltaY,
                BoolValue = request.BoolValue ? 1 : 0,
                Key = key,
                Modifiers = request.Modifiers,
                Sensitive = request.Sensitive ? 1 : 0,
                ScrollUnit = request.ScrollUnit,
            };
            var result = Native.gua_enqueue_action(_handle, in descriptor, out requestId);
            return result == 1 ? GuaActionError.None : (GuaActionError)result;
        }
        finally
        {
            Marshal.FreeCoTaskMem(nodeId);
            Marshal.FreeCoTaskMem(value);
            Marshal.FreeCoTaskMem(key);
        }
    }

    public GuaActionCancelResult CancelAction(ulong requestId)
    {
        ThrowIfDisposed();
        return (GuaActionCancelResult)Native.gua_cancel_action_request(_handle, requestId);
    }

    public bool TryPollActionEvent(out GuaActionEvent e)
    {
        return TryPollActionEventCore(null, out e);
    }

    public bool TryPollActionEvent(ulong requestId, out GuaActionEvent e)
    {
        Guard.NotZero(requestId, nameof(requestId));
        return TryPollActionEventCore(requestId, out e);
    }

    private bool TryPollActionEventCore(ulong? requestId, out GuaActionEvent e)
    {
        ThrowIfDisposed();
        unsafe
        {
            Native.GuaNativeEventV3 nativeEvent = new()
            {
                StructSize = (uint)sizeof(Native.GuaNativeEventV3),
                Base = new Native.GuaNativeEventV2 { StructSize = (uint)sizeof(Native.GuaNativeEventV2) },
            };
            var found = requestId.HasValue
                ? Native.gua_poll_event_v3_for_request(_handle, requestId.Value, &nativeEvent)
                : Native.gua_poll_event_v3(_handle, &nativeEvent);
            if (found == 0)
            {
                e = default;
                return false;
            }
            e = new GuaActionEvent(
                nativeEvent.Base.RequestId,
                (GuaActionType)nativeEvent.Base.Action,
                nativeEvent.Base.Status == 1,
                (GuaActionError)nativeEvent.Base.ErrorCode,
                Marshal.PtrToStringUTF8((nint)nativeEvent.Base.NodeId) ?? string.Empty,
                Marshal.PtrToStringUTF8((nint)nativeEvent.Base.Value) ?? string.Empty,
                nativeEvent.Base.Sensitive != 0,
                nativeEvent.SessionEpoch, nativeEvent.FrameSequence, nativeEvent.Revision);
            return true;
        }
    }

    public bool TryConsumeAction(GuaActionType action, string? nodeId, out GuaActionRequest request)
    {
        ThrowIfDisposed();
        unsafe
        {
            Native.GuaNativeActionRequest nativeRequest = new() { StructSize = (uint)sizeof(Native.GuaNativeActionRequest) };
            if (Native.gua_consume_action_request(_handle, (int)action, nodeId, &nativeRequest) == 0)
            {
                request = default;
                return false;
            }
            request = new GuaActionRequest(
                (GuaActionType)nativeRequest.Action,
                Marshal.PtrToStringUTF8((nint)nativeRequest.NodeId),
                Marshal.PtrToStringUTF8((nint)nativeRequest.Value),
                nativeRequest.DeltaX,
                nativeRequest.DeltaY,
                nativeRequest.BoolValue != 0,
                Marshal.PtrToStringUTF8((nint)nativeRequest.Key),
                nativeRequest.Modifiers,
                nativeRequest.Sensitive != 0,
                nativeRequest.ScrollUnit,
                nativeRequest.RequestId);
            return true;
        }
    }

    public bool EmitActionResult(GuaActionEvent e)
    {
        ThrowIfDisposed();
        var nodeId = Marshal.StringToCoTaskMemUTF8(e.NodeId);
        var value = Marshal.StringToCoTaskMemUTF8(e.Value);
        try
        {
            var result = new Native.GuaNativeActionResult
            {
                StructSize = (uint)Marshal.SizeOf<Native.GuaNativeActionResult>(),
                RequestId = e.RequestId,
                Action = (int)e.Action,
                Status = e.Succeeded ? 1 : 2,
                ErrorCode = (int)e.Error,
                NodeId = nodeId,
                Value = value,
                Sensitive = e.Sensitive ? 1 : 0,
            };
            return Native.gua_emit_action_result(_handle, in result) != 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(nodeId);
            Marshal.FreeCoTaskMem(value);
        }
    }

    public bool ConsumeClickRequest(string id)
    {
        ThrowIfDisposed();
        return Native.gua_consume_click_request(_handle, id) != 0;
    }

    public bool EmitClick(string id)
    {
        ThrowIfDisposed();
        return Native.gua_emit_click(_handle, id) != 0;
    }

    public bool TryPollEvent(out GuaEvent e)
    {
        ThrowIfDisposed();

        unsafe
        {
            Native.GuaNativeEvent nativeEvent;
            if (Native.gua_poll_event(_handle, &nativeEvent) == 0)
            {
                e = default;
                return false;
            }

            e = new GuaEvent(
                (GuaEventType)nativeEvent.Type,
                ReadNativeEventNodeId(&nativeEvent));
            return true;
        }
    }

    public void Dispose() { lock (ObserveGate) DisposeCore(); }

    private void DisposeCore()
    {
        if (_handle == 0)
        {
            return;
        }

        Native.gua_destroy_context(_handle);
        _handle = 0;
    }

    private void ThrowIfDisposed()
    {
        if (_handle == 0)
        {
            throw new ObjectDisposedException(nameof(GuaContext));
        }
    }

    private static unsafe string ReadUtf8NodeId(byte* buffer)
    {
        return System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)buffer)
            ?? throw new InvalidOperationException("Native Gua returned an invalid node id.");
    }

    private static unsafe string ReadNativeEventNodeId(Native.GuaNativeEvent* nativeEvent)
    {
        return System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)nativeEvent->NodeId)
            ?? throw new InvalidOperationException("Native Gua returned an invalid event node id.");
    }

    private static unsafe string ReadCopiedJson(JsonSource source, nint handle)
    {
        var requiredSize = CopyJson(source, handle, null, 0);
        if (requiredSize <= 0)
        {
            throw new InvalidOperationException("Native Gua returned an invalid JSON size.");
        }

        while (true)
        {
            var buffer = new byte[requiredSize];
            fixed (byte* bufferPointer = buffer)
            {
                var actualSize = CopyJson(source, handle, bufferPointer, buffer.Length);
                if (actualSize <= 0)
                {
                    throw new InvalidOperationException("Native Gua returned an invalid JSON size.");
                }

                if (actualSize <= buffer.Length)
                {
                    return System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)bufferPointer)
                        ?? throw new InvalidOperationException("Native Gua returned an invalid UTF-8 string.");
                }

                requiredSize = actualSize;
            }
        }
    }

    private static unsafe int CopyJson(JsonSource source, nint handle, byte* buffer, int bufferSize)
    {
        return source switch
        {
            JsonSource.UiTree => Native.gua_copy_ui_tree_json(handle, buffer, bufferSize),
            JsonSource.Logs => Native.gua_copy_logs_json(handle, buffer, bufferSize),
            JsonSource.Screenshot => Native.gua_copy_screenshot_json(handle, buffer, bufferSize),
            JsonSource.Diagnostics => Native.gua_copy_diagnostics_json(handle, buffer, bufferSize),
            JsonSource.Version => Native.gua_copy_version_json(buffer, bufferSize),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
        };
    }

    private enum JsonSource
    {
        UiTree,
        Logs,
        Screenshot,
        Diagnostics,
        Version,
    }

    private static readonly System.Text.Json.JsonSerializerOptions QueryJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
