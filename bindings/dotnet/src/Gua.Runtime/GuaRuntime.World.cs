using System.Runtime.InteropServices;
using System.Globalization;
using System.Numerics;
using System.Text;
using Gua.Core;

namespace Gua.Runtime;

public sealed partial class GuaRuntime
{
    public GuaObservationProfile ObservationProfile
    {
        get
        {
            ThrowIfDisposed();
            return (GuaObservationProfile)Native.gua_runtime_get_observation_profile(_handle);
        }
    }

    public void EnableWorldObjectTreeAdapter() { ThrowIfDisposed(); Native.gua_runtime_set_world_object_tree_enabled(_handle, 1); }
    public void SetObservationProfile(GuaObservationProfile profile)
    {
        ThrowIfDisposed();
        if (profile is not GuaObservationProfile.Debug and not GuaObservationProfile.Player) throw new ArgumentOutOfRangeException(nameof(profile));
        if (Native.gua_runtime_set_observation_profile(_handle, (int)profile) == 0)
            throw new InvalidOperationException("The observation profile cannot change while the Inspector bridge is running.");
    }
    public void SetPlayerScreenshotEnabled(bool enabled)
    {
        ThrowIfDisposed();
        if (Native.gua_runtime_set_player_screenshot_enabled(_handle, enabled ? 1 : 0) == 0)
            throw new InvalidOperationException("Player screenshot policy cannot change while the Inspector bridge is running.");
    }
    public void BeginWorldFrame(string scene) { ThrowIfDisposed(); if (Native.gua_runtime_begin_world_frame(_handle, scene) == 0) throw new InvalidOperationException("Failed to begin the Gua world frame."); }
    public void EndWorldFrame() {
        var observations = Observations;
        lock (observations.ObserveGate) { ThrowIfDisposed(); observations.SampleObservations(false);
            if (_handle != 0 && Native.gua_runtime_end_world_frame(_handle) == 0) throw new InvalidOperationException("The Gua world frame was rejected."); }
    }
    public void AbortWorldFrame() { ThrowIfDisposed(); if (Native.gua_runtime_abort_world_frame(_handle) == 0) throw new InvalidOperationException("There is no active Gua world frame to abort."); }

    public void RegisterWorldObject(GuaWorldObjectDescriptor source)
    {
        ThrowIfDisposed();
        if (source is null) throw new ArgumentNullException(nameof(source));
        var allocations = new List<nint>();
        using var policy = new RuntimeAgentPolicy(source.AgentPolicy, source.AgentExposure);
        nint Text(string? value) { if (value is null) return 0; var p = (nint)Marshal.StringToCoTaskMemUTF8(value); allocations.Add(p); return p; }
        try {
            var tagPointers = (source.Tags ?? []).Select(Text).ToArray();
            nint tags = 0;
            if (tagPointers.Length != 0) { tags = (nint)Marshal.AllocCoTaskMem(IntPtr.Size * tagPointers.Length); allocations.Add(tags); Marshal.Copy(tagPointers, 0, tags, tagPointers.Length); }
            var values = new List<Native.WorldState>();
            foreach (var pair in source.State ?? new Dictionary<string, object?>()) {
                var state = new Native.WorldState { StructSize = (uint)Marshal.SizeOf<Native.WorldState>(), Key = Text(pair.Key) };
                switch (pair.Value) {
                    case null: state.Type = 0; break;
                    case string text: state.Type = 1; state.StringValue = Text(text); break;
                    case bool boolean: state.Type = 3; state.BoolValue = boolean ? 1 : 0; break;
                    case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                        state.Type = 2; state.NumberValue = WorldNumber(pair.Key, pair.Value); break;
                    default: throw new ArgumentException($"World state '{pair.Key}' must be a primitive JSON value.");
                }
                values.Add(state);
            }
            nint states = 0; var stateSize = Marshal.SizeOf<Native.WorldState>();
            if (values.Count != 0) { states = (nint)Marshal.AllocCoTaskMem(stateSize * values.Count); allocations.Add(states); for (var i = 0; i < values.Count; i++) Marshal.StructureToPtr(values[i], states + i * stateSize, false); }
            var native = new Native.WorldObject { StructSize = (uint)Marshal.SizeOf<Native.WorldObject>(), Id = Text(source.Id), ParentId = Text(source.ParentId), Kind = Text(source.Kind), Label = Text(source.Label), Description = Text(source.Description), Space = (int)source.Space,
                X = source.Position.X, Y = source.Position.Y, Z = source.Position.Z, VisibleToPlayer = source.VisibleToPlayer ? 1 : 0, Active = source.Active ? 1 : 0, AgentExposure = (int)source.AgentExposure,
                DomainId = Text(source.DomainId), RelatedUiNodeId = Text(source.RelatedUiNodeId), Tags = tags, TagCount = (uint)tagPointers.Length, StateValues = states, StateValueCount = (uint)values.Count };
            var secured = new Native.WorldObjectV2 { StructSize = (uint)Marshal.SizeOf<Native.WorldObjectV2>(), Base = native, AgentPolicy = policy.Value };
            if (Native.gua_runtime_register_world_object_v2(_handle, in secured) == 0) throw new InvalidOperationException($"Failed to register Gua world object '{source.Id}'.");
        } finally { foreach (var allocation in allocations) Marshal.FreeCoTaskMem(allocation); }
    }

