export interface GuaToolDefinition<Name extends string = string> {
  name: Name;
  description: string;
  inputSchema: Record<string, unknown>;
}

export const guaWebMcpToolNames = [
  "get_ui_tree",
  "click_node",
  "focus_node",
  "set_value",
  "set_checked",
  "select",
  "scroll",
  "press_key",
  "wait_for_node",
  "get_screenshot",
] as const;

export type GuaWebMcpToolName = (typeof guaWebMcpToolNames)[number];

export const guaGameInputToolNames = [
  "get_game_input_actions_v2", "find_game_input_actions_v2", "get_game_input_actions", "find_game_input_actions", "press_game_input_action", "set_game_input_action", "release_game_input_action",
  "get_game_input_state", "release_all_game_inputs", "key_down", "key_up", "press_physical_key", "pointer_move",
  "pointer_button_down", "pointer_button_up", "pointer_wheel", "gamepad_button_down", "gamepad_button_up",
  "set_gamepad_axis", "reset_gamepad", "text_input",
] as const;

export type GuaGameInputToolName = (typeof guaGameInputToolNames)[number];

export const maxBrowserTimerDelayMs = 2_147_483_647;

export const guaPhysicalKeyboardCodes: readonly string[] = [
  "Backquote", "Backslash", "Backspace", "BracketLeft", "BracketRight", "CapsLock", "Comma",
  "ContextMenu", "Delete", "End", "Enter", "Equal", "Escape", "Home", "Insert", "MetaLeft",
  "MetaRight", "Minus", "NumLock", "PageDown", "PageUp", "Pause", "Period", "Quote", "ScrollLock",
  "Semicolon", "ShiftLeft", "ShiftRight", "Slash", "Space", "Tab", "ControlLeft", "ControlRight",
  "AltLeft", "AltRight", "ArrowDown", "ArrowLeft", "ArrowRight", "ArrowUp", "PrintScreen",
  "NumpadAdd", "NumpadDecimal", "NumpadDivide", "NumpadEnter", "NumpadMultiply",
  "NumpadSubtract",
  ...Array.from({ length: 26 }, (_, index) => `Key${String.fromCharCode(65 + index)}`),
  ...Array.from({ length: 10 }, (_, index) => `Digit${index}`),
  ...Array.from({ length: 24 }, (_, index) => `F${index + 1}`),
  ...Array.from({ length: 10 }, (_, index) => `Numpad${index}`),
];

export const guaWebMcpToolDefinitions: readonly GuaToolDefinition<GuaWebMcpToolName>[] = [
  {
    name: "get_ui_tree",
    description: "Read the current Gua semantic UI tree from the running game bridge.",
    inputSchema: objectSchema({}),
  },
  {
    name: "click_node",
    description: "Click a visible semantic UI node and wait for request-correlated host completion when supported.",
    inputSchema: objectSchema({ nodeId: stringProperty("The target Gua node id.") }, ["nodeId"]),
  },
  {
    name: "focus_node",
    description: "Focus a semantic UI node and wait for request-correlated host completion.",
    inputSchema: objectSchema({ nodeId: stringProperty("The target Gua node id.") }, ["nodeId"]),
  },
  {
    name: "set_value",
    description: "Set a semantic UI node value. Sensitive values are never returned by the adapter.",
    inputSchema: objectSchema({
      nodeId: stringProperty("The target Gua node id."),
      value: { type: "string", description: "The value to send to the host." },
      sensitive: { type: "boolean", description: "Redact the value from results and diagnostics." },
    }, ["nodeId", "value"]),
  },
  {
    name: "set_checked",
    description: "Set the checked state of a semantic UI node and wait for host completion.",
    inputSchema: objectSchema({
      nodeId: stringProperty("The target Gua node id."),
      checked: { type: "boolean" },
    }, ["nodeId", "checked"]),
  },
  {
    name: "select",
    description: "Select a value on a semantic UI node and wait for host completion.",
    inputSchema: objectSchema({
      nodeId: stringProperty("The target Gua node id."),
      value: stringProperty("The option value to select."),
    }, ["nodeId", "value"]),
  },
  {
    name: "scroll",
    description: "Scroll a semantic UI node using host pixels or semantic lines and wait for host completion.",
    inputSchema: objectSchema({
      nodeId: stringProperty("The target Gua node id."),
      deltaX: numberProperty("Horizontal scroll delta."),
      deltaY: numberProperty("Vertical scroll delta."),
      scrollUnit: { type: "integer", enum: [0, 1], description: "0 = pixels, 1 = semantic lines." },
    }, ["nodeId", "deltaX", "deltaY"]),
  },
  {
    name: "press_key",
    description: "Send a key press to a node or the host's current focus and wait for host completion.",
    inputSchema: objectSchema({
      key: stringProperty("The logical key name to press, such as Enter or Escape."),
      nodeId: stringProperty("Optional target node id; omit to use current focus."),
      modifiers: { type: "integer", minimum: 0, maximum: 15, description: "Shift=1, Alt=2, Control=4, Meta=8." },
    }, ["key"]),
  },
  {
    name: "wait_for_node",
    description: "Poll the live semantic UI tree until a node id appears or the timeout expires.",
    inputSchema: objectSchema({
      nodeId: stringProperty("The target Gua node id."),
      timeoutMs: { type: "integer", minimum: 0, maximum: maxBrowserTimerDelayMs, description: "Maximum wait time in milliseconds, up to 2147483647. Defaults to 5000." },
    }, ["nodeId"]),
  },
  {
    name: "get_screenshot",
    description: "Read the latest screenshot published by the running game bridge.",
    inputSchema: objectSchema({}),
  },
];

