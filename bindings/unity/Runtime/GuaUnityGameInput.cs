using System;
using System.Collections.Generic;
using System.Text.Json;
using Gua.Runtime;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

namespace Gua.Unity
{

public sealed partial class GuaUnityRuntime
{
    private readonly struct OwnedSemanticValue
    {
        public OwnedSemanticValue(object? value, long sequence, bool sensitive) { Value = value; Sequence = sequence; Sensitive = sensitive; }
        public object? Value { get; }
        public long Sequence { get; }
        public bool Sensitive { get; }
    }

    private readonly Dictionary<string, object?> gameInputValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> gameInputValueSensitivity = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<ulong, OwnedSemanticValue>> semanticValuesByOwner = new(StringComparer.Ordinal);
    private long gameInputSequence;
    private GuaGameInputCapabilities gameInputCapabilities;
    private GuaGameInputCapabilities playerGameInputCapabilities;
    private GuaGameInputMap? publishedGameInputMap;
    private bool hasPublishedGameInputMap;
    private bool gameInputMapRefreshPending = true;
    public static event Action<string, object?>? GameInputChanged;

#if ENABLE_INPUT_SYSTEM
    private Keyboard? virtualKeyboard;
    private Mouse? virtualMouse;
    private Vector2 syntheticPointerPosition;
    private readonly Gamepad?[] virtualGamepads = new Gamepad?[4];
    private readonly Dictionary<ButtonControl, HashSet<ulong>> buttonOwners = new();
    private readonly Dictionary<AxisControl, Dictionary<ulong, (float Value, long Sequence)>> axisValuesByOwner = new();
#endif

    public static object? GetGameInputValue(string actionId, object? fallback = null) =>
        activeRuntime != null && activeRuntime.gameInputValues.TryGetValue(actionId, out var value) ? value : fallback;

    private void InitializeGameInput()
    {
        if (runtime == null) return;
        var map = FindGameInputMapInActiveScene();
        if (hasPublishedGameInputMap && ReferenceEquals(map, publishedGameInputMap)) { gameInputMapRefreshPending = false; return; }
#if GUA_REMOTE_AUTOMATION
        ReleaseWebGameInputSession();
#endif
        DisposeGameInput();
        if (map == null)
        {
            runtime.PublishGameInputActions("unavailable", Array.Empty<GuaGameInputActionDescriptor>());
            runtime.EnableGameInput(GuaGameInputCapabilities.None, DisposeGameInput);
            publishedGameInputMap = null;
            hasPublishedGameInputMap = true;
            gameInputMapRefreshPending = false;
            return;
        }
        runtime.PublishGameInputActions(map.Context, map.Descriptors());
        var capabilities = GuaGameInputCapabilities.Semantic;
#if ENABLE_INPUT_SYSTEM
        if (map.EnableRawInput)
        {
            virtualKeyboard ??= InputSystem.AddDevice<Keyboard>();
            virtualMouse ??= InputSystem.AddDevice<Mouse>();
            for (var index = 0; index < virtualGamepads.Length; index++)
                virtualGamepads[index] ??= InputSystem.AddDevice<Gamepad>();
            capabilities |= GuaGameInputCapabilities.Keyboard | GuaGameInputCapabilities.Pointer |
                GuaGameInputCapabilities.Gamepad | GuaGameInputCapabilities.Text;
        }
#endif
        var playerCapabilities = map.AllowPlayerAgentSemanticInput
            ? GuaGameInputCapabilities.Semantic : GuaGameInputCapabilities.None;
        if (map.AllowPlayerAgentRawInput)
            playerCapabilities |= capabilities & (GuaGameInputCapabilities.Keyboard | GuaGameInputCapabilities.Pointer |
                GuaGameInputCapabilities.Gamepad | GuaGameInputCapabilities.Text);
        runtime.EnableGameInput(capabilities, DisposeGameInput, playerCapabilities);
        gameInputCapabilities = capabilities;
        playerGameInputCapabilities = playerCapabilities;
        publishedGameInputMap = map;
        hasPublishedGameInputMap = true;
        gameInputMapRefreshPending = false;
    }

    private static GuaGameInputMap? FindGameInputMapInActiveScene()
    {
        var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (!activeScene.IsValid() || !activeScene.isLoaded) return null;
        foreach (var root in activeScene.GetRootGameObjects())
        {
            var maps = root.GetComponentsInChildren<GuaGameInputMap>(true);
            if (maps.Length > 0) return maps[0];
        }
        return null;
    }

