export * from "./tool-definitions.js";
export * from "./ports.js";
export type {
  GuaWorldObject,
  GuaWorldObjectTree,
  GuaWorldQueryResult,
  GuaWorldSelector,
  WorldPrimitive,
} from "gua-world-tools";
export { selectorFromArguments, worldObservationTools } from "gua-world-tools";

import {
  parseWorldQueryResult,
  selectorFromArguments,
  worldObservationTools,
  type GuaWorldObject,
  type GuaWorldObjectTree,
  type GuaWorldQueryResult,
  type GuaWorldSelector,
} from "gua-world-tools";

export interface GuaBounds { x?: number; y?: number; w?: number; h?: number }
export interface GuaNodeState {
  focused?: boolean;
  hovered?: boolean;
  pressed?: boolean;
  checked?: boolean;
  selected?: boolean;
  caretPosition?: number;
  selectionStart?: number;
  selectionEnd?: number;
  scrollX?: number;
  scrollY?: number;
  scrollMaxX?: number;
  scrollMaxY?: number;
  rangeValue?: number;
  rangeMin?: number;
  rangeMax?: number;
  selectedIndex?: number;
  value?: number | string | boolean | null;
}
export interface GuaNode {
  id: string;
  parentId?: string;
  role: string;
  label?: string;
  text?: string;
  value?: number | string | boolean | null;
  visible: boolean;
  enabled: boolean;
  bounds: GuaBounds;
  state?: GuaNodeState;
  actions: string[];
}
export interface GuaUiTree {
  schemaVersion: 2;
  sessionEpoch?: number;
  frameSequence: number;
  revision: number;
  screen: string;
  nodes: GuaNode[];
}
export interface GuaScreenshot { dataUri: string; width: number; height: number }

export const guaGameInputCapabilities = [
  "semantic_game_input_v1", "semantic_game_input_search_v1", "raw_keyboard_input_v1", "raw_pointer_input_v1", "raw_gamepad_input_v1",
  "text_input_v1", "game_input_lease_v1",
] as const;
export type GuaGameInputCapability = (typeof guaGameInputCapabilities)[number];
export type GuaGameInputValueType = "button" | "axis1d" | "vector2" | "text";
export interface GuaGameInputAction {
  id: string; description: string; valueType: GuaGameInputValueType; range?: { minimum: number; maximum: number };
  holdable: boolean; active: boolean; bindings: string[]; risk: string; requiresConfirmation: boolean;
  category?: string; aliases?: string[]; tags?: string[]; agentExposure?: "auto" | "private";
}
export interface GuaGameInputActionMap {
  schemaVersion: 1; sessionEpoch: number; revision: number; context: string; actions: GuaGameInputAction[];
}
export interface GuaGameInputActionSelector {
  id?: string; query?: string; valueType?: GuaGameInputValueType; active?: boolean; context?: string;
  category?: string; tags?: string[]; limit?: number;
}
export interface GuaGameInputActionSearchResult {
  schemaVersion: 1; sessionEpoch: number; revision: number; context: string; count: number; truncated: boolean;
  actions: GuaGameInputAction[];
}
export interface GuaGameInputState { schemaVersion: 1; held: unknown[] }
export type GuaGameInputRequest =
  | { type: "press_game_input_action"; actionId: string; confirmed?: boolean }
  | { type: "set_game_input_action"; actionId: string; value: unknown; leaseMs?: number; confirmed?: boolean; sensitive?: boolean }
  | { type: "release_game_input_action"; actionId: string }
  | { type: "release_all_game_inputs" }
  | { type: "key_down" | "key_up" | "press_physical_key"; code: string; leaseMs?: number }
  | { type: "pointer_move"; mode: "absolute" | "delta"; coordinateSpace?: "viewport_normalized" | "viewport_pixels"; x: number; y: number }
  | { type: "pointer_button_down" | "pointer_button_up"; button: "primary" | "secondary" | "auxiliary" | "back" | "forward"; leaseMs?: number }
  | { type: "pointer_wheel"; deltaX: number; deltaY: number; wheelUnit?: "pixels" | "lines" }
  | { type: "gamepad_button_down" | "gamepad_button_up"; gamepadIndex?: number; button: string; leaseMs?: number }
  | { type: "set_gamepad_axis"; gamepadIndex?: number; axis: "left_stick_x" | "left_stick_y" | "right_stick_x" | "right_stick_y"; value: number; leaseMs?: number }
  | { type: "reset_gamepad"; gamepadIndex?: number }
  | { type: "text_input"; text: string; sensitive?: boolean };
export interface GuaGameInputCompletion { completed: true; requestId: number; succeeded: boolean; errorCode: number }

