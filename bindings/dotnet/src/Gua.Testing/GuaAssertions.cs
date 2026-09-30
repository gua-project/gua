using System.Diagnostics;
using System.Text.Json;
using Gua.Core;

namespace Gua.Testing;

public static partial class GuaAssertions
{
    private static readonly AsyncLocal<GuaAssertionOptions?> CurrentOptions = new();

    public static GuaAssertionOptions Options
    {
        get => CurrentOptions.Value ?? GuaAssertionOptions.Default;
        set => CurrentOptions.Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static GuaNodeExpectation GetById(IGuaContext context, string id)
    {
        Guard.NotNull(context, nameof(context));
        Guard.NotNullOrWhiteSpace(id, nameof(id));
        return Query(context).ById(id).Get();
    }

    public static GuaNodeExpectation GetByRole(IGuaContext context, string role, string? name = null)
    {
        Guard.NotNull(context, nameof(context));
        Guard.NotNullOrWhiteSpace(role, nameof(role));

        return Query(context).ByRole(role, name).Get();
    }

    public static GuaNodeExpectation GetByText(IGuaContext context, string text)
    {
        Guard.NotNull(context, nameof(context));
        Guard.NotNullOrWhiteSpace(text, nameof(text));
        return Query(context).ByText(text).Get();
    }

    public static GuaLocatorQuery Query(IGuaContext context)
    {
        Guard.NotNull(context, nameof(context));
        return new GuaLocatorQuery(context);
    }

    public static GuaNodeExpectation ExpectNode(IGuaContext context, string id)
    {
        return GetById(context, id);
    }

    public static void WaitFor(GuaNodeExpectation expectation, TimeSpan? timeout = null)
    {
        Guard.NotNull(expectation, nameof(expectation));
        expectation.WaitFor(timeout);
    }

    public static GuaNodeExpectation WaitForId(IGuaContext context, string id, TimeSpan? timeout = null, TimeSpan? pollInterval = null)
    {
        return WaitForQuery(context, () => GetById(context, id).RequireExistingSnapshot(), $"id '{id}'", timeout, pollInterval);
    }

    public static GuaNodeExpectation WaitForRole(IGuaContext context, string role, string? name = null, TimeSpan? timeout = null, TimeSpan? pollInterval = null)
    {
        var description = name is null ? $"role '{role}'" : $"role '{role}' and label '{name}'";
        return WaitForQuery(context, () => GetByRole(context, role, name).RequireExistingSnapshot(), description, timeout, pollInterval);
    }

    public static GuaNodeExpectation WaitForText(IGuaContext context, string text, TimeSpan? timeout = null, TimeSpan? pollInterval = null)
    {
        return WaitForQuery(context, () => GetByText(context, text).RequireExistingSnapshot(), $"text '{text}'", timeout, pollInterval);
    }

    public static void WaitUntil(
        Func<bool> condition,
        string description,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        WaitUntilAsync(condition, description, timeout, pollInterval).GetAwaiter().GetResult();
    }

    internal static void Fail(string message)
    {
        Options.Fail(message);
        throw new GuaAssertionException(message);
    }

    internal static void Fail(IGuaContext context, string message, string? initialUiTreeJson = null)
    {
        var failure = new GuaAssertionException(message);
        var session = Options.DiagnosticsSession;
        if (session is not null)
        {
            var result = session.Capture(failure, initialUiTreeJson);
            var sessionSuffix = result.ArtifactPath is null
                ? result.CaptureErrors.Count == 0 ? string.Empty : $" Gua diagnostics capture error: {result.CaptureErrors[0].Message}"
                : $" Gua diagnostics: {result.ArtifactPath}";
            Options.Fail(message + sessionSuffix);
            throw new GuaAssertionException(message + sessionSuffix, failure);
        }
        var diagnostics = Options.Diagnostics;
        var suffix = diagnostics is null
            ? string.Empty
            : GuaDiagnosticWriter.Capture(context, message, diagnostics, initialUiTreeJson).MessageSuffix;
        Options.Fail(message + suffix);
        throw new GuaAssertionException(message + suffix);
    }

    internal static GuaNodeSnapshot? TryGetSnapshot(IGuaContext context, string id, string? uiTreeJson = null)
    {
        using var document = JsonDocument.Parse(uiTreeJson ?? context.GetUiTreeJson());
        if (!document.RootElement.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var node in nodes.EnumerateArray())
        {
            var nodeId = node.GetProperty("id").GetString();
            if (!string.Equals(nodeId, id, StringComparison.Ordinal))
            {
                continue;
            }

            var bounds = node.GetProperty("bounds");
            float? OptionalBound(string name) => bounds.TryGetProperty(name, out var coordinate) ? coordinate.GetSingle() : null;
            var boundsX = OptionalBound("x");
            var boundsY = OptionalBound("y");
            var boundsWidth = OptionalBound("w");
            var boundsHeight = OptionalBound("h");
            var knownBounds = (boundsX.HasValue ? GuaBoundsKnownState.X : GuaBoundsKnownState.None) |
                (boundsY.HasValue ? GuaBoundsKnownState.Y : GuaBoundsKnownState.None) |
                (boundsWidth.HasValue ? GuaBoundsKnownState.Width : GuaBoundsKnownState.None) |
                (boundsHeight.HasValue ? GuaBoundsKnownState.Height : GuaBoundsKnownState.None);
            var actions = new List<string>();
            if (node.TryGetProperty("actions", out var actionValues) && actionValues.ValueKind == JsonValueKind.Array)
            {
                actions.AddRange(actionValues.EnumerateArray().Select(action => action.GetString() ?? string.Empty));
            }

            JsonElement state = default;
            var hasState = node.TryGetProperty("state", out state) && state.ValueKind == JsonValueKind.Object;
            bool? OptionalBoolean(string name) => hasState && state.TryGetProperty(name, out var value)
                ? value.GetBoolean()
                : null;
            long? OptionalStateInt64(string name) => hasState && state.TryGetProperty(name, out var value) ? value.GetInt64() : null;
            double? OptionalStateDouble(string name) => hasState && state.TryGetProperty(name, out var value) ? value.GetDouble() : null;
            string? OptionalString(string name) => node.TryGetProperty(name, out var value)
                ? value.GetString()
                : null;
            string? OptionalScalar(string name) => node.TryGetProperty(name, out var value)
                ? value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                    JsonValueKind.Null => null,
                    _ => throw new JsonException($"Gua node property '{name}' must be a scalar value."),
                }
                : null;
            ulong? OptionalRootUInt64(string name) => document.RootElement.TryGetProperty(name, out var value)
                ? value.GetUInt64()
                : null;

            return new GuaNodeSnapshot(
                Id: id,
                Role: node.GetProperty("role").GetString() ?? string.Empty,
                Label: OptionalString("label") ?? string.Empty,
                Bounds: new GuaBounds(
                    boundsX ?? 0,
                    boundsY ?? 0,
                    boundsWidth ?? 0,
                    boundsHeight ?? 0),
                Visible: node.GetProperty("visible").GetBoolean(),
                Enabled: node.GetProperty("enabled").GetBoolean(),
                Actions: actions,
                ParentId: OptionalString("parentId"),
                Text: OptionalString("text"),
                Value: OptionalScalar("value"),
                Focused: OptionalBoolean("focused"),
                Hovered: OptionalBoolean("hovered"),
                Pressed: OptionalBoolean("pressed"),
                Checked: OptionalBoolean("checked"),
                Selected: OptionalBoolean("selected"),
                SchemaVersion: document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion) ? schemaVersion.GetInt32() : null,
                SessionEpoch: OptionalRootUInt64("sessionEpoch"),
                FrameSequence: OptionalRootUInt64("frameSequence"),
                Revision: OptionalRootUInt64("revision"),
                CaretPosition: OptionalStateInt64("caretPosition"), SelectionStart: OptionalStateInt64("selectionStart"), SelectionEnd: OptionalStateInt64("selectionEnd"),
                ScrollX: OptionalStateDouble("scrollX"), ScrollY: OptionalStateDouble("scrollY"), ScrollMaxX: OptionalStateDouble("scrollMaxX"), ScrollMaxY: OptionalStateDouble("scrollMaxY"),
                RangeValue: OptionalStateDouble("rangeValue"), RangeMin: OptionalStateDouble("rangeMin"), RangeMax: OptionalStateDouble("rangeMax"), SelectedIndex: OptionalStateInt64("selectedIndex"),
                KnownBounds: knownBounds, HasLabel: node.TryGetProperty("label", out _))
            {
                HasValue = node.TryGetProperty("value", out _),
            };
        }