    private void PumpGameInput()
    {
        if (runtime == null) return;
        runtime.TickGameInputLeases(TimeSpan.FromSeconds(Time.unscaledDeltaTime));
        while (runtime.TryConsumeGameInput(out var request))
        {
            try
            {
                var succeeded = ApplyGameInput(request);
                runtime.CompleteGameInput(request, succeeded, succeeded ? 0 : -4);
            }
            catch (Exception error)
            {
                Debug.LogError($"Gua game input {request.RequestId} failed: {error.Message}");
                runtime.CompleteGameInput(request, false, -6);
            }
        }
#if ENABLE_INPUT_SYSTEM
        InputSystem.Update();
#endif
    }

    private bool ApplyGameInput(GuaGameInputRequest request)
    {
        if (request.Kind == GuaGameInputKind.Cleanup || request.Operation == GuaGameInputOperation.ReleaseAll)
        {
            ReleaseOwnerInjectedInput(request.OwnerId);
            return true;
        }
        if (request.Kind == GuaGameInputKind.Semantic)
        {
            if (request.Operation == GuaGameInputOperation.Press)
            {
                PulseSemantic(request.Target, true, request.Sensitive);
                return true;
            }
            if (request.Operation == GuaGameInputOperation.Release)
            {
                ReleaseSemantic(request.OwnerId, request.Target);
                return true;
            }
            if (request.Operation != GuaGameInputOperation.Set) return false;
            using var document = JsonDocument.Parse(request.ValueJson);
            var root = document.RootElement;
            object? value = root.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => root.GetDouble(),
                JsonValueKind.String => root.GetString(),
                JsonValueKind.Object when root.TryGetProperty("x", out var x) && root.TryGetProperty("y", out var y) =>
                    new Vector2(x.GetSingle(), y.GetSingle()),
                _ => null,
            };
            if (value is string) PulseSemantic(request.Target, value, request.Sensitive);
            else SetSemantic(request.OwnerId, request.Target, value, request.Sensitive);
            return value != null;
        }
#if ENABLE_INPUT_SYSTEM
        if (request.Kind == GuaGameInputKind.Keyboard && virtualKeyboard != null)
        {
            if (!TryKey(request.Target, out var key)) return false;
            var control = virtualKeyboard[key];
            if (request.Operation == GuaGameInputOperation.Press)
            {
                var restorePressed = ButtonIsHeld(control);
                if (restorePressed)
                {
                    InputSystem.QueueDeltaStateEvent(control, 0f);
                    InputSystem.Update();
                }
                InputSystem.QueueDeltaStateEvent(control, 1f);
                InputSystem.Update();
                InputSystem.QueueDeltaStateEvent(control, 0f);
                InputSystem.Update();
                if (restorePressed)
                {
                    InputSystem.QueueDeltaStateEvent(control, 1f);
                    InputSystem.Update();
                }
            }
            else
            {
                var pressed = request.Operation == GuaGameInputOperation.Down;
                SetButtonOwner(control, request.OwnerId, pressed);
            }
            return true;
        }
        if (request.Kind == GuaGameInputKind.Pointer && virtualMouse != null)
        {
            if (request.Operation is GuaGameInputOperation.MoveAbsolute or GuaGameInputOperation.MoveDelta)
            {
                var position = new Vector2((float)request.X, (float)request.Y);
                if (request.Operation == GuaGameInputOperation.MoveAbsolute && request.Target.EndsWith("viewport_normalized", StringComparison.Ordinal))
                    position = Vector2.Scale(position, new Vector2(Screen.width, Screen.height));
                if (request.Operation == GuaGameInputOperation.MoveAbsolute)
                {
                    position.y = Screen.height - position.y;
                    syntheticPointerPosition = position;
                    InputSystem.QueueDeltaStateEvent(virtualMouse.position, syntheticPointerPosition);
                }
                else
                {
                    position.y = -position.y;
                    syntheticPointerPosition += position;
                    syntheticPointerPosition.x = Mathf.Clamp(syntheticPointerPosition.x, 0f, Screen.width);
                    syntheticPointerPosition.y = Mathf.Clamp(syntheticPointerPosition.y, 0f, Screen.height);
                    InputSystem.QueueDeltaStateEvent(virtualMouse.delta, position);
                    InputSystem.QueueDeltaStateEvent(virtualMouse.position, syntheticPointerPosition);
                }
                return true;
            }
            if (request.Operation == GuaGameInputOperation.Wheel)
            {
                InputSystem.QueueDeltaStateEvent(virtualMouse.scroll, new Vector2((float)request.X, (float)request.Y));
                return true;
            }
            var button = PointerButton(request.Target);
            if (button == null) return false;
            var pressed = request.Operation == GuaGameInputOperation.Down;
            SetButtonOwner(button, request.OwnerId, pressed);
            return true;
        }
        if (request.Kind == GuaGameInputKind.Gamepad && request.DeviceIndex >= 0 && request.DeviceIndex < virtualGamepads.Length &&
            virtualGamepads[request.DeviceIndex] is { } gamepad)
        {
            if (request.Operation == GuaGameInputOperation.Reset) { ReleaseOwnerGamepad(request.OwnerId, gamepad); return true; }
            if (request.Operation == GuaGameInputOperation.Set)
            {
                var axis = GamepadAxis(gamepad, request.Target);
                if (axis == null) return false;
                using var value = JsonDocument.Parse(request.ValueJson);
                SetAxisOwner(axis, request.OwnerId, value.RootElement.GetSingle());
                return true;
            }
            if (request.Operation == GuaGameInputOperation.Release)
            {
                var released = false;
                var releasedButton = GamepadButton(gamepad, request.Target);
                if (releasedButton != null)
                {
                    SetButtonOwner(releasedButton, request.OwnerId, false);
                    released = true;
                }
                var axis = GamepadAxis(gamepad, request.Target);
                if (axis != null)
                {
                    ReleaseAxisOwner(axis, request.OwnerId);
                    released = true;
                }
                return released;
            }
            var button = GamepadButton(gamepad, request.Target);
            if (button == null) return false;
            var pressed = request.Operation == GuaGameInputOperation.Down;
            SetButtonOwner(button, request.OwnerId, pressed);
            return true;
        }
        if (request.Kind == GuaGameInputKind.TextInput && virtualKeyboard != null)
        {
            using var text = JsonDocument.Parse(request.ValueJson);
            foreach (var character in text.RootElement.GetString() ?? "") InputSystem.QueueTextEvent(virtualKeyboard, character);
            return true;
        }
#endif
        return false;
    }