export type GuaWebAction = "click" | "focus" | "set_value" | "set_checked" | "select" | "scroll" | "press_key";
export type GuaWebActionRequest =
  | { action: "click" | "focus"; nodeId: string }
  | { action: "set_value"; nodeId: string; value: string; sensitive?: boolean }
  | { action: "set_checked"; nodeId: string; checked: boolean }
  | { action: "select"; nodeId: string; value: string }
  | { action: "scroll"; nodeId: string; deltaX: number; deltaY: number; scrollUnit?: 0 | 1 }
  | { action: "press_key"; nodeId?: string; key: string; modifiers?: number };
export interface GuaWebActionCompletion {
  requestId: number;
  action: GuaWebAction | number;
  succeeded: boolean;
  error?: string | number;
  nodeId?: string;
  value?: string;
  sensitive?: boolean;
  sessionEpoch?: number;
  frameSequence?: number;
  revision?: number;
}

export interface GuaBridgeCallOptions { signal?: AbortSignal; timeoutMs?: number }

/** Implemented by the engine adapter in the same page. performAction resolves only after host completion. */
export interface GuaBrowserBridge {
  getUiTree(): Promise<GuaUiTree>;
  performAction(request: GuaWebActionRequest, options?: GuaBridgeCallOptions): Promise<GuaWebActionCompletion>;
  getScreenshot?(): Promise<GuaScreenshot>;
  getWorldObjectTree?(options?: GuaBridgeCallOptions): Promise<GuaWorldObjectTree>;
  findWorldObjects?(selector: GuaWorldSelector, options?: GuaBridgeCallOptions): Promise<GuaWorldQueryResult>;
  getGameInputCapabilities?(): Promise<GuaGameInputCapability[]>;
  getGameInputActions?(): Promise<GuaGameInputActionMap>;
  findGameInputActions?(selector: GuaGameInputActionSelector): Promise<GuaGameInputActionSearchResult>;
  getGameInputState?(): Promise<GuaGameInputState>;
  performGameInput?(request: GuaGameInputRequest, options?: GuaBridgeCallOptions): Promise<GuaGameInputCompletion>;
}

export type GuaWebErrorCode =
  | "webmcp_unsupported" | "engine_unsupported" | "invalid_request" | "node_not_found"
  | "hidden" | "disabled" | "unsupported_action" | "action_failed" | "timeout" | "aborted";

export class GuaWebError extends Error {
  constructor(public readonly code: GuaWebErrorCode, message: string, public readonly details?: Record<string, unknown>) {
    super(message);
    this.name = "GuaWebError";
  }
  toJSON() { return { code: this.code, message: this.message, ...(this.details ? { details: this.details } : {}) }; }
}

interface WebMcpExecutionOptions { signal?: AbortSignal }
interface WebMcpToolRegistration {
  name: string;
  description: string;
  inputSchema: Record<string, unknown>;
  execute(input: Record<string, unknown>, options?: WebMcpExecutionOptions): Promise<unknown>;
}
interface ModelContext {
  registerTool(tool: WebMcpToolRegistration, options?: { signal?: AbortSignal }): Promise<void> | void;
}

export interface RegisterGuaWebMcpOptions {
  document?: Document & { modelContext?: ModelContext };
  pollIntervalMs?: number;
  defaultTimeoutMs?: number;
}
export interface GuaWebMcpRegistration {
  supported: boolean;
  registeredTools: string[];
  unregister(): void;
  error?: GuaWebError;
}

import { guaGameInputToolDefinitions, guaWebMcpToolDefinitions, maxBrowserTimerDelayMs } from "./tool-definitions.js";

