import {
  GuaWebError, guaGameInputCapabilities, type GuaBridgeCallOptions, type GuaBrowserBridge,
  type GuaGameInputActionMap, type GuaGameInputCapability, type GuaGameInputCompletion, type GuaGameInputRequest,
  type GuaGameInputActionSearchResult, type GuaGameInputActionSelector,
  type GuaGameInputState, type GuaScreenshot, type GuaUiTree, type GuaWebActionCompletion, type GuaWebActionRequest,
} from "./index.js";
import { parseObserveTransport, observeSubscriptionId, type ObserveSubscription } from "gua-value";
import { parseInputActionMetadataJson, validInputActionMetadata } from "./input-metadata.js";
import {
  parseWorldObjectTree,
  parseWorldQueryResult,
  type GuaWorldSelector,
} from "gua-world-tools";

export interface GuaInPagePort {
  readonly capabilities?: readonly string[];
  /** A same-page engine call. No network transport or session routing is permitted here. */
  invoke(command: GuaInPageCommand, options?: GuaBridgeCallOptions): Promise<unknown>;
}

export type GuaInPageCommand =
  | { type: "get_observe_snapshot" | "subscribe_observations" }
  | { type: "poll_observations" | "unsubscribe_observations"; subscriptionId: number }
  | { type: "get_ui_tree" }
  | { type: "get_world_object_tree" }
  | ({ type: "query_world_objects" } & GuaWorldWireSelector)
  | { type: "perform_action"; request: GuaWebActionRequest }
  | { type: "get_game_input_capabilities" }
  | { type: "get_game_input_actions_v2" }
  | ({ type: "find_game_input_actions_v2" } & GuaGameInputActionSelector)
  | { type: "get_game_input_actions" }
  | ({ type: "find_game_input_actions" } & GuaGameInputActionSelector)
  | { type: "get_game_input_state" }
  | { type: "perform_game_input"; request: GuaGameInputRequest }
  | { type: "get_screenshot" };

export interface GuaInPageBridgeOptions { screenshot?: boolean; world?: boolean; gameInput?: boolean; observe?: boolean }
const portResolvers = new WeakMap<GuaInPagePort, () => GuaInPagePort>();

interface GuaWorldWireSelector {
  worldId?: string;
  kind?: string;
  label?: string;
  tag?: string;
  parentId?: string;
  directChild?: 0 | 1;
  visibleToPlayer?: 0 | 1 | 2;
  active?: 0 | 1 | 2;
  stateKey?: string;
  stateType?: 0 | 1 | 2 | 3;
  stateString?: string;
  stateNumber?: number;
  stateBool?: boolean;
  relativeToObjectId?: string;
  maxDistance?: number;
  limit?: number;
}