export const guaGameInputToolDefinitions: readonly GuaToolDefinition<GuaGameInputToolName>[] = [
  { name: "get_game_input_actions", description: "Read the host-published semantic game action map.", inputSchema: objectSchema({}) },
  { name: "find_game_input_actions", description: "Search the current host-authorized semantic game action map.", inputSchema: objectSchema({
    id: { type: "string", pattern: "^[a-z][a-z0-9_.-]*$", maxLength: 127 },
    query: { type: "string", minLength: 1, maxLength: 128, pattern: "^[^\\u0000]+$" },
    valueType: { type: "string", enum: ["button", "axis1d", "vector2", "text"] }, active: { type: "boolean" },
    context: { type: "string", minLength: 1, pattern: "^[^\\u0000]+$" }, category: { type: "string", pattern: "^[a-z][a-z0-9_.-]*$", maxLength: 127 },
    tags: { type: "array", maxItems: 16, uniqueItems: true, items: { type: "string", minLength: 1, maxLength: 64, pattern: "^[^\\u0000]+$" } },
    limit: { type: "integer", minimum: 1, maximum: 100, default: 20 },
  }) },
  { name: "get_game_input_actions_v2", description: "Explicitly request the action map with value schema and Set examples.", inputSchema: objectSchema({}) },
  { name: "find_game_input_actions_v2", description: "Explicitly search with value schema and Set examples; metadata is not execution authority.", inputSchema: objectSchema({
    id: { type: "string", pattern: "^[a-z][a-z0-9_.-]*$", maxLength: 127 },
    query: { type: "string", minLength: 1, maxLength: 128, pattern: "^[^\\u0000]+$" },
    valueType: { type: "string", enum: ["button", "axis1d", "vector2", "text"] }, active: { type: "boolean" },
    context: { type: "string", minLength: 1, pattern: "^[^\\u0000]+$" }, category: { type: "string", pattern: "^[a-z][a-z0-9_.-]*$", maxLength: 127 },
    tags: { type: "array", maxItems: 16, uniqueItems: true, items: { type: "string", minLength: 1, maxLength: 64, pattern: "^[^\\u0000]+$" } },
    limit: { type: "integer", minimum: 1, maximum: 100, default: 20 },
  }) },
  { name: "press_game_input_action", description: "Press a semantic button action and wait for host completion.", inputSchema: objectSchema({
    actionId: stringProperty("Stable host-published action id."), confirmed: { type: "boolean" },
  }, ["actionId"]) },
  { name: "set_game_input_action", description: "Set and optionally hold a semantic game action value.", inputSchema: objectSchema({
    actionId: stringProperty("Stable host-published action id."),
    value: { anyOf: [{ type: "boolean" }, { type: "number" }, { type: "string", maxLength: 40 }, { type: "object" }] },
    leaseMs: leaseProperty(), confirmed: { type: "boolean" },
    sensitive: { type: "boolean" },
  }, ["actionId", "value"]) },
  { name: "release_game_input_action", description: "Release a held semantic game action.", inputSchema: objectSchema({
    actionId: stringProperty("Stable host-published action id."),
  }, ["actionId"]) },
  { name: "get_game_input_state", description: "Inspect held inputs owned by this page-local WebMCP session.", inputSchema: objectSchema({}) },
  { name: "release_all_game_inputs", description: "Release every semantic and raw input owned by this page-local WebMCP session.", inputSchema: objectSchema({}) },
  ...(["key_down", "key_up", "press_physical_key"] as const).map((name) => ({
    name, description: `${name} using a supported W3C KeyboardEvent.code identifier.`,
    inputSchema: objectSchema({ code: { type: "string", enum: guaPhysicalKeyboardCodes,
      description: "Supported W3C KeyboardEvent.code such as KeyW or NumpadEnter." }, leaseMs: leaseProperty() }, ["code"]),
  })),
  { name: "pointer_move", description: "Move the engine pointer using absolute or delta coordinates.", inputSchema: objectSchema({
    mode: { type: "string", enum: ["absolute", "delta"] }, coordinateSpace: { type: "string", enum: ["viewport_normalized", "viewport_pixels"] },
    x: { type: "number" }, y: { type: "number" },
  }, ["mode", "x", "y"]) },
  ...(["pointer_button_down", "pointer_button_up"] as const).map((name) => ({
    name, description: `${name} for an engine pointer button.`,
    inputSchema: objectSchema({ button: { type: "string", enum: ["primary", "secondary", "auxiliary", "back", "forward"] },
      leaseMs: leaseProperty() }, ["button"]),
  })),
  { name: "pointer_wheel", description: "Inject pointer wheel movement.", inputSchema: {
    ...objectSchema({ deltaX: { type: "number" }, deltaY: { type: "number" }, wheelUnit: { type: "string", enum: ["pixels", "lines"] } }),
    anyOf: [{ required: ["deltaX"] }, { required: ["deltaY"] }],
  } },
  ...(["gamepad_button_down", "gamepad_button_up"] as const).map((name) => ({
    name, description: `${name} using Standard Gamepad mapping names.`,
    inputSchema: objectSchema({ gamepadIndex: gamepadIndexProperty(), button: stringProperty("Standard Gamepad button name."),
      leaseMs: leaseProperty() }, ["button"]),
  })),
  { name: "set_gamepad_axis", description: "Set and hold a Standard Gamepad axis.", inputSchema: objectSchema({
    gamepadIndex: gamepadIndexProperty(),
    axis: { type: "string", enum: ["left_stick_x", "left_stick_y", "right_stick_x", "right_stick_y"] },
    value: { type: "number", minimum: -1, maximum: 1 }, leaseMs: leaseProperty(),
  }, ["axis", "value"]) },
  { name: "reset_gamepad", description: "Reset one virtual gamepad to neutral.", inputSchema: objectSchema({ gamepadIndex: gamepadIndexProperty() }) },
  { name: "text_input", description: "Inject text through the engine input route.", inputSchema: objectSchema({
    text: { type: "string", maxLength: 40 }, sensitive: { type: "boolean" },
  }, ["text"]) },
];

function objectSchema(properties: Record<string, unknown>, required: string[] = []): Record<string, unknown> {
  return { type: "object", additionalProperties: false, properties, ...(required.length === 0 ? {} : { required }) };
}

function stringProperty(description: string): Record<string, unknown> {
  return { type: "string", minLength: 1, description };
}

function numberProperty(description: string): Record<string, unknown> {
  return { type: "number", description };
}

function leaseProperty(): Record<string, unknown> {
  return { type: "integer", minimum: 1, maximum: 60000 };
}

function gamepadIndexProperty(): Record<string, unknown> {
  return { type: "integer", minimum: 0, maximum: 3 };
}
