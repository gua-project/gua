using System.Diagnostics;
using System.Text.Json;
using Gua.Core;

namespace Gua.Testing;

public static partial class GuaAssertions
{
    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(10);

    public static Task<GuaNodeExpectation> WaitForVisibleAsync(
        IGuaContext context, string id, TimeSpan? timeout = null, TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default) =>
        WaitForNodeAsync(context, id, node => node is { Visible: true }, "be visible", timeout, pollInterval, cancellationToken);

    public static Task<GuaNodeExpectation> WaitForHiddenAsync(
        IGuaContext context, string id, TimeSpan? timeout = null, TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default) =>
        WaitForNodeAsync(context, id, node => node is null || !node.Visible, "be hidden or removed", timeout, pollInterval, cancellationToken);

    public static Task<GuaNodeExpectation> WaitForEnabledAsync(
        IGuaContext context, string id, TimeSpan? timeout = null, TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default) =>
        WaitForNodeAsync(context, id, node => node is { Enabled: true }, "be enabled", timeout, pollInterval, cancellationToken);

    public static Task<GuaNodeExpectation> WaitForDisabledAsync(
        IGuaContext context, string id, TimeSpan? timeout = null, TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default) =>
        WaitForNodeAsync(context, id, node => node is { Enabled: false }, "be disabled", timeout, pollInterval, cancellationToken);

    public static Task<GuaNodeExpectation> WaitForTextAsync(
        IGuaContext context, string id, string expected, TimeSpan? timeout = null, TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default) =>
        WaitForNodeAsync(context, id, node => string.Equals(node?.Text, expected, StringComparison.Ordinal),
            $"have text '{expected}'", timeout, pollInterval, cancellationToken);

    public static Task<GuaNodeExpectation> WaitForValueAsync(
        IGuaContext context, string id, string expected, TimeSpan? timeout = null, TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default) =>
        WaitForNodeAsync(context, id, node => string.Equals(node?.Value, expected, StringComparison.Ordinal),
            $"have value '{expected}'", timeout, pollInterval, cancellationToken);

    public static Task<GuaNodeExpectation> WaitForFocusedAsync(
        IGuaContext context, string id, bool expected = true, TimeSpan? timeout = null, TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default) =>
        WaitForNodeAsync(context, id, node => node?.Focused == expected,
            $"have focused={expected}", timeout, pollInterval, cancellationToken);

    public static Task<GuaNodeExpectation> WaitForSelectedAsync(
        IGuaContext context, string id, bool expected = true, TimeSpan? timeout = null, TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default) =>
        WaitForNodeAsync(context, id, node => node?.Selected == expected,
            $"have selected={expected}", timeout, pollInterval, cancellationToken);

    public static Task<GuaNodeExpectation> WaitForCheckedAsync(
        IGuaContext context, string id, bool expected = true, TimeSpan? timeout = null, TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default) =>
        WaitForNodeAsync(context, id, node => node?.Checked == expected,
            $"have checked={expected}", timeout, pollInterval, cancellationToken);

    public static Task<GuaNodeExpectation> WaitForStateAsync(
        IGuaContext context, string id, Func<GuaNodeSnapshot, bool> predicate, string description = "match semantic state",
        TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(predicate, nameof(predicate));
        return WaitForNodeAsync(context, id, node => node is not null && predicate(node), description, timeout, pollInterval, cancellationToken);
    }

    public static GuaNodeExpectation WaitForVisible(IGuaContext context, string id, TimeSpan? timeout = null, TimeSpan? pollInterval = null) =>
        WaitForVisibleAsync(context, id, timeout, pollInterval).GetAwaiter().GetResult();

    public static GuaNodeExpectation WaitForHidden(IGuaContext context, string id, TimeSpan? timeout = null, TimeSpan? pollInterval = null) =>
        WaitForHiddenAsync(context, id, timeout, pollInterval).GetAwaiter().GetResult();

    public static GuaNodeExpectation WaitForEnabled(IGuaContext context, string id, TimeSpan? timeout = null, TimeSpan? pollInterval = null) =>
        WaitForEnabledAsync(context, id, timeout, pollInterval).GetAwaiter().GetResult();

    public static GuaNodeExpectation WaitForDisabled(IGuaContext context, string id, TimeSpan? timeout = null, TimeSpan? pollInterval = null) =>
        WaitForDisabledAsync(context, id, timeout, pollInterval).GetAwaiter().GetResult();

    public static GuaNodeExpectation WaitForText(IGuaContext context, string id, string expected, TimeSpan? timeout = null, TimeSpan? pollInterval = null) =>
        WaitForTextAsync(context, id, expected, timeout, pollInterval).GetAwaiter().GetResult();

