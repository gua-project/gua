using Gua.Core;
using System.Text.Json;
using System.Diagnostics;

namespace Gua.Testing;

public sealed partial record GuaLocatorQuery
{
    private readonly IGuaContext _context;
    private readonly GuaSelector _selector;
    private readonly string? _value;
    private readonly GuaMatchMode _valueMatch;
    private readonly bool? _focused;
    private readonly bool? _selected;
    private readonly bool? _checked;
    private readonly string? _action;

    internal GuaLocatorQuery(
        IGuaContext context, GuaSelector? selector = null, string? value = null,
        GuaMatchMode valueMatch = GuaMatchMode.Exact, bool? focused = null,
        bool? selected = null, bool? @checked = null, string? action = null)
    {
        _context = context;
        _selector = selector ?? new GuaSelector();
        _value = value;
        _valueMatch = valueMatch;
        _focused = focused;
        _selected = selected;
        _checked = @checked;
        _action = action;
    }

    public GuaLocatorQuery ById(string id, GuaMatchMode match = GuaMatchMode.Exact) => Next(_selector with { Id = id, IdMatch = match });
    public GuaLocatorQuery ByRole(string role, string? name = null, GuaMatchMode match = GuaMatchMode.Exact) =>
        Next(_selector with { Role = role, RoleMatch = match, Name = name, NameMatch = match });
    public GuaLocatorQuery ByText(string text, GuaMatchMode match = GuaMatchMode.Exact) => Next(_selector with { Text = text, TextMatch = match });
    public GuaLocatorQuery Within(string parentId, bool directChild = false) => Next(_selector with { ParentId = parentId, DirectChild = directChild });
    public GuaLocatorQuery WhereVisible(bool visible = true) => Next(_selector with { Visible = visible ? GuaStateFilter.True : GuaStateFilter.False });
    public GuaLocatorQuery WhereEnabled(bool enabled = true) => Next(_selector with { Enabled = enabled ? GuaStateFilter.True : GuaStateFilter.False });
    public GuaLocatorQuery ByValue(string value, GuaMatchMode match = GuaMatchMode.Exact) => Copy(value: value, valueMatch: match);
    public GuaLocatorQuery WhereFocused(bool focused = true) => Copy(focused: focused);
    public GuaLocatorQuery WhereSelected(bool selected = true) => Copy(selected: selected);
    public GuaLocatorQuery WhereChecked(bool @checked = true) => Copy(@checked: @checked);
    public GuaLocatorQuery ByAction(string action) => Copy(action: action);

    public IReadOnlyList<GuaNodeExpectation> QueryAll()
    {
        var result = Execute();
        return result.Matches.Select(match => new GuaNodeExpectation(_context, match.Id, Describe())).ToArray();
    }

    public GuaNodeExpectation Get()
    {
        var result = Execute();
        if (result.Matches.Count == 0)
            GuaAssertions.Fail(_context, $"Strict Gua selector matched no nodes: {Describe()}. Candidates: {DescribeAllCandidates()}.");
        if (result.Matches.Count > 1)
        {
            var candidates = string.Join("; ", result.Matches.Select(match =>
                $"{match.Id} ({match.Role}, '{match.Label ?? "<unknown>"}', parentId='{match.ParentId ?? "<root>"}')"));
            GuaAssertions.Fail(_context, $"Strict Gua selector matched {result.Matches.Count} nodes: {Describe()}. Candidates: {candidates}. Narrow the scope with Within(...) or add stable id/state filters.");
        }
        return new GuaNodeExpectation(_context, result.Matches[0].Id, Describe());
    }

    public GuaLocatorQuery AssertCount(int expected)
    {
        var actual = Execute().Matches.Count;
        if (actual != expected)
            GuaAssertions.Fail(_context, $"Expected selector {Describe()} to match {expected} nodes, but matched {actual}.");
        return this;
    }