    private static object? NeutralSemantic(object? value) => value is Vector2 ? Vector2.zero : value is double or float ? 0d : value is string ? "" : false;

    private void PublishSemantic(string actionId, object? value, bool sensitive = false)
    {
        gameInputValues[actionId] = value;
        gameInputValueSensitivity[actionId] = sensitive;
        var subscribers = GameInputChanged;
        if (subscribers == null) return;
        foreach (var subscriber in subscribers.GetInvocationList())
        {
            try { ((Action<string, object?>)subscriber)(actionId, value); }
            catch (Exception error)
            {
                Debug.LogError(sensitive
                    ? $"Gua GameInputChanged subscriber failed for sensitive action '{actionId}'; details were [REDACTED]."
                    : $"Gua GameInputChanged subscriber failed for '{actionId}': {error.Message}");
            }
        }
    }

    private void PulseSemantic(string actionId, object? value, bool sensitive)
    {
        gameInputValues.TryGetValue(actionId, out var previous);
        var previousSensitive = gameInputValueSensitivity.TryGetValue(actionId, out var wasSensitive) && wasSensitive;
        PublishSemantic(actionId, value, sensitive);
        PublishSemantic(actionId, previous ?? NeutralSemantic(value), previous != null && previousSensitive);
    }

    private void SetSemantic(ulong ownerId, string actionId, object? value, bool sensitive)
    {
        if (!semanticValuesByOwner.TryGetValue(actionId, out var owners))
            semanticValuesByOwner[actionId] = owners = new();
        owners[ownerId] = new OwnedSemanticValue(value, ++gameInputSequence, sensitive);
        PublishSemantic(actionId, value, sensitive);
    }

    private void ReleaseSemantic(ulong ownerId, string actionId)
    {
        if (!semanticValuesByOwner.TryGetValue(actionId, out var owners)) return;
        owners.Remove(ownerId);
        object? value = null;
        var sensitive = false;
        long sequence = long.MinValue;
        foreach (var owned in owners.Values)
            if (owned.Sequence > sequence) { value = owned.Value; sensitive = owned.Sensitive; sequence = owned.Sequence; }
        if (owners.Count == 0) semanticValuesByOwner.Remove(actionId);
        PublishSemantic(actionId, sequence == long.MinValue
            ? NeutralSemantic(gameInputValues.TryGetValue(actionId, out var current) ? current : null)
            : value, sequence != long.MinValue && sensitive);
    }

