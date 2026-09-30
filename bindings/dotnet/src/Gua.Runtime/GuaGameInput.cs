using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gua.Core;

namespace Gua.Runtime;

[Flags]
public enum GuaGameInputCapabilities : uint
{
    None = 0, Semantic = 1, Keyboard = 2, Pointer = 4, Gamepad = 8, Text = 16,
    All = Semantic | Keyboard | Pointer | Gamepad | Text,
}

public enum GuaGameInputValueType { Button = 1, Axis1D = 2, Vector2 = 3, Text = 4 }
public enum GuaGameInputKind { Semantic = 1, Keyboard = 2, Pointer = 3, Gamepad = 4, TextInput = 5, Cleanup = 6 }
public enum GuaGameInputOperation { Press = 1, Set = 2, Release = 3, Down = 4, Up = 5, MoveAbsolute = 6, MoveDelta = 7, Wheel = 8, Reset = 9, ReleaseAll = 10 }

public sealed record GuaGameInputActionDescriptor(
    string Id, string Description, GuaGameInputValueType ValueType,
    double? Minimum = null, double? Maximum = null, bool Holdable = false, bool Active = true,
    IReadOnlyList<string>? Bindings = null, string Risk = "safe", bool RequiresConfirmation = false,
    string? Category = null, IReadOnlyList<string>? Aliases = null, IReadOnlyList<string>? Tags = null,
    GuaAgentExposure AgentExposure = GuaAgentExposure.Auto)
{
    public string? ValueSchemaJson { get; init; }
    public string? ExamplesJson { get; init; }
}

public sealed record GuaGameInputActionSelector(
    string? Id = null, string? Query = null, GuaGameInputValueType? ValueType = null, bool? Active = null,
    string? Context = null, string? Category = null, IReadOnlyList<string>? Tags = null, uint Limit = 20);

public sealed record GuaGameInputAction(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("valueType")] string ValueType,
    [property: JsonPropertyName("holdable")] bool Holdable,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("requiresConfirmation")] bool RequiresConfirmation,
    [property: JsonPropertyName("range")] GuaGameInputActionRange? Range = null,
    [property: JsonPropertyName("bindings")] IReadOnlyList<string>? Bindings = null,
    [property: JsonPropertyName("risk")] string Risk = "safe",
    [property: JsonPropertyName("category")] string? Category = null,
    [property: JsonPropertyName("aliases")] IReadOnlyList<string>? Aliases = null,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags = null,
    [property: JsonPropertyName("agentExposure")] string AgentExposure = "auto")
{
    [JsonPropertyName("valueSchema")] public JsonElement? ValueSchema { get; init; }
    [JsonPropertyName("examples")] public IReadOnlyList<JsonElement>? Examples { get; init; }
}

public sealed record GuaGameInputActionRange(
    [property: JsonPropertyName("minimum")] double Minimum,
    [property: JsonPropertyName("maximum")] double Maximum);

public sealed record GuaGameInputActionSearchResult(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("sessionEpoch")] ulong SessionEpoch,
    [property: JsonPropertyName("revision")] ulong Revision,
    [property: JsonPropertyName("context")] string Context,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("actions")] IReadOnlyList<GuaGameInputAction> Actions);

public sealed record GuaGameInputRequest(
    ulong RequestId, ulong OwnerId, GuaGameInputKind Kind, GuaGameInputOperation Operation,
    string Target, string ValueJson, double X, double Y, uint LeaseMs, int DeviceIndex, bool Sensitive);

public sealed record GuaGameInputResult(
    [property: JsonPropertyName("completed")] bool Completed,
    [property: JsonPropertyName("requestId")] ulong? RequestId = null,
    [property: JsonPropertyName("succeeded")] bool? Succeeded = null,
    [property: JsonPropertyName("errorCode")] int? ErrorCode = null);