    public GuaLocatorQuery WaitForCount(int expected, TimeSpan? timeout = null, TimeSpan? pollInterval = null) =>
        WaitForCount(count => count == expected, $"exactly {expected}", timeout, pollInterval);

    public GuaLocatorQuery WaitForMinimumCount(int minimum, TimeSpan? timeout = null, TimeSpan? pollInterval = null) =>
        WaitForCount(count => count >= minimum, $"at least {minimum}", timeout, pollInterval);

    public GuaLocatorQuery WaitForCount(
        Func<int, bool> condition, TimeSpan? timeout = null, TimeSpan? pollInterval = null) =>
        WaitForCount(condition, "the requested count condition", timeout, pollInterval);

    public async Task<GuaLocatorQuery> WaitForCountAsync(
        int expected, TimeSpan? timeout = null, TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default) =>
        await WaitForCountAsync(count => count == expected, $"exactly {expected}", timeout, pollInterval, cancellationToken).ConfigureAwait(false);

    public async Task<GuaLocatorQuery> WaitForMinimumCountAsync(
        int minimum, TimeSpan? timeout = null, TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default) =>
        await WaitForCountAsync(count => count >= minimum, $"at least {minimum}", timeout, pollInterval, cancellationToken).ConfigureAwait(false);

    public Task<GuaLocatorQuery> WaitForCountAsync(
        Func<int, bool> condition, TimeSpan? timeout = null, TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default) =>
        WaitForCountAsync(condition, "the requested count condition", timeout, pollInterval, cancellationToken);

    private GuaLocatorQuery WaitForCount(
        Func<int, bool> condition, string expected, TimeSpan? timeout, TimeSpan? pollInterval) =>
        WaitForCountAsync(condition, expected, timeout, pollInterval).GetAwaiter().GetResult();

    private async Task<GuaLocatorQuery> WaitForCountAsync(
        Func<int, bool> condition, string expected, TimeSpan? timeout, TimeSpan? pollInterval,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(condition, nameof(condition));
        var limit = timeout ?? TimeSpan.FromSeconds(1);
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(10);
        if (limit < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        var stopwatch = Stopwatch.StartNew();
        var lastCount = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastCount = Execute().Matches.Count;
            if (condition(lastCount)) return this;
            if (stopwatch.Elapsed >= limit) break;
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        } while (true);

        GuaAssertions.Fail(_context,
            $"Timed out after {limit:g} waiting for selector {Describe()} to match {expected}; last count={lastCount}. {GuaAssertions.DescribeSnapshot(_context)}");
        throw new InvalidOperationException("Gua assertion failure handler returned unexpectedly.");
    }

    private GuaQueryResult Execute()
    {
        GuaQueryResult result;
        try
        {
            result = _context.Query(_selector);
        }
        catch (NotSupportedException) when (CanUseLegacyFallback())
        {
            var id = _selector.Id is not null
                ? _context.FindNodeById(_selector.Id)
                : _selector.Role is not null
                    ? _context.FindNodeByRole(_selector.Role, _selector.Name)
                    : _context.FindNodeByText(_selector.Text!);
            result = new GuaQueryResult(true, [new GuaNodeQueryMatch(id, "legacy", "legacy", null)]);
        }
        if (!result.Valid)
            GuaAssertions.Fail(_context, $"Invalid Gua selector {Describe()}: {result.Error ?? "unknown syntax error"}");
        if (_value is null && _focused is null && _selected is null && _checked is null && _action is null) return result;
        var matches = result.Matches.Where(match => MatchesV2(GuaAssertions.TryGetSnapshot(_context, match.Id))).ToArray();
        return result with { Matches = matches };
    }

    private bool CanUseLegacyFallback() =>
        _selector.ParentId is null && !_selector.DirectChild &&
        _selector.Visible == GuaStateFilter.Any && _selector.Enabled == GuaStateFilter.Any &&
        _selector.IdMatch == GuaMatchMode.Exact && _selector.RoleMatch == GuaMatchMode.Exact &&
        _selector.NameMatch == GuaMatchMode.Exact && _selector.TextMatch == GuaMatchMode.Exact &&
        (_selector.Id is not null || _selector.Role is not null || _selector.Text is not null);