        return null;
    }

    internal static string DescribeTree(IGuaContext context)
    {
        try
        {
            using var document = JsonDocument.Parse(context.GetUiTreeJson());
            if (!document.RootElement.TryGetProperty("nodes", out var nodes) || nodes.GetArrayLength() == 0)
            {
                return "Current Gua tree has no nodes.";
            }

            var descriptions = nodes.EnumerateArray()
                .Take(8)
                .Select(node =>
                {
                    var id = node.GetProperty("id").GetString();
                    var role = node.GetProperty("role").GetString();
                    var label = node.GetProperty("label").GetString();
                    var visible = node.GetProperty("visible").GetBoolean() ? "visible" : "hidden";
                    var enabled = node.GetProperty("enabled").GetBoolean() ? "enabled" : "disabled";
                    return $"{id} ({role}, '{label}', {visible}, {enabled})";
                });

            return "Current Gua nodes: " + string.Join("; ", descriptions) + ".";
        }
        catch (JsonException)
        {
            return "Current Gua tree JSON could not be parsed.";
        }
    }

    internal static string DescribeSnapshot(IGuaContext context)
    {
        try
        {
            using var document = JsonDocument.Parse(context.GetUiTreeJson());
            var root = document.RootElement;
            string Metadata(string name) => root.TryGetProperty(name, out var value) ? value.GetRawText() : "<unknown>";
            var screen = root.TryGetProperty("screen", out var screenValue) ? screenValue.GetString() : "<unknown>";
            return $"Last screen='{screen}', frameSequence={Metadata("frameSequence")}, revision={Metadata("revision")}. {DescribeTree(context)}";
        }
        catch (JsonException)
        {
            return "Last UI tree could not be parsed.";
        }
    }

    public static GuaActionEvent PressKey(
        IGuaContext context, string key, uint modifiers = 0, TimeSpan? timeout = null, TimeSpan? pollInterval = null) =>
        GuaActionCompletion.EnqueueAndWait(context,
            new GuaActionRequest(GuaActionType.PressKey, Key: key, Modifiers: modifiers), timeout, pollInterval);

    public static Task<GuaActionEvent> PressKeyAsync(
        IGuaContext context, string key, uint modifiers = 0, TimeSpan? timeout = null, TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default) =>
        GuaActionCompletion.EnqueueAndWaitAsync(context,
            new GuaActionRequest(GuaActionType.PressKey, Key: key, Modifiers: modifiers), timeout, pollInterval, cancellationToken);

    private static GuaNodeExpectation WaitForQuery(IGuaContext context, Func<GuaNodeExpectation> query, string description, TimeSpan? timeout, TimeSpan? pollInterval)
    {
        var initialUiTreeJson = context.GetUiTreeJson();
        var stopwatch = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(1);
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(10);
        if (limit < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        Exception? lastException = null;
        do
        {
            try
            {
                return query();
            }
            catch (Exception ex) when (ex is InvalidOperationException or GuaAssertionException)
            {
                lastException = ex;
                Thread.Sleep(interval);
            }
        }
        while (stopwatch.Elapsed < limit);

        var last = ReadWaitSnapshot(context);
        Fail(context, $"Timed out after {FormatTimeout(timeout)} (elapsed {stopwatch.Elapsed:g}) waiting for Gua node by {description}. Last frameSequence={Format(last.FrameSequence)}, revision={Format(last.Revision)}. {lastException?.Message}", initialUiTreeJson);
        throw new InvalidOperationException("Gua assertion failure handler returned unexpectedly.");
    }

    private static string FormatTimeout(TimeSpan? timeout)
    {
        return (timeout ?? TimeSpan.FromSeconds(1)).ToString("g");
    }
}