public sealed class GuaGameInputSession : IDisposable
{
    private GuaRuntime? runtime;
    internal GuaGameInputSession(GuaRuntime runtime, ulong ownerId, GuaObservationProfile profile)
    { this.runtime = runtime; OwnerId = ownerId; ObservationProfile = profile; }
    public ulong OwnerId { get; }
    public GuaObservationProfile ObservationProfile { get; }
    public ulong Send(GuaGameInputKind kind, GuaGameInputOperation operation, string target,
        object? value = null, TimeSpan? lease = null, double x = 0, double y = 0, int deviceIndex = 0,
        bool sensitive = false) => Send(kind, operation, target, value, lease, x, y, deviceIndex, sensitive, false);
    public ulong Send(GuaGameInputKind kind, GuaGameInputOperation operation, string target,
        object? value, TimeSpan? lease, double x, double y, int deviceIndex, bool sensitive, bool confirmed)
    {
        var owner = runtime ?? throw new ObjectDisposedException(nameof(GuaGameInputSession));
        return owner.EnqueueGameInput(OwnerId, ObservationProfile, kind, operation, target, value, lease, x, y, deviceIndex, sensitive, confirmed);
    }
    public string GetStateJson() => (runtime ?? throw new ObjectDisposedException(nameof(GuaGameInputSession))).GetGameInputStateJson(OwnerId);
    public GuaGameInputResult PollResult(ulong requestId) =>
        (runtime ?? throw new ObjectDisposedException(nameof(GuaGameInputSession))).GetGameInputResult(OwnerId, requestId);
    public void Dispose() { var owner = runtime; runtime = null; if (owner is not null) owner.ReleaseGameInputOwner(OwnerId); }
}

public sealed partial class GuaRuntime
{
    private unsafe delegate int CopyGameInputJsonDelegate(byte* output, int size);
    private Action? gameInputShutdown;

    public void EnableGameInput(GuaGameInputCapabilities capabilities, Action shutdown,
        GuaGameInputCapabilities playerCapabilities = GuaGameInputCapabilities.None)
    {
        ThrowIfDisposed();
        if (shutdown is null) throw new ArgumentNullException(nameof(shutdown));
        gameInputShutdown = shutdown;
        Native.gua_runtime_set_game_input_capabilities(_handle, (uint)capabilities);
        Native.gua_runtime_set_player_game_input_capabilities(_handle, (uint)playerCapabilities);
    }

    internal void ShutdownGameInputHost()
    {
        var shutdown = gameInputShutdown;
        gameInputShutdown = null;
        shutdown?.Invoke();
    }