export function createGuaInPageBridge(port: GuaInPagePort, options: GuaInPageBridgeOptions = {}): GuaBrowserBridge {
  const bridge: GuaBrowserBridge = {
    getUiTree: async () => parseTree(await invoke(port, { type: "get_ui_tree" })),
    performAction: async (request, callOptions) => {
      const validatedRequest = validateActionRequest(request);
      return parseCompletion(await invoke(port, { type: "perform_action", request: validatedRequest }, callOptions));
    },
  };
  if (options.screenshot) bridge.getScreenshot = async () => parseScreenshot(await invoke(port, { type: "get_screenshot" }));
  if (options.observe ?? port.capabilities?.includes("observe_v1")) {
    const subscriptions = new Map<number, { port: GuaInPagePort; remoteId: number }>(); let generation = 0, nextHandle = 1;
    const currentPort = () => portResolvers.get(port)?.() ?? port;
    const isCurrentPort = (owner: GuaInPagePort) => { try { return owner === currentPort(); } catch { return false; } };
    bridge.getObserveSnapshot = async options => parseObserveTransport(await invoke(port, { type: "get_observe_snapshot" }, options), "snapshot");
    bridge.subscribeObservations = async options => {
      const started = generation;
      const ownerPort = currentPort();
      const result = await invoke(ownerPort, { type: "subscribe_observations" }, options) as ObserveSubscription;
      const remoteId = observeSubscriptionId(result.subscriptionId);
      if (started !== generation || options?.signal?.aborted || !isCurrentPort(ownerPort)) {
        await invoke(ownerPort, { type: "unsubscribe_observations", subscriptionId: remoteId });
        throw new GuaWebError("aborted", "Observation registration ended.");
      }
      try { const snapshot = parseObserveTransport(result.snapshot, "snapshot"), subscriptionId = observeSubscriptionId(nextHandle++);
        subscriptions.set(subscriptionId, {port: ownerPort, remoteId}); return { subscriptionId, snapshot }; }
      catch (error) { await invoke(ownerPort, { type: "unsubscribe_observations", subscriptionId: remoteId }); throw error; }
    };
    bridge.pollObservations = async (subscriptionId, options) => {
      const owner = subscriptions.get(observeSubscriptionId(subscriptionId));
      if (!owner || !isCurrentPort(owner.port)) throw new GuaWebError("invalid_request", "Observe subscription belongs to an inactive connection.");
      let invalidated = false;
      const invalidate = () => {
        if (invalidated) return;
        invalidated = true; subscriptions.delete(subscriptionId);
        void invoke(owner.port, { type: "unsubscribe_observations", subscriptionId: owner.remoteId }).catch(() => undefined);
      };
      options?.signal?.addEventListener("abort", invalidate, {once: true});
      try {
        if (options?.signal?.aborted) { invalidate(); throw new GuaWebError("aborted", "Observation poll ended."); }
        const result = await invoke(owner.port, { type: "poll_observations", subscriptionId: owner.remoteId }, options);
        if (invalidated) throw new GuaWebError("aborted", "Observation poll ended.");
        return parseObserveTransport(result, "changes");
      } catch (error) { invalidate(); throw error; }
      finally { options?.signal?.removeEventListener("abort", invalidate); }
    };
    bridge.unsubscribeObservations = async (subscriptionId, options) => {
      const owner = subscriptions.get(observeSubscriptionId(subscriptionId));
      if (!owner) return;
      // Cleanup targets the captured port even after the global port is replaced.
      await invoke(owner.port, { type: "unsubscribe_observations", subscriptionId: owner.remoteId }, options); subscriptions.delete(subscriptionId);
    };
    bridge.disposeObservations = async () => { generation++; const owners = [...subscriptions.values()]; subscriptions.clear();
      await Promise.all(owners.map(owner => invoke(owner.port, { type: "unsubscribe_observations", subscriptionId: owner.remoteId }).catch(() => undefined))); };
  }
  if (options.world) {
    bridge.getWorldObjectTree = async (callOptions) => parseWorldObjectTree(await invoke(port, { type: "get_world_object_tree" }, callOptions));
    bridge.findWorldObjects = async (selector, callOptions) => parseWorldQueryResult(await invoke(port, worldQueryCommand(selector), callOptions), selector);
  }
  if (options.gameInput) {
    bridge.getGameInputCapabilities = async () => parseGameInputCapabilities(await invoke(port, { type: "get_game_input_capabilities" }));
    bridge.getGameInputActions = async () => parseGameInputActions(await invoke(port, { type: "get_game_input_actions" })) as GuaGameInputActionMap;
    bridge.findGameInputActions = async (selector) => parseGameInputActionSearch(await invoke(port, { type: "find_game_input_actions", ...selector })) as GuaGameInputActionSearchResult;
    bridge.getGameInputActionsV2 = async () => parseGameInputActions(await invoke(port, { type: "get_game_input_actions_v2" }), 2) as import("./index.js").GuaGameInputActionMapV2;
    bridge.findGameInputActionsV2 = async (selector) => parseGameInputActionSearch(await invoke(port, { type: "find_game_input_actions_v2", ...selector }), 2) as import("./index.js").GuaGameInputActionSearchResultV2;
    bridge.getGameInputState = async () => parseGameInputState(await invoke(port, { type: "get_game_input_state" }));
    bridge.performGameInput = async (request, callOptions) => parseGameInputCompletion(await invoke(
      port, { type: "perform_game_input", request: validateGameInputRequest(request) }, callOptions,
    ));
  }
  return bridge;
}

export function createGodotWebBridge(portName = "__guaGodotWebPort", options: GuaInPageBridgeOptions = {}): GuaBrowserBridge {
  return createGuaInPageBridge(globalPort(portName, "Godot Web Export"), { ...options, world: options.world ?? true, gameInput: options.gameInput ?? true });
}