public sealed class GuaNodeExpectation
{
    private readonly IGuaContext _context;
    private readonly string _id;
    private readonly string _description;
    private GuaNodeSnapshot? _retainedSnapshot;

    internal GuaNodeExpectation(IGuaContext context, string id, string description)
    {
        _context = context;
        _id = id;
        _description = description;
    }

    public string Id => _id;

    public GuaNodeSnapshot Snapshot => GetSnapshotOrFail();

    /// <summary>Discards the retained wait snapshot and captures the node from the latest published frame.</summary>
    public GuaNodeExpectation Refresh()
    {
        _retainedSnapshot = GuaAssertions.TryGetSnapshot(_context, _id);
        if (_retainedSnapshot is null)
        {
            GuaAssertions.Fail(_context, $"Expected Gua node {_description} to exist after refresh. {GuaAssertions.DescribeTree(_context)}");
        }
        return this;
    }

    public GuaNodeExpectation ToExist()
    {
        _ = GetSnapshotOrFail();
        return this;
    }

    public GuaNodeExpectation NotToExist()
    {
        if (GuaAssertions.TryGetSnapshot(_context, _id) is not null)
        {
            GuaAssertions.Fail(_context, $"Expected Gua node {_description} not to exist. {GuaAssertions.DescribeTree(_context)}");
        }

        return this;
    }

