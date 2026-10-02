using System.Reflection;
using System.Runtime.InteropServices;
using Gua.Core;

namespace Gua.Runtime;

internal static unsafe partial class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct NodeV2 { internal uint StructSize; internal ulong KnownMask; internal nint Id, ParentId, Role, Label, Text, Value; internal GuaBounds Bounds; internal int Visible, Enabled, Focused, Hovered, Pressed, Checked, Selected; }
    [StructLayout(LayoutKind.Sequential)] internal struct NodeV3 { internal uint StructSize; internal NodeV2 Base; internal long CaretPosition, SelectionStart, SelectionEnd; internal double ScrollX, ScrollY, ScrollMaxX, ScrollMaxY, RangeValue, RangeMin, RangeMax; internal long SelectedIndex; }
    [StructLayout(LayoutKind.Sequential)] internal struct AgentFieldRule { internal uint StructSize; internal nint Path; internal int Mode, ReplacementType; internal nint StringValue; internal double NumberValue; internal int BoolValue; internal double Quantum; }
    [StructLayout(LayoutKind.Sequential)] internal struct AgentPolicy { internal uint StructSize; internal int Exposure, HasAllowedActions; internal ulong AllowedActions; internal nint FieldRules; internal uint FieldRuleCount; }
    [StructLayout(LayoutKind.Sequential)] internal struct NodeV4 { internal uint StructSize; internal NodeV3 Base; internal AgentPolicy AgentPolicy; }
    [StructLayout(LayoutKind.Sequential)] internal unsafe struct ActionRequest { internal uint StructSize; internal ulong RequestId; internal int Action; internal fixed byte NodeId[128]; internal fixed byte Value[256]; internal float DeltaX, DeltaY; internal int BoolValue; internal fixed byte Key[64]; internal uint Modifiers; internal int Sensitive, ScrollUnit; }
    [StructLayout(LayoutKind.Sequential)] internal struct ActionResult { internal uint StructSize; internal ulong RequestId; internal int Action, Status, ErrorCode; internal nint NodeId, Value; internal int Sensitive; }
    [StructLayout(LayoutKind.Sequential)] internal struct ActionDescriptor { internal uint StructSize; internal int Action; internal nint NodeId, Value; internal float DeltaX, DeltaY; internal int BoolValue; internal nint Key; internal uint Modifiers; internal int Sensitive, ScrollUnit; }
    [StructLayout(LayoutKind.Sequential)] internal unsafe struct ActionEventV2 { internal uint StructSize; internal ulong RequestId; internal int Action, Status, ErrorCode; internal fixed byte NodeId[128]; internal fixed byte Value[256]; internal int Sensitive; }
    [StructLayout(LayoutKind.Sequential)] internal unsafe struct ActionEventV3 { internal uint StructSize; internal ActionEventV2 Base; internal ulong SessionEpoch, FrameSequence, Revision; }
    [StructLayout(LayoutKind.Sequential)] internal struct ScreenshotRequest { internal uint StructSize; internal ulong RequestId, SessionEpoch, AfterFrameSequence; }
    [StructLayout(LayoutKind.Sequential)] internal unsafe struct LegacyEvent { internal int Type; internal fixed byte NodeId[128]; }
    [StructLayout(LayoutKind.Sequential)] internal struct ClockStatus { internal uint StructSize; internal int Installed, Paused; internal double NowMs, DefaultStepMs, PendingMs; internal ulong Generation; }
    [StructLayout(LayoutKind.Sequential)] internal struct ClockStep { internal uint StructSize; internal double DeltaMs; internal int FinalStep; internal ulong Generation; }
    [StructLayout(LayoutKind.Sequential)] internal struct GameInputAction { internal uint StructSize; internal nint Id, Description; internal int ValueType; internal double Minimum, Maximum; internal int HasRange, Holdable, Active; internal nint BindingsJson, Risk; internal int RequiresConfirmation; }
    [StructLayout(LayoutKind.Sequential)] internal struct GameInputActionV3 { internal uint StructSize; internal GameInputActionV2 Base; internal nint ValueSchemaJson, ExamplesJson; }
    [StructLayout(LayoutKind.Sequential)] internal struct GameInputActionV2 { internal uint StructSize; internal GameInputAction Base; internal nint Category, Aliases; internal uint AliasCount; internal nint Tags; internal uint TagCount; internal int AgentExposure; }
    [StructLayout(LayoutKind.Sequential)] internal struct GameInputActionSelector { internal uint StructSize; internal nint Id, Query; internal int ValueType, Active; internal nint Context, Category, Tags; internal uint TagCount, Limit; }
    [StructLayout(LayoutKind.Sequential)] internal struct GameInputRequestDescriptor { internal uint StructSize; internal ulong OwnerId; internal int Kind, Operation; internal nint Target, ValueJson; internal double X, Y; internal uint LeaseMs; internal int DeviceIndex, Sensitive; }
    [StructLayout(LayoutKind.Sequential)] internal struct GameInputRequestDescriptorV2 { internal uint StructSize; internal ulong OwnerId; internal int Kind, Operation; internal nint Target, ValueJson; internal double X, Y; internal uint LeaseMs; internal int DeviceIndex, Sensitive, Confirmed; }
    [StructLayout(LayoutKind.Sequential)] internal unsafe struct GameInputRequest { internal uint StructSize; internal ulong RequestId, OwnerId; internal int Kind, Operation; internal fixed byte Target[128]; internal fixed byte ValueJson[512]; internal double X, Y; internal uint LeaseMs; internal int DeviceIndex, Sensitive; }
    [StructLayout(LayoutKind.Sequential)] internal struct WorldState { internal uint StructSize; internal nint Key; internal int Type; internal nint StringValue; internal double NumberValue; internal int BoolValue; }
    [StructLayout(LayoutKind.Sequential)] internal struct WorldObject { internal uint StructSize; internal nint Id, ParentId, Kind, Label, Description; internal int Space; internal double X, Y, Z; internal int VisibleToPlayer, Active, AgentExposure; internal nint DomainId, RelatedUiNodeId, Tags; internal uint TagCount; internal nint StateValues; internal uint StateValueCount; }
    [StructLayout(LayoutKind.Sequential)] internal struct WorldObjectV2 { internal uint StructSize; internal WorldObject Base; internal AgentPolicy AgentPolicy; }
    [StructLayout(LayoutKind.Sequential)] internal struct WorldSelector { internal uint StructSize; internal nint Id; internal int IdMatch; internal nint Kind; internal int KindMatch; internal nint Label; internal int LabelMatch; internal nint Tag; internal int TagMatch; internal nint ParentId; internal int DirectChild, VisibleToPlayer, Active; internal nint State; }
    [StructLayout(LayoutKind.Sequential)] internal struct WorldNear { internal uint StructSize; internal nint RelativeToObjectId; internal double MaxDistance; }
    [StructLayout(LayoutKind.Sequential)] internal struct WorldSelectorV2 { internal uint StructSize; internal WorldSelector Base; internal nint Near; internal uint Limit; }