export function createUnityWebGlBridge(portName = "__guaUnityWebPort", options: GuaInPageBridgeOptions = {}): GuaBrowserBridge {
  return createGuaInPageBridge(globalPort(portName, "Unity WebGL"), { ...options, world: options.world ?? true, gameInput: options.gameInput ?? true });
}

function worldQueryCommand(selector: GuaWorldSelector): GuaInPageCommand {
  const state = selector.state;
  return compact({
    type: "query_world_objects" as const,
    worldId: selector.id,
    kind: selector.kind,
    label: selector.label,
    tag: selector.tag,
    parentId: selector.parentId,
    directChild: selector.directChild ? 1 as const : undefined,
    visibleToPlayer: filter(selector.visibleToPlayer),
    active: filter(selector.active),
    stateKey: state?.key,
    stateType: state === undefined ? undefined : state.value === null ? 0 as const
      : typeof state.value === "string" ? 1 as const : typeof state.value === "number" ? 2 as const : 3 as const,
    stateString: typeof state?.value === "string" ? state.value : undefined,
    stateNumber: typeof state?.value === "number" ? state.value : undefined,
    stateBool: typeof state?.value === "boolean" ? state.value : undefined,
    relativeToObjectId: selector.near?.relativeToObjectId,
    maxDistance: selector.near?.maxDistance,
    limit: selector.limit,
  }) as GuaInPageCommand;
}

function filter(value: boolean | undefined): 0 | 1 | 2 | undefined {
  return value === undefined ? undefined : value ? 2 : 1;
}

function compact<T extends Record<string, unknown>>(value: T): T {
  return Object.fromEntries(Object.entries(value).filter(([, item]) => item !== undefined)) as T;
}

function globalPort(name: string, engine: string): GuaInPagePort {
  const resolvePort = () => {
    const port = (globalThis as Record<string, unknown>)[name] as GuaInPagePort | undefined;
    if (!port || typeof port.invoke !== "function") throw new GuaWebError("engine_unsupported", `${engine} did not install the same-page Gua port '${name}'.`);
    return port;
  };
  const dynamicPort: GuaInPagePort = {
    get capabilities() { return ((globalThis as Record<string, unknown>)[name] as GuaInPagePort | undefined)?.capabilities; },
    async invoke(command, options) {
      return await resolvePort().invoke(command, options);
    },
  };
  portResolvers.set(dynamicPort, resolvePort);
  return dynamicPort;
}

async function invoke(port: GuaInPagePort, command: GuaInPageCommand, options?: GuaBridgeCallOptions): Promise<unknown> {
  try { return await port.invoke(command, options); }
  catch (error) {
    if (error instanceof GuaWebError) throw error;
    const record = asRecord(error);
    if (typeof record?.code === "string" && typeof record.message === "string") {
      const code = isErrorCode(record.code) ? record.code : "action_failed";
      throw new GuaWebError(code, record.message, { engineCode: record.code });
    }
    throw error;
  }
}

function parseTree(value: unknown): GuaUiTree {
  const parsed = parseJson(value);
  const record = asRecord(parsed);
  if (!record || !hasOnlyProperties(record, treeProperties) ||
      typeof record.screen !== "string" || record.screen.length === 0 ||
      !Array.isArray(record.nodes) || !record.nodes.every(isProtocolNode)) {
    throw new GuaWebError("invalid_request", "The engine returned an invalid protocol UI tree.");
  }
  const hasMetadata = record.schemaVersion !== undefined || record.sessionEpoch !== undefined ||
    record.frameSequence !== undefined || record.revision !== undefined;
  if (!hasMetadata) {
    return { ...record, schemaVersion: 2, frameSequence: 0, revision: 0 } as unknown as GuaUiTree;
  }
  if (record.schemaVersion !== 2 ||
      (record.sessionEpoch !== undefined &&
        (!Number.isInteger(record.sessionEpoch) || (record.sessionEpoch as number) < 1)) ||
      !Number.isInteger(record.frameSequence) || (record.frameSequence as number) < 0 ||
      !Number.isInteger(record.revision) || (record.revision as number) < 0) {
    throw new GuaWebError("invalid_request", "The engine returned an invalid protocol UI tree.");
  }
  return parsed as GuaUiTree;
}