export async function registerGuaWebMcp(
  bridge: GuaBrowserBridge,
  options: RegisterGuaWebMcpOptions = {},
): Promise<GuaWebMcpRegistration> {
  const documentValue = options.document ?? (globalThis as { document?: Document }).document as RegisterGuaWebMcpOptions["document"];
  const modelContext = documentValue?.modelContext;
  const controller = new AbortController();
  const registeredTools: string[] = [];
  if (!modelContext || typeof modelContext.registerTool !== "function") {
    return {
      supported: false,
      registeredTools,
      unregister: () => controller.abort(),
      error: new GuaWebError("webmcp_unsupported", "This browser does not expose document.modelContext.registerTool()."),
    };
  }

  let defaultTimeoutMs: number;
  try { defaultTimeoutMs = timerDelay(options.defaultTimeoutMs ?? 5000, "defaultTimeoutMs"); }
  catch (error) {
    return {
      supported: false,
      registeredTools,
      unregister: () => controller.abort(),
      error: error instanceof GuaWebError ? error : new GuaWebError("invalid_request", "Invalid WebMCP timeout."),
    };
  }

  let gameInputCapabilities: GuaGameInputCapability[] = [];
  if (bridge.getGameInputCapabilities && bridge.performGameInput) {
    try { gameInputCapabilities = await withTimeout(bridge.getGameInputCapabilities(), defaultTimeoutMs, controller.signal,
      "Timed out reading game input capabilities."); }
    catch { gameInputCapabilities = []; }
  }
  const gameInputTools = gameInputDefinitions(gameInputCapabilities, bridge);
  const definitions = [
    ...guaWebMcpToolDefinitions.filter((definition) => definition.name !== "get_screenshot" || bridge.getScreenshot),
    ...(bridge.getWorldObjectTree ? worldObservationTools.filter((definition) => definition.name === "get_world_object_tree") : []),
    ...(bridge.findWorldObjects ? worldObservationTools.filter((definition) => definition.name !== "get_world_object_tree") : []),
    ...gameInputTools,
  ];
  try {
    const pollIntervalMs = timerDelay(options.pollIntervalMs ?? 25, "pollIntervalMs");
    for (const definition of definitions) {
      await modelContext.registerTool({
        ...definition,
        execute: async (input, executionOptions) => executeTool(
          definition.name,
          input ?? {},
          bridge,
          executionOptions?.signal,
          pollIntervalMs,
          defaultTimeoutMs,
          gameInputCapabilities.includes("semantic_game_input_search_v1"),
        ),
      }, { signal: controller.signal });
      registeredTools.push(definition.name);
    }
  } catch (error) {
    controller.abort();
    const message = error instanceof Error ? error.message : "The browser rejected WebMCP tool registration.";
    return {
      supported: false,
      registeredTools: [],
      unregister: () => controller.abort(),
      error: error instanceof GuaWebError ? error : new GuaWebError("webmcp_unsupported", message),
    };
  }
  return {
    supported: true,
    registeredTools,
    unregister: () => {
      if (bridge.performGameInput && gameInputCapabilities.length > 0) {
        void bridge.performGameInput({ type: "release_all_game_inputs" }).catch(() => undefined);
      }
      controller.abort();
    },
  };
}

function gameInputDefinitions(capabilities: GuaGameInputCapability[], bridge: GuaBrowserBridge) {
  const enabled = new Set<string>();
  if (capabilities.includes("semantic_game_input_v1") && bridge.getGameInputActions) {
    for (const name of ["get_game_input_actions", "press_game_input_action", "set_game_input_action", "release_game_input_action"]) enabled.add(name);
  }
  if (capabilities.includes("semantic_game_input_search_v1") && bridge.findGameInputActions) enabled.add("find_game_input_actions");
  if (capabilities.length > 0) enabled.add("release_all_game_inputs");
  if (capabilities.length > 0 && bridge.getGameInputState) enabled.add("get_game_input_state");
  if (capabilities.includes("raw_keyboard_input_v1")) for (const name of ["key_down", "key_up", "press_physical_key"]) enabled.add(name);
  if (capabilities.includes("raw_pointer_input_v1")) for (const name of ["pointer_move", "pointer_button_down", "pointer_button_up", "pointer_wheel"]) enabled.add(name);
  if (capabilities.includes("raw_gamepad_input_v1")) for (const name of ["gamepad_button_down", "gamepad_button_up", "set_gamepad_axis", "reset_gamepad"]) enabled.add(name);
  if (capabilities.includes("text_input_v1")) enabled.add("text_input");
  return guaGameInputToolDefinitions.filter((definition) => enabled.has(definition.name));
}

