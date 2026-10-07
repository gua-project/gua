namespace Gua.Core;

public enum GuaActionType
{
    Click = 1,
    Focus = 2,
    SetValue = 3,
    SetChecked = 4,
    Select = 5,
    Scroll = 6,
    PressKey = 7,
}

public enum GuaActionError
{
    None = 0,
    InvalidArgument = -1,
    NodeNotFound = -2,
    Hidden = -3,
    Disabled = -4,
    Unsupported = -5,
    InvalidValue = -6,
    StaleGuard = -7,
    Cancelled = -8,
}

public enum GuaActionCancelResult
{
    InFlight = -1,
    NotFound = 0,
    Cancelled = 1,
}

public readonly record struct GuaActionRequest(
    GuaActionType Action,
    string? NodeId = null,
    string? Value = null,
    float DeltaX = 0,
    float DeltaY = 0,
    bool BoolValue = false,
    string? Key = null,
    uint Modifiers = 0,
    bool Sensitive = false,
    int ScrollUnit = 0,
    ulong RequestId = 0,
    GuaObservationProfile ObservationProfile = GuaObservationProfile.Debug);

public readonly record struct GuaActionEvent(
    ulong RequestId,
    GuaActionType Action,
    bool Succeeded,
    GuaActionError Error,
    string NodeId,
    string Value,
    bool Sensitive,
    ulong? SessionEpoch = null,
    ulong? FrameSequence = null,
    ulong? Revision = null);