    public GuaNodeExpectation ToBeVisible()
    {
        var snapshot = GetSnapshotOrFail();
        if (!snapshot.Visible)
        {
            GuaAssertions.Fail(_context, $"Expected Gua node {_description} to be visible, but it was hidden. {GuaAssertions.DescribeTree(_context)}");
        }

        return this;
    }

    public GuaNodeExpectation ToBeHidden()
    {
        var snapshot = GetSnapshotOrFail();
        if (snapshot.Visible)
        {
            GuaAssertions.Fail(_context, $"Expected Gua node {_description} to be hidden, but it was visible. {GuaAssertions.DescribeTree(_context)}");
        }

        return this;
    }

    public GuaNodeExpectation ToBeEnabled()
    {
        var snapshot = GetSnapshotOrFail();
        if (!snapshot.Enabled)
        {
            GuaAssertions.Fail(_context, $"Expected Gua node {_description} to be enabled, but it was disabled. {GuaAssertions.DescribeTree(_context)}");
        }

        return this;
    }

    public GuaNodeExpectation ToBeDisabled()
    {
        var snapshot = GetSnapshotOrFail();
        if (snapshot.Enabled)
        {
            GuaAssertions.Fail(_context, $"Expected Gua node {_description} to be disabled, but it was enabled. {GuaAssertions.DescribeTree(_context)}");
        }

        return this;
    }

    public GuaNodeExpectation ToHaveRole(string role)
    {
        Guard.NotNullOrWhiteSpace(role, nameof(role));
        var snapshot = GetSnapshotOrFail();
        if (!string.Equals(snapshot.Role, role, StringComparison.Ordinal))
        {
            GuaAssertions.Fail(_context, $"Expected Gua node {_description} to have role '{role}', but it had role '{snapshot.Role}'.");
        }

        return this;
    }

    public GuaNodeExpectation ToHaveLabel(string label)
    {
        Guard.NotNull(label, nameof(label));
        var snapshot = GetSnapshotOrFail();
        if (!snapshot.HasLabel || !string.Equals(snapshot.Label, label, StringComparison.Ordinal))
        {
            var actual = snapshot.HasLabel ? $"'{snapshot.Label}'" : "an unknown label";
            GuaAssertions.Fail(_context, $"Expected Gua node {_description} to have label '{label}', but it had {actual}.");
        }

        return this;
    }

    public GuaNodeExpectation ToHaveAction(string action)
    {
        Guard.NotNullOrWhiteSpace(action, nameof(action));
        var snapshot = GetSnapshotOrFail();
        if (!snapshot.Actions.Contains(action, StringComparer.Ordinal))
        {
            GuaAssertions.Fail(_context, $"Expected Gua node {_description} to expose action '{action}', but actions were [{string.Join(", ", snapshot.Actions)}].");
        }

        return this;
    }

    public GuaNodeExpectation ToBeFocused(bool expected = true) => AssertOptionalState("focused", GetSnapshotOrFail().Focused, expected);
    public GuaNodeExpectation ToBeSelected(bool expected = true) => AssertOptionalState("selected", GetSnapshotOrFail().Selected, expected);
    public GuaNodeExpectation ToBeChecked(bool expected = true) => AssertOptionalState("checked", GetSnapshotOrFail().Checked, expected);