    private GuaLocatorQuery Next(GuaSelector selector) => new(_context, selector, _value, _valueMatch, _focused, _selected, _checked, _action);

    private GuaLocatorQuery Copy(
        string? value = null, GuaMatchMode? valueMatch = null, bool? focused = null,
        bool? selected = null, bool? @checked = null, string? action = null) =>
        new(_context, _selector, value ?? _value, valueMatch ?? _valueMatch,
            focused ?? _focused, selected ?? _selected, @checked ?? _checked, action ?? _action);

    private bool MatchesV2(GuaNodeSnapshot? node, TimeSpan? regexTimeout = null)
    {
        if (node is null) return false;
        if (_value is not null && !Matches(node.Value, _value, _valueMatch, regexTimeout)) return false;
        if (_focused is not null && node.Focused != _focused) return false;
        if (_selected is not null && node.Selected != _selected) return false;
        if (_checked is not null && node.Checked != _checked) return false;
        return _action is null || node.Actions.Contains(_action, StringComparer.Ordinal);
    }

    private static bool Matches(string? actual, string expected, GuaMatchMode mode, TimeSpan? regexTimeout = null) => mode switch
    {
        GuaMatchMode.Exact => string.Equals(actual, expected, StringComparison.Ordinal),
        GuaMatchMode.Contains => actual?.Contains(expected, StringComparison.Ordinal) == true,
        GuaMatchMode.Regex => actual is not null && System.Text.RegularExpressions.Regex.IsMatch(actual, expected,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            regexTimeout ?? System.Text.RegularExpressions.Regex.InfiniteMatchTimeout),
        _ => false,
    };

    private string Describe(bool redact = false)
    {
        var fields = new List<string>();
        if (_selector.Id is not null) fields.Add($"id={_selector.IdMatch}:{_selector.Id}");
        if (_selector.Role is not null) fields.Add($"role={_selector.RoleMatch}:{_selector.Role}");
        if (_selector.Name is not null) fields.Add($"name={_selector.NameMatch}:{(redact ? "<redacted>" : _selector.Name)}");
        if (_selector.Text is not null) fields.Add($"text={_selector.TextMatch}:{(redact ? "<redacted>" : _selector.Text)}");
        if (_selector.ParentId is not null) fields.Add($"scope={_selector.ParentId} ({(_selector.DirectChild ? "direct children" : "descendants")})");
        if (_selector.Visible != GuaStateFilter.Any) fields.Add($"visible={_selector.Visible == GuaStateFilter.True}");
        if (_selector.Enabled != GuaStateFilter.Any) fields.Add($"enabled={_selector.Enabled == GuaStateFilter.True}");
        if (_value is not null) fields.Add($"value={_valueMatch}:{(redact ? "<redacted>" : _value)}");
        if (_focused is not null) fields.Add($"focused={_focused}");
        if (_selected is not null) fields.Add($"selected={_selected}");
        if (_checked is not null) fields.Add($"checked={_checked}");
        if (_action is not null) fields.Add($"action={_action}");
        return "{" + string.Join(", ", fields) + "}";
    }

    private string DescribeAllCandidates()
    {
        try
        {
            using var document = JsonDocument.Parse(_context.GetUiTreeJson());
            if (!document.RootElement.TryGetProperty("nodes", out var nodes) || nodes.GetArrayLength() == 0) return "<none>";
            return string.Join("; ", nodes.EnumerateArray().Take(12).Select(node =>
                $"{node.GetProperty("id").GetString()} ({node.GetProperty("role").GetString()}, '{node.GetProperty("label").GetString()}', parentId='{(node.TryGetProperty("parentId", out var parent) ? parent.GetString() : "<root>")}')"));
        }
        catch (JsonException)
        {
            return "<UI tree could not be parsed>";
        }
    }
}