async function executeTool(
  name: string,
  input: Record<string, unknown>,
  bridge: GuaBrowserBridge,
  signal: AbortSignal | undefined,
  pollIntervalMs: number,
  defaultTimeoutMs: number,
  gameInputSearchSupported: boolean,
): Promise<unknown> {
  try {
    throwIfAborted(signal);
    if (name === "get_ui_tree") {
      return await withTimeout(
        bridge.getUiTree(),
        defaultTimeoutMs,
        signal,
        "Timed out reading the current Gua semantic UI tree.",
      );
    }
    if (name === "get_world_object_tree") {
      rejectUnknownArguments(input, new Set());
      if (!bridge.getWorldObjectTree) throw new GuaWebError("engine_unsupported", "The engine bridge does not support the World Object Tree.");
      return await withTimeout(
        bridge.getWorldObjectTree({ signal, timeoutMs: defaultTimeoutMs }),
        defaultTimeoutMs,
        signal,
        "Timed out reading the current Gua World Object Tree.",
      );
    }
    if (name === "find_world_objects" || name === "wait_for_world_object") {
      if (!bridge.findWorldObjects) throw new GuaWebError("engine_unsupported", "The engine bridge does not support world object queries.");
      if (name === "find_world_objects" && Object.prototype.hasOwnProperty.call(input, "timeoutMs")) {
        throw new GuaWebError("invalid_request", "Unknown find_world_objects argument: timeoutMs.");
      }
      const selector = worldSelector(input);
      if (name === "find_world_objects") {
        const result = parseWorldQueryResult(await withTimeout(
          bridge.findWorldObjects(selector, { signal, timeoutMs: defaultTimeoutMs }),
          defaultTimeoutMs,
          signal,
          "Timed out querying the current Gua World Object Tree.",
        ), selector);
        if (!result.valid) throw new GuaWebError("invalid_request", result.error ?? "The host rejected the world selector.");
        return result;
      }
      const timeoutMs = input.timeoutMs === undefined
        ? Math.min(defaultTimeoutMs, 300_000)
        : optionalInteger(input, "timeoutMs", defaultTimeoutMs, 1, 300_000);
      return await waitForWorldObject(bridge, selector, timeoutMs, pollIntervalMs, signal);
    }
    if (name === "get_screenshot") {
      if (!bridge.getScreenshot) throw new GuaWebError("engine_unsupported", "The engine bridge does not support screenshots.");
      return await withTimeout(
        bridge.getScreenshot(),
        defaultTimeoutMs,
        signal,
        "Timed out reading the latest Gua screenshot.",
      );
    }
    if (name === "get_game_input_actions") {
      rejectUnknownArguments(input, new Set());
      if (!bridge.getGameInputActions) throw new GuaWebError("engine_unsupported", "The engine bridge does not support semantic game input.");
      return await withTimeout(bridge.getGameInputActions(), defaultTimeoutMs, signal, "Timed out reading the game input action map.");
    }
    if (name === "find_game_input_actions") {
      if (!bridge.findGameInputActions) throw new GuaWebError("engine_unsupported", "The engine bridge does not support game input search.");
      return await withTimeout(bridge.findGameInputActions(gameInputSelector(input)), defaultTimeoutMs, signal,
        "Timed out searching the game input action map.");
    }
    if (name === "get_game_input_state") {
      rejectUnknownArguments(input, new Set());
      if (!bridge.getGameInputState) throw new GuaWebError("engine_unsupported", "The engine bridge does not expose page-owned game input state.");
      return await withTimeout(bridge.getGameInputState(), defaultTimeoutMs, signal, "Timed out reading page-owned game input state.");
    }
    if (isGameInputTool(name)) {
      if (!bridge.performGameInput) throw new GuaWebError("engine_unsupported", "The engine bridge does not support game input.");
      const request = gameInputRequest(name, input);
      if ((request.type === "press_game_input_action" || request.type === "set_game_input_action") && (bridge.getGameInputActions || (gameInputSearchSupported && bridge.findGameInputActions))) {
        const actions = gameInputSearchSupported && bridge.findGameInputActions
          ? (await withTimeout(bridge.findGameInputActions({ id: request.actionId, limit: 1 }), defaultTimeoutMs, signal,
              "Timed out validating the game input action map.")).actions
          : (await withTimeout(bridge.getGameInputActions!(), defaultTimeoutMs, signal, "Timed out validating the game input action map.")).actions;
        const action = actions.find((candidate) => candidate.id === request.actionId);
        if (!action) throw new GuaWebError("invalid_request", `Unknown game input action: ${request.actionId}`);
        if (action.requiresConfirmation && request.confirmed !== true) {
          throw new GuaWebError("invalid_request", `Game input action '${request.actionId}' requires confirmed=true.`);
        }
      }
      const completion = await performGameInputWithCancellation(bridge, request, defaultTimeoutMs, signal);
      if (!completion.completed || !Number.isInteger(completion.requestId) || completion.requestId < 1) {
        throw new GuaWebError("invalid_request", "The engine bridge returned no request-correlated game input completion.");
      }
      if (!completion.succeeded) throw new GuaWebError("action_failed", `The host rejected ${request.type}.`, {
        requestId: completion.requestId, hostError: completion.errorCode,
      });
      return completion;
    }
    if (name === "wait_for_node") {
      const nodeId = requiredString(input, "nodeId");
      const timeoutMs = optionalInteger(input, "timeoutMs", defaultTimeoutMs, 0, maxBrowserTimerDelayMs);
      return await waitForNode(bridge, nodeId, timeoutMs, pollIntervalMs, signal);
    }
    const request = actionRequest(name, input);
    await validateAction(bridge, request, defaultTimeoutMs, signal);
    throwIfAborted(signal);
    const completion = await performActionWithCancellation(
      bridge,
      request,
      defaultTimeoutMs,
      signal,
      `Timed out waiting for ${request.action} host completion.`,
    );
    if (!completion || !Number.isInteger(completion.requestId) || completion.requestId < 1) {
      throw new GuaWebError("invalid_request", "The engine bridge returned no request-correlated completion.");
    }
    if (!completion.succeeded) {
      throw new GuaWebError(completionErrorCode(completion.error), `The host rejected ${request.action} for ${request.nodeId ?? "current focus"}.`, {
        requestId: completion.requestId,
        hostError: completion.error ?? "unknown",
      });
    }
    const safeCompletion = (request.action === "set_value" && request.sensitive) || completion.sensitive
      ? { ...completion, value: "", sensitive: true }
      : completion;
    return safeCompletion;
  } catch (error) {
    if (name === "text_input" && input.sensitive === true) {
      const code = error instanceof GuaWebError ? error.code : "action_failed";
      const normalized = new GuaWebError(code, "Sensitive text input failed; engine details were [REDACTED].");
      return { content: [{ type: "text", text: JSON.stringify({ error: normalized.toJSON() }) }], isError: true };
    }
    const secret = input.sensitive === true
      ? String(name === "text_input" ? input.text ?? "" : input.value ?? "")
      : undefined;
    const normalized = normalizeError(error, secret);
    return { content: [{ type: "text", text: JSON.stringify({ error: normalized.toJSON() }) }], isError: true };
  }
}