const treeProperties = new Set(["schemaVersion", "sessionEpoch", "frameSequence", "revision", "screen", "nodes"]);
const nodeProperties = new Set([
  "id", "parentId", "role", "label", "text", "value", "visible", "enabled", "bounds", "state", "actions",
]);
const boundProperties = new Set(["x", "y", "w", "h"]);
const stateProperties = new Set([
  "focused", "hovered", "pressed", "checked", "selected", "caretPosition", "selectionStart", "selectionEnd",
  "scrollX", "scrollY", "scrollMaxX", "scrollMaxY", "rangeValue", "rangeMin", "rangeMax", "selectedIndex", "value",
]);
const nodeRoles = new Set([
  "button", "text", "image", "checkbox", "radio", "slider", "textbox", "list", "listitem", "panel", "screen",
  "dialog", "menu", "menuitem", "combobox", "tablist", "tab", "scrollarea",
]);
const nodeActions = new Set(["click", "focus", "set_value", "set_checked", "select", "scroll", "press_key"]);

function isProtocolNode(value: unknown): boolean {
  const node = asRecord(value);
  const bounds = asRecord(node?.bounds);
  if (!node || !hasOnlyProperties(node, nodeProperties) ||
      !isNonEmptyString(node.id) || !isOptionalNonEmptyString(node.parentId) ||
      typeof node.role !== "string" || !nodeRoles.has(node.role) ||
      !isOptionalString(node.label) || !isOptionalString(node.text) || !isOptionalProtocolValue(node.value) ||
      typeof node.visible !== "boolean" || typeof node.enabled !== "boolean" ||
      !bounds || !hasOnlyProperties(bounds, boundProperties) ||
      !isOptionalFiniteNumber(bounds.x) || !isOptionalFiniteNumber(bounds.y) ||
      !isOptionalNonNegativeNumber(bounds.w) || !isOptionalNonNegativeNumber(bounds.h) ||
      !isProtocolState(node.state) || !Array.isArray(node.actions) ||
      !node.actions.every(action => typeof action === "string" && nodeActions.has(action)) ||
      new Set(node.actions).size !== node.actions.length) {
    return false;
  }
  return true;
}

function isProtocolState(value: unknown): boolean {
  if (value === undefined) return true;
  const state = asRecord(value);
  if (!state || !hasOnlyProperties(state, stateProperties)) return false;
  for (const key of ["focused", "hovered", "pressed", "checked", "selected"]) {
    if (state[key] !== undefined && typeof state[key] !== "boolean") return false;
  }
  for (const key of ["caretPosition", "selectionStart", "selectionEnd"]) {
    if (state[key] !== undefined && (!Number.isInteger(state[key]) || (state[key] as number) < 0)) return false;
  }
  for (const key of ["scrollX", "scrollY", "rangeValue", "rangeMin", "rangeMax"]) {
    if (state[key] !== undefined && !isFiniteNumber(state[key])) return false;
  }
  for (const key of ["scrollMaxX", "scrollMaxY"]) {
    if (state[key] !== undefined && !isNonNegativeNumber(state[key])) return false;
  }
  if (state.selectedIndex !== undefined &&
      (!Number.isInteger(state.selectedIndex) || (state.selectedIndex as number) < -1)) return false;
  return isOptionalProtocolValue(state.value);
}

function hasOnlyProperties(record: Record<string, unknown>, allowed: Set<string>): boolean {
  return Object.keys(record).every(key => allowed.has(key));
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.length > 0;
}

function isOptionalNonEmptyString(value: unknown): boolean {
  return value === undefined || isNonEmptyString(value);
}

function isOptionalString(value: unknown): boolean {
  return value === undefined || typeof value === "string";
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}

function isNonNegativeNumber(value: unknown): boolean {
  return isFiniteNumber(value) && value >= 0;
}

function isOptionalFiniteNumber(value: unknown): boolean {
  return value === undefined || isFiniteNumber(value);
}

function isOptionalNonNegativeNumber(value: unknown): boolean {
  return value === undefined || isNonNegativeNumber(value);
}

function isOptionalProtocolValue(value: unknown): boolean {
  return value === undefined || value === null || typeof value === "string" || typeof value === "boolean" || isFiniteNumber(value);
}