    internal static double WorldNumber(string key, object value)
    {
        var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (!double.IsFinite(number)) throw new ArgumentException($"World state '{key}' must be a finite number.");
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong) {
            var integral = BigInteger.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
            if (new BigInteger(number) != integral)
                throw new ArgumentException($"World state '{key}' cannot be represented precisely as an ABI double.");
        } else if (value is decimal) {
            try {
                if (Convert.ToDecimal(value, CultureInfo.InvariantCulture) != Convert.ToDecimal(number))
                    throw new ArgumentException($"World state '{key}' cannot be represented precisely as an ABI double.");
            } catch (OverflowException) {
                throw new ArgumentException($"World state '{key}' cannot be represented precisely as an ABI double.");
            }
        }
        return number;
    }

    public string GetWorldObjectTreeJson() => GetWorldObjectTreeJson(playerProjection: false);
    public string GetPlayerWorldObjectTreeJson() => GetWorldObjectTreeJson(playerProjection: true);

    private unsafe string GetWorldObjectTreeJson(bool playerProjection)
    {
        ThrowIfDisposed();
        var required = playerProjection
            ? Native.gua_runtime_copy_player_world_object_tree_json(_handle, null, 0)
            : Native.gua_runtime_copy_world_object_tree_json(_handle, null, 0);
        if (required <= 0) throw new InvalidOperationException("World Object Tree is unavailable.");
        while (true) {
            var bytes = new byte[required];
            fixed (byte* output = bytes) {
                var actual = playerProjection
                    ? Native.gua_runtime_copy_player_world_object_tree_json(_handle, output, bytes.Length)
                    : Native.gua_runtime_copy_world_object_tree_json(_handle, output, bytes.Length);
                if (actual <= 0) throw new InvalidOperationException("World Object Tree is unavailable.");
                if (actual <= bytes.Length) return Encoding.UTF8.GetString(bytes, 0, actual - 1);
                required = actual;
            }
        }
    }

    public string QueryWorldObjectsJson(GuaWorldSelector selector) => QueryWorldObjectsJson(selector, playerProjection: false);
    public string QueryPlayerWorldObjectsJson(GuaWorldSelector selector) => QueryWorldObjectsJson(selector, playerProjection: true);

    private unsafe string QueryWorldObjectsJson(GuaWorldSelector selector, bool playerProjection)
    {
        ThrowIfDisposed();
        if (selector is null) throw new ArgumentNullException(nameof(selector));
        using var native = new RuntimeWorldSelector(selector);
        var value = native.Value;
        var required = playerProjection
            ? Native.gua_runtime_query_player_world_objects_v2_json(_handle, in value, null, 0)
            : Native.gua_runtime_query_world_objects_v2_json(_handle, in value, null, 0);
        if (required <= 0) throw new InvalidOperationException("World object query is unavailable.");
        while (true) {
            var bytes = new byte[required];
            fixed (byte* output = bytes) {
                var actual = playerProjection
                    ? Native.gua_runtime_query_player_world_objects_v2_json(_handle, in value, output, bytes.Length)
                    : Native.gua_runtime_query_world_objects_v2_json(_handle, in value, output, bytes.Length);
                if (actual <= 0) throw new InvalidOperationException("World object query is unavailable.");
                if (actual <= bytes.Length) return Encoding.UTF8.GetString(bytes, 0, actual - 1);
                required = actual;
            }
        }
    }
}