    public GuaNodeExpectation ToHaveText(string expected)
    {
        Guard.NotNull(expected, nameof(expected));
        var actual = GetSnapshotOrFail().Text;
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            GuaAssertions.Fail(_context, $"Expected Gua node {_description} to have text '{expected}', but it had '{actual ?? "<unknown>"}'.");
        return this;
    }

    public GuaNodeExpectation ToHaveValue(string expected)
    {
        Guard.NotNull(expected, nameof(expected));
        var actual = GetSnapshotOrFail().Value;
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            GuaAssertions.Fail(_context, $"Expected Gua node {_description} to have value '{expected}', but it had '{actual ?? "<unknown>"}'.");
        return this;
    }

    public GuaNodeExpectation Click()
    {
        ToBeVisible();
        ToBeEnabled();
        ToHaveAction("click");

        if (!_context.EnqueueClick(_id))
        {
            GuaAssertions.Fail(_context,
                $"Failed to enqueue click for Gua node {_description}. Click() records a request; the game adapter or GuaTestHost must consume it and emit a click event.");
        }

        return this;
    }

    public GuaNodeExpectation ClickAndWaitForEvent(TimeSpan? timeout = null)
    {
        Click();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(1));
        do
        {
            while (_context.TryPollEvent(out var e))
            {
                if (e.Type == GuaEventType.Click && string.Equals(e.NodeId, _id, StringComparison.Ordinal))
                {
                    return this;
                }
            }

            Thread.Sleep(10);
        }
        while (DateTime.UtcNow < deadline);

