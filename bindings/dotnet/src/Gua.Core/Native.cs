using System.Runtime.InteropServices;
using System.Reflection;

namespace Gua.Core;

internal static partial class Native
{
    internal const int NodeIdBufferSize = 128;
    private static readonly object ResolveLock = new();
    private static string[] _lastCandidateDirectories = [];

    internal unsafe struct GuaNativeEvent
    {
        public int Type;
        public fixed byte NodeId[NodeIdBufferSize];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuaNativeActionRequestDescriptor
    {
        public uint StructSize;
        public int Action;
        public nint NodeId;
        public nint Value;
        public float DeltaX;
        public float DeltaY;
        public int BoolValue;
        public nint Key;
        public uint Modifiers;
        public int Sensitive;
        public int ScrollUnit;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct GuaNativeEventV2
    {
        public uint StructSize;
        public ulong RequestId;
        public int Action;
        public int Status;
        public int ErrorCode;
        public fixed byte NodeId[128];
        public fixed byte Value[256];
        public int Sensitive;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct GuaNativeContextStatus
    {
        public uint StructSize;
        public ulong SessionEpoch;
        public ulong FrameSequence;
        public ulong Revision;
        public uint NodeCount;
        public uint PendingRequestCount;
        public uint InFlightRequestCount;
        public uint UnconsumedEventCount;
        public uint LogCount;
        public int HasScreenshot;
        public int FirstPendingAction;
        public fixed byte FirstPendingNodeId[128];
        public int FirstEventAction;
        public fixed byte FirstEventNodeId[128];
        public ulong WorldFrameSequence;
        public ulong WorldRevision;
        public uint WorldObjectCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuaNativeClockStatus
    { public uint StructSize; public int Installed; public int Paused; public double NowMs; public double DefaultStepMs; public double PendingMs; public ulong Generation; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuaNativeClockStep
    { public uint StructSize; public double DeltaMs; public int FinalStep; public ulong Generation; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuaNativeResetOptions
    {
        public uint StructSize;
        public uint Flags;
        public int Strict;
        public ulong ExpectedSessionEpoch;
        public uint FlagsVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct GuaNativeResetReport
    {
        public uint StructSize;
        public int Result;
        public ulong PreviousSessionEpoch;
        public ulong SessionEpoch;
        public uint PendingRequestCount;
        public uint InFlightRequestCount;
        public uint UnconsumedEventCount;
        public uint DiscardedNodeCount;
        public uint DiscardedPendingRequestCount;
        public uint DiscardedInFlightRequestCount;
        public uint DiscardedEventCount;
        public uint DiscardedLogCount;
        public int DiscardedScreenshot;
        public int FirstPendingAction;
        public fixed byte FirstPendingNodeId[128];
        public int FirstEventAction;
        public fixed byte FirstEventNodeId[128];
        public uint DiscardedWorldObjectCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct GuaNativeActionRequest
    {
        public uint StructSize;
        public ulong RequestId;
        public int Action;
        public fixed byte NodeId[128];
        public fixed byte Value[256];
        public float DeltaX;
        public float DeltaY;
        public int BoolValue;
        public fixed byte Key[64];
        public uint Modifiers;
        public int Sensitive;
        public int ScrollUnit;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuaNativeActionResult
    {
        public uint StructSize;
        public ulong RequestId;
        public int Action;
        public int Status;
        public int ErrorCode;
        public nint NodeId;
        public nint Value;
        public int Sensitive;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuaNativeNodeDescriptorV2
    {
        public uint StructSize;
        public ulong KnownMask;
        public nint Id;
        public nint ParentId;
        public nint Role;
        public nint Label;
        public nint Text;
        public nint Value;
        public GuaBounds Bounds;
        public int Visible;
        public int Enabled;
        public int Focused;
        public int Hovered;
        public int Pressed;
        public int Checked;
        public int Selected;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct GuaNativeEventV3
    {
        public uint StructSize;
        public GuaNativeEventV2 Base;
        public ulong SessionEpoch;
        public ulong FrameSequence;
        public ulong Revision;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuaNativeNodeDescriptorV3
    {
        public uint StructSize;
        public GuaNativeNodeDescriptorV2 Base;
        public long CaretPosition, SelectionStart, SelectionEnd;
        public double ScrollX, ScrollY, ScrollMaxX, ScrollMaxY;
        public double RangeValue, RangeMin, RangeMax;
        public long SelectedIndex;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct GuaNativeAgentFieldRuleV1
    { public uint StructSize; public nint Path; public int Mode; public int ReplacementType; public nint StringValue; public double NumberValue; public int BoolValue; public double Quantum; }
    [StructLayout(LayoutKind.Sequential)] internal struct GuaNativeAgentPolicyV1
    { public uint StructSize; public int Exposure; public int HasAllowedActions; public ulong AllowedActions; public nint FieldRules; public uint FieldRuleCount; }
    [StructLayout(LayoutKind.Sequential)] internal struct GuaNativeNodeDescriptorV4
    { public uint StructSize; public GuaNativeNodeDescriptorV3 Base; public GuaNativeAgentPolicyV1 AgentPolicy; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuaNativeWorldStateValueV1
    {
        public uint StructSize; public nint Key; public int Type; public nint StringValue; public double NumberValue; public int BoolValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuaNativeWorldObjectDescriptorV1
    {
        public uint StructSize; public nint Id; public nint ParentId; public nint Kind; public nint Label; public nint Description;
        public int Space; public double PositionX, PositionY, PositionZ; public int VisibleToPlayer; public int Active; public int AgentExposure;
        public nint DomainId; public nint RelatedUiNodeId; public nint Tags; public uint TagCount; public nint StateValues; public uint StateValueCount;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct GuaNativeWorldObjectDescriptorV2
    { public uint StructSize; public GuaNativeWorldObjectDescriptorV1 Base; public GuaNativeAgentPolicyV1 AgentPolicy; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuaNativeWorldSelectorV1
    {
        public uint StructSize; public nint Id; public int IdMatch; public nint Kind; public int KindMatch; public nint Label; public int LabelMatch;
        public nint Tag; public int TagMatch; public nint ParentId; public int DirectChild; public int VisibleToPlayer; public int Active; public nint State;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct GuaNativeWorldNearV1
    { public uint StructSize; public nint RelativeToObjectId; public double MaxDistance; }
    [StructLayout(LayoutKind.Sequential)] internal struct GuaNativeWorldSelectorV2
    { public uint StructSize; public GuaNativeWorldSelectorV1 Base; public nint Near; public uint Limit; }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct GuaNativeNodeStateV2
    {
        public uint StructSize;
        public ulong KnownMask;
        public int Visible;
        public int Enabled;
        public int Focused;
        public int Hovered;
        public int Pressed;
        public int Checked;
        public int Selected;
        public fixed byte ParentId[128];
        public fixed byte Text[256];
        public fixed byte Value[256];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuaNativeSelectorV1
    {
        public uint StructSize;
        public nint Id;
        public int IdMatch;
        public nint Role;
        public int RoleMatch;
        public nint Name;
        public int NameMatch;
        public nint Text;
        public int TextMatch;
        public nint ParentId;
        public int DirectChild;
        public int Visible;
        public int Enabled;
    }

    static Native()
    {
#if !NETSTANDARD2_1
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, ResolveGuaLibrary);
#endif
    }

#if !NETSTANDARD2_1
    private static nint ResolveGuaLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "gua", StringComparison.Ordinal))
        {
            return nint.Zero;
        }

        // A game-owned initializer can pin a verified module. In that case never
        // fall back to environment variables, working directory, or loader search.
        if (AppContext.GetData("Gua.GameVerifiedCore") is nint verified && verified != 0)
            return verified;

        var fileName = OperatingSystem.IsWindows()
            ? "gua.dll"
            : OperatingSystem.IsMacOS()
                ? "libgua.dylib"
                : "libgua.so";

        var directories = CandidateNativeDirectories(assembly).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (ResolveLock)
        {
            _lastCandidateDirectories = directories;
        }

        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var fallbackHandle)
            ? fallbackHandle
            : nint.Zero;
    }
#endif

    internal static string NativeLoadErrorMessage(Exception exception)
    {
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "gua.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "libgua.dylib"
                : "libgua.so";

        string[] directories;
        lock (ResolveLock)
        {
            directories = _lastCandidateDirectories;
        }

        if (directories.Length == 0)
        {
            directories = CandidateNativeDirectories(typeof(Native).Assembly).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        var searched = string.Join(Environment.NewLine, directories.Select(directory => $"  - {Path.Combine(directory, fileName)}"));
#if NETSTANDARD2_1
        return
            $"Failed to load the native Gua runtime '{fileName}'. Place it in the application output directory or configure it as a Unity native plug-in. The netstandard2.1 target uses the host's normal P/Invoke resolution." +
            $"{Environment.NewLine}Expected locations:{Environment.NewLine}{searched}{Environment.NewLine}Original error: {exception.Message}";
#else
        return
            $"Failed to load the native Gua runtime '{fileName}'. Build native/gua-core and make the library discoverable with GUA_NATIVE_DIR, the .NET output directory, or the current directory." +
            $"{Environment.NewLine}Searched:{Environment.NewLine}{searched}{Environment.NewLine}Original error: {exception.Message}";
#endif
    }

    private static IEnumerable<string> CandidateNativeDirectories(Assembly assembly)
    {
#if !NETSTANDARD2_1
        var configured = Environment.GetEnvironmentVariable("GUA_NATIVE_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }
#endif

        yield return AppContext.BaseDirectory;

        var assemblyLocation = assembly.Location;
        if (!string.IsNullOrWhiteSpace(assemblyLocation))
        {
            var assemblyDirectory = Path.GetDirectoryName(assemblyLocation);
            if (!string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                yield return assemblyDirectory;
            }
        }

        yield return Environment.CurrentDirectory;
    }

#if !NETSTANDARD2_1
    [LibraryImport("gua")]
    internal static partial nint gua_create_context();

    [LibraryImport("gua")]
    internal static partial void gua_destroy_context(nint context);

    [LibraryImport("gua", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void gua_begin_frame(nint context, string screen);

    [LibraryImport("gua")]
    internal static partial void gua_end_frame(nint context);

    [LibraryImport("gua", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void gua_register_node(
        nint context,
        string id,
        string role,
        string label,
        GuaBounds bounds,
        int visible,
        int enabled);

    [LibraryImport("gua")]
    internal static partial int gua_register_node_v2(nint context, in GuaNativeNodeDescriptorV2 descriptor);

    [LibraryImport("gua")]
    internal static partial int gua_register_node_v3(nint context, in GuaNativeNodeDescriptorV3 descriptor);
    [LibraryImport("gua")]
    internal static partial int gua_register_node_v4(nint context, in GuaNativeNodeDescriptorV4 descriptor);

    [LibraryImport("gua", StringMarshalling = StringMarshalling.Utf8)] internal static partial int gua_begin_world_frame(nint context, string scene);
    [LibraryImport("gua")] internal static partial int gua_register_world_object_v1(nint context, in GuaNativeWorldObjectDescriptorV1 descriptor);
    [LibraryImport("gua")] internal static partial int gua_register_world_object_v2(nint context, in GuaNativeWorldObjectDescriptorV2 descriptor);
    [LibraryImport("gua")] internal static partial int gua_end_world_frame(nint context);
    [LibraryImport("gua")] internal static partial int gua_abort_world_frame(nint context);
    [LibraryImport("gua")] internal static unsafe partial int gua_copy_world_object_tree_json(nint context, int profile, byte* outJson, int outJsonSize);
    [LibraryImport("gua")] internal static unsafe partial int gua_query_world_objects_json(nint context, in GuaNativeWorldSelectorV1 selector, int profile, byte* outJson, int outJsonSize);
    [LibraryImport("gua")] internal static unsafe partial int gua_query_world_objects_v2_json(nint context, in GuaNativeWorldSelectorV2 selector, int profile, byte* outJson, int outJsonSize);

    [LibraryImport("gua")]
    internal static partial nint gua_get_ui_tree_json(nint context);

    [LibraryImport("gua")]
    internal static unsafe partial int gua_copy_ui_tree_json(nint context, byte* outJson, int outJsonSize);
    [LibraryImport("gua")]
    internal static unsafe partial int gua_copy_ui_tree_json_for_profile(nint context, int profile, byte* outJson, int outJsonSize);

    [LibraryImport("gua", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void gua_add_log(nint context, int level, string message);

    [LibraryImport("gua")]
    internal static partial nint gua_get_logs_json(nint context);

    [LibraryImport("gua")]
    internal static unsafe partial int gua_copy_logs_json(nint context, byte* outJson, int outJsonSize);

    [LibraryImport("gua", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void gua_set_screenshot(nint context, string dataUri, int width, int height);

    [LibraryImport("gua")]
    internal static partial nint gua_get_screenshot_json(nint context);

    [LibraryImport("gua")]
    internal static unsafe partial int gua_copy_screenshot_json(nint context, byte* outJson, int outJsonSize);

    [LibraryImport("gua")]
    internal static partial int gua_set_diagnostics_history_limit(nint context, uint historyLimit);

    [LibraryImport("gua", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int gua_set_diagnostics_environment_json(nint context, string environmentJson);

    [LibraryImport("gua")]
    internal static unsafe partial int gua_copy_diagnostics_json(nint context, byte* outJson, int outJsonSize);
    [LibraryImport("gua")]
    internal static unsafe partial int gua_copy_diagnostics_json_for_profile(nint context, int profile, byte* outJson, int outJsonSize);

    [LibraryImport("gua")]
    internal static unsafe partial int gua_copy_version_json(byte* outJson, int outJsonSize);

    [LibraryImport("gua")] internal static partial int gua_clock_install(nint context, double initialTimeMs, double stepMs);
    [LibraryImport("gua")] internal static partial int gua_clock_pause(nint context);
    [LibraryImport("gua")] internal static partial int gua_clock_run_for(nint context, double durationMs, double stepMs);
    [LibraryImport("gua")] internal static partial int gua_clock_resume(nint context);
    [LibraryImport("gua")] internal static partial int gua_clock_get_status(nint context, ref GuaNativeClockStatus status);
    [LibraryImport("gua")] internal static partial int gua_clock_consume_step(nint context, ref GuaNativeClockStep step);

    [LibraryImport("gua", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int gua_get_node_state(nint context, string nodeId, out GuaNodeState state);

    [LibraryImport("gua", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int gua_get_node_state_v2(nint context, string nodeId, GuaNativeNodeStateV2* state);

    [LibraryImport("gua", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int gua_find_node_by_id(nint context, string nodeId, byte* outNodeId, int outNodeIdSize);

    [LibraryImport("gua", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int gua_find_node_by_role(nint context, string role, string? name, byte* outNodeId, int outNodeIdSize);

    [LibraryImport("gua", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int gua_find_node_by_text(nint context, string text, byte* outNodeId, int outNodeIdSize);

    [LibraryImport("gua")]
    internal static unsafe partial int gua_query_nodes_json(nint context, in GuaNativeSelectorV1 selector, byte* outJson, int outJsonSize);

    [LibraryImport("gua", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int gua_enqueue_click(nint context, string nodeId);

    [LibraryImport("gua", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int gua_consume_click_request(nint context, string nodeId);

    [LibraryImport("gua", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int gua_emit_click(nint context, string nodeId);

    [LibraryImport("gua")]
    internal static unsafe partial int gua_poll_event(nint context, GuaNativeEvent* outEvent);

    [LibraryImport("gua")]
    internal static partial int gua_enqueue_action(nint context, in GuaNativeActionRequestDescriptor descriptor, out ulong requestId);

    [LibraryImport("gua")]
    internal static partial int gua_cancel_action_request(nint context, ulong requestId);

    [LibraryImport("gua")]
    internal static unsafe partial int gua_poll_event_v2(nint context, GuaNativeEventV2* outEvent);

    [LibraryImport("gua")]
    internal static unsafe partial int gua_poll_event_v2_for_request(nint context, ulong requestId, GuaNativeEventV2* outEvent);

    [LibraryImport("gua")]
    internal static unsafe partial int gua_poll_event_v3(nint context, GuaNativeEventV3* outEvent);

    [LibraryImport("gua")]
    internal static unsafe partial int gua_poll_event_v3_for_request(nint context, ulong requestId, GuaNativeEventV3* outEvent);

    [LibraryImport("gua", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int gua_consume_action_request(nint context, int action, string? nodeId, GuaNativeActionRequest* outRequest);

    [LibraryImport("gua")]
    internal static partial int gua_emit_action_result(nint context, in GuaNativeActionResult result);

    [LibraryImport("gua")]
    internal static unsafe partial int gua_get_context_status(nint context, GuaNativeContextStatus* status);

    [LibraryImport("gua")]
    internal static unsafe partial int gua_reset_context(nint context, in GuaNativeResetOptions options, GuaNativeResetReport* report);
#endif
}