function validateActionRequest(value: unknown): GuaWebActionRequest {
  const request = asRecord(value);
  if (!request || !hasProperty(request, "action") || typeof request.action !== "string") invalidActionRequest();
  switch (request.action) {
    case "click":
    case "focus":
      if (!hasOnlyProperties(request, new Set(["action", "nodeId"])) ||
          !hasProperty(request, "nodeId") || !isNonEmptyString(request.nodeId)) invalidActionRequest();
      return { action: request.action, nodeId: request.nodeId };
    case "set_value":
      if (!hasOnlyProperties(request, new Set(["action", "nodeId", "value", "sensitive"])) ||
          !hasProperty(request, "nodeId") || !isNonEmptyString(request.nodeId) ||
          !hasProperty(request, "value") || typeof request.value !== "string" ||
          (hasProperty(request, "sensitive") && typeof request.sensitive !== "boolean")) invalidActionRequest();
      return {
        action: "set_value",
        nodeId: request.nodeId,
        value: request.value,
        ...(hasProperty(request, "sensitive") ? { sensitive: request.sensitive as boolean } : {}),
      };
    case "set_checked":
      if (!hasOnlyProperties(request, new Set(["action", "nodeId", "checked"])) ||
          !hasProperty(request, "nodeId") || !isNonEmptyString(request.nodeId) ||
          !hasProperty(request, "checked") || typeof request.checked !== "boolean") invalidActionRequest();
      return { action: "set_checked", nodeId: request.nodeId, checked: request.checked };
    case "select":
      if (!hasOnlyProperties(request, new Set(["action", "nodeId", "value"])) ||
          !hasProperty(request, "nodeId") || !isNonEmptyString(request.nodeId) ||
          !hasProperty(request, "value") || !isNonEmptyString(request.value)) invalidActionRequest();
      return { action: "select", nodeId: request.nodeId, value: request.value };
    case "scroll":
      if (!hasOnlyProperties(request, new Set(["action", "nodeId", "deltaX", "deltaY", "scrollUnit"])) ||
          !hasProperty(request, "nodeId") || !isNonEmptyString(request.nodeId) ||
          !hasProperty(request, "deltaX") || !isFiniteNumber(request.deltaX) ||
          !hasProperty(request, "deltaY") || !isFiniteNumber(request.deltaY) ||
          (hasProperty(request, "scrollUnit") && request.scrollUnit !== 0 && request.scrollUnit !== 1)) invalidActionRequest();
      return {
        action: "scroll",
        nodeId: request.nodeId,
        deltaX: request.deltaX,
        deltaY: request.deltaY,
        ...(hasProperty(request, "scrollUnit") ? { scrollUnit: request.scrollUnit as 0 | 1 } : {}),
      };
    case "press_key":
      if (!hasOnlyProperties(request, new Set(["action", "nodeId", "key", "modifiers"])) ||
          !hasProperty(request, "key") || !isNonEmptyString(request.key) ||
          (hasProperty(request, "nodeId") && !isNonEmptyString(request.nodeId)) ||
          (hasProperty(request, "modifiers") &&
            (!Number.isInteger(request.modifiers) || (request.modifiers as number) < 0 || (request.modifiers as number) > 15))) {
        invalidActionRequest();
      }
      return {
        action: "press_key",
        ...(hasProperty(request, "nodeId") ? { nodeId: request.nodeId as string } : {}),
        key: request.key,
        ...(hasProperty(request, "modifiers") ? { modifiers: request.modifiers as number } : {}),
      };
    default:
      invalidActionRequest();
  }
}

function hasProperty(record: Record<string, unknown>, key: string): boolean {
  return Object.prototype.hasOwnProperty.call(record, key);
}

function invalidActionRequest(): never {
  throw new GuaWebError("invalid_request", "The browser supplied an invalid Gua action request.");
}

function parseCompletion(value: unknown): GuaWebActionCompletion {
  const parsed = parseJson(value);
  const record = asRecord(parsed);
  if (!record || !Number.isInteger(record.requestId) || (record.requestId as number) < 1 ||
      typeof record.succeeded !== "boolean") {
    throw new GuaWebError("invalid_request", "The engine returned an invalid action completion.");
  }
  return parsed as GuaWebActionCompletion;
}

function parseGameInputCapabilities(value: unknown): GuaGameInputCapability[] {
  const parsed = parseJson(value);
  if (!Array.isArray(parsed) || !parsed.every((item) => typeof item === "string" && guaGameInputCapabilities.includes(item as GuaGameInputCapability))) {
    throw new GuaWebError("invalid_request", "The engine returned invalid game input capabilities.");
  }
  return [...new Set(parsed)] as GuaGameInputCapability[];
}

