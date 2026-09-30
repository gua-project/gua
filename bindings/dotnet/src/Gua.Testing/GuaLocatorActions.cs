using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gua.Core;

namespace Gua.Testing;

public sealed partial record GuaLocatorQuery
{
    public GuaNodeExpectation Resolve(TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        ResolveAsync(timeout, pollInterval, cancellationToken).GetAwaiter().GetResult();

    public async Task<GuaNodeExpectation> ResolveAsync(TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        var budget = new LocatorBudget(timeout, pollInterval);
        var node = await WaitForMatchAsync(null, budget, cancellationToken).ConfigureAwait(false);
        return new GuaNodeExpectation(_context, node.Id, Describe(true));
    }

    public GuaNodeExpectation WaitForActionable(GuaActionType action, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        WaitForActionableAsync(action, timeout, pollInterval, cancellationToken).GetAwaiter().GetResult();

    public async Task<GuaNodeExpectation> WaitForActionableAsync(GuaActionType action, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        ActionName(action);
        var budget = new LocatorBudget(timeout, pollInterval);
        var node = await WaitForMatchAsync(action, budget, cancellationToken).ConfigureAwait(false);
        return new GuaNodeExpectation(_context, node.Id, Describe(true));
    }

    public Task<GuaActionEvent> ClickAsync(TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        ActAsync(new(GuaActionType.Click), timeout, pollInterval, cancellationToken);

    public GuaActionEvent Click(TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        ClickAsync(timeout, pollInterval, cancellationToken).GetAwaiter().GetResult();

    public Task<GuaActionEvent> FocusAsync(TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        ActAsync(new(GuaActionType.Focus), timeout, pollInterval, cancellationToken);

    public GuaActionEvent Focus(TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        FocusAsync(timeout, pollInterval, cancellationToken).GetAwaiter().GetResult();

    public Task<GuaActionEvent> SetValueAsync(string value, bool sensitive = false, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        ActAsync(new(GuaActionType.SetValue, Value: value, Sensitive: sensitive), timeout, pollInterval, cancellationToken);

    public GuaActionEvent SetValue(string value, bool sensitive = false, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        SetValueAsync(value, sensitive, timeout, pollInterval, cancellationToken).GetAwaiter().GetResult();

    public Task<GuaActionEvent> SetCheckedAsync(bool value, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        ActAsync(new(GuaActionType.SetChecked, BoolValue: value), timeout, pollInterval, cancellationToken);

    public GuaActionEvent SetChecked(bool value, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        SetCheckedAsync(value, timeout, pollInterval, cancellationToken).GetAwaiter().GetResult();

    public Task<GuaActionEvent> SelectAsync(string value, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        ActAsync(new(GuaActionType.Select, Value: value), timeout, pollInterval, cancellationToken);

    public GuaActionEvent Select(string value, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        SelectAsync(value, timeout, pollInterval, cancellationToken).GetAwaiter().GetResult();

    public Task<GuaActionEvent> ScrollAsync(float deltaX, float deltaY, int unit = 0, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        ActAsync(new(GuaActionType.Scroll, DeltaX: deltaX, DeltaY: deltaY, ScrollUnit: unit), timeout, pollInterval, cancellationToken);

    public GuaActionEvent Scroll(float deltaX, float deltaY, int unit = 0, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        ScrollAsync(deltaX, deltaY, unit, timeout, pollInterval, cancellationToken).GetAwaiter().GetResult();

    public Task<GuaActionEvent> PressKeyAsync(string key, uint modifiers = 0, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        ActAsync(new(GuaActionType.PressKey, Key: key, Modifiers: modifiers), timeout, pollInterval, cancellationToken);

    public GuaActionEvent PressKey(string key, uint modifiers = 0, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default) =>
        PressKeyAsync(key, modifiers, timeout, pollInterval, cancellationToken).GetAwaiter().GetResult();

    private async Task<GuaActionEvent> ActAsync(GuaActionRequest request, TimeSpan? timeout, TimeSpan? pollInterval, CancellationToken cancellationToken)
    {
        var budget = new LocatorBudget(timeout, pollInterval);
        await WaitForMatchAsync(request.Action, budget, cancellationToken).ConfigureAwait(false);
        // Resolve again immediately before sending. Never retry once enqueue is attempted.
        var node = await WaitForMatchAsync(request.Action, budget, cancellationToken).ConfigureAwait(false);
        CheckBudget(budget, request.Action, "enqueue", cancellationToken);
        return await GuaActionCompletion.EnqueueWithBudgetAsync(_context, request with { NodeId = node.Id },
            budget.Limit, budget.Interval, budget.Clock, Describe(true), budget.LastState, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GuaNodeSnapshot> WaitForMatchAsync(GuaActionType? action, LocatorBudget budget, CancellationToken cancellationToken)
    {
        var phase = action.HasValue ? "actionability" : "resolve";
        while (true)
        {
            CheckBudget(budget, action, phase, cancellationToken);
            GuaQueryResult result;
            GuaNodeSnapshot? node = null;
            try
            {
                // Require strict query support. Never fall back to a legacy first-match lookup.
                result = _context.Query(_selector);
                CheckBudget(budget, action, phase, cancellationToken);
                if (!result.Valid)
                    GuaAssertions.Fail(_context, $"Invalid Gua selector {Describe(true)}; phase={phase}.");
                // Filter every candidate and inspect actionability from one published snapshot.
                var tree = _context.GetUiTreeJson();
                budget.Metadata = DescribeMetadataJson(tree);
                CheckBudget(budget, action, phase, cancellationToken);
                var snapshots = new List<GuaNodeSnapshot>();
                foreach (var match in result.Matches)
                {
                    CheckBudget(budget, action, phase, cancellationToken);
                    var candidate = GuaAssertions.TryGetSnapshot(_context, match.Id, tree);
                    var regexLimit = budget.Limit - budget.Clock.Elapsed;
                    regexLimit = TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(int.MaxValue - 1, regexLimit.TotalMilliseconds)));
                    if (candidate is not null && MatchesV2(candidate, regexLimit)) snapshots.Add(candidate);
                }
                result = result with { Matches = snapshots.Select(candidate =>
                    new GuaNodeQueryMatch(candidate!.Id, candidate.Role, candidate.Label, candidate.ParentId)).ToArray() };
                node = snapshots.Count == 1 ? snapshots[0] : null;
            }
            catch (RegexMatchTimeoutException)
            {
                var message = $"Gua locator regex evaluation timed out: selector={Describe(true)}, phase={phase}. {budget.Metadata}";
                if (action.HasValue)
                    throw GuaActionCompletion.Create(_context, GuaActionFailureKind.TimedOut, 0, action.Value, null, GuaActionError.None, message, snapshotMetadata: "");
                GuaAssertions.Fail(_context, message);
                throw new InvalidOperationException("Gua assertion failure handler returned unexpectedly.");
            }
            catch (ArgumentException) when (_value is not null && _valueMatch == GuaMatchMode.Regex)
            {
                // Regex exceptions contain both pattern and input. Do not retain them as an inner exception.
                GuaAssertions.Fail(_context, $"Invalid Gua value regex: selector={Describe(true)}, phase={phase}. {budget.Metadata}");
                throw new InvalidOperationException("Gua assertion failure handler returned unexpectedly.");
            }
            budget.LastState = $"count={result.Matches.Count}";
            if (result.Matches.Count > 1)
            {
                var candidates = string.Join("; ", result.Matches.Take(12).Select(match =>
                    $"{match.Id} ({match.Role}, label='<redacted>', parentId='{match.ParentId ?? "<root>"}')"));
                GuaAssertions.Fail(_context, $"Strict Gua selector matched {result.Matches.Count} nodes: {Describe(true)}; phase={phase}. Candidates: {candidates}. Narrow the scope with Within(...) or add stable id/state filters. {budget.Metadata}");
            }
            if (result.Matches.Count == 1)
            {
                budget.LastState = node is null ? "node disappeared or locator predicates changed" :
                    $"nodeId='{node.Id}', visible={node.Visible}, enabled={node.Enabled}, actionPublished={(!action.HasValue || node.Actions.Contains(ActionName(action.Value), StringComparer.Ordinal))}, sessionEpoch={node.SessionEpoch}, frameSequence={node.FrameSequence}, revision={node.Revision}";
                CheckBudget(budget, action, phase, cancellationToken);
                if (node is not null && (!action.HasValue ||
                    (node.Visible && node.Enabled && node.Actions.Contains(ActionName(action.Value), StringComparer.Ordinal))))
                    return node;
            }
            CheckBudget(budget, action, phase, cancellationToken);
            var remaining = budget.Limit - budget.Clock.Elapsed;
            await DelayAsync(remaining < budget.Interval ? remaining : budget.Interval, budget, action, phase, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DelayAsync(TimeSpan delay, LocatorBudget budget, GuaActionType? action, string phase, CancellationToken token)
    {
        try { await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { CheckBudget(budget, action, phase, token); throw; }
    }

    private void CheckBudget(LocatorBudget budget, GuaActionType? action, string phase, CancellationToken token)
    {
        if (!token.IsCancellationRequested && budget.Clock.Elapsed < budget.Limit) return;
        var message = $"Gua locator {(token.IsCancellationRequested ? "cancelled" : "timed out")}: selector={Describe(true)}, phase={phase}, last state: {budget.LastState}. {budget.Metadata}";
        if (action.HasValue)
            throw GuaActionCompletion.Create(_context, token.IsCancellationRequested ? GuaActionFailureKind.Cancelled : GuaActionFailureKind.TimedOut,
                0, action.Value, null, GuaActionError.None, message, snapshotMetadata: "");
        if (token.IsCancellationRequested) throw new OperationCanceledException(message, token);
        GuaAssertions.Fail(_context, message);
    }

    private static string DescribeMetadataJson(string tree)
    {
        try
        {
            using var document = JsonDocument.Parse(tree);
            string Number(string name) => document.RootElement.TryGetProperty(name, out var value) && value.TryGetUInt64(out var number)
                ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "<unknown>";
            return $"sessionEpoch={Number("sessionEpoch")}, frameSequence={Number("frameSequence")}, revision={Number("revision")}.";
        }
        catch { return "Snapshot metadata unavailable."; }
    }

    private static string ActionName(GuaActionType action) => action switch
    {
        GuaActionType.Click => "click",
        GuaActionType.Focus => "focus",
        GuaActionType.SetValue => "set_value",
        GuaActionType.SetChecked => "set_checked",
        GuaActionType.Select => "select",
        GuaActionType.Scroll => "scroll",
        GuaActionType.PressKey => "press_key",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private sealed class LocatorBudget
    {
        internal LocatorBudget(TimeSpan? timeout, TimeSpan? pollInterval)
        {
            Limit = timeout ?? GuaActionCompletion.DefaultTimeout;
            Interval = pollInterval ?? GuaActionCompletion.DefaultPollInterval;
            if (Limit < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            if (Interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }
        internal TimeSpan Limit { get; }
        internal TimeSpan Interval { get; }
        internal Stopwatch Clock { get; } = Stopwatch.StartNew();
        internal string LastState { get; set; } = "not observed";
        internal string Metadata { get; set; } = "sessionEpoch=<unknown>, frameSequence=<unknown>, revision=<unknown>.";
    }
}