#if !NETSTANDARD2_1
    static Native() => NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != "gua_runtime") return 0;
        var file = OperatingSystem.IsWindows() ? "gua_runtime.dll" : OperatingSystem.IsMacOS() ? "libgua_runtime.dylib" : "libgua_runtime.so";
        foreach (var directory in CandidateDirectories(assembly))
        {
            var candidate = Path.Combine(directory, file);
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        }
        return NativeLibrary.TryLoad(name, assembly, searchPath, out var fallback) ? fallback : 0;
    }
    private static IEnumerable<string> CandidateDirectories(Assembly assembly)
    {
        var configured = Environment.GetEnvironmentVariable("GUA_RUNTIME_NATIVE_DIR"); if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
        var legacy = Environment.GetEnvironmentVariable("GUA_NATIVE_DIR"); if (!string.IsNullOrWhiteSpace(legacy)) yield return legacy;
        yield return AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(assembly.Location) && Path.GetDirectoryName(assembly.Location) is { } directory) yield return directory;
        yield return Environment.CurrentDirectory;
    }
#endif

#if GUA_STATIC_LINK
    private const string Library = "__Internal";
#else
    private const string Library = "gua_runtime";
#endif
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gua_runtime_create();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void gua_runtime_destroy(nint runtime);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void gua_runtime_begin_frame(nint runtime, [MarshalAs(UnmanagedType.LPUTF8Str)] string screen);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void gua_runtime_end_frame(nint runtime);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_register_node_v3(nint runtime, in NodeV3 node);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_register_node_v4(nint runtime, in NodeV4 node);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_begin_world_frame(nint runtime, [MarshalAs(UnmanagedType.LPUTF8Str)] string scene);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_register_world_object_v1(nint runtime, in WorldObject descriptor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_register_world_object_v2(nint runtime, in WorldObjectV2 descriptor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_end_world_frame(nint runtime);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_abort_world_frame(nint runtime);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int gua_runtime_copy_world_object_tree_json(nint runtime, byte* output, int size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int gua_runtime_query_world_objects_json(nint runtime, in WorldSelector selector, byte* output, int size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int gua_runtime_query_world_objects_v2_json(nint runtime, in WorldSelectorV2 selector, byte* output, int size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int gua_runtime_copy_player_world_object_tree_json(nint runtime, byte* output, int size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int gua_runtime_query_player_world_objects_json(nint runtime, in WorldSelector selector, byte* output, int size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int gua_runtime_query_player_world_objects_v2_json(nint runtime, in WorldSelectorV2 selector, byte* output, int size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_consume_action_request(nint runtime, int action, [MarshalAs(UnmanagedType.LPUTF8Str)] string? nodeId, ref ActionRequest request);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_enqueue_action(nint runtime, in ActionDescriptor descriptor, out ulong requestId);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_enqueue_player_action(nint runtime, in ActionDescriptor descriptor, out ulong requestId);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_cancel_action_request(nint runtime, ulong requestId);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_get_action_request_observation_profile(nint runtime, ulong requestId);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_poll_event_v3_for_request(nint runtime, ulong requestId, ref ActionEventV3 result);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_emit_action_result(nint runtime, in ActionResult result);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_consume_screenshot_request(nint runtime, ref ScreenshotRequest request);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_complete_screenshot_request(nint runtime, ulong requestId, int result, [MarshalAs(UnmanagedType.LPUTF8Str)] string dataUri, int width, int height);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void gua_runtime_add_log(nint runtime, int level, [MarshalAs(UnmanagedType.LPUTF8Str)] string message);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int gua_runtime_copy_ui_tree_json(nint runtime, byte* output, int size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int gua_runtime_copy_player_ui_tree_json(nint runtime, byte* output, int size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int gua_runtime_copy_version_json(nint runtime, byte* output, int size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_clock_install(nint runtime, double initialTimeMs, double stepMs);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_clock_pause(nint runtime);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_clock_run_for(nint runtime, double durationMs, double stepMs);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_clock_advance(nint runtime, double durationMs);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_clock_resume(nint runtime);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_clock_get_status(nint runtime, ref ClockStatus status);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_clock_consume_step(nint runtime, ref ClockStep step);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void gua_runtime_set_adapter_version(nint runtime, [MarshalAs(UnmanagedType.LPUTF8Str)] string adapter, [MarshalAs(UnmanagedType.LPUTF8Str)] string version);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void gua_runtime_set_godot_plugin_version(nint runtime, [MarshalAs(UnmanagedType.LPUTF8Str)] string version);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void gua_runtime_set_virtual_clock_enabled(nint runtime, int enabled);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void gua_runtime_set_game_input_capabilities(nint runtime, uint capabilities);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void gua_runtime_set_player_game_input_capabilities(nint runtime, uint capabilities);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern uint gua_runtime_get_game_input_capabilities(nint runtime, int observationProfile);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_begin_game_input_frame(nint runtime, [MarshalAs(UnmanagedType.LPUTF8Str)] string inputContext);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_register_game_input_action_v1(nint runtime, in GameInputAction action);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_register_game_input_action_v3(nint runtime, in GameInputActionV3 action);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int gua_runtime_copy_game_input_actions_json_v2(nint runtime, int profile, byte* output, int size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int gua_runtime_query_game_input_actions_json_v2(nint runtime, in GameInputActionSelector selector, int profile, byte* output, int size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_register_game_input_action_v2(nint runtime, in GameInputActionV2 action);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_end_game_input_frame(nint runtime);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_abort_game_input_frame(nint runtime);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong gua_runtime_create_game_input_owner(nint runtime);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_release_game_input_owner(nint runtime, ulong ownerId);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_enqueue_game_input(nint runtime, in GameInputRequestDescriptor request, out ulong requestId);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_enqueue_game_input_v2(nint runtime, in GameInputRequestDescriptorV2 request, out ulong requestId);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_enqueue_game_input_for_profile_v2(nint runtime, in GameInputRequestDescriptorV2 request, int observationProfile, out ulong requestId);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_enqueue_game_input_guarded_v2(nint runtime, in GameInputRequestDescriptorV2 request, int observationProfile, ulong expectedEpoch, ulong expectedRevision, out ulong requestId);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_validate_game_input_guarded_v2(nint runtime, in GameInputRequestDescriptorV2 request, int observationProfile, ulong expectedEpoch, ulong expectedRevision);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_consume_game_input_request(nint runtime, ref GameInputRequest request);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_complete_game_input_request(nint runtime, ulong requestId, int succeeded, int errorCode);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_tick_game_input_leases(nint runtime, double elapsedMs);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int gua_runtime_copy_game_input_actions_json(nint runtime, byte* output, int size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int gua_runtime_copy_player_game_input_actions_json(nint runtime, byte* output, int size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int gua_runtime_query_game_input_actions_json(nint runtime, in GameInputActionSelector selector, int observationProfile, byte* output, int size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int gua_runtime_copy_game_input_state_json(nint runtime, ulong ownerId, byte* output, int size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_get_game_input_owner_health(nint runtime, ulong ownerId, out ulong sessionEpoch, out int leaseExpired);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int gua_runtime_copy_game_input_result_json(nint runtime, ulong ownerId, ulong requestId, byte* output, int size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void gua_runtime_set_world_object_tree_enabled(nint runtime, int enabled);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_set_observation_profile(nint runtime, int profile);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_get_observation_profile(nint runtime);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_set_player_screenshot_enabled(nint runtime, int enabled);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_enqueue_click(nint runtime, [MarshalAs(UnmanagedType.LPUTF8Str)] string id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_consume_click_request(nint runtime, [MarshalAs(UnmanagedType.LPUTF8Str)] string id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_emit_click(nint runtime, [MarshalAs(UnmanagedType.LPUTF8Str)] string id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int gua_runtime_find_node_by_role(nint runtime, [MarshalAs(UnmanagedType.LPUTF8Str)] string role, [MarshalAs(UnmanagedType.LPUTF8Str)] string label, byte* output, int size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern unsafe int gua_runtime_poll_event(nint runtime, LegacyEvent* result);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_start_inspector_bridge(nint runtime, int port);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void gua_runtime_stop_inspector_bridge(nint runtime);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_inspector_bridge_running(nint runtime);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gua_runtime_inspector_bridge_url(nint runtime);
}