    public static GuaNodeExpectation WaitForValue(IGuaContext context, string id, string expected, TimeSpan? timeout = null, TimeSpan? pollInterval = null) =>
        WaitForValueAsync(context, id, expected, timeout, pollInterval).GetAwaiter().GetResult();

    public static GuaNodeExpectation WaitForFocused(IGuaContext context, string id, bool expected = true, TimeSpan? timeout = null, TimeSpan? pollInterval = null) =>
        WaitForFocusedAsync(context, id, expected, timeout, pollInterval).GetAwaiter().GetResult();

    public static GuaNodeExpectation WaitForSelected(IGuaContext context, string id, bool expected = true, TimeSpan? timeout = null, TimeSpan? pollInterval = null) =>
        WaitForSelectedAsync(context, id, expected, timeout, pollInterval).GetAwaiter().GetResult();

    public static GuaNodeExpectation WaitForChecked(IGuaContext context, string id, bool expected = true, TimeSpan? timeout = null, TimeSpan? pollInterval = null) =>
        WaitForCheckedAsync(context, id, expected, timeout, pollInterval).GetAwaiter().GetResult();

    public static async Task WaitUntilAsync(
        Func<bool> condition, string description, TimeSpan? timeout = null, TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(condition, nameof(condition));
        Guard.NotNullOrWhiteSpace(description, nameof(description));
        var (limit, interval) = ValidateWait(timeout, pollInterval);
        var stopwatch = Stopwatch.StartNew();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition()) return;
            if (stopwatch.Elapsed >= limit) break;
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
        while (true);

