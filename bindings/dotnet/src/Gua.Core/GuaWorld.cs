using System.Runtime.InteropServices;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Gua.Core;

public enum GuaWorldSpace { World2D = 1, World3D = 2 }
public enum GuaObservationProfile { Debug = 0, Player = 1 }

public sealed record GuaWorldPosition(double X, double Y, double Z = 0);
public sealed record GuaWorldObjectDescriptor(
    string Id, string Kind, string Label, GuaWorldSpace Space, GuaWorldPosition Position,
    bool VisibleToPlayer = false, bool Active = true, string? ParentId = null, string? Description = null,
    GuaAgentExposure AgentExposure = GuaAgentExposure.Auto, IReadOnlyList<string>? Tags = null,
    IReadOnlyDictionary<string, object?>? State = null, string? DomainId = null, string? RelatedUiNodeId = null,
    GuaAgentPolicy? AgentPolicy = null);

public sealed record GuaWorldStateCriterion(string Key, object? Value);
public sealed record GuaWorldNear(string RelativeToObjectId, double MaxDistance);
public sealed record GuaWorldSelector(
    string? Id = null, GuaMatchMode IdMatch = GuaMatchMode.Exact,
    string? Kind = null, GuaMatchMode KindMatch = GuaMatchMode.Exact,
    string? Label = null, GuaMatchMode LabelMatch = GuaMatchMode.Exact,
    string? Tag = null, GuaMatchMode TagMatch = GuaMatchMode.Exact,
    string? ParentId = null, bool DirectChild = false, bool? VisibleToPlayer = null,
    bool? Active = null, GuaWorldStateCriterion? State = null)
{
    public GuaWorldNear? Near { get; init; }
    public uint? Limit { get; init; }
}

public sealed record GuaWorldObject(
    string Id, string Kind, string? Label, string Space, JsonElement Position, bool VisibleToPlayer,
    bool Active, string AgentExposure, string? ParentId, string? Description,
    IReadOnlyList<string>? Tags, IReadOnlyDictionary<string, JsonElement> State,
    string? DomainId, string? RelatedUiNodeId);
public sealed record GuaWorldTree(int SchemaVersion, ulong SessionEpoch, ulong FrameSequence, ulong Revision, string Scene, IReadOnlyList<GuaWorldObject> Objects);
public sealed record GuaWorldDistance(string ObjectId, double Distance);
public sealed record GuaWorldSpatialResult(string RelativeToObjectId, bool Truncated, IReadOnlyList<GuaWorldDistance> Distances);
public sealed record GuaWorldQueryResult(bool Valid, IReadOnlyList<GuaWorldObject> Matches, string? Error = null)
{
    public ulong? SessionEpoch { get; init; }
    public ulong? FrameSequence { get; init; }
    public ulong? Revision { get; init; }
    public GuaWorldSpatialResult? Spatial { get; init; }
}

public interface IGuaWorldContext
{
    string GetWorldObjectTreeJson(GuaObservationProfile profile = GuaObservationProfile.Debug);
    GuaWorldTree GetWorldObjectTree(GuaObservationProfile profile = GuaObservationProfile.Debug);
    GuaWorldQueryResult QueryWorldObjects(GuaWorldSelector selector, GuaObservationProfile profile = GuaObservationProfile.Debug);
    Task<GuaWorldObject> WaitForWorldObjectAsync(GuaWorldSelector selector, TimeSpan? timeout = null,
        GuaObservationProfile profile = GuaObservationProfile.Debug, CancellationToken cancellationToken = default);
}

public sealed partial class GuaContext : IGuaWorldContext
{
    public void BeginWorldFrame(string scene)
    {
        ThrowIfDisposed();
        if (Native.gua_begin_world_frame(_handle, scene) == 0) throw new InvalidOperationException("Failed to begin the Gua world frame.");
    }

    public void RegisterWorldObject(GuaWorldObjectDescriptor descriptor)
    {
        if (descriptor is null) throw new ArgumentNullException(nameof(descriptor));
        ThrowIfDisposed();
        using var native = new NativeWorldDescriptor(descriptor);
        var value = native.ValueV2;
        if (Native.gua_register_world_object_v2(_handle, in value) == 0)
            throw new InvalidOperationException($"Failed to register Gua world object '{descriptor.Id}'.");
    }

    public void EndWorldFrame()
    {
        ThrowIfDisposed();
        lock (ObserveGate) { ThrowIfDisposed(); SampleObservations(false); if (ObserveDisposed) return; if (Native.gua_end_world_frame(ObserveHandle) == 0) throw new InvalidOperationException("The Gua world frame was rejected."); }
    }