function parseGameInputActions(value: unknown, version = 1): GuaGameInputActionMap | import("./index.js").GuaGameInputActionMapV2 {
  const parsed = parseJson(value, version === 2);
  const record = asRecord(parsed);
  if (!record || record.schemaVersion !== version || !Number.isInteger(record.sessionEpoch) || (record.sessionEpoch as number) < 1 ||
      !Number.isInteger(record.revision) || (record.revision as number) < 0 || !isNonEmptyString(record.context) ||
      !Array.isArray(record.actions) || !record.actions.every(isGameInputAction) ||
      (version === 2 && Object.keys(record).some(key => !gameInputMapV2Properties.has(key))) ||
      !record.actions.every(action => version === 2 ? isGameInputActionV2(action) :
        !("valueSchema" in action) && !("examples" in action))) {
    throw new GuaWebError("invalid_request", "The engine returned an invalid game input action map.");
  }
  return parsed as GuaGameInputActionMap;
}

function parseGameInputActionSearch(value: unknown, version = 1): GuaGameInputActionSearchResult | import("./index.js").GuaGameInputActionSearchResultV2 {
  const parsed = parseJson(value, version === 2);
  const record = asRecord(parsed);
  if (!record || record.schemaVersion !== version || !Number.isInteger(record.sessionEpoch) || (record.sessionEpoch as number) < 1 ||
      !Number.isInteger(record.revision) || (record.revision as number) < 0 || !isNonEmptyString(record.context) ||
      !Number.isInteger(record.count) || (record.count as number) < 0 || typeof record.truncated !== "boolean" ||
      !Array.isArray(record.actions) || record.count !== record.actions.length || !record.actions.every(isGameInputAction) ||
      (version === 2 && Object.keys(record).some(key => !gameInputSearchV2Properties.has(key))) ||
      !record.actions.every(action => version === 2 ? isGameInputActionV2(action) :
        !("valueSchema" in action) && !("examples" in action)))
    throw new GuaWebError("invalid_request", "The engine returned an invalid game input action search result.");
  return parsed as GuaGameInputActionSearchResult;
}

const gameInputMapV2Properties = new Set(["schemaVersion", "sessionEpoch", "revision", "context", "actions"]);
const gameInputSearchV2Properties = new Set([...gameInputMapV2Properties, "count", "truncated"]);
const gameInputActionV2Properties = new Set([
  "id", "description", "valueType", "range", "holdable", "active", "bindings", "risk", "requiresConfirmation",
  "category", "aliases", "tags", "agentExposure", "valueSchema", "examples",
]);
const gameInputIdentifier = /^[a-z][a-z0-9_.-]*$/;
function isGameInputActionV2(value: unknown): boolean {
  const action = asRecord(value);
  if (!action || !isGameInputAction(action) || Object.keys(action).some(key => !gameInputActionV2Properties.has(key)) ||
      typeof action.valueType !== "string" ||
      !gameInputIdentifier.test(action.id as string) || !["safe", "caution", "dangerous"].includes(action.risk as string)) return false;
  if (action.category !== undefined && (!gameInputIdentifier.test(action.category as string) || (action.category as string).length > 127)) return false;
  for (const field of ["aliases", "tags"]) {
    const entries = action[field] as string[] | undefined;
    if (entries && (entries.length > 16 || new Set(entries).size !== entries.length ||
        entries.some(entry => [...entry].length > 64 || entry.includes("\0")))) return false;
  }
  return validInputActionMetadata(action);
}

function isGameInputAction(value: unknown): boolean {
  const action = asRecord(value);
  if (!action || !isNonEmptyString(action.id) || typeof action.description !== "string" ||
      !["button", "axis1d", "vector2", "text"].includes(String(action.valueType)) ||
      typeof action.holdable !== "boolean" || typeof action.active !== "boolean" ||
      !Array.isArray(action.bindings) || !action.bindings.every(isNonEmptyString) ||
      typeof action.risk !== "string" || typeof action.requiresConfirmation !== "boolean") return false;
  if (action.category !== undefined && !isNonEmptyString(action.category)) return false;
  if (action.aliases !== undefined && (!Array.isArray(action.aliases) || !action.aliases.every(isNonEmptyString))) return false;
  if (action.tags !== undefined && (!Array.isArray(action.tags) || !action.tags.every(isNonEmptyString))) return false;
  if (action.agentExposure !== undefined && action.agentExposure !== "auto" && action.agentExposure !== "private") return false;
  if (action.minimum !== undefined || action.maximum !== undefined) return false;
  if (action.range === undefined) return true;
  const range = asRecord(action.range);
  if (range === undefined) return false;
  return Object.keys(range).every((key) => key === "minimum" || key === "maximum") &&
    isFiniteNumber(range.minimum) && isFiniteNumber(range.maximum) && range.minimum <= range.maximum;
}