function worldSelector(input: Record<string, unknown>): GuaWorldSelector {
  try { return selectorFromArguments(input); }
  catch (error) {
    throw new GuaWebError("invalid_request", error instanceof Error ? error.message : "Invalid world selector.");
  }
}

async function waitForWorldObject(
  bridge: GuaBrowserBridge,
  selector: GuaWorldSelector,
  timeoutMs: number,
  pollIntervalMs: number,
  signal?: AbortSignal,
): Promise<GuaWorldObject> {
  const deadline = performance.now() + timeoutMs;
  do {
    throwIfAborted(signal);
    const remainingMs = Math.ceil(Math.max(0, deadline - performance.now()));
    const result = parseWorldQueryResult(await withTimeout(
      bridge.findWorldObjects!(selector, { signal, timeoutMs: remainingMs }),
      remainingMs,
      signal,
      "Timed out waiting for a Gua world object.",
    ), selector);
    if (!result.valid) throw new GuaWebError("invalid_request", result.error ?? "The host rejected the world selector.");
    if (result.matches[0]) return result.matches[0];
    if (performance.now() >= deadline) break;
    await delay(Math.min(pollIntervalMs, Math.max(0, deadline - performance.now())), signal);
  } while (true);
  throw new GuaWebError("timeout", "Timed out waiting for a Gua world object.", { timeoutMs });
}

function gameInputSelector(input: Record<string, unknown>): GuaGameInputActionSelector {
  rejectUnknownArguments(input, new Set(["id", "query", "valueType", "active", "context", "category", "tags", "limit"]));
  if (input.limit === null) throw new GuaWebError("invalid_request", "limit must be an integer from 1 to 100.");
  const stringValue = (key: string, maximumCodePoints?: number): string | undefined => {
    const value = input[key];
    if (value === undefined) return undefined;
    if (typeof value !== "string" || value.length === 0 || value.includes("\0") ||
        maximumCodePoints !== undefined && [...value].length > maximumCodePoints)
      throw new GuaWebError("invalid_request", `${key} is invalid.`);
    return value;
  };
  const tags = input.tags;
  if (tags !== undefined && (!Array.isArray(tags) || tags.length > 16 || !tags.every((tag) => typeof tag === "string" &&
      tag.length > 0 && !tag.includes("\0") && [...tag].length <= 64) || new Set(tags).size !== tags.length))
    throw new GuaWebError("invalid_request", "tags are invalid.");
  const valueType = stringValue("valueType");
  if (valueType !== undefined && !["button", "axis1d", "vector2", "text"].includes(valueType))
    throw new GuaWebError("invalid_request", "valueType is invalid.");
  if (input.active !== undefined && typeof input.active !== "boolean") throw new GuaWebError("invalid_request", "active must be boolean.");
  const id = stringValue("id"), category = stringValue("category");
  if (id !== undefined && !/^[a-z][a-z0-9_.-]{0,126}$/.test(id) ||
      category !== undefined && !/^[a-z][a-z0-9_.-]{0,126}$/.test(category))
    throw new GuaWebError("invalid_request", "id or category is invalid.");
  return {
    id, query: stringValue("query", 128), valueType: valueType as GuaGameInputValueType | undefined,
    active: input.active as boolean | undefined, context: stringValue("context"), category,
    tags: tags as string[] | undefined, limit: optionalInteger(input, "limit", 20, 1, 100),
  };
}

function rejectUnknownArguments(input: Record<string, unknown>, allowed: Set<string>): void {
  const unknown = Object.keys(input).find((key) => !allowed.has(key));
  if (unknown !== undefined) throw new GuaWebError("invalid_request", `Unknown argument: ${unknown}.`);
}

async function performActionWithCancellation(
  bridge: GuaBrowserBridge,
  request: GuaWebActionRequest,
  timeoutMs: number,
  signal: AbortSignal | undefined,
  timeoutMessage: string,
): Promise<GuaWebActionCompletion> {
  throwIfAborted(signal);
  const controller = new AbortController();
  const forwardAbort = () => controller.abort();
  signal?.addEventListener("abort", forwardAbort, { once: true });
  if (signal?.aborted) {
    signal.removeEventListener("abort", forwardAbort);
    throw new GuaWebError("aborted", "The Gua WebMCP tool call was aborted.");
  }
  try {
    return await withTimeout(
      bridge.performAction(request, { signal: controller.signal, timeoutMs }),
      timeoutMs,
      signal,
      timeoutMessage,
    );
  } catch (error) {
    controller.abort();
    throw error;
  } finally {
    signal?.removeEventListener("abort", forwardAbort);
  }
}