        Fail($"Timed out after {limit:g} (elapsed {stopwatch.Elapsed:g}) waiting for {description}.");
    }

    public static async Task WaitForStableSnapshotAsync(
        IGuaContext context, int stableFrames = 3, TimeSpan? timeout = null, TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context, nameof(context));
        if (stableFrames <= 0) throw new ArgumentOutOfRangeException(nameof(stableFrames));
        var (limit, interval) = ValidateWait(timeout, pollInterval);
        var stopwatch = Stopwatch.StartNew();
        var initialUiTreeJson = context.GetUiTreeJson();
        ulong? sessionEpoch = null;
        ulong? lastFrame = null;
        ulong? stableRevision = null;
        var observedStableFrames = 0;
        WaitSnapshot? last = null;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = ReadWaitSnapshot(context);
            if (last.FrameSequence is { } frame && last.Revision is { } revision && frame != lastFrame)
            {
                if (last.SessionEpoch != sessionEpoch || stableRevision != revision)
                {
                    sessionEpoch = last.SessionEpoch;
                    stableRevision = revision;
                    observedStableFrames = 1;
                }
                else
                {
                    observedStableFrames++;
                }
                lastFrame = frame;
                if (observedStableFrames >= stableFrames) return;
            }

            if (stopwatch.Elapsed >= limit) break;
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
        while (true);

        Fail(context, $"Timed out after {limit:g} (elapsed {stopwatch.Elapsed:g}) waiting for {stableFrames} distinct stable semantic frames; observed {observedStableFrames}. Last frameSequence={Format(last?.FrameSequence)}, revision={Format(last?.Revision)}, sessionEpoch={Format(last?.SessionEpoch)}.", initialUiTreeJson);
    }

    public static void WaitForStableSnapshot(IGuaContext context, int stableFrames = 3, TimeSpan? timeout = null, TimeSpan? pollInterval = null) =>
        WaitForStableSnapshotAsync(context, stableFrames, timeout, pollInterval).GetAwaiter().GetResult();

    private static async Task<GuaNodeExpectation> WaitForNodeAsync(
        IGuaContext context, string id, Func<GuaNodeSnapshot?, bool> condition, string description,
        TimeSpan? timeout, TimeSpan? pollInterval, CancellationToken cancellationToken)
    {
        Guard.NotNull(context, nameof(context));
        Guard.NotNullOrWhiteSpace(id, nameof(id));
        var (limit, interval) = ValidateWait(timeout, pollInterval);
        var stopwatch = Stopwatch.StartNew();
        var initialUiTreeJson = context.GetUiTreeJson();
        WaitSnapshot? last = null;
        GuaNodeSnapshot? node = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = ReadWaitSnapshot(context);
            node = last.Nodes.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (condition(node)) return new GuaNodeExpectation(context, id, $"id '{id}'");
            if (stopwatch.Elapsed >= limit) break;
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
        while (true);

        Fail(context, $"Timed out after {limit:g} (elapsed {stopwatch.Elapsed:g}) waiting for Gua node id '{id}' to {description}. Last state: {DescribeNode(node)}; frameSequence={Format(last?.FrameSequence)}, revision={Format(last?.Revision)}.", initialUiTreeJson);
        throw new InvalidOperationException("Gua assertion failure handler returned unexpectedly.");
    }

    private static (TimeSpan Timeout, TimeSpan PollInterval) ValidateWait(TimeSpan? timeout, TimeSpan? pollInterval)
    {
        var limit = timeout ?? DefaultWaitTimeout;
        var interval = pollInterval ?? DefaultPollInterval;
        if (limit < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        return (limit, interval);
    }

    private static WaitSnapshot ReadWaitSnapshot(IGuaContext context)
    {
        using var document = JsonDocument.Parse(context.GetUiTreeJson());
        var root = document.RootElement;
        var nodes = new List<GuaNodeSnapshot>();
        if (root.TryGetProperty("nodes", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray()) nodes.Add(ReadWaitNode(element, root));
        }
        return new WaitSnapshot(RootUInt64(root, "sessionEpoch"), RootUInt64(root, "frameSequence"), RootUInt64(root, "revision"), nodes);
    }

    private static GuaNodeSnapshot ReadWaitNode(JsonElement node, JsonElement root)
    {
        var bounds = node.GetProperty("bounds");
        float? Bound(string name) => bounds.TryGetProperty(name, out var value) ? value.GetSingle() : null;
        var boundsX = Bound("x");
        var boundsY = Bound("y");
        var boundsWidth = Bound("w");
        var boundsHeight = Bound("h");
        var knownBounds = (boundsX.HasValue ? GuaBoundsKnownState.X : GuaBoundsKnownState.None) |
            (boundsY.HasValue ? GuaBoundsKnownState.Y : GuaBoundsKnownState.None) |
            (boundsWidth.HasValue ? GuaBoundsKnownState.Width : GuaBoundsKnownState.None) |
            (boundsHeight.HasValue ? GuaBoundsKnownState.Height : GuaBoundsKnownState.None);
        var actions = node.TryGetProperty("actions", out var actionValues) && actionValues.ValueKind == JsonValueKind.Array
            ? actionValues.EnumerateArray().Select(action => action.GetString() ?? string.Empty).ToArray()
            : [];
        var hasState = node.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object;
        bool? StateBoolean(string name) => hasState && state.TryGetProperty(name, out var value) ? value.GetBoolean() : null;
        long? StateInt64(string name) => hasState && state.TryGetProperty(name, out var value) ? value.GetInt64() : null;
        double? StateDouble(string name) => hasState && state.TryGetProperty(name, out var value) ? value.GetDouble() : null;
        string? NodeString(string name) => node.TryGetProperty(name, out var value) ? value.GetString() : null;
        string? NodeValue() => node.TryGetProperty("value", out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            JsonValueKind.Null => null,
            _ => throw new JsonException("Gua node property 'value' must be a scalar value."),
        } : null;
        return new GuaNodeSnapshot(
            node.GetProperty("id").GetString() ?? string.Empty,
            node.GetProperty("role").GetString() ?? string.Empty,
            NodeString("label") ?? string.Empty,
            new GuaBounds(boundsX ?? 0, boundsY ?? 0, boundsWidth ?? 0, boundsHeight ?? 0),
            node.GetProperty("visible").GetBoolean(), node.GetProperty("enabled").GetBoolean(), actions,
            NodeString("parentId"), NodeString("text"), NodeValue(), StateBoolean("focused"), StateBoolean("hovered"),
            StateBoolean("pressed"), StateBoolean("checked"), StateBoolean("selected"),
            root.TryGetProperty("schemaVersion", out var schemaVersion) ? schemaVersion.GetInt32() : null,
            RootUInt64(root, "sessionEpoch"), RootUInt64(root, "frameSequence"), RootUInt64(root, "revision"),
            StateInt64("caretPosition"), StateInt64("selectionStart"), StateInt64("selectionEnd"),
            StateDouble("scrollX"), StateDouble("scrollY"), StateDouble("scrollMaxX"), StateDouble("scrollMaxY"),
            StateDouble("rangeValue"), StateDouble("rangeMin"), StateDouble("rangeMax"), StateInt64("selectedIndex"),
            knownBounds, node.TryGetProperty("label", out _))
        {
            HasValue = node.TryGetProperty("value", out _),
        };
    }

    private static ulong? RootUInt64(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.GetUInt64() : null;
    private static string Format(ulong? value) => value?.ToString() ?? "<unknown>";
    private static string DescribeNode(GuaNodeSnapshot? node) => node is null
        ? "<removed or not found>"
        : $"visible={node.Visible}, enabled={node.Enabled}, focused={node.Focused?.ToString() ?? "<unknown>"}, selected={node.Selected?.ToString() ?? "<unknown>"}, checked={node.Checked?.ToString() ?? "<unknown>"}, text='{node.Text ?? "<unknown>"}', value='{node.Value ?? "<unknown>"}'";

    private sealed record WaitSnapshot(ulong? SessionEpoch, ulong? FrameSequence, ulong? Revision, IReadOnlyList<GuaNodeSnapshot> Nodes);
}