    public void AbortWorldFrame()
    {
        ThrowIfDisposed();
        if (Native.gua_abort_world_frame(_handle) == 0) throw new InvalidOperationException("There is no active Gua world frame to abort.");
    }

    public string GetWorldObjectTreeJson(GuaObservationProfile profile = GuaObservationProfile.Debug) =>
        CopyWorldJson(null, profile);

    public GuaWorldTree GetWorldObjectTree(GuaObservationProfile profile = GuaObservationProfile.Debug) =>
        ParseTree(GetWorldObjectTreeJson(profile));

    public GuaWorldQueryResult QueryWorldObjects(GuaWorldSelector selector, GuaObservationProfile profile = GuaObservationProfile.Debug)
    {
        if (selector is null) throw new ArgumentNullException(nameof(selector));
        using var native = new NativeWorldSelector(selector);
        return ParseQuery(CopyWorldJson(native.Value, profile));
    }

    public async Task<GuaWorldObject> WaitForWorldObjectAsync(GuaWorldSelector selector, TimeSpan? timeout = null,
        GuaObservationProfile profile = GuaObservationProfile.Debug, CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (started.Elapsed <= limit) {
            cancellationToken.ThrowIfCancellationRequested();
            var result = QueryWorldObjects(selector, profile);
            if (!result.Valid) throw new ArgumentException(result.Error ?? "Invalid world selector.", nameof(selector));
            var match = result.Matches.FirstOrDefault();
            if (match is not null) return match;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("Timed out waiting for a Gua world object.");
    }

    private unsafe string CopyWorldJson(Native.GuaNativeWorldSelectorV2? selector, GuaObservationProfile profile)
    {
        ThrowIfDisposed();
        int Required(byte* buffer, int size) => selector is { } value
            ? Native.gua_query_world_objects_v2_json(_handle, in value, (int)profile, buffer, size)
            : Native.gua_copy_world_object_tree_json(_handle, (int)profile, buffer, size);
        var required = Required(null, 0);
        if (required <= 0) throw new InvalidOperationException("Native Gua returned an invalid World Object Tree JSON size.");
        while (true) {
            var bytes = new byte[required];
            fixed (byte* pointer = bytes) {
                var actual = Required(pointer, bytes.Length);
                if (actual <= bytes.Length) return Encoding.UTF8.GetString(bytes, 0, actual - 1);
                required = actual;
            }
        }
    }

    internal static GuaWorldTree ParseTree(string json) => JsonSerializer.Deserialize<GuaWorldTree>(json,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("Invalid World Object Tree JSON.");
    internal static GuaWorldQueryResult ParseQuery(string json) => JsonSerializer.Deserialize<GuaWorldQueryResult>(json,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("Invalid world query JSON.");
}

internal sealed class NativeWorldDescriptor : IDisposable
{
    private readonly List<nint> allocations = [];
    private readonly NativeAgentPolicy policy;
    public Native.GuaNativeWorldObjectDescriptorV1 Value { get; }
    public Native.GuaNativeWorldObjectDescriptorV2 ValueV2 { get; }

    public NativeWorldDescriptor(GuaWorldObjectDescriptor source)
    {
        policy = new NativeAgentPolicy(source.AgentPolicy, source.AgentExposure);
        nint Text(string? value) { if (value is null) return 0; var p = Marshal.StringToCoTaskMemUTF8(value); allocations.Add(p); return p; }
        try {
            var tags = source.Tags ?? [];
            var tagPointers = tags.Select(Text).ToArray();
            nint tagsMemory = 0;
            if (tagPointers.Length != 0) tagsMemory = (nint)Marshal.AllocCoTaskMem(IntPtr.Size * tagPointers.Length);
            if (tagsMemory != 0) { allocations.Add(tagsMemory); Marshal.Copy(tagPointers, 0, tagsMemory, tagPointers.Length); }
            var states = (source.State ?? new Dictionary<string, object?>()).Select(pair => State(pair.Key, pair.Value, Text)).ToArray();
            var stateSize = Marshal.SizeOf<Native.GuaNativeWorldStateValueV1>();
            nint statesMemory = 0;
            if (states.Length != 0) statesMemory = (nint)Marshal.AllocCoTaskMem(stateSize * states.Length);
            if (statesMemory != 0) { allocations.Add(statesMemory); for (var i = 0; i < states.Length; i++) Marshal.StructureToPtr(states[i], statesMemory + i * stateSize, false); }
            Value = new Native.GuaNativeWorldObjectDescriptorV1 {
                StructSize = (uint)Marshal.SizeOf<Native.GuaNativeWorldObjectDescriptorV1>(), Id = Text(source.Id), ParentId = Text(source.ParentId),
                Kind = Text(source.Kind), Label = Text(source.Label), Description = Text(source.Description), Space = (int)source.Space,
                PositionX = source.Position.X, PositionY = source.Position.Y, PositionZ = source.Position.Z,
                VisibleToPlayer = source.VisibleToPlayer ? 1 : 0, Active = source.Active ? 1 : 0, AgentExposure = (int)source.AgentExposure,
                DomainId = Text(source.DomainId), RelatedUiNodeId = Text(source.RelatedUiNodeId), Tags = tagsMemory, TagCount = (uint)tagPointers.Length,
                StateValues = statesMemory, StateValueCount = (uint)states.Length
            };
            ValueV2 = new Native.GuaNativeWorldObjectDescriptorV2 {
                StructSize = (uint)Marshal.SizeOf<Native.GuaNativeWorldObjectDescriptorV2>(), Base = Value, AgentPolicy = policy.Value
            };
        } catch { Dispose(); throw; }
    }

    internal static Native.GuaNativeWorldStateValueV1 State(string key, object? value, Func<string?, nint> text) => value switch {
        null => New(0), string item => New(1, text(item)), bool item => New(3, boolean: item),
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => New(2, number: Number(key, value)),
        _ => throw new ArgumentException($"World state '{key}' must be a primitive JSON value.")
    } with { Key = text(key) };
    private static double Number(string key, object value)
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
    private static Native.GuaNativeWorldStateValueV1 New(int type, nint text = 0, double number = 0, bool boolean = false) =>
        new() { StructSize = (uint)Marshal.SizeOf<Native.GuaNativeWorldStateValueV1>(), Type = type, StringValue = text, NumberValue = number, BoolValue = boolean ? 1 : 0 };
    public void Dispose() { policy.Dispose(); foreach (var allocation in allocations) Marshal.FreeCoTaskMem(allocation); }
}

internal sealed class NativeWorldSelector : IDisposable
{
    private readonly List<nint> allocations = [];
    public Native.GuaNativeWorldSelectorV2 Value { get; }
    public NativeWorldSelector(GuaWorldSelector source)
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
        nint Text(string? value) { if (value is null) return 0; var p = Marshal.StringToCoTaskMemUTF8(value); allocations.Add(p); return p; }
        try {
            nint statePointer = 0;
            if (source.State is { } state) { var native = NativeWorldDescriptor.State(state.Key, state.Value, Text); statePointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<Native.GuaNativeWorldStateValueV1>()); allocations.Add(statePointer); Marshal.StructureToPtr(native, statePointer, false); }
            var baseSelector = new Native.GuaNativeWorldSelectorV1 { StructSize = (uint)Marshal.SizeOf<Native.GuaNativeWorldSelectorV1>(),
                Id = Text(source.Id), IdMatch = (int)source.IdMatch, Kind = Text(source.Kind), KindMatch = (int)source.KindMatch,
                Label = Text(source.Label), LabelMatch = (int)source.LabelMatch, Tag = Text(source.Tag), TagMatch = (int)source.TagMatch,
                ParentId = Text(source.ParentId), DirectChild = source.DirectChild ? 1 : 0,
                VisibleToPlayer = Filter(source.VisibleToPlayer), Active = Filter(source.Active), State = statePointer };
            nint nearPointer = 0;
            if (source.Near is { } spatial) {
                var nativeNear = new Native.GuaNativeWorldNearV1 { StructSize = (uint)Marshal.SizeOf<Native.GuaNativeWorldNearV1>(),
                    RelativeToObjectId = Text(spatial.RelativeToObjectId), MaxDistance = spatial.MaxDistance };
                nearPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<Native.GuaNativeWorldNearV1>()); allocations.Add(nearPointer);
                Marshal.StructureToPtr(nativeNear, nearPointer, false);
            }
            Value = new Native.GuaNativeWorldSelectorV2 { StructSize = (uint)Marshal.SizeOf<Native.GuaNativeWorldSelectorV2>(),
                Base = baseSelector, Near = nearPointer, Limit = source.Limit ?? 0 };
        } catch { Dispose(); throw; }
    }
    private static int Filter(bool? value) => value is null ? 0 : value.Value ? 2 : 1;
    public void Dispose() { foreach (var allocation in allocations) Marshal.FreeCoTaskMem(allocation); }
}