async function validateAction(
  bridge: GuaBrowserBridge,
  request: GuaWebActionRequest,
  timeoutMs: number,
  signal?: AbortSignal,
): Promise<void> {
  if (request.action === "press_key" && !request.nodeId) return;
  const tree = await withTimeout(
    bridge.getUiTree(),
    timeoutMs,
    signal,
    "Timed out reading the current Gua semantic UI tree before dispatch.",
  );
  const node = tree.nodes.find((candidate) => candidate.id === request.nodeId);
  if (!node) throw new GuaWebError("node_not_found", `Gua node not found: ${request.nodeId}`);
  if (!node.visible) throw new GuaWebError("hidden", `Gua node is hidden: ${request.nodeId}`);
  if (!node.enabled) throw new GuaWebError("disabled", `Gua node is disabled: ${request.nodeId}`);
  if (!node.actions.includes(request.action)) {
    throw new GuaWebError("unsupported_action", `Gua node does not support ${request.action}: ${request.nodeId}`);
  }
}

async function waitForNode(bridge: GuaBrowserBridge, nodeId: string, timeoutMs: number, pollIntervalMs: number, signal?: AbortSignal): Promise<GuaNode> {
  const deadline = performance.now() + timeoutMs;
  do {
    throwIfAborted(signal);
    const remainingMs = Math.max(0, deadline - performance.now());
    const tree = await withTimeout(
      bridge.getUiTree(),
      remainingMs,
      signal,
      `Timed out waiting for Gua node: ${nodeId}`,
    );
    const node = tree.nodes.find((candidate) => candidate.id === nodeId);
    if (node) return node;
    if (performance.now() >= deadline) break;
    await delay(Math.min(pollIntervalMs, Math.max(0, deadline - performance.now())), signal);
  } while (true);
  throw new GuaWebError("timeout", `Timed out waiting for Gua node: ${nodeId}`, { timeoutMs });
}

function actionRequest(name: string, input: Record<string, unknown>): GuaWebActionRequest {
  const action = name.replace(/_node$/, "") as GuaWebAction;
  switch (action) {
    case "click":
    case "focus":
      return { action, nodeId: requiredString(input, "nodeId") };
    case "set_value":
      return {
        action,
        nodeId: requiredString(input, "nodeId"),
        value: requiredString(input, "value", true),
        sensitive: input.sensitive === undefined ? false : requiredBoolean(input, "sensitive"),
      };
    case "set_checked":
      return { action, nodeId: requiredString(input, "nodeId"), checked: requiredBoolean(input, "checked") };
    case "select":
      return { action, nodeId: requiredString(input, "nodeId"), value: requiredString(input, "value") };
    case "scroll":
      return {
        action,
        nodeId: requiredString(input, "nodeId"),
        deltaX: requiredNumber(input, "deltaX"),
        deltaY: requiredNumber(input, "deltaY"),
        scrollUnit: optionalInteger(input, "scrollUnit", 0, 0, 1) as 0 | 1,
      };
    case "press_key":
      return {
        action,
        ...(input.nodeId === undefined ? {} : { nodeId: requiredString(input, "nodeId") }),
        key: requiredString(input, "key"),
        modifiers: optionalInteger(input, "modifiers", 0, 0, 15),
      };
    default:
      throw new GuaWebError("invalid_request", `Unknown Gua WebMCP tool: ${name}`);
  }
}

const gameInputToolNames = new Set<string>(guaGameInputToolDefinitions.map((definition) => definition.name));

function isGameInputTool(name: string): boolean {
  return gameInputToolNames.has(name) && name !== "get_game_input_actions" && name !== "get_game_input_state";
}