    private void DisposeGameInput()
    {
        gameInputCapabilities = GuaGameInputCapabilities.None;
        playerGameInputCapabilities = GuaGameInputCapabilities.None;
        ReleaseAllInjectedInput();
        gameInputValues.Clear();
        gameInputValueSensitivity.Clear();
#if ENABLE_INPUT_SYSTEM
        if (virtualKeyboard != null) InputSystem.RemoveDevice(virtualKeyboard);
        if (virtualMouse != null) InputSystem.RemoveDevice(virtualMouse);
        foreach (var gamepad in virtualGamepads)
            if (gamepad != null) InputSystem.RemoveDevice(gamepad);
        virtualKeyboard = null; virtualMouse = null; syntheticPointerPosition = Vector2.zero;
        Array.Clear(virtualGamepads, 0, virtualGamepads.Length);
#endif
    }

    private void ReleaseAllInjectedInput()
    {
        semanticValuesByOwner.Clear();
        foreach (var action in new List<string>(gameInputValues.Keys)) PublishSemantic(action, NeutralSemantic(gameInputValues[action]));
#if ENABLE_INPUT_SYSTEM
        foreach (var button in buttonOwners.Keys) InputSystem.QueueDeltaStateEvent(button, 0f);
        foreach (var axis in axisValuesByOwner.Keys) InputSystem.QueueDeltaStateEvent(axis, 0f);
        buttonOwners.Clear(); axisValuesByOwner.Clear();
        InputSystem.Update();
#endif
    }

    private void ReleaseOwnerInjectedInput(ulong ownerId)
    {
        foreach (var action in new List<string>(semanticValuesByOwner.Keys)) ReleaseSemantic(ownerId, action);
#if ENABLE_INPUT_SYSTEM
        foreach (var button in new List<ButtonControl>(buttonOwners.Keys)) SetButtonOwner(button, ownerId, false);
        foreach (var axis in new List<AxisControl>(axisValuesByOwner.Keys)) ReleaseAxisOwner(axis, ownerId);
#endif
    }

#if ENABLE_INPUT_SYSTEM
    private static readonly IReadOnlyDictionary<string, Key> W3cNamedKeys = new Dictionary<string, Key>(StringComparer.Ordinal)
    {
        ["Backquote"] = Key.Backquote, ["Backslash"] = Key.Backslash, ["Backspace"] = Key.Backspace,
        ["BracketLeft"] = Key.LeftBracket, ["BracketRight"] = Key.RightBracket, ["CapsLock"] = Key.CapsLock,
        ["Comma"] = Key.Comma, ["ContextMenu"] = Key.ContextMenu, ["Delete"] = Key.Delete, ["End"] = Key.End,
        ["Enter"] = Key.Enter, ["Equal"] = Key.Equals, ["Escape"] = Key.Escape, ["Home"] = Key.Home,
        ["Insert"] = Key.Insert, ["MetaLeft"] = Key.LeftMeta, ["MetaRight"] = Key.RightMeta, ["Minus"] = Key.Minus,
        ["NumLock"] = Key.NumLock, ["PageDown"] = Key.PageDown, ["PageUp"] = Key.PageUp, ["Pause"] = Key.Pause,
        ["Period"] = Key.Period, ["Quote"] = Key.Quote, ["ScrollLock"] = Key.ScrollLock,
        ["Semicolon"] = Key.Semicolon, ["ShiftLeft"] = Key.LeftShift, ["ShiftRight"] = Key.RightShift,
        ["Slash"] = Key.Slash, ["Space"] = Key.Space, ["Tab"] = Key.Tab,
        ["ControlLeft"] = Key.LeftCtrl, ["ControlRight"] = Key.RightCtrl,
        ["AltLeft"] = Key.LeftAlt, ["AltRight"] = Key.RightAlt,
        ["ArrowDown"] = Key.DownArrow, ["ArrowLeft"] = Key.LeftArrow,
        ["ArrowRight"] = Key.RightArrow, ["ArrowUp"] = Key.UpArrow,
        ["PrintScreen"] = Key.PrintScreen, ["NumpadAdd"] = Key.NumpadPlus,
        ["NumpadDecimal"] = Key.NumpadPeriod, ["NumpadDivide"] = Key.NumpadDivide,
        ["NumpadEnter"] = Key.NumpadEnter, ["NumpadMultiply"] = Key.NumpadMultiply,
        ["NumpadSubtract"] = Key.NumpadMinus,
    };

