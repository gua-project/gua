using Gua.Core;

namespace Gua.Testing;

[Flags]
public enum GuaBoundsKnownState
{
    None = 0,
    X = 1 << 0,
    Y = 1 << 1,
    Width = 1 << 2,
    Height = 1 << 3,
    All = X | Y | Width | Height,
}

public sealed record GuaNodeSnapshot(
    string Id,
    string Role,
    string Label,
    GuaBounds Bounds,
    bool Visible,
    bool Enabled,
    IReadOnlyList<string> Actions,
    string? ParentId = null,
    string? Text = null,
    string? Value = null,
    bool? Focused = null,
    bool? Hovered = null,
    bool? Pressed = null,
    bool? Checked = null,
    bool? Selected = null,
    int? SchemaVersion = null,
    ulong? SessionEpoch = null,
    ulong? FrameSequence = null,
    ulong? Revision = null,
    long? CaretPosition = null,
    long? SelectionStart = null,
    long? SelectionEnd = null,
    double? ScrollX = null,
    double? ScrollY = null,
    double? ScrollMaxX = null,
    double? ScrollMaxY = null,
    double? RangeValue = null,
    double? RangeMin = null,
    double? RangeMax = null,
    long? SelectedIndex = null,
    GuaBoundsKnownState KnownBounds = GuaBoundsKnownState.All,
    bool HasLabel = true)
{
    /// <summary>Whether the tree published value, including an explicit null.</summary>
    public bool HasValue { get; init; } = Value is not null;
}