        GuaAssertions.Fail(_context, $"Timed out waiting for click event from Gua node {_description}. The adapter consumed no click request for '{_id}'.");
        return this;
    }

    public ulong Focus() => Enqueue(new GuaActionRequest(GuaActionType.Focus, _id), "focus");

    public ulong SetValue(string value, bool sensitive = false) =>
        Enqueue(new GuaActionRequest(GuaActionType.SetValue, _id, Value: value, Sensitive: sensitive), "set_value");

    public ulong SetChecked(bool value) =>
        Enqueue(new GuaActionRequest(GuaActionType.SetChecked, _id, BoolValue: value), "set_checked");

    public ulong Select(string value) =>
        Enqueue(new GuaActionRequest(GuaActionType.Select, _id, Value: value), "select");

    public ulong Scroll(float deltaX, float deltaY, int unit = 0) =>
        Enqueue(new GuaActionRequest(GuaActionType.Scroll, _id, DeltaX: deltaX, DeltaY: deltaY, ScrollUnit: unit), "scroll");

    public ulong PressKey(string key, uint modifiers = 0) =>
        Enqueue(new GuaActionRequest(GuaActionType.PressKey, _id, Key: key, Modifiers: modifiers), "press_key");

    public GuaActionEvent FocusAndWait(TimeSpan? timeout = null, TimeSpan? pollInterval = null) => Complete(new(GuaActionType.Focus, _id), timeout, pollInterval);
    public GuaActionEvent ClickAndWait(TimeSpan? timeout = null, TimeSpan? pollInterval = null) => Complete(new(GuaActionType.Click, _id), timeout, pollInterval);
    public GuaActionEvent SetValueAndWait(string value, bool sensitive = false, TimeSpan? timeout = null, TimeSpan? pollInterval = null) => Complete(new(GuaActionType.SetValue, _id, Value: value, Sensitive: sensitive), timeout, pollInterval);
    public GuaActionEvent SetCheckedAndWait(bool value, TimeSpan? timeout = null, TimeSpan? pollInterval = null) => Complete(new(GuaActionType.SetChecked, _id, BoolValue: value), timeout, pollInterval);
    public GuaActionEvent SelectAndWait(string value, TimeSpan? timeout = null, TimeSpan? pollInterval = null) => Complete(new(GuaActionType.Select, _id, Value: value), timeout, pollInterval);
    public GuaActionEvent ScrollAndWait(float deltaX, float deltaY, int unit = 0, TimeSpan? timeout = null, TimeSpan? pollInterval = null) => Complete(new(GuaActionType.Scroll, _id, DeltaX: deltaX, DeltaY: deltaY, ScrollUnit: unit), timeout, pollInterval);
    public GuaActionEvent PressKeyAndWait(string key, uint modifiers = 0, TimeSpan? timeout = null, TimeSpan? pollInterval = null) => Complete(new(GuaActionType.PressKey, _id, Key: key, Modifiers: modifiers), timeout, pollInterval);

    public Task<GuaActionEvent> FocusAsync(TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) => CompleteAsync(new(GuaActionType.Focus, _id), timeout, pollInterval, cancellationToken);
    public Task<GuaActionEvent> ClickAsync(TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) => CompleteAsync(new(GuaActionType.Click, _id), timeout, pollInterval, cancellationToken);
    public Task<GuaActionEvent> SetValueAsync(string value, bool sensitive = false, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) => CompleteAsync(new(GuaActionType.SetValue, _id, Value: value, Sensitive: sensitive), timeout, pollInterval, cancellationToken);
    public Task<GuaActionEvent> SetCheckedAsync(bool value, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) => CompleteAsync(new(GuaActionType.SetChecked, _id, BoolValue: value), timeout, pollInterval, cancellationToken);
    public Task<GuaActionEvent> SelectAsync(string value, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) => CompleteAsync(new(GuaActionType.Select, _id, Value: value), timeout, pollInterval, cancellationToken);
    public Task<GuaActionEvent> ScrollAsync(float deltaX, float deltaY, int unit = 0, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) => CompleteAsync(new(GuaActionType.Scroll, _id, DeltaX: deltaX, DeltaY: deltaY, ScrollUnit: unit), timeout, pollInterval, cancellationToken);
    public Task<GuaActionEvent> PressKeyAsync(string key, uint modifiers = 0, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) => CompleteAsync(new(GuaActionType.PressKey, _id, Key: key, Modifiers: modifiers), timeout, pollInterval, cancellationToken);

    public GuaNodeExpectation WaitForAction(ulong requestId, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)((timeout ?? TimeSpan.FromSeconds(1)).TotalSeconds * Stopwatch.Frequency);
        do
        {
            if (_context.TryPollActionEvent(requestId, out var e))
            {
                if (!e.Succeeded) GuaAssertions.Fail(_context, $"Gua action {e.Action} failed for {_description}: {e.Error}.");
                return this;
            }
            Thread.Sleep(10);
        }
        while (Stopwatch.GetTimestamp() < deadline);
        GuaAssertions.Fail(_context, $"Timed out waiting for action request {requestId} on Gua node {_description}.");
        return this;
    }

    public GuaNodeExpectation WaitFor(TimeSpan? timeout = null)
    {
        return GuaAssertions.WaitForId(_context, _id, timeout);
    }

    public GuaNodeExpectation WaitUntilVisible(TimeSpan? timeout = null, TimeSpan? pollInterval = null)
    {
        return GuaAssertions.WaitForVisible(_context, _id, timeout, pollInterval);
    }

    public GuaNodeExpectation WaitUntilHidden(TimeSpan? timeout = null, TimeSpan? pollInterval = null)
    {
        return GuaAssertions.WaitForHidden(_context, _id, timeout, pollInterval);
    }

    public GuaNodeExpectation WaitUntilEnabled(TimeSpan? timeout = null, TimeSpan? pollInterval = null)
    {
        return GuaAssertions.WaitForEnabled(_context, _id, timeout, pollInterval);
    }

    public GuaNodeExpectation WaitUntilDisabled(TimeSpan? timeout = null, TimeSpan? pollInterval = null)
    {
        return GuaAssertions.WaitForDisabled(_context, _id, timeout, pollInterval);
    }

    public Task<GuaNodeExpectation> WaitUntilVisibleAsync(TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        GuaAssertions.WaitForVisibleAsync(_context, _id, timeout, pollInterval, cancellationToken);

    public Task<GuaNodeExpectation> WaitUntilHiddenAsync(TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        GuaAssertions.WaitForHiddenAsync(_context, _id, timeout, pollInterval, cancellationToken);

    public Task<GuaNodeExpectation> WaitUntilEnabledAsync(TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        GuaAssertions.WaitForEnabledAsync(_context, _id, timeout, pollInterval, cancellationToken);

    public Task<GuaNodeExpectation> WaitUntilDisabledAsync(TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        GuaAssertions.WaitForDisabledAsync(_context, _id, timeout, pollInterval, cancellationToken);

    public Task<GuaNodeExpectation> WaitForTextAsync(string expected, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        GuaAssertions.WaitForTextAsync(_context, _id, expected, timeout, pollInterval, cancellationToken);

    public Task<GuaNodeExpectation> WaitForValueAsync(string expected, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        GuaAssertions.WaitForValueAsync(_context, _id, expected, timeout, pollInterval, cancellationToken);

    public GuaNodeExpectation WaitUntilFocused(bool expected = true, TimeSpan? timeout = null, TimeSpan? pollInterval = null) =>
        GuaAssertions.WaitForFocused(_context, _id, expected, timeout, pollInterval);

    public GuaNodeExpectation WaitUntilSelected(bool expected = true, TimeSpan? timeout = null, TimeSpan? pollInterval = null) =>
        GuaAssertions.WaitForSelected(_context, _id, expected, timeout, pollInterval);

    public GuaNodeExpectation WaitUntilChecked(bool expected = true, TimeSpan? timeout = null, TimeSpan? pollInterval = null) =>
        GuaAssertions.WaitForChecked(_context, _id, expected, timeout, pollInterval);

    public Task<GuaNodeExpectation> WaitUntilFocusedAsync(bool expected = true, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        GuaAssertions.WaitForFocusedAsync(_context, _id, expected, timeout, pollInterval, cancellationToken);

    public Task<GuaNodeExpectation> WaitUntilSelectedAsync(bool expected = true, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        GuaAssertions.WaitForSelectedAsync(_context, _id, expected, timeout, pollInterval, cancellationToken);

    public Task<GuaNodeExpectation> WaitUntilCheckedAsync(bool expected = true, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        GuaAssertions.WaitForCheckedAsync(_context, _id, expected, timeout, pollInterval, cancellationToken);

    internal GuaNodeExpectation RequireExistingSnapshot()
    {
        _retainedSnapshot = GuaAssertions.TryGetSnapshot(_context, _id);
        if (_retainedSnapshot is null)
        {
            GuaAssertions.Fail(_context, $"Expected Gua node {_description} to exist. {GuaAssertions.DescribeTree(_context)}");
        }
        return this;
    }

    private GuaNodeSnapshot GetSnapshotOrFail()
    {
        var snapshot = _retainedSnapshot ?? GuaAssertions.TryGetSnapshot(_context, _id);
        if (snapshot is null)
        {
            GuaAssertions.Fail(_context, $"Expected Gua node {_description} to exist. {GuaAssertions.DescribeTree(_context)}");
            throw new InvalidOperationException("Gua assertion failure handler returned unexpectedly.");
        }

        return snapshot;
    }

    private ulong Enqueue(GuaActionRequest request, string action)
    {
        ToBeVisible();
        ToBeEnabled();
        ToHaveAction(action);
        var error = _context.EnqueueAction(request, out var requestId);
        if (error != GuaActionError.None) GuaAssertions.Fail(_context, $"Failed to enqueue {action} for Gua node {_description}: {error}.");
        return requestId;
    }

    private GuaNodeExpectation AssertOptionalState(string name, bool? actual, bool expected)
    {
        if (actual != expected)
            GuaAssertions.Fail(_context, $"Expected Gua node {_description} to have {name}={expected}, but it was {actual?.ToString() ?? "<unknown>"}.");
        return this;
    }

    private GuaActionEvent Complete(GuaActionRequest request, TimeSpan? timeout, TimeSpan? pollInterval)
    {
        return GuaActionCompletion.EnqueueAndWait(_context, request, timeout, pollInterval);
    }

    private Task<GuaActionEvent> CompleteAsync(GuaActionRequest request, TimeSpan? timeout, TimeSpan? pollInterval, CancellationToken cancellationToken)
    {
        return GuaActionCompletion.EnqueueAndWaitAsync(_context, request, timeout, pollInterval, cancellationToken);
    }
}