internal sealed class RuntimeWorldSelector : IDisposable
{
    private readonly List<nint> allocations = [];
    public Native.WorldSelectorV2 Value { get; }

    public RuntimeWorldSelector(GuaWorldSelector source)
    {
        foreach (var (name, value) in new[] {
            (nameof(source.Id), source.Id), (nameof(source.Kind), source.Kind), (nameof(source.Label), source.Label),
            (nameof(source.Tag), source.Tag), (nameof(source.ParentId), source.ParentId) })
            if (value is not null && value.Length == 0)
                throw new ArgumentException($"World selector criterion '{name}' must be a non-empty string.", nameof(source));
        if (source.DirectChild && source.ParentId is null)
            throw new ArgumentException("World selector ParentId is required when DirectChild is true.", nameof(source));
        if (source.Limit is 0)
            throw new ArgumentException("World selector Limit must be positive when supplied.", nameof(source));
        if (source.Limit.HasValue && source.Near is null)
            throw new ArgumentException("World selector Near is required when Limit is supplied.", nameof(source));
        if (source.Near is { } near && (string.IsNullOrEmpty(near.RelativeToObjectId) || double.IsNaN(near.MaxDistance) || double.IsInfinity(near.MaxDistance) || near.MaxDistance < 0))
            throw new ArgumentException("World selector Near requires a non-empty object ID and a finite non-negative distance.", nameof(source));
        nint Text(string? text) { if (text is null) return 0; var pointer = Marshal.StringToCoTaskMemUTF8(text); allocations.Add(pointer); return pointer; }
        try {
            nint statePointer = 0;
            if (source.State is { } state) {
                if (string.IsNullOrEmpty(state.Key)) throw new ArgumentException("World selector state key must be non-empty.", nameof(source));
                var nativeState = new Native.WorldState { StructSize = (uint)Marshal.SizeOf<Native.WorldState>(), Key = Text(state.Key) };
                switch (state.Value) {
                    case null: nativeState.Type = 0; break;
                    case string text: nativeState.Type = 1; nativeState.StringValue = Text(text); break;
                    case bool boolean: nativeState.Type = 3; nativeState.BoolValue = boolean ? 1 : 0; break;
                    case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                        nativeState.Type = 2; nativeState.NumberValue = GuaRuntime.WorldNumber(state.Key, state.Value); break;
                    default: throw new ArgumentException("World selector state value must be a primitive JSON value.", nameof(source));
                }
                statePointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<Native.WorldState>());
                allocations.Add(statePointer);
                Marshal.StructureToPtr(nativeState, statePointer, false);
            }
            var baseSelector = new Native.WorldSelector {
                StructSize = (uint)Marshal.SizeOf<Native.WorldSelector>(),
                Id = Text(source.Id), IdMatch = (int)source.IdMatch,
                Kind = Text(source.Kind), KindMatch = (int)source.KindMatch,
                Label = Text(source.Label), LabelMatch = (int)source.LabelMatch,
                Tag = Text(source.Tag), TagMatch = (int)source.TagMatch,
                ParentId = Text(source.ParentId), DirectChild = source.DirectChild ? 1 : 0,
                VisibleToPlayer = Filter(source.VisibleToPlayer), Active = Filter(source.Active), State = statePointer,
            };
            nint nearPointer = 0;
            if (source.Near is { } spatial) {
                var nativeNear = new Native.WorldNear { StructSize = (uint)Marshal.SizeOf<Native.WorldNear>(),
                    RelativeToObjectId = Text(spatial.RelativeToObjectId), MaxDistance = spatial.MaxDistance };
                nearPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<Native.WorldNear>()); allocations.Add(nearPointer);
                Marshal.StructureToPtr(nativeNear, nearPointer, false);
            }
            Value = new Native.WorldSelectorV2 { StructSize = (uint)Marshal.SizeOf<Native.WorldSelectorV2>(),
                Base = baseSelector, Near = nearPointer, Limit = source.Limit ?? 0 };
        } catch { Dispose(); throw; }
    }

    private static int Filter(bool? value) => value is null ? 0 : value.Value ? 2 : 1;
    public void Dispose() { foreach (var allocation in allocations) Marshal.FreeCoTaskMem(allocation); }
}