    private static bool TryKey(string code, out Key key)
    {
        if (W3cNamedKeys.TryGetValue(code, out key)) return true;
        if (code.StartsWith("Key", StringComparison.Ordinal) && code.Length == 4)
            return Enum.TryParse(code.Substring(3), true, out key);
        if (code.StartsWith("Digit", StringComparison.Ordinal) && code.Length == 6)
            return Enum.TryParse(code, true, out key);
        if ((code.StartsWith("F", StringComparison.Ordinal) || code.StartsWith("Numpad", StringComparison.Ordinal)) &&
            Enum.TryParse(code, true, out key) && key != Key.None) return true;
        key = Key.None;
        return false;
    }
    private ButtonControl? PointerButton(string name) => name switch
    {
        "primary" => virtualMouse?.leftButton, "secondary" => virtualMouse?.rightButton,
        "auxiliary" => virtualMouse?.middleButton, "back" => virtualMouse?.backButton,
        "forward" => virtualMouse?.forwardButton, _ => null,
    };
    private static AxisControl? GamepadAxis(Gamepad gamepad, string name) => name switch
    {
        "left_stick_x" => gamepad.leftStick.x, "left_stick_y" => gamepad.leftStick.y,
        "right_stick_x" => gamepad.rightStick.x, "right_stick_y" => gamepad.rightStick.y, _ => null,
    };
    private static ButtonControl? GamepadButton(Gamepad gamepad, string name) => name switch
    {
        "south" => gamepad.buttonSouth, "east" => gamepad.buttonEast,
        "west" => gamepad.buttonWest, "north" => gamepad.buttonNorth,
        "left_shoulder" => gamepad.leftShoulder, "right_shoulder" => gamepad.rightShoulder,
        "left_trigger" => gamepad.leftTrigger, "right_trigger" => gamepad.rightTrigger,
        "back" => gamepad.selectButton, "start" => gamepad.startButton,
        "left_stick" => gamepad.leftStickButton, "right_stick" => gamepad.rightStickButton,
        "dpad_up" => gamepad.dpad.up, "dpad_down" => gamepad.dpad.down,
        "dpad_left" => gamepad.dpad.left, "dpad_right" => gamepad.dpad.right, _ => null,
    };

    private bool ButtonIsHeld(ButtonControl button) => buttonOwners.TryGetValue(button, out var owners) && owners.Count != 0;

    private void SetButtonOwner(ButtonControl button, ulong ownerId, bool pressed)
    {
        if (!buttonOwners.TryGetValue(button, out var owners)) buttonOwners[button] = owners = new();
        if (pressed) owners.Add(ownerId); else owners.Remove(ownerId);
        if (owners.Count == 0) buttonOwners.Remove(button);
        InputSystem.QueueDeltaStateEvent(button, owners.Count == 0 ? 0f : 1f);
    }

    private void SetAxisOwner(AxisControl axis, ulong ownerId, float value)
    {
        if (!axisValuesByOwner.TryGetValue(axis, out var owners)) axisValuesByOwner[axis] = owners = new();
        owners[ownerId] = (value, ++gameInputSequence);
        InputSystem.QueueDeltaStateEvent(axis, value);
    }

    private void ReleaseAxisOwner(AxisControl axis, ulong ownerId)
    {
        if (!axisValuesByOwner.TryGetValue(axis, out var owners)) return;
        owners.Remove(ownerId);
        var value = 0f;
        var sequence = long.MinValue;
        foreach (var owned in owners.Values)
            if (owned.Sequence > sequence) { value = owned.Value; sequence = owned.Sequence; }
        if (owners.Count == 0) axisValuesByOwner.Remove(axis);
        InputSystem.QueueDeltaStateEvent(axis, value);
    }

    private void ReleaseOwnerGamepad(ulong ownerId, Gamepad gamepad)
    {
        foreach (var button in new List<ButtonControl>(buttonOwners.Keys))
            if (button.device == gamepad) SetButtonOwner(button, ownerId, false);
        foreach (var axis in new List<AxisControl>(axisValuesByOwner.Keys))
            if (axis.device == gamepad) ReleaseAxisOwner(axis, ownerId);
    }
#endif
}

}
