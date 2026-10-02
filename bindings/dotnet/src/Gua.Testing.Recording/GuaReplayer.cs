using Gua.Core;
using Gua.Testing;

namespace Gua.Testing.Recording;

public enum GuaReplayTimingMode { PreserveDelays, PreferConditions }

public sealed class GuaReplayOptions
{
    public GuaReplayTimingMode TimingMode { get; init; } = GuaReplayTimingMode.PreferConditions;
    public bool FailOnCoordinateFallback { get; init; } = true;
    public Func<string, string?>? SecretResolver { get; init; }
    public Func<GuaCoordinateFallback, CancellationToken, Task>? CoordinateExecutor { get; init; }
    public TimeSpan? ActionTimeout { get; init; }
    public TimeSpan? PollInterval { get; init; }
}

public sealed record GuaReplayStepResult(int Index, GuaRecordedAction Action, ulong RecordedRequestId,
    GuaActionEvent? Completion);
public sealed record GuaReplayResult(IReadOnlyList<GuaReplayStepResult> Steps);

public static class GuaReplayer
{
    public static Task<GuaTimedSegmentResult> ReplayTimedSegmentAsync(IGuaTimedSegmentHost host,
        GuaTimedSegment segment, Func<string, System.Text.Json.JsonElement?>? secretResolver = null,
        CancellationToken cancellationToken = default) =>
        GuaTimedSegmentReplay.ReplayAsync(host, segment, secretResolver, cancellationToken);

    public static async Task<GuaReplayResult> ReplayAsync(IGuaContext context, GuaRecording recording,
        GuaReplayOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        GuaRecordingFile.Validate(recording);
        options ??= new();
        long previous = 0;
        var results = new List<GuaReplayStepResult>(recording.Steps.Count);
        for (var index = 0; index < recording.Steps.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = recording.Steps[index];
            if (options.TimingMode == GuaReplayTimingMode.PreferConditions && step.WaitCondition is { } wait)
                await GuaRecordingRuntime.WaitAsync(
                    context, wait, options.ActionTimeout, options.PollInterval, cancellationToken).ConfigureAwait(false);
            else if (step.RelativeMilliseconds > previous)
                await Task.Delay(TimeSpan.FromMilliseconds(step.RelativeMilliseconds - previous), cancellationToken).ConfigureAwait(false);
            previous = step.RelativeMilliseconds;

            if (step.Target is null)
            {
                if (options.FailOnCoordinateFallback)
                    throw new InvalidOperationException($"Coordinate fallback is disabled for replay step {index}.");
                if (options.CoordinateExecutor is null)
                    throw new InvalidOperationException("Coordinate replay requires CoordinateExecutor.");
                await options.CoordinateExecutor(step.CoordinateFallback!, cancellationToken).ConfigureAwait(false);
                results.Add(new(index, step.Action, step.RequestId, null));
                continue;
            }

            var value = step.Sensitive ? options.SecretResolver?.Invoke(step.SecretKey!) : step.Value;
            if (step.Sensitive && value is null)
                throw new InvalidOperationException($"A secret resolver value is required for key '{step.SecretKey}'.");
            var nodeId = GuaRecordingRuntime.Resolve(context, step.Target);
            var request = GuaRecordingRuntime.CreateRequest(step, nodeId, value);
            var completion = await GuaActionCompletion.EnqueueAndWaitAsync(
                context, request, options.ActionTimeout, options.PollInterval, cancellationToken).ConfigureAwait(false);
            results.Add(new(index, step.Action, step.RequestId, completion));
        }
        return new(results);
    }

}

internal static class GuaRecordingRuntime
{
    internal static async Task WaitAsync(IGuaContext context, string condition, TimeSpan? timeout,
        TimeSpan? pollInterval, CancellationToken cancellationToken)
    {
        var parts = condition.Split(':');
        if (parts.Length < 2) throw new InvalidDataException($"Unsupported recording wait condition: {condition}.");
        var id = Uri.UnescapeDataString(parts[1]);
        switch (parts[0])
        {
            case "visible": await GuaAssertions.WaitForVisibleAsync(context, id, timeout, pollInterval, cancellationToken); break;
            case "hidden": await GuaAssertions.WaitForHiddenAsync(context, id, timeout, pollInterval, cancellationToken); break;
            case "enabled": await GuaAssertions.WaitForEnabledAsync(context, id, timeout, pollInterval, cancellationToken); break;
            case "disabled": await GuaAssertions.WaitForDisabledAsync(context, id, timeout, pollInterval, cancellationToken); break;
            case "focused": await GuaAssertions.WaitForFocusedAsync(context, id, true, timeout, pollInterval, cancellationToken); break;
            case "unfocused": await GuaAssertions.WaitForFocusedAsync(context, id, false, timeout, pollInterval, cancellationToken); break;
            case "checked": await GuaAssertions.WaitForCheckedAsync(context, id, true, timeout, pollInterval, cancellationToken); break;
            case "unchecked": await GuaAssertions.WaitForCheckedAsync(context, id, false, timeout, pollInterval, cancellationToken); break;
            case "text" when parts.Length == 3:
                await GuaAssertions.WaitForTextAsync(context, id, Uri.UnescapeDataString(parts[2]), timeout, pollInterval, cancellationToken); break;
            case "value" when parts.Length == 3:
                await GuaAssertions.WaitForValueAsync(context, id, Uri.UnescapeDataString(parts[2]), timeout, pollInterval, cancellationToken); break;
            default: throw new InvalidDataException($"Unsupported recording wait condition: {condition}.");
        }
    }

    internal static string? Resolve(IGuaContext context, GuaRecordingTarget target)
    {
        if (target.CurrentFocus) return null;
        if (!string.IsNullOrWhiteSpace(target.Id)) return GuaAssertions.Query(context).ById(target.Id).Get().Id;
        var query = GuaAssertions.Query(context).ByRole(target.Role!, target.Name);
        if (!string.IsNullOrWhiteSpace(target.Scope)) query = query.Within(target.Scope);
        return query.Get().Id;
    }

    internal static GuaActionRequest CreateRequest(GuaRecordingStep step, string? nodeId, string? value) => step.Action switch
    {
        GuaRecordedAction.click => new(GuaActionType.Click, nodeId),
        GuaRecordedAction.focus => new(GuaActionType.Focus, nodeId),
        GuaRecordedAction.set_value => new(GuaActionType.SetValue, nodeId, value, Sensitive: step.Sensitive),
        GuaRecordedAction.set_checked => new(GuaActionType.SetChecked, nodeId, BoolValue: bool.Parse(value!)),
        GuaRecordedAction.select => new(GuaActionType.Select, nodeId, value),
        GuaRecordedAction.scroll => new(GuaActionType.Scroll, nodeId, DeltaX: step.DeltaX, DeltaY: step.DeltaY,
            ScrollUnit: step.ScrollUnit),
        GuaRecordedAction.press_key => new(GuaActionType.PressKey, nodeId, Key: value, Modifiers: step.Modifiers),
        _ => throw new InvalidOperationException($"Unsupported recorded action: {step.Action}."),
    };
}