function parseGameInputState(value: unknown): GuaGameInputState {
  const parsed = parseJson(value);
  const record = asRecord(parsed);
  if (!record || record.schemaVersion !== 1 || !Array.isArray(record.held)) {
    throw new GuaWebError("invalid_request", "The engine returned invalid game input state.");
  }
  return parsed as GuaGameInputState;
}

function parseGameInputCompletion(value: unknown): GuaGameInputCompletion {
  const parsed = parseJson(value);
  const record = asRecord(parsed);
  if (!record || record.completed !== true || !Number.isInteger(record.requestId) || (record.requestId as number) < 1 ||
      typeof record.succeeded !== "boolean" || !Number.isInteger(record.errorCode)) {
    throw new GuaWebError("invalid_request", "The engine returned an invalid game input completion.");
  }
  return parsed as GuaGameInputCompletion;
}

function validateGameInputRequest(value: unknown): GuaGameInputRequest {
  const request = asRecord(value);
  if (!request || typeof request.type !== "string" || !gameInputRequestTypes.has(request.type) || !isFiniteJsonValue(request)) {
    throw new GuaWebError("invalid_request", "The browser supplied an invalid game input request.");
  }
  if (request.leaseMs !== undefined && (!Number.isInteger(request.leaseMs) || (request.leaseMs as number) < 1 || (request.leaseMs as number) > 60000)) {
    throw new GuaWebError("invalid_request", "leaseMs must be an integer from 1 to 60000.");
  }
  if (request.gamepadIndex !== undefined && (!Number.isInteger(request.gamepadIndex) || (request.gamepadIndex as number) < 0 || (request.gamepadIndex as number) > 3)) {
    throw new GuaWebError("invalid_request", "gamepadIndex must be an integer from 0 to 3.");
  }
  return value as GuaGameInputRequest;
}

const gameInputRequestTypes = new Set([
  "press_game_input_action", "set_game_input_action", "release_game_input_action", "release_all_game_inputs",
  "key_down", "key_up", "press_physical_key", "pointer_move", "pointer_button_down", "pointer_button_up",
  "pointer_wheel", "gamepad_button_down", "gamepad_button_up", "set_gamepad_axis", "reset_gamepad", "text_input",
]);

function isFiniteJsonValue(value: unknown): boolean {
  if (value === null || typeof value === "string" || typeof value === "boolean") return true;
  if (typeof value === "number") return Number.isFinite(value);
  if (Array.isArray(value)) return value.every(isFiniteJsonValue);
  const record = asRecord(value);
  return !!record && Object.values(record).every(isFiniteJsonValue);
}

function parseScreenshot(value: unknown): GuaScreenshot {
  const parsed = parseJson(value);
  const record = asRecord(parsed);
  if (!record || typeof record.dataUri !== "string" || typeof record.width !== "number" || typeof record.height !== "number") {
    throw new GuaWebError("engine_unsupported", "The engine returned no supported screenshot payload.");
  }
  return parsed as GuaScreenshot;
}

function parseJson(value: unknown, inputMetadata = false): unknown {
  if (typeof value !== "string") return value;
  try { return inputMetadata ? parseInputActionMetadataJson(value) : JSON.parse(value); }
  catch { throw new GuaWebError("invalid_request", inputMetadata ? "The engine returned invalid game input metadata JSON." : "The engine returned malformed JSON."); }
}

function asRecord(value: unknown): Record<string, unknown> | undefined {
  return value !== null && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}

function isErrorCode(value: string): value is GuaWebError["code"] {
  return [
    "webmcp_unsupported", "engine_unsupported", "invalid_request", "node_not_found", "hidden", "disabled",
    "unsupported_action", "action_failed", "timeout", "aborted",
  ].includes(value);
}