function gameInputRequest(name: string, input: Record<string, unknown>): GuaGameInputRequest {
  const leaseMs = () => optionalInteger(input, "leaseMs", 5000, 1, 60000);
  const gamepadIndex = () => optionalInteger(input, "gamepadIndex", 0, 0, 3);
  switch (name) {
    case "press_game_input_action":
      rejectUnknownArguments(input, new Set(["actionId", "confirmed"]));
      return { type: name, actionId: requiredString(input, "actionId"), confirmed: optionalBoolean(input, "confirmed", false) };
    case "set_game_input_action":
      rejectUnknownArguments(input, new Set(["actionId", "value", "leaseMs", "confirmed", "sensitive"]));
      if (!Object.prototype.hasOwnProperty.call(input, "value")) throw new GuaWebError("invalid_request", "value is required.");
      return { type: name, actionId: requiredString(input, "actionId"), value: input.value, leaseMs: leaseMs(),
        confirmed: optionalBoolean(input, "confirmed", false), sensitive: optionalBoolean(input, "sensitive", false) };
    case "release_game_input_action":
      rejectUnknownArguments(input, new Set(["actionId"]));
      return { type: name, actionId: requiredString(input, "actionId") };
    case "release_all_game_inputs":
      rejectUnknownArguments(input, new Set());
      return { type: name };
    case "key_down": case "key_up": case "press_physical_key":
      rejectUnknownArguments(input, new Set(["code", "leaseMs"]));
      return { type: name, code: requiredString(input, "code"), leaseMs: leaseMs() };
    case "pointer_move": {
      rejectUnknownArguments(input, new Set(["mode", "coordinateSpace", "x", "y"]));
      const mode = requiredEnum(input, "mode", ["absolute", "delta"] as const);
      const coordinateSpace = input.coordinateSpace === undefined ? "viewport_pixels" : requiredEnum(input, "coordinateSpace", ["viewport_normalized", "viewport_pixels"] as const);
      if (mode === "delta" && input.coordinateSpace !== undefined) throw new GuaWebError("invalid_request", "coordinateSpace is valid only for absolute pointer moves.");
      const x = requiredNumber(input, "x"), y = requiredNumber(input, "y");
      if (mode === "absolute" && coordinateSpace === "viewport_normalized" && (x < 0 || x > 1 || y < 0 || y > 1)) {
        throw new GuaWebError("invalid_request", "Normalized pointer coordinates must be within 0 and 1.");
      }
      return { type: name, mode, ...(mode === "absolute" ? { coordinateSpace } : {}), x, y };
    }
    case "pointer_button_down": case "pointer_button_up":
      rejectUnknownArguments(input, new Set(["button", "leaseMs"]));
      return { type: name, button: requiredEnum(input, "button", ["primary", "secondary", "auxiliary", "back", "forward"] as const), leaseMs: leaseMs() };
    case "pointer_wheel":
      rejectUnknownArguments(input, new Set(["deltaX", "deltaY", "wheelUnit"]));
      if (input.deltaX === undefined && input.deltaY === undefined) throw new GuaWebError("invalid_request", "deltaX or deltaY is required.");
      return { type: name, deltaX: optionalNumber(input, "deltaX", 0), deltaY: optionalNumber(input, "deltaY", 0),
        wheelUnit: input.wheelUnit === undefined ? "pixels" : requiredEnum(input, "wheelUnit", ["pixels", "lines"] as const) };
    case "gamepad_button_down": case "gamepad_button_up":
      rejectUnknownArguments(input, new Set(["gamepadIndex", "button", "leaseMs"]));
      return { type: name, gamepadIndex: gamepadIndex(), button: requiredString(input, "button"), leaseMs: leaseMs() };
    case "set_gamepad_axis": {
      rejectUnknownArguments(input, new Set(["gamepadIndex", "axis", "value", "leaseMs"]));
      const value = requiredNumber(input, "value");
      if (value < -1 || value > 1) throw new GuaWebError("invalid_request", "value must be from -1 to 1.");
      return { type: name, gamepadIndex: gamepadIndex(), axis: requiredEnum(input, "axis", ["left_stick_x", "left_stick_y", "right_stick_x", "right_stick_y"] as const), value, leaseMs: leaseMs() };
    }
    case "reset_gamepad":
      rejectUnknownArguments(input, new Set(["gamepadIndex"]));
      return { type: name, gamepadIndex: gamepadIndex() };
    case "text_input":
      rejectUnknownArguments(input, new Set(["text", "sensitive"]));
      return { type: name, text: boundedTextInput(input, "text"), sensitive: optionalBoolean(input, "sensitive", false) };
    default:
      throw new GuaWebError("invalid_request", `Unknown Gua game input tool: ${name}`);
  }
}

async function performGameInputWithCancellation(
  bridge: GuaBrowserBridge, request: GuaGameInputRequest, timeoutMs: number, signal?: AbortSignal,
): Promise<GuaGameInputCompletion> {
  throwIfAborted(signal);
  const controller = new AbortController();
  const forwardAbort = () => controller.abort();
  signal?.addEventListener("abort", forwardAbort, { once: true });
  try {
    return await withTimeout(
      bridge.performGameInput!(request, { signal: controller.signal, timeoutMs }), timeoutMs, signal,
      `Timed out waiting for ${request.type} host completion.`,
    );
  } catch (error) {
    controller.abort();
    throw error;
  } finally {
    signal?.removeEventListener("abort", forwardAbort);
  }
}