    public void PublishGameInputActions(string context, IReadOnlyList<GuaGameInputActionDescriptor> actions)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(context)) throw new ArgumentException("Input context is required.", nameof(context));
        if (Native.gua_runtime_begin_game_input_frame(_handle, context) == 0)
            throw new InvalidOperationException("Failed to begin the game input action frame.");
        try
        {
            foreach (var action in actions)
            {
                if (action.Minimum.HasValue != action.Maximum.HasValue)
                    throw new ArgumentException($"Action '{action.Id}' must specify both range bounds.");
                if ((action.ValueSchemaJson?.Contains('\0') ?? false) || (action.ExamplesJson?.Contains('\0') ?? false) || (action.Category?.Contains('\0') ?? false) ||
                    (action.Aliases?.Any(value => value.Contains('\0')) ?? false) ||
                    (action.Tags?.Any(value => value.Contains('\0')) ?? false))
                    throw new ArgumentException($"Action '{action.Id}' category, aliases, and tags must not contain embedded NUL characters.", nameof(actions));
                var strings = new[] { action.Id, action.Description, JsonSerializer.Serialize(action.Bindings ?? []), action.Risk, action.Category, action.ValueSchemaJson, action.ExamplesJson };
                var pointers = strings.Select(value => value is null ? 0 : (nint)Marshal.StringToCoTaskMemUTF8(value)).ToArray();
                var aliases = AllocateStrings(action.Aliases ?? [], out var aliasArray);
                var tags = AllocateStrings(action.Tags ?? [], out var tagArray);
                try
                {
                    var baseAction = new Native.GameInputAction
                    {
                        StructSize = (uint)Marshal.SizeOf<Native.GameInputAction>(), Id = pointers[0], Description = pointers[1],
                        ValueType = (int)action.ValueType, Minimum = action.Minimum ?? 0, Maximum = action.Maximum ?? 0,
                        HasRange = action.Minimum.HasValue ? 1 : 0, Holdable = action.Holdable ? 1 : 0, Active = action.Active ? 1 : 0,
                        BindingsJson = pointers[2], Risk = pointers[3], RequiresConfirmation = action.RequiresConfirmation ? 1 : 0,
                    };
                    var native = new Native.GameInputActionV2 { StructSize = (uint)Marshal.SizeOf<Native.GameInputActionV2>(),
                        Base = baseAction, Category = pointers[4], Aliases = aliasArray, AliasCount = (uint)aliases.Length,
                        Tags = tagArray, TagCount = (uint)tags.Length, AgentExposure = (int)action.AgentExposure };
                    var extended = new Native.GameInputActionV3 { StructSize = (uint)Marshal.SizeOf<Native.GameInputActionV3>(),
                        Base = native, ValueSchemaJson = pointers[5], ExamplesJson = pointers[6] };
                    var registered = action.ValueSchemaJson is null && action.ExamplesJson is null
                        ? Native.gua_runtime_register_game_input_action_v2(_handle, in native)
                        : Native.gua_runtime_register_game_input_action_v3(_handle, in extended);
                    if (registered == 0)
                        throw new ArgumentException($"Invalid game input action '{action.Id}'.", nameof(actions));
                }
                finally {
                    foreach (var pointer in pointers) if (pointer != 0) Marshal.FreeCoTaskMem(pointer);
                    foreach (var pointer in aliases) Marshal.FreeCoTaskMem(pointer);
                    foreach (var pointer in tags) Marshal.FreeCoTaskMem(pointer);
                    if (aliasArray != 0) Marshal.FreeCoTaskMem(aliasArray);
                    if (tagArray != 0) Marshal.FreeCoTaskMem(tagArray);
                }
            }
            if (Native.gua_runtime_end_game_input_frame(_handle) == 0)
                throw new InvalidOperationException("Failed to commit the game input action frame.");
        }
        catch
        {
            Native.gua_runtime_abort_game_input_frame(_handle);
            throw;
        }
    }

    public GuaGameInputSession CreateGameInputSession() => CreateGameInputSession(ObservationProfile);

    public GuaGameInputSession CreateGameInputSession(GuaObservationProfile observationProfile)
    {
        ThrowIfDisposed();
        if (observationProfile is not GuaObservationProfile.Debug and not GuaObservationProfile.Player)
            throw new ArgumentOutOfRangeException(nameof(observationProfile));
        var ownerId = Native.gua_runtime_create_game_input_owner(_handle);
        if (ownerId == 0) throw new InvalidOperationException("Failed to create a game input session.");
        return new(this, ownerId, observationProfile);
    }

    public GuaGameInputCapabilities GetGameInputCapabilities(GuaObservationProfile observationProfile)
    {
        ThrowIfDisposed();
        return (GuaGameInputCapabilities)Native.gua_runtime_get_game_input_capabilities(_handle, (int)observationProfile);
    }

    internal void ReleaseGameInputOwner(ulong ownerId)
    {
        if (_handle != 0) Native.gua_runtime_release_game_input_owner(_handle, ownerId);
    }

    internal ulong EnqueueGameInput(ulong ownerId, GuaObservationProfile observationProfile,
        GuaGameInputKind kind, GuaGameInputOperation operation,
        string target, object? value, TimeSpan? lease, double x, double y, int deviceIndex, bool sensitive, bool confirmed)
    {
        ThrowIfDisposed();
        var leaseMs = lease is null ? 5000 : checked((uint)lease.Value.TotalMilliseconds);
        if (leaseMs is < 1 or > 60000) throw new ArgumentOutOfRangeException(nameof(lease));
        // Sensitive values still have to reach the host input path. The native
        // request marks them sensitive so diagnostics and recordings redact them.
        var json = JsonSerializer.Serialize(value);
        var targetPointer = Marshal.StringToCoTaskMemUTF8(target);
        var valuePointer = Marshal.StringToCoTaskMemUTF8(json);
        try
        {
            var request = new Native.GameInputRequestDescriptorV2
            {
                StructSize = (uint)Marshal.SizeOf<Native.GameInputRequestDescriptorV2>(), OwnerId = ownerId,
                Kind = (int)kind, Operation = (int)operation, Target = targetPointer, ValueJson = valuePointer,
                X = x, Y = y, LeaseMs = leaseMs, DeviceIndex = deviceIndex, Sensitive = sensitive ? 1 : 0,
                Confirmed = confirmed ? 1 : 0,
            };
            var result = Native.gua_runtime_enqueue_game_input_for_profile_v2(_handle, in request,
                (int)observationProfile, out var requestId);
            if (result != 1) throw new InvalidOperationException($"Game input request was rejected ({result}).");
            return requestId;
        }
        finally { Marshal.FreeCoTaskMem(targetPointer); Marshal.FreeCoTaskMem(valuePointer); }
    }

    public unsafe bool TryConsumeGameInput(out GuaGameInputRequest request)
    {
        ThrowIfDisposed();
        var native = new Native.GameInputRequest { StructSize = (uint)sizeof(Native.GameInputRequest) };
        if (Native.gua_runtime_consume_game_input_request(_handle, ref native) == 0) { request = default!; return false; }
        byte* target = native.Target; byte* value = native.ValueJson;
        request = new(native.RequestId, native.OwnerId, (GuaGameInputKind)native.Kind, (GuaGameInputOperation)native.Operation,
            Utf8(target, 128), Utf8(value, 512), native.X, native.Y, native.LeaseMs, native.DeviceIndex, native.Sensitive != 0);
        return true;
    }

    public void CompleteGameInput(GuaGameInputRequest request, bool succeeded, int errorCode = 0)
    {
        ThrowIfDisposed();
        if (Native.gua_runtime_complete_game_input_request(_handle, request.RequestId, succeeded ? 1 : 0, errorCode) == 0)
            throw new InvalidOperationException($"Unknown game input request {request.RequestId}.");
    }

    public int TickGameInputLeases(TimeSpan unscaledElapsed)
    {
        ThrowIfDisposed();
        return Native.gua_runtime_tick_game_input_leases(_handle, unscaledElapsed.TotalMilliseconds);
    }

    public unsafe string GetGameInputActionsJsonV2(GuaObservationProfile observationProfile = GuaObservationProfile.Debug)
    {
        int Copy(byte* output, int size) => Native.gua_runtime_copy_game_input_actions_json_v2(_handle, (int)observationProfile, output, size);
        return CopyGameInputJson(Copy);
    }
    public GuaGameInputActionSearchResult FindGameInputActionsV2(GuaGameInputActionSelector selector,
        GuaObservationProfile observationProfile = GuaObservationProfile.Debug) =>
        JsonSerializer.Deserialize<GuaGameInputActionSearchResult>(FindGameInputActionsJsonV2(selector, observationProfile))
        ?? throw new InvalidOperationException("Native game input search JSON is invalid.");
    public string FindGameInputActionsJsonV2(GuaGameInputActionSelector selector,
        GuaObservationProfile observationProfile = GuaObservationProfile.Debug) => FindGameInputActionsJsonImpl(selector, observationProfile, true);
    public unsafe string GetGameInputActionsJson() => CopyGameInputJson(0, false);
    public unsafe string GetPlayerGameInputActionsJson()
    {
        int Copy(byte* output, int size) => Native.gua_runtime_copy_player_game_input_actions_json(_handle, output, size);
        return CopyGameInputJson(Copy);
    }
    public GuaGameInputActionSearchResult FindGameInputActions(GuaGameInputActionSelector selector,
        GuaObservationProfile observationProfile = GuaObservationProfile.Debug)
    {
        var json = FindGameInputActionsJson(selector, observationProfile);
        return JsonSerializer.Deserialize<GuaGameInputActionSearchResult>(json)
            ?? throw new InvalidOperationException("Native game input search JSON is invalid.");
    }
    public string FindGameInputActionsJson(GuaGameInputActionSelector selector,
        GuaObservationProfile observationProfile = GuaObservationProfile.Debug) => FindGameInputActionsJsonImpl(selector, observationProfile, false);
    private unsafe string FindGameInputActionsJsonImpl(GuaGameInputActionSelector selector, GuaObservationProfile observationProfile, bool metadata)
    {
        ThrowIfDisposed();
        if (selector is null) throw new ArgumentNullException(nameof(selector));
        static bool ContainsNul(string? value) => value?.Contains('\0') == true;
        if (ContainsNul(selector.Id) || ContainsNul(selector.Query) || ContainsNul(selector.Context) ||
            ContainsNul(selector.Category) || (selector.Tags?.Any(ContainsNul) ?? false))
            throw new ArgumentException("Game input selectors must not contain embedded NUL characters.", nameof(selector));
        var allocations = new List<nint>();
        nint Text(string? value) { if (string.IsNullOrEmpty(value)) return 0; var pointer = (nint)Marshal.StringToCoTaskMemUTF8(value); allocations.Add(pointer); return pointer; }
        var tagPointers = AllocateStrings(selector.Tags ?? [], out var tagArray);
        try {
            var native = new Native.GameInputActionSelector { StructSize = (uint)Marshal.SizeOf<Native.GameInputActionSelector>(),
                Id = Text(selector.Id), Query = Text(selector.Query), ValueType = selector.ValueType.HasValue ? (int)selector.ValueType.Value : 0,
                Active = selector.Active.HasValue ? (selector.Active.Value ? 2 : 1) : 0, Context = Text(selector.Context),
                Category = Text(selector.Category), Tags = tagArray, TagCount = (uint)tagPointers.Length, Limit = selector.Limit };
            int Copy(byte* output, int size) => metadata
                ? Native.gua_runtime_query_game_input_actions_json_v2(_handle, in native, (int)observationProfile, output, size)
                : Native.gua_runtime_query_game_input_actions_json(_handle, in native, (int)observationProfile, output, size);
            return CopyGameInputJson(Copy);
        } finally {
            foreach (var pointer in allocations) Marshal.FreeCoTaskMem(pointer);
            foreach (var pointer in tagPointers) Marshal.FreeCoTaskMem(pointer);
            if (tagArray != 0) Marshal.FreeCoTaskMem(tagArray);
        }
    }
    internal unsafe string GetGameInputStateJson(ulong ownerId) => CopyGameInputJson(ownerId, true);
    internal unsafe GuaGameInputResult GetGameInputResult(ulong ownerId, ulong requestId)
    {
        ThrowIfDisposed();
        int Copy(byte* output, int size) => Native.gua_runtime_copy_game_input_result_json(_handle, ownerId, requestId, output, size);
        var json = CopyGameInputJson(Copy);
        return JsonSerializer.Deserialize<GuaGameInputResult>(json)
            ?? throw new InvalidOperationException("Native game input result JSON is invalid.");
    }
    private unsafe string CopyGameInputJson(ulong ownerId, bool state)
    {
        ThrowIfDisposed();
        int Copy(byte* output, int size) => state
            ? Native.gua_runtime_copy_game_input_state_json(_handle, ownerId, output, size)
            : Native.gua_runtime_copy_game_input_actions_json(_handle, output, size);
        return CopyGameInputJson(Copy);
    }
    private static unsafe string CopyGameInputJson(CopyGameInputJsonDelegate copy)
    {
        var required = copy(null, 0);
        if (required <= 0) throw new InvalidOperationException("Native game input JSON is unavailable.");
        while (true)
        {
            var bytes = new byte[required];
            fixed (byte* pointer = bytes)
            {
                var actual = copy(pointer, bytes.Length);
                if (actual <= 0) throw new InvalidOperationException("Native game input JSON is unavailable.");
                if (actual <= bytes.Length) return Encoding.UTF8.GetString(bytes, 0, actual - 1);
                required = actual;
            }
        }
    }

    private static nint[] AllocateStrings(IReadOnlyList<string> values, out nint array)
    {
        var pointers = values.Select(value => (nint)Marshal.StringToCoTaskMemUTF8(value)).ToArray();
        array = 0;
        if (pointers.Length != 0) { array = (nint)Marshal.AllocCoTaskMem(IntPtr.Size * pointers.Length); Marshal.Copy(pointers, 0, array, pointers.Length); }
        return pointers;
    }
}