function requiredString(input: Record<string, unknown>, key: string, allowEmpty = false): string {
  if (typeof input[key] !== "string" || (!allowEmpty && (input[key] as string).length === 0)) {
    throw new GuaWebError("invalid_request", `${key} must be ${allowEmpty ? "a string" : "a non-empty string"}.`);
  }
  return input[key] as string;
}
function requiredBoolean(input: Record<string, unknown>, key: string): boolean {
  if (typeof input[key] !== "boolean") throw new GuaWebError("invalid_request", `${key} must be a boolean.`);
  return input[key] as boolean;
}
function optionalBoolean(input: Record<string, unknown>, key: string, fallback: boolean): boolean {
  return input[key] === undefined ? fallback : requiredBoolean(input, key);
}
function requiredEnum<const T extends readonly string[]>(input: Record<string, unknown>, key: string, values: T): T[number] {
  const value = requiredString(input, key);
  if (!values.includes(value)) throw new GuaWebError("invalid_request", `${key} must be one of ${values.join(", ")}.`);
  return value as T[number];
}
function requiredNumber(input: Record<string, unknown>, key: string): number {
  if (typeof input[key] !== "number" || !Number.isFinite(input[key])) throw new GuaWebError("invalid_request", `${key} must be a finite number.`);
  return input[key] as number;
}
function boundedTextInput(input: Record<string, unknown>, key: string): string {
  const value = requiredString(input, key, true);
  if ([...value].length > 40) throw new GuaWebError("invalid_request", `${key} must contain at most 40 Unicode code points.`);
  return value;
}
function optionalNumber(input: Record<string, unknown>, key: string, fallback: number): number {
  return input[key] === undefined ? fallback : requiredNumber(input, key);
}
function optionalInteger(input: Record<string, unknown>, key: string, fallback: number, minimum: number, maximum = Number.MAX_SAFE_INTEGER): number {
  const value = input[key] ?? fallback;
  if (!Number.isInteger(value) || (value as number) < minimum || (value as number) > maximum) {
    throw new GuaWebError("invalid_request", `${key} must be an integer from ${minimum} to ${maximum}.`);
  }
  return value as number;
}
function timerDelay(value: number, key: string): number {
  if (!Number.isInteger(value) || value < 0 || value > maxBrowserTimerDelayMs) {
    throw new GuaWebError("invalid_request", `${key} must be an integer from 0 to ${maxBrowserTimerDelayMs}.`);
  }
  return value;
}
function throwIfAborted(signal?: AbortSignal): void {
  if (signal?.aborted) throw new GuaWebError("aborted", "The Gua WebMCP tool call was aborted.");
}
function delay(milliseconds: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    let settled = false;
    const finish = (settle: () => void) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      signal?.removeEventListener("abort", aborted);
      settle();
    };
    const aborted = () => finish(() => reject(new GuaWebError("aborted", "The Gua WebMCP tool call was aborted.")));
    const timer = setTimeout(() => finish(resolve), milliseconds);
    signal?.addEventListener("abort", aborted, { once: true });
    if (signal?.aborted) aborted();
  });
}
function withTimeout<T>(operation: Promise<T>, timeoutMs: number, signal: AbortSignal | undefined, message: string): Promise<T> {
  throwIfAborted(signal);
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => finish(() => reject(new GuaWebError("timeout", message, { timeoutMs }))), Math.max(0, timeoutMs));
    const aborted = () => finish(() => reject(new GuaWebError("aborted", "The Gua WebMCP tool call was aborted.")));
    const finish = (settle: () => void) => {
      clearTimeout(timer);
      signal?.removeEventListener("abort", aborted);
      settle();
    };
    signal?.addEventListener("abort", aborted, { once: true });
    operation.then((value) => finish(() => resolve(value)), (error) => finish(() => reject(error)));
  });
}
function normalizeError(error: unknown, secret?: string): GuaWebError {
  let message = error instanceof Error ? error.message : "The engine bridge failed.";
  if (secret) message = message.split(secret).join("[REDACTED]");
  if (error instanceof GuaWebError) {
    const details = secret && error.details ? redactStrings(error.details, secret) as Record<string, unknown> : error.details;
    return new GuaWebError(error.code, message, details);
  }
  return new GuaWebError("action_failed", message);
}

function completionErrorCode(error: GuaWebActionCompletion["error"]): GuaWebErrorCode {
  switch (error) {
    case -2:
    case "-2":
    case "node_not_found":
      return "node_not_found";
    case -3:
    case "-3":
    case "hidden":
      return "hidden";
    case -4:
    case "-4":
    case "disabled":
      return "disabled";
    case -5:
    case "-5":
    case "unsupported":
    case "unsupported_action":
      return "unsupported_action";
    case -6:
    case "-6":
    case "invalid_value":
      return "invalid_request";
    default:
      return "action_failed";
  }
}

function redactStrings(value: unknown, secret: string): unknown {
  if (typeof value === "string") return value.split(secret).join("[REDACTED]");
  if (Array.isArray(value)) return value.map((item) => redactStrings(item, secret));
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, redactStrings(item, secret)]));
  }
  return value;
}
