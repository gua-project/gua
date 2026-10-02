import path from "node:path";
import { spatialTools, spatialBatchArguments, type GuaSpatialAdvertisement, type GuaSpatialBatch, type GuaSpatialBatchResult } from "gua-world-tools";
import { ObserveWireRejectionError, decodeObserveWireResponse, observeTools, observeSubscriptionId, parseObserveTransport } from "gua-value";

import {
  guaPhysicalKeyboardCodes,
  guaWebMcpToolDefinitions,
  type GuaToolDefinition,
  type GuaWebMcpToolName,
} from "gua-webmcp";

import {
  GuaAutomationManager,
  type GuaRecording,
  type RecordedAction,
  type RecordingStep,
  validateRecording,
} from "./automation.js";
import { parseWorldQueryResult, selectorFromArguments, worldObservationTools, type GuaWorldQueryResult, type GuaWorldObjectTree, type GuaWorldSelector } from "gua-world-tools";

type JsonRpcId = string | number | null;

interface JsonRpcRequest {
  jsonrpc?: "2.0";
  id?: JsonRpcId;
  method: string;
  params?: unknown;
}

interface JsonRpcResponse {
  jsonrpc: "2.0";
  id: JsonRpcId;
  result?: unknown;
  error?: JsonRpcError;
}

interface JsonRpcError {
  code: number;
  message: string;
  data?: unknown;
}

interface McpRequestState {
  active: Map<string, AbortController>;
  tasks: Set<Promise<void>>;
}

interface GuaBounds {
  x: number;
  y: number;
  w: number;
  h: number;
}

interface GuaNodeState {
  focused?: boolean;
  hovered?: boolean;
  pressed?: boolean;
  checked?: boolean;
  value?: number | string | boolean | null;
}

interface GuaNode {
  id: string;
  parentId?: string;
  role: string;
  label?: string;
  visible: boolean;
  enabled: boolean;
  bounds: GuaBounds;
  state?: GuaNodeState;
  actions: string[];
}

interface GuaUiTree {
  sessionEpoch?: number;
  frameSequence?: number;
  revision?: number;
  screen: string;
  nodes: GuaNode[];
}

interface GuaLogEntry {
  sequence: number;
  level: "trace" | "debug" | "info" | "warn" | "error";
  message: string;
}

interface GuaScreenshot {
  dataUri: string;
  width: number;
  height: number;
}

type GuaBridgeResponse =
  | { id: number; ok: true; result: BridgeResult }
  | { id: number; ok: false; error: string };

interface McpTool {
  name: GuaMcpTool;
  description: string;
  inputSchema: Record<string, unknown>;
}

type ToolResult = {
  content: Array<{ type: "text"; text: string }>;
  isError?: boolean;
};

interface GuaActionReceipt { requestId: number }
type ActionFailureStage = "before_send" | "awaiting_receipt" | "awaiting_completion" | "awaiting_observation";

export class GuaActionOutcomeError extends Error {
  constructor(
    readonly stage: ActionFailureStage,
    readonly requestSent: boolean,
    readonly requestId?: number,
    readonly bridgeCommandId?: number,
  ) {
    super(requestSent
      ? "Gua action was sent, but completion could not be confirmed. Do not retry automatically."
      : "Gua action was not sent to the bridge.");
  }

  diagnostics(): Record<string, unknown> {
    return compactResult({ error: this.message,
      outcome: this.requestSent ? "completion_unconfirmed" : "not_sent",
      stage: this.stage, requestSent: this.requestSent,
      requestId: this.requestId, bridgeCommandId: this.bridgeCommandId });
  }
}

class BridgeRejectionError extends Error {}
interface GuaActionEvent {
  requestId: number;
  action: number;
  succeeded: boolean;
  error: number;
  nodeId: string;
  value: string;
  sensitive: boolean;
  sessionEpoch: number;
  frameSequence: number;
  revision: number;
}

interface GuaContextStatus {
  sessionEpoch: number;
  frameSequence: number;
}

type BridgeResult = unknown;

export interface GuaMcpServerOptions {
  bridgeUrl?: string;
  artifactDirectory?: string;
}

const defaultBridgeUrl = "ws://127.0.0.1:8765";
const clockPauseResponseTimeoutMs = 11_000;

export const guaMcpTools = [
  "get_spatial_info", "query_spatial_batch",
  "get_observe_snapshot", "subscribe_observations", "poll_observations", "unsubscribe_observations",
  "get_ui_tree",
  "get_world_object_tree",
  "find_world_objects",
  "wait_for_world_object",
  "click_node",
  "focus_node",
  "set_value",
  "set_checked",
  "select",
  "scroll",
  "press_key",
  "get_game_input_actions_v2",
  "find_game_input_actions_v2",
  "get_game_input_actions",
  "find_game_input_actions",
  "press_game_input_action",
  "set_game_input_action",
  "release_game_input_action",
  "get_game_input_state",
  "release_all_game_inputs",
  "key_down",
  "key_up",
  "press_physical_key",
  "pointer_move",
  "pointer_button_down",
  "pointer_button_up",
  "pointer_wheel",
  "gamepad_button_down",
  "gamepad_button_up",
  "set_gamepad_axis",
  "reset_gamepad",
  "text_input",
  "wait_for_node",
  "get_screenshot",
  "get_logs",
  "get_clock",
  "clock_install",
  "clock_pause",
  "clock_run_for",
  "clock_resume",
  "start_recording",
  "stop_recording",
  "save_recording",
  "replay_recording",
  "compare_screenshot",
  "get_visual_artifacts",
  "run_test",
] as const;

export type GuaMcpTool = (typeof guaMcpTools)[number];

export const guaMcpToolDefinitions: readonly McpTool[] = [
  ...spatialTools,
  ...observeTools,
  ...guaWebMcpToolDefinitions.map(toMcpToolDefinition),
  ...worldObservationTools,
  { name: "get_game_input_actions", description: "Read the host-published semantic game action map.", inputSchema: objectSchema({}) },
  { name: "find_game_input_actions", description: "Search the current host-authorized semantic game action map.", inputSchema: objectSchema({
    id: { type: "string", pattern: "^[a-z][a-z0-9_.-]*$", maxLength: 127 }, query: { type: "string", minLength: 1, maxLength: 128, pattern: "^[^\\u0000]+$" },
    valueType: { type: "string", enum: ["button", "axis1d", "vector2", "text"] }, active: { type: "boolean" },
    context: { type: "string", minLength: 1, pattern: "^[^\\u0000]+$" }, category: { type: "string", pattern: "^[a-z][a-z0-9_.-]*$", maxLength: 127 },
    tags: { type: "array", maxItems: 16, uniqueItems: true, items: { type: "string", minLength: 1, maxLength: 64, pattern: "^[^\\u0000]+$" } },
    limit: { type: "integer", minimum: 1, maximum: 100, default: 20 },
  }) },
  { name: "get_game_input_actions_v2", description: "Explicitly request the action map with value schema and Set examples; requires semantic_game_input_metadata_v1.", inputSchema: objectSchema({}) },
  { name: "find_game_input_actions_v2", description: "Explicitly search with value schema and Set examples; metadata is not execution authority.", inputSchema: objectSchema({
    id: { type: "string", pattern: "^[a-z][a-z0-9_.-]*$", maxLength: 127 }, query: { type: "string", minLength: 1, maxLength: 128, pattern: "^[^\\u0000]+$" },
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
    leaseMs: { type: "integer", minimum: 1, maximum: 60000 },
    confirmed: { type: "boolean" }, sensitive: { type: "boolean" }, secretKey: stringProperty("Secret used only for recording replay."),
  }, ["actionId", "value"]) },
  { name: "release_game_input_action", description: "Release a held semantic game action.", inputSchema: objectSchema({
    actionId: stringProperty("Stable host-published action id."),
  }, ["actionId"]) },
  { name: "get_game_input_state", description: "Inspect held inputs owned by this MCP connection.", inputSchema: objectSchema({}) },
  { name: "release_all_game_inputs", description: "Release every semantic and raw input owned by this MCP connection.", inputSchema: objectSchema({}) },
  ...(["key_down", "key_up", "press_physical_key"] as const).map((name) => ({
    name, description: `${name} using a supported W3C KeyboardEvent.code identifier.`,
    inputSchema: objectSchema({ code: { type: "string", enum: guaPhysicalKeyboardCodes,
      description: "Supported W3C KeyboardEvent.code such as KeyW or NumpadEnter." }, leaseMs: { type: "integer", minimum: 1, maximum: 60000 } }, ["code"]),
  })),
  { name: "pointer_move", description: "Move the engine pointer using absolute or delta coordinates.", inputSchema: objectSchema({
    mode: { type: "string", enum: ["absolute", "delta"] }, coordinateSpace: { type: "string", enum: ["viewport_normalized", "viewport_pixels"] },
    x: { type: "number" }, y: { type: "number" },
  }, ["mode", "x", "y"]) },
  ...(["pointer_button_down", "pointer_button_up"] as const).map((name) => ({
    name, description: `${name} for an engine pointer button.`,
    inputSchema: objectSchema({ button: { type: "string", enum: ["primary", "secondary", "auxiliary", "back", "forward"] },
      leaseMs: { type: "integer", minimum: 1, maximum: 60000 } }, ["button"]),
  })),
  { name: "pointer_wheel", description: "Inject pointer wheel movement.", inputSchema: {
    ...objectSchema({ deltaX: { type: "number" }, deltaY: { type: "number" }, wheelUnit: { type: "string", enum: ["pixels", "lines"] } }),
    anyOf: [{ required: ["deltaX"] }, { required: ["deltaY"] }],
  } },
  ...(["gamepad_button_down", "gamepad_button_up"] as const).map((name) => ({
    name, description: `${name} using Standard Gamepad mapping names.`,
    inputSchema: objectSchema({ gamepadIndex: { type: "integer", minimum: 0, maximum: 3 }, button: stringProperty("Standard Gamepad button name."),
      leaseMs: { type: "integer", minimum: 1, maximum: 60000 } }, ["button"]),
  })),
  { name: "set_gamepad_axis", description: "Set and hold a Standard Gamepad axis.", inputSchema: objectSchema({
    gamepadIndex: { type: "integer", minimum: 0, maximum: 3 },
    axis: { type: "string", enum: ["left_stick_x", "left_stick_y", "right_stick_x", "right_stick_y"] },
    value: { type: "number", minimum: -1, maximum: 1 }, leaseMs: { type: "integer", minimum: 1, maximum: 60000 },
  }, ["axis", "value"]) },
  { name: "reset_gamepad", description: "Reset one virtual gamepad to neutral.", inputSchema: objectSchema({
    gamepadIndex: { type: "integer", minimum: 0, maximum: 3 },
  }) },
  { name: "text_input", description: "Inject text through the engine input route.", inputSchema: objectSchema({
    text: { type: "string", maxLength: 40 }, sensitive: { type: "boolean" }, secretKey: stringProperty("Secret used only for recording replay."),
  }, ["text"]) },
  {
    name: "get_logs",
    description: "Read ordered runtime logs from the running game bridge.",
    inputSchema: objectSchema({}),
  },
  { name: "get_clock", description: "Read the opt-in Gua virtual clock state.", inputSchema: objectSchema({}) },
  { name: "clock_install", description: "Install the Gua virtual clock in running state.", inputSchema: objectSchema({
    initialTimeMs: { type: "number", minimum: 0 }, stepMs: { type: "number", exclusiveMinimum: 0 },
  }) },
  { name: "clock_pause", description: "Pause work explicitly driven by GuaClock.", inputSchema: objectSchema({}) },
  { name: "clock_run_for", description: "Advance a paused GuaClock without waiting for wall time.", inputSchema: objectSchema({
    durationMs: { type: "number", minimum: 0 }, stepMs: { type: "number", exclusiveMinimum: 0 },
  }, ["durationMs"]) },
  { name: "clock_resume", description: "Resume real-time advancement of GuaClock.", inputSchema: objectSchema({}) },
  {
    name: "start_recording",
    description: "Start recording semantic actions invoked through this MCP server.",
    inputSchema: objectSchema({}),
  },
  {
    name: "stop_recording",
    description: "Stop the active recording and return a recording.schema.json-compatible document.",
    inputSchema: objectSchema({}),
  },
  {
    name: "save_recording",
    description: "Save the most recently completed recording under the configured Gua artifact directory.",
    inputSchema: objectSchema({ name: stringProperty("Safe recording name without a directory path.") }, ["name"]),
  },
  {
    name: "replay_recording",
    description: "Replay a saved or inline recording and wait for each semantic action's correlated completion.",
    inputSchema: objectSchema({
      name: stringProperty("Saved recording name. Omit when providing recording or replaying the last recording."),
      recording: { type: "object", description: "Inline recording.schema.json-compatible document." },
      secrets: { type: "object", additionalProperties: { type: "string" }, description: "secretKey to plaintext value map." },
      timingMode: { type: "string", enum: ["prefer_conditions", "preserve_delays"] },
      timeoutMs: { type: "integer", minimum: 1, maximum: 300000 },
    }),
  },
  {
    name: "compare_screenshot",
    description: "Compare the latest PNG screenshot with an explicit name/variant baseline and write machine-readable artifacts.",
    inputSchema: objectSchema({
      name: stringProperty("Stable test name."),
      variant: stringProperty("Explicit renderer/OS variant. Defaults to default."),
      updateBaseline: { type: "boolean", description: "Create or replace an approved baseline explicitly." },
      pixelThreshold: { type: "number", minimum: 0, maximum: 255 },
      maxDifferentPixelRatio: { type: "number", minimum: 0, maximum: 1 },
      masks: {
        type: "array",
        items: objectSchema({
          x: { type: "integer", minimum: 0 }, y: { type: "integer", minimum: 0 },
          width: { type: "integer", minimum: 1 }, height: { type: "integer", minimum: 1 },
        }, ["x", "y", "width", "height"]),
      },
    }, ["name"]),
  },
  {
    name: "get_visual_artifacts",
    description: "List visual comparison artifacts and return the latest comparison manifest.",
    inputSchema: objectSchema({ name: stringProperty("Optional stable test name filter.") }),
  },
  {
    name: "run_test",
    description: "Run a small protocol-level UI test made of wait and click steps.",
    inputSchema: objectSchema({
      steps: {
        type: "array",
        minItems: 1,
        items: {
          type: "object",
          additionalProperties: false,
          required: ["action", "nodeId"],
          properties: {
            action: {
              type: "string",
              enum: ["wait_for_node", "click_node"],
            },
            nodeId: stringProperty("The target Gua node id."),
            timeoutMs: {
              type: "integer",
              minimum: 0,
            },
          },
        },
      },
    }, ["steps"]),
  },
];

export async function runGuaMcpServer(options: GuaMcpServerOptions = {}): Promise<void> {
  const bridgeUrl = options.bridgeUrl ?? Bun.env.GUA_BRIDGE_URL ?? defaultBridgeUrl;
  const bridge = new GuaBridgeClient(bridgeUrl);
  const artifactDirectory = path.resolve(options.artifactDirectory ?? Bun.env.GUA_ARTIFACT_DIR ?? ".gua");
  const automation = new GuaAutomationManager(artifactDirectory);

  writeLog("info", `Gua MCP server connecting to ${bridgeUrl}; artifacts: ${artifactDirectory}`);

  let pending = "";
  const decoder = new TextDecoder();
  const requestState: McpRequestState = { active: new Map(), tasks: new Set() };
  try {
    for await (const chunk of Bun.stdin.stream()) {
      pending += decoder.decode(chunk, { stream: true });
      pending = await drainPendingLines(pending, bridge, automation, requestState);
    }

    pending += decoder.decode();
    const finalLine = pending.trim();
    if (finalLine.length > 0) {
      await handleLine(finalLine, bridge, automation, requestState);
    }
    await Promise.allSettled([...requestState.tasks]);
  } finally {
    bridge.close();
  }
}

async function drainPendingLines(input: string, bridge: GuaBridgeClient, automation: GuaAutomationManager,
  requestState: McpRequestState): Promise<string> {
  let pending = input;
  while (true) {
    const newlineIndex = pending.indexOf("\n");
    if (newlineIndex < 0) {
      return pending;
    }

    const line = pending.slice(0, newlineIndex).trim();
    pending = pending.slice(newlineIndex + 1);

    if (line.length === 0) {
      continue;
    }

    await handleLine(line, bridge, automation, requestState);
  }
}

async function handleLine(line: string, bridge: GuaBridgeClient, automation: GuaAutomationManager,
  requestState: McpRequestState): Promise<void> {
  let request: unknown;
  try {
    request = JSON.parse(line) as JsonRpcRequest;
  } catch (error) {
    writeResponse({
      jsonrpc: "2.0",
      id: null,
      error: jsonRpcError(-32700, `Invalid JSON: ${(error as Error).message}`),
    });
    return;
  }

  if (!isJsonRpcRequest(request)) {
    writeResponse({
      jsonrpc: "2.0",
      id: isRecord(request) && isJsonRpcId(request.id) ? request.id : null,
      error: jsonRpcError(-32600, "Invalid JSON-RPC request."),
    });
    return;
  }

  if (request.id === undefined) {
    await handleNotification(request, requestState.active);
    return;
  }

  const key = jsonRpcIdKey(request.id);
  const controller = new AbortController();
  requestState.active.set(key, controller);
  let task!: Promise<void>;
  task = (async () => {
    try {
      writeResponse({
        jsonrpc: "2.0",
        id: request.id as JsonRpcId,
        result: await handleRequest(request, bridge, automation, controller.signal),
      });
    } catch (error) {
      writeResponse({
        jsonrpc: "2.0",
        id: request.id as JsonRpcId,
        error: toJsonRpcError(error),
      });
    } finally {
      if (requestState.active.get(key) === controller) requestState.active.delete(key);
      requestState.tasks.delete(task);
    }
  })();
  requestState.tasks.add(task);
}

async function handleNotification(request: JsonRpcRequest, active: Map<string, AbortController>): Promise<void> {
  if (request.method === "notifications/cancelled") {
    if (isRecord(request.params) && isJsonRpcId(request.params.requestId)) {
      active.get(jsonRpcIdKey(request.params.requestId))?.abort(request.params.reason);
    }
    return;
  }

  if (request.method === "notifications/initialized") {
    return;
  }
}

async function handleRequest(request: JsonRpcRequest, bridge: GuaBridgeClient, automation: GuaAutomationManager,
  signal: AbortSignal): Promise<unknown> {
  switch (request.method) {
    case "initialize":
      return {
        protocolVersion: "2024-11-05",
        capabilities: {
          tools: {},
        },
        serverInfo: {
          name: "gui-mcp",
          version: "0.0.0",
        },
      };
    case "ping":
      return {};
    case "tools/list":
      return { tools: guaMcpToolDefinitions };
    case "tools/call":
      return callTool(request.params, bridge, automation, signal);
    default:
      throw new RpcFailure(-32601, `Unsupported MCP method: ${request.method}`);
  }
}

async function callTool(params: unknown, bridge: GuaBridgeClient, automation: GuaAutomationManager,
  signal: AbortSignal): Promise<ToolResult> {
  if (!isRecord(params) || typeof params.name !== "string") {
    throw new RpcFailure(-32602, "tools/call requires a tool name.");
  }

  const name = params.name;
  if (!isGuaMcpTool(name)) {
    throw new RpcFailure(-32602, `Unknown Gua MCP tool: ${name}`);
  }

  try {
    if (observeTools.some(tool => tool.name === name) && params.arguments !== undefined && !isRecord(params.arguments)) throw new Error("Invalid Observe arguments.");
    const result = await executeTool(name, isRecord(params.arguments) ? params.arguments : {}, bridge, automation, signal);
    return textResult(result);
  } catch (error) {
    if (error instanceof GuaActionOutcomeError) return textResult(error.diagnostics(), true);
    return textResult({ error: (error as Error).message }, true);
  }
}

async function executeTool(
  name: GuaMcpTool,
  args: Record<string, unknown>,
  bridge: GuaBridgeClient,
  automation: GuaAutomationManager,
  signal?: AbortSignal,
): Promise<unknown> {
  switch (name) {
    case "get_spatial_info":
      if (Object.keys(args).length) throw new Error("invalid_request");
      return bridge.spatialInfo(signal);
    case "query_spatial_batch": return bridge.spatialBatch(spatialBatchArguments(args), signal);
    case "get_observe_snapshot": case "subscribe_observations": case "poll_observations": case "unsubscribe_observations": {
      const needsId = name === "poll_observations" || name === "unsubscribe_observations";
      if (Object.keys(args).some(key => !needsId || key !== "subscriptionId")) throw new Error("Invalid Observe arguments.");
      return bridge.observeCommand(name, needsId ? observeSubscriptionId(args.subscriptionId) : undefined, signal);
    }
    case "get_ui_tree":
      return bridge.getUiTree();
    case "get_world_object_tree":
      return bridge.getWorldObjectTree();
    case "find_world_objects":
      return bridge.findWorldObjects(selectorFromArguments(args), undefined, signal);
    case "wait_for_world_object":
      return bridge.waitForWorldObject(selectorFromArguments(args), readIntegerArg(args, "timeoutMs", 5000), signal);
    case "click_node":
      return performAndRecord(bridge, automation, { action: "click", nodeId: readStringArg(args, "nodeId") });
    case "focus_node":
      return performAndRecord(bridge, automation, { action: "focus", nodeId: readStringArg(args, "nodeId") });
    case "set_value": {
      const sensitive = readBooleanArg(args, "sensitive", false);
      const secretKey = readOptionalStringArg(args, "secretKey");
      if (sensitive && secretKey === undefined) throw new Error("Sensitive set_value requires secretKey.");
      return performAndRecord(bridge, automation, {
        action: "set_value", nodeId: readStringArg(args, "nodeId"), value: readStringArg(args, "value", true),
        sensitive, secretKey,
      });
    }
    case "set_checked":
      return performAndRecord(bridge, automation, {
        action: "set_checked", nodeId: readStringArg(args, "nodeId"), checked: readRequiredBooleanArg(args, "checked"),
      });
    case "select":
      return performAndRecord(bridge, automation, {
        action: "select", nodeId: readStringArg(args, "nodeId"), value: readStringArg(args, "value"),
      });
    case "scroll":
      return performAndRecord(bridge, automation, {
        action: "scroll", nodeId: readStringArg(args, "nodeId"),
        deltaX: readNumberArg(args, "deltaX", 0), deltaY: readNumberArg(args, "deltaY", 0),
        scrollUnit: readIntegerArg(args, "scrollUnit", 0),
      });
    case "press_key":
      return performAndRecord(bridge, automation, {
        action: "press_key", nodeId: readOptionalStringArg(args, "nodeId"), key: readStringArg(args, "key"),
        modifiers: readIntegerArg(args, "modifiers", 0),
      });
    case "get_game_input_actions_v2": return bridge.getGameInputActionsV2();
    case "find_game_input_actions_v2": return bridge.findGameInputActionsV2(gameInputSearchCommand(args));
    case "get_game_input_actions": return bridge.getGameInputActions();
    case "find_game_input_actions": return bridge.findGameInputActions(gameInputSearchCommand(args));
    case "get_game_input_state": return bridge.getGameInputState();
    case "press_game_input_action":
      return performGameInput(bridge, automation, { type: name, actionId: readStringArg(args, "actionId"),
        confirmed: readBooleanArg(args, "confirmed", false) }, 10000, signal);
    case "set_game_input_action": {
      if (!("value" in args)) throw new Error("set_game_input_action requires value.");
      const sensitive = readBooleanArg(args, "sensitive", false);
      const secretKey = readOptionalStringArg(args, "secretKey");
      if (sensitive && secretKey === undefined) throw new Error("Sensitive game input requires secretKey.");
      return performGameInput(bridge, automation, compactResult({ type: name, actionId: readStringArg(args, "actionId"),
        value: sensitive ? args.value : args.value, leaseMs: readIntegerArg(args, "leaseMs", 5000),
        confirmed: readBooleanArg(args, "confirmed", false), sensitive, secretKey }) as GameInputCommandInput, 10000, signal);
    }
    case "release_game_input_action":
      return performGameInput(bridge, automation, { type: name, actionId: readStringArg(args, "actionId") }, 10000, signal);
    case "release_all_game_inputs":
      return performGameInput(bridge, automation, { type: name }, 10000, signal);
    case "key_down":
    case "key_up":
    case "press_physical_key":
      return performGameInput(bridge, automation, { type: name, code: readStringArg(args, "code"),
        leaseMs: readIntegerArg(args, "leaseMs", 5000) }, 10000, signal);
    case "pointer_move": {
      const mode = readEnumArg(args, "mode", ["absolute", "delta"]);
      return performGameInput(bridge, automation, compactResult({ type: name, mode,
        coordinateSpace: mode === "absolute"
          ? readEnumArg(args, "coordinateSpace", ["viewport_normalized", "viewport_pixels"], "viewport_pixels")
          : undefined,
        x: readNumberArg(args, "x", 0), y: readNumberArg(args, "y", 0) }) as GameInputCommandInput, 10000, signal);
    }
    case "pointer_button_down":
    case "pointer_button_up":
      return performGameInput(bridge, automation, { type: name,
        button: readEnumArg(args, "button", ["primary", "secondary", "auxiliary", "back", "forward"]),
        leaseMs: readIntegerArg(args, "leaseMs", 5000) }, 10000, signal);
    case "pointer_wheel":
      return performGameInput(bridge, automation, { type: name, deltaX: readNumberArg(args, "deltaX", 0),
        deltaY: readNumberArg(args, "deltaY", 0), wheelUnit: readEnumArg(args, "wheelUnit", ["pixels", "lines"], "pixels") }, 10000, signal);
    case "gamepad_button_down":
    case "gamepad_button_up":
      return performGameInput(bridge, automation, { type: name, gamepadIndex: readIntegerArg(args, "gamepadIndex", 0),
        button: readStringArg(args, "button"), leaseMs: readIntegerArg(args, "leaseMs", 5000) }, 10000, signal);
    case "set_gamepad_axis":
      return performGameInput(bridge, automation, { type: name, gamepadIndex: readIntegerArg(args, "gamepadIndex", 0),
        axis: readEnumArg(args, "axis", ["left_stick_x", "left_stick_y", "right_stick_x", "right_stick_y"]),
        value: readNumberArg(args, "value", 0), leaseMs: readIntegerArg(args, "leaseMs", 5000) }, 10000, signal);
    case "reset_gamepad":
      return performGameInput(bridge, automation, { type: name, gamepadIndex: readIntegerArg(args, "gamepadIndex", 0) }, 10000, signal);
    case "text_input": {
      const sensitive = readBooleanArg(args, "sensitive", false);
      const secretKey = readOptionalStringArg(args, "secretKey");
      if (sensitive && secretKey === undefined) throw new Error("Sensitive text_input requires secretKey.");
      const text = readStringArg(args, "text", true);
      if ([...text].length > 40) throw new Error("text must contain at most 40 Unicode code points.");
      return performGameInput(bridge, automation, { type: name, text, sensitive, secretKey }, 10000, signal);
    }
    case "wait_for_node":
      return bridge.waitForNode(
        readStringArg(args, "nodeId"),
        readIntegerArg(args, "timeoutMs", 5000),
      );
    case "get_screenshot":
      return bridge.getScreenshot();
    case "get_logs":
      return bridge.getLogs();
    case "get_clock":
      return bridge.getClock();
    case "clock_install":
      return bridge.controlClock({ type: "clock_install", initialTimeMs: readNumberArg(args, "initialTimeMs", 0), stepMs: readOptionalNumberArg(args, "stepMs") });
    case "clock_pause":
      return bridge.controlClock({ type: "clock_pause" });
    case "clock_run_for":
      return bridge.controlClock(parseClockRunForArguments(args));
    case "clock_resume":
      return bridge.controlClock({ type: "clock_resume" });
    case "start_recording":
      return { ...automation.startRecording(), artifactDirectory: automation.artifactRoot };
    case "stop_recording": {
      const recording = automation.stopRecording();
      return { recording, stepCount: recording.steps.length };
    }
    case "save_recording":
      return automation.saveRecording(readStringArg(args, "name"));
    case "replay_recording": {
      const inline = args.recording;
      let recording: GuaRecording;
      if (inline !== undefined) {
        validateRecording(inline);
        recording = inline;
        automation.setLastRecording(recording);
      } else if (readOptionalStringArg(args, "name") !== undefined) {
        recording = await automation.loadRecording(readStringArg(args, "name"));
      } else {
        recording = automation.getLastRecording();
      }
      const secrets = isRecord(args.secrets) ? args.secrets : {};
      return replayRecording(
        recording, bridge, secrets,
        args.timingMode === "preserve_delays" ? "preserve_delays" : "prefer_conditions",
        readIntegerArg(args, "timeoutMs", 10000),
        signal,
      );
    }
    case "compare_screenshot":
      return automation.compareScreenshot(await bridge.getScreenshot(), {
        name: readStringArg(args, "name"),
        variant: readOptionalStringArg(args, "variant"),
        updateBaseline: readBooleanArg(args, "updateBaseline", false),
        pixelThreshold: readNumberArg(args, "pixelThreshold", 0),
        maxDifferentPixelRatio: readNumberArg(args, "maxDifferentPixelRatio", 0),
        masks: readMasks(args.masks),
      });
    case "get_visual_artifacts":
      return automation.getVisualArtifacts(readOptionalStringArg(args, "name"));
    case "run_test":
      return runTest(readTestSteps(args), bridge, automation);
  }
}

async function runTest(
  steps: TestStep[],
  bridge: GuaBridgeClient,
  automation: GuaAutomationManager,
): Promise<{ ok: true; steps: TestStepResult[] }> {
  const results: TestStepResult[] = [];

  for (const [index, step] of steps.entries()) {
    if (step.action === "wait_for_node") {
      await bridge.waitForNode(step.nodeId, step.timeoutMs ?? 5000);
    } else {
      await performAndRecord(bridge, automation, { action: "click", nodeId: step.nodeId });
    }

    results.push({
      index,
      action: step.action,
      nodeId: step.nodeId,
      ok: true,
    });
  }

  return { ok: true, steps: results };
}

type GameInputCommandInput =
  | { type: "press_game_input_action"; actionId: string; confirmed?: boolean }
  | { type: "set_game_input_action"; actionId: string; value: unknown; leaseMs?: number; confirmed?: boolean; sensitive?: boolean; secretKey?: string }
  | { type: "release_game_input_action"; actionId: string }
  | { type: "release_all_game_inputs" }
  | { type: "key_down" | "key_up" | "press_physical_key"; code: string; leaseMs?: number }
  | { type: "pointer_move"; mode: "absolute" | "delta"; coordinateSpace?: "viewport_normalized" | "viewport_pixels"; x: number; y: number }
  | { type: "pointer_button_down" | "pointer_button_up"; button: "primary" | "secondary" | "auxiliary" | "back" | "forward"; leaseMs?: number }
  | { type: "pointer_wheel"; deltaX: number; deltaY: number; wheelUnit?: "pixels" | "lines" }
  | { type: "gamepad_button_down" | "gamepad_button_up"; gamepadIndex?: number; button: string; leaseMs?: number }
  | { type: "set_gamepad_axis"; gamepadIndex?: number; axis: "left_stick_x" | "left_stick_y" | "right_stick_x" | "right_stick_y"; value: number; leaseMs?: number }
  | { type: "reset_gamepad"; gamepadIndex?: number }
  | { type: "text_input"; text: string; sensitive?: boolean; secretKey?: string };

function gameInputSearchCommand(input: Record<string, unknown>): Omit<Extract<BridgeCommandInput, { type: "find_game_input_actions" }>, "type"> {
  const valueType = readOptionalStringArg(input, "valueType");
  const valueTypeCode = valueType === undefined ? undefined : ({ button: 1, axis1d: 2, vector2: 3, text: 4 } as const)[valueType as "button" | "axis1d" | "vector2" | "text"];
  if (valueType !== undefined && valueTypeCode === undefined) throw new Error("valueType is invalid.");
  const tags = input.tags;
  if (tags !== undefined && (!Array.isArray(tags) || !tags.every((tag) => typeof tag === "string" && tag.length > 0)))
    throw new Error("tags must contain non-empty strings.");
  return compactResult({ actionId: readOptionalStringArg(input, "id"), query: readOptionalStringArg(input, "query"),
    valueType: valueTypeCode, active: input.active === undefined ? undefined : readRequiredBooleanArg(input, "active") ? 2 : 1,
    context: readOptionalStringArg(input, "context"), category: readOptionalStringArg(input, "category"),
    tags: tags as string[] | undefined, limit: readIntegerArg(input, "limit", 20) });
}

async function performGameInput(
  bridge: GuaBridgeClient,
  automation: GuaAutomationManager | undefined,
  input: GameInputCommandInput,
  timeoutMs = 10000,
  signal?: AbortSignal,
): Promise<{ ok: true; requestId: number; completion: GameInputCompletion }> {
  if (input.type === "press_game_input_action" || input.type === "set_game_input_action") {
    const snapshot = await findGameInputActionSnapshot(bridge, input.actionId);
    if (!isRecord(snapshot) || !Array.isArray(snapshot.actions)) throw new Error("Gua returned an invalid game input action map.");
    const action = snapshot.actions.find((candidate) => isRecord(candidate) && candidate.id === input.actionId);
    if (isRecord(action) && action.requiresConfirmation === true && input.confirmed !== true) {
      throw new Error(`Game input action '${input.actionId}' requires confirmed=true.`);
    }
  }
  throwIfAborted(signal);
  const receipt = await bridge.performGameInput(input);
  const completion = await bridge.waitForGameInput(receipt.requestId, timeoutMs);
  if (!completion.succeeded) throw new Error(`Gua game input ${input.type} failed with error ${completion.errorCode}.`);
  const recordedArguments = { ...input } as Record<string, unknown>;
  delete recordedArguments.type;
  delete recordedArguments.secretKey;
  automation?.recordGameInput({
    operation: input.type,
    requestId: receipt.requestId,
    arguments: compactResult({ ...recordedArguments, value: "sensitive" in input && input.sensitive ? undefined : "value" in input ? input.value : undefined,
      text: "sensitive" in input && input.sensitive ? undefined : "text" in input ? input.text : undefined }),
    sensitive: "sensitive" in input && input.sensitive === true,
    secretKey: "secretKey" in input ? input.secretKey : undefined,
  });
  return { ok: true, requestId: receipt.requestId, completion };
}

interface GameInputCompletion { completed: true; requestId: number; succeeded: boolean; errorCode: number }

interface SemanticActionInput {
  action: Exclude<RecordedAction, "game_input">;
  nodeId?: string;
  value?: string;
  checked?: boolean;
  key?: string;
  modifiers?: number;
  deltaX?: number;
  deltaY?: number;
  scrollUnit?: number;
  sensitive?: boolean;
  secretKey?: string;
  waitCondition?: string;
}

async function performAndRecord(
  bridge: GuaBridgeClient,
  automation: GuaAutomationManager | undefined,
  input: SemanticActionInput,
  timeoutMs = 10000,
  signal?: AbortSignal,
): Promise<{ ok: true; requestId?: number; completion?: GuaActionEvent }> {
  let before: GuaUiTree;
  try { before = await bridge.getUiTree(); }
  catch (error) {
    if (error instanceof RpcFailure) throw error;
    throw new GuaActionOutcomeError("before_send", false);
  }
  throwIfAborted(signal);
  let sent: { socket: WebSocket; id: number } | undefined;
  let receipt: GuaActionReceipt | null;
  try {
    receipt = await bridge.performAction(input, (socket, id) => { sent = { socket, id }; });
  } catch (error) {
    if (error instanceof BridgeRejectionError || error instanceof RpcFailure) throw error;
    throw new GuaActionOutcomeError(sent ? "awaiting_receipt" : "before_send", sent !== undefined, undefined, sent?.id);
  }
  if (receipt !== null && (!isRecord(receipt) || !Number.isSafeInteger(receipt.requestId) || receipt.requestId <= 0)) {
    throw new GuaActionOutcomeError("awaiting_receipt", true, undefined, sent?.id);
  }
  let completion: GuaActionEvent | undefined;
  let after: GuaUiTree | undefined;
  try {
    completion = receipt === null ? undefined : await bridge.waitForAction(receipt.requestId, timeoutMs, sent?.socket);
    if (completion === undefined) after = await bridge.getUiTree(sent?.socket);
  } catch (error) {
    if (error instanceof RpcFailure) throw error;
    throw new GuaActionOutcomeError(receipt === null ? "awaiting_observation" : "awaiting_completion", true, receipt?.requestId, sent?.id);
  }
  if (completion?.succeeded === false) {
    throw new Error(`Gua action ${input.action} failed with error ${completion.error}.`);
  }
  automation?.recordAction({
    action: input.action,
    requestId: receipt?.requestId,
    nodeId: input.nodeId,
    preRevision: before.revision ?? 0,
    postRevision: completion?.revision ?? after?.revision ?? before.revision ?? 0,
    waitCondition: input.waitCondition,
    value: actionValue(input),
    sensitive: input.sensitive,
    secretKey: input.secretKey,
    deltaX: input.deltaX,
    deltaY: input.deltaY,
    scrollUnit: input.scrollUnit,
    modifiers: input.modifiers,
  });
  return compactResult({ ok: true as const, requestId: receipt?.requestId, completion });
}

export async function replayRecording(
  recording: GuaRecording,
  bridge: GuaBridgeClient,
  secrets: Record<string, unknown>,
  timingMode: "prefer_conditions" | "preserve_delays",
  timeoutMs: number,
  signal?: AbortSignal,
): Promise<{ ok: true; steps: Array<{ index: number; action: RecordedAction; requestId?: number }> }> {
  validateRecording(recording);
  const results: Array<{ index: number; action: RecordedAction; requestId?: number }> = [];
  let previous = 0;
  let replaySucceeded = false;
  try {
  for (const [index, step] of recording.steps.entries()) {
    throwIfAborted(signal);
    if (timingMode === "prefer_conditions" && step.waitCondition !== undefined) {
      await waitForCondition(bridge, step.waitCondition, timeoutMs, signal);
    } else if (step.relativeMilliseconds > previous) {
      await sleep(step.relativeMilliseconds - previous, signal);
    }
    throwIfAborted(signal);
    previous = step.relativeMilliseconds;
    if (step.action === "game_input") {
      const argumentsValue = { ...(step.arguments ?? {}) } as Record<string, unknown>;
      argumentsValue.type = step.operation;
      if (step.sensitive) {
        const secret = readSecret(secrets, step.secretKey as string);
        if (step.operation === "text_input") argumentsValue.text = secret;
        else if (step.operation === "set_game_input_action")
          argumentsValue.value = await decodeGameInputSecret(bridge, String(argumentsValue.actionId), secret);
        else argumentsValue.value = secret;
      }
      throwIfAborted(signal);
      const result = await performGameInput(bridge, undefined, argumentsValue as GameInputCommandInput, timeoutMs, signal);
      results.push({ index, action: step.action, requestId: result.requestId });
      continue;
    }
    const nodeId = await resolveTarget(bridge, step);
    const value = step.sensitive ? readSecret(secrets, step.secretKey as string) : step.value;
    throwIfAborted(signal);
    const result = await performAndRecord(bridge, undefined, {
      action: step.action,
      nodeId,
      value,
      checked: step.action === "set_checked" ? value === "true" : undefined,
      key: step.action === "press_key" ? value : undefined,
      modifiers: step.modifiers,
      deltaX: step.deltaX,
      deltaY: step.deltaY,
      scrollUnit: step.scrollUnit,
      sensitive: step.sensitive,
      secretKey: step.secretKey,
    }, timeoutMs, signal);
    results.push(compactResult({ index, action: step.action, requestId: result.requestId }));
  }
  replaySucceeded = true;
  } finally {
    try { await performGameInput(bridge, undefined, { type: "release_all_game_inputs" }, timeoutMs); }
    catch (cleanupError) {
      if (replaySucceeded) throw cleanupError;
      // Preserve the original replay failure after best-effort cleanup.
    }
  }
  return { ok: true, steps: results };
}

async function resolveTarget(bridge: GuaBridgeClient, step: RecordingStep): Promise<string | undefined> {
  if (step.coordinateFallback !== undefined) {
    throw new Error("Coordinate fallback replay is disabled; use a semantic target.");
  }
  const target = step.target as NonNullable<RecordingStep["target"]>;
  if (target.currentFocus === true) return undefined;
  if (target.id !== undefined) return target.id;
  const tree = await bridge.getUiTree();
  const candidates = tree.nodes.filter((node) => node.role === target.role &&
    (target.name === undefined || node.label === target.name) &&
    (target.scope === undefined || isDescendantOf(node, target.scope, tree.nodes)));
  if (candidates.length !== 1) {
    throw new Error(`Recording target ${target.role}/${target.name ?? ""} matched ${candidates.length} nodes.`);
  }
  return candidates[0]?.id;
}

function isDescendantOf(node: GuaNode, scopeId: string, nodes: GuaNode[]): boolean {
  let parentId = node.parentId;
  while (parentId !== undefined) {
    if (parentId === scopeId) return true;
    parentId = nodes.find((candidate) => candidate.id === parentId)?.parentId;
  }
  return false;
}

async function waitForCondition(bridge: GuaBridgeClient, condition: string, timeoutMs: number,
  signal?: AbortSignal): Promise<void> {
  const parts = condition.split(":");
  if (parts.length < 2) throw new Error(`Unsupported recording wait condition: ${condition}`);
  const id = decodeURIComponent(parts[1] as string);
  const expected = parts[2] === undefined ? undefined : decodeURIComponent(parts[2]);
  const startedAt = Date.now();
  while (Date.now() - startedAt <= timeoutMs) {
    throwIfAborted(signal);
    const node = (await bridge.getUiTree()).nodes.find((candidate) => candidate.id === id);
    const matched = parts[0] === "visible" ? node?.visible === true
      : parts[0] === "hidden" ? node === undefined || node.visible === false
      : parts[0] === "enabled" ? node?.enabled === true
      : parts[0] === "disabled" ? node !== undefined && node.enabled === false
      : parts[0] === "focused" ? node?.state?.focused === true
      : parts[0] === "unfocused" ? node !== undefined && node.state?.focused !== true
      : parts[0] === "checked" ? node?.state?.checked === true
      : parts[0] === "unchecked" ? node !== undefined && node.state?.checked !== true
      : parts[0] === "text" ? node?.label === expected
      : parts[0] === "value" ? String(node?.state?.value ?? "") === expected
      : false;
    if (matched) return;
    await sleep(50, signal);
  }
  throw new Error(`Timed out waiting for recording condition: ${condition}`);
}

function actionValue(input: SemanticActionInput): string | undefined {
  if (input.action === "set_checked") return String(input.checked === true);
  if (input.action === "press_key") return input.key;
  if (input.action === "set_value" || input.action === "select") return input.value;
  return undefined;
}

function actionCommandType(action: RecordedAction): BridgeCommandInput["type"] {
  switch (action) {
    case "click": return "click_node";
    case "focus": return "focus_node";
    case "set_value": return "set_value";
    case "set_checked": return "set_checked";
    case "select": return "select";
    case "scroll": return "scroll";
    case "press_key": return "press_key";
    case "game_input": throw new Error("game_input uses its recorded operation directly.");
  }
}

function readSecret(secrets: Record<string, unknown>, key: string): string {
  const value = secrets[key];
  if (typeof value !== "string") throw new Error(`Replay requires a secret value for key '${key}'.`);
  return value;
}

async function decodeGameInputSecret(bridge: GuaBridgeClient, actionId: string, secret: string): Promise<unknown> {
  const snapshot = await findGameInputActionSnapshot(bridge, actionId);
  if (!isRecord(snapshot) || !Array.isArray(snapshot.actions)) throw new Error("Gua returned an invalid game input action map.");
  const action = snapshot.actions.find((candidate) => isRecord(candidate) && candidate.id === actionId);
  if (!isRecord(action) || typeof action.valueType !== "string") throw new Error(`Unknown game input action '${actionId}'.`);
  if (action.valueType === "text") return secret;
  if (action.valueType === "axis1d") {
    const value = Number(secret);
    if (!Number.isFinite(value)) throw new Error(`Secret for '${actionId}' must be a finite number.`);
    return value;
  }
  if (action.valueType === "button") {
    if (secret === "true") return true;
    if (secret === "false") return false;
    throw new Error(`Secret for '${actionId}' must be true or false.`);
  }
  if (action.valueType === "vector2") {
    let value: unknown;
    try {
      value = JSON.parse(secret) as unknown;
    } catch {
      throw new Error(`Secret for '${actionId}' must be a finite vector2 JSON object.`);
    }
    if (!isRecord(value) || typeof value.x !== "number" || !Number.isFinite(value.x) ||
        typeof value.y !== "number" || !Number.isFinite(value.y))
      throw new Error(`Secret for '${actionId}' must be a finite vector2 JSON object.`);
    return { x: value.x, y: value.y };
  }
  throw new Error(`Unsupported value type for game input action '${actionId}'.`);
}

async function findGameInputActionSnapshot(bridge: GuaBridgeClient, actionId: string): Promise<unknown> {
  const compatible = bridge as GuaBridgeClient & { findGameInputActions?: (selector: { actionId: string; limit: number }) => Promise<unknown> };
  if (typeof compatible.findGameInputActions === "function") {
    try { return await compatible.findGameInputActions({ actionId, limit: 1 }); }
    catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      if (!/unsupported|unknown command/i.test(message)) throw error;
    }
  }
  return bridge.getGameInputActions();
}

function compactResult<T extends Record<string, unknown>>(value: T): T {
  return Object.fromEntries(Object.entries(value).filter(([, item]) => item !== undefined)) as T;
}

export class GuaBridgeClient {
  async spatialInfo(signal?: AbortSignal): Promise<GuaSpatialAdvertisement> {
    return this.request({ type: "get_spatial_info" }, this.requestTimeoutMs, signal);
  }
  async spatialBatch(batch: GuaSpatialBatch, signal?: AbortSignal): Promise<GuaSpatialBatchResult> {
    const socket = await this.connectForRequest("get_spatial_info", this.requestTimeoutMs, signal);
    const started = Date.now();
    try {
      await this.request({ type: "query_spatial_batch", batch }, this.requestTimeoutMs, signal, socket);
      while (Date.now() - started < this.requestTimeoutMs) {
        const result = await this.request<GuaSpatialBatchResult | null>({ type: "poll_spatial_batch", batchId: batch.batchId },
          Math.max(1, this.requestTimeoutMs - (Date.now() - started)), signal, socket);
        if (result !== null) {
          if (result.schemaVersion !== "spatial-host-r1" || result.documentType !== "batchResult" || result.batchId !== batch.batchId ||
            result.items.length !== batch.queries.length || result.items.some((item, index) => item.requestId !== batch.queries[index]!.requestId || item.queryId !== batch.queries[index]!.queryId))
            throw new Error("Spatial correlation mismatch.");
          return result;
        }
        await new Promise(resolve => setTimeout(resolve, 10));
        throwIfAborted(signal);
      }
      throw new Error("Spatial batch completion timed out.");
    } catch (error) { socket.close(); throw error; }
  }
  async observeCommand(type: "get_observe_snapshot" | "subscribe_observations" | "poll_observations" | "unsubscribe_observations", subscriptionId?: number, signal?: AbortSignal): Promise<unknown> {
    const needsId = type === "poll_observations" || type === "unsubscribe_observations";
    const owner = needsId ? this.observeSubscriptions.get(observeSubscriptionId(subscriptionId)) : undefined;
    if (needsId && (!owner || owner.socket !== this.socket || owner.socket.readyState !== WebSocket.OPEN)) {
      if (type === "unsubscribe_observations") { this.observeSubscriptions.delete(subscriptionId!); return null; }
      throw new Error("Observe subscription belongs to an inactive connection.");
    }
    const socket = owner?.socket ?? await this.connectForRequest("get_version", this.requestTimeoutMs, signal);
    const version = await this.request<{ capabilities: string[] }>({ type: "get_version" }, this.requestTimeoutMs, signal, socket);
    if (!version.capabilities.includes("observe_v1")) throw new Error("observe_v1 is unsupported.");
    const result = await this.request<any>(owner === undefined ? { type: type as "get_observe_snapshot" | "subscribe_observations" } : { type: type as "poll_observations" | "unsubscribe_observations", subscriptionId: owner.remoteId }, this.requestTimeoutMs, signal, socket);
    if (type === "unsubscribe_observations") { this.observeSubscriptions.delete(subscriptionId!); return null; }
    if (type === "subscribe_observations") {
      const remoteId = observeSubscriptionId(result.subscriptionId), snapshot = parseObserveTransport(result.snapshot);
      if (socket !== this.socket || socket.readyState !== WebSocket.OPEN) throw new Error("Observe subscription belongs to an inactive connection.");
      const handle = observeSubscriptionId(this.nextObserveHandle++);
      this.observeSubscriptions.set(handle, { socket, remoteId });
      return { subscriptionId: handle, snapshot };
    }
    return parseObserveTransport(result);
  }
  private socket: WebSocket | null = null;
  private connectionAttempt: ConnectionAttempt | null = null;
  private nextId = 1;
  private nextObserveHandle = 1;
  private readonly observeSubscriptions = new Map<number, { socket: WebSocket; remoteId: number }>();
  private readonly pending = new Map<number, PendingRequest>();

  constructor(
    private readonly url: string,
    private readonly requestTimeoutMs = 5000,
    private readonly createWebSocket: (url: string) => WebSocket = (url) => new WebSocket(url),
  ) {
  }

  async getUiTree(ownerSocket?: WebSocket): Promise<GuaUiTree> {
    return this.request<GuaUiTree>({ type: "get_ui_tree" }, this.requestTimeoutMs, undefined, ownerSocket);
  }

  async getWorldObjectTree(): Promise<GuaWorldObjectTree> {
    return this.request<GuaWorldObjectTree>({ type: "get_world_object_tree" });
  }

  async findWorldObjects(selector: GuaWorldSelector, timeoutMs = this.requestTimeoutMs,
    signal?: AbortSignal): Promise<GuaWorldQueryResult> {
    const state = selector.state;
    const result = await this.request<unknown>({ type: "query_world_objects", worldId: selector.id, kind: selector.kind,
      label: selector.label, tag: selector.tag, parentId: selector.parentId, directChild: selector.directChild ? 1 : 0,
      visibleToPlayer: filter(selector.visibleToPlayer), active: filter(selector.active), stateKey: state?.key,
      stateType: state === undefined ? undefined : state.value === null ? 0 : typeof state.value === "string" ? 1 : typeof state.value === "number" ? 2 : 3,
      stateString: typeof state?.value === "string" ? state.value : undefined,
      stateNumber: typeof state?.value === "number" ? state.value : undefined,
      stateBool: typeof state?.value === "boolean" ? state.value : undefined,
      relativeToObjectId: selector.near?.relativeToObjectId, maxDistance: selector.near?.maxDistance,
      limit: selector.limit }, timeoutMs, signal);
    return parseWorldQueryResult(result, selector);
  }

  async waitForWorldObject(selector: GuaWorldSelector, timeoutMs: number, signal?: AbortSignal) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() <= deadline) {
      throwIfAborted(signal);
      const remainingRequestMs = Math.max(1, deadline - Date.now());
      let result: GuaWorldQueryResult;
      try {
        result = await this.findWorldObjects(selector, remainingRequestMs, signal);
      } catch (error) {
        if (Date.now() >= deadline) break;
        throw error;
      }
      if (!result.valid) throw new Error(result.error ?? "Invalid world selector.");
      const match = result.matches[0];
      if (match !== undefined) return match;
      const remainingSleepMs = deadline - Date.now();
      if (remainingSleepMs <= 0) break;
      await sleep(Math.min(50, remainingSleepMs), signal);
    }
    throw new Error("Timed out waiting for a Gua world object.");
  }

  async getLogs(): Promise<GuaLogEntry[]> {
    return this.request<GuaLogEntry[]>({ type: "get_logs" });
  }

  async getScreenshot(): Promise<GuaScreenshot> {
    return this.request<GuaScreenshot>({ type: "get_screenshot" });
  }

  async getContextStatus(): Promise<GuaContextStatus> {
    return this.request<GuaContextStatus>({ type: "get_context_status" });
  }

  async getClock(): Promise<unknown> { return this.request({ type: "get_clock" }); }
  private async requireGameInputMetadata(): Promise<void> {
    const version = await this.request<{ capabilities?: string[] }>({ type: "get_version" });
    if (!version.capabilities?.includes("semantic_game_input_metadata_v1")) throw new Error("unsupported game input metadata");
  }
  async getGameInputActionsV2(): Promise<unknown> {
    await this.requireGameInputMetadata(); return this.request({ type: "get_game_input_actions_v2" });
  }
  async findGameInputActionsV2(selector: Omit<Extract<BridgeCommandInput, { type: "find_game_input_actions" }>, "type">): Promise<unknown> {
    await this.requireGameInputMetadata(); return this.request({ type: "find_game_input_actions_v2", ...selector });
  }
  async getGameInputActions(): Promise<unknown> { return this.request({ type: "get_game_input_actions" }); }
  async findGameInputActions(selector: Omit<Extract<BridgeCommandInput, { type: "find_game_input_actions" }>, "type">): Promise<unknown> {
    return this.request({ type: "find_game_input_actions", ...selector });
  }
  async getGameInputState(): Promise<unknown> { return this.request({ type: "get_game_input_state" }); }
  async performGameInput(input: GameInputCommandInput): Promise<GuaActionReceipt> {
    return this.request<GuaActionReceipt>(input as BridgeCommandInput);
  }
  async waitForGameInput(requestId: number, timeoutMs: number): Promise<GameInputCompletion> {
    const started = Date.now();
    while (Date.now() - started <= timeoutMs) {
      const completion = await this.request<{ completed: boolean; requestId?: number; succeeded?: boolean; errorCode?: number }>({
        type: "poll_game_input", requestId,
      });
      if (completion.completed) return completion as GameInputCompletion;
      await sleep(25);
    }
    throw new Error(`Timed out waiting for Gua game input request ${requestId}.`);
  }
  async controlClock(command: { type: "clock_install"; initialTimeMs?: number; stepMs?: number } |
    { type: "clock_run_for"; durationMs: number; stepMs?: number } | { type: "clock_pause" | "clock_resume" }): Promise<unknown>
  {
    const responseTimeoutMs = command.type === "clock_pause"
      ? Math.max(this.requestTimeoutMs, clockPauseResponseTimeoutMs)
      : this.requestTimeoutMs;
    let result = await this.request<{ pendingMs?: number; completedOperationSequence: number; operationSequence?: number; completionSessionEpoch?: number; completionAfterFrameSequence?: number }>(command, responseTimeoutMs);
    if (command.type !== "clock_run_for") return result;
    const completionEpoch = result.completionSessionEpoch;
    const completionFrame = result.completionAfterFrameSequence;
    const operationSequence = result.operationSequence;
    if (completionEpoch === undefined || completionFrame === undefined || operationSequence === undefined) throw new Error("unsupported");
    const started = Date.now();
    while (Date.now() - started < 10000) {
      const context = await this.getContextStatus();
      if (context.sessionEpoch !== completionEpoch) throw new Error("stale_session");
      if (result.completedOperationSequence >= operationSequence && context.frameSequence > completionFrame) return result;
      await sleep(5);
      const status = await this.request<{
        schemaVersion: number;
        installed: boolean;
        state: "running" | "paused";
        nowMs: number;
        defaultStepMs: number;
        pendingMs: number;
        generation: number;
        completedOperationSequence: number;
      }>({ type: "get_clock" });
      result = { ...result, ...status };
    }
    throw new Error("Timed out waiting for Gua clock run_for host completion.");
  }

  async performAction(input: SemanticActionInput, onSent?: (socket: WebSocket, id: number) => void): Promise<GuaActionReceipt | null> {
    const type = actionCommandType(input.action);
    return this.request<GuaActionReceipt | null>(compactResult({
      type,
      nodeId: input.nodeId,
      value: input.action === "press_key" ? undefined : input.value,
      checked: input.checked,
      key: input.key,
      modifiers: input.modifiers,
      deltaX: input.deltaX,
      deltaY: input.deltaY,
      scrollUnit: input.scrollUnit,
      sensitive: input.sensitive,
    }) as BridgeCommandInput, this.requestTimeoutMs, undefined, undefined, onSent);
  }

  async waitForAction(requestId: number, timeoutMs: number, ownerSocket = this.socket): Promise<GuaActionEvent> {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      if (ownerSocket === null) throw new Error("Gua action connection is inactive.");
      const event = await this.request<GuaActionEvent | null>({ type: "poll_events", requestId },
        Math.min(this.requestTimeoutMs, deadline - Date.now()), undefined, ownerSocket);
      if (event !== null) {
        if (!isRecord(event) || event.requestId !== requestId || typeof event.succeeded !== "boolean") {
          throw new Error("Invalid correlated Gua action completion.");
        }
        return event;
      }
      await sleep(Math.min(25, Math.max(0, deadline - Date.now())));
    }
    throw new Error(`Timed out waiting for Gua action request ${requestId}.`);
  }

  async waitForNode(nodeId: string, timeoutMs: number): Promise<{ ok: true; node: GuaNode }> {
    const startedAt = Date.now();

    while (Date.now() - startedAt <= timeoutMs) {
      const tree = await this.getUiTree();
      const node = tree.nodes.find((candidate) => candidate.id === nodeId);
      if (node !== undefined) {
        return { ok: true, node };
      }

      await sleep(50);
    }

    throw new Error(`Timed out waiting for Gua node: ${nodeId}`);
  }

  close(): void {
    this.observeSubscriptions.clear();
    const error = new Error("Gua MCP bridge client closed.");
    this.rejectAll(error);
    const attempt = this.connectionAttempt;
    this.connectionAttempt = null;
    attempt?.reject(error);
    if (attempt !== null) closePendingSocket(attempt.socket);
    this.socket?.close();
    this.socket = null;
  }

  private async request<T>(command: BridgeCommandInput, timeoutMs = this.requestTimeoutMs,
    signal?: AbortSignal, ownerSocket?: WebSocket, onSent?: (socket: WebSocket, id: number) => void): Promise<T> {
    throwIfAborted(signal);
    const deadline = Date.now() + timeoutMs;
    const socket = ownerSocket ?? await this.connectForRequest(command.type, timeoutMs, signal);
    if (ownerSocket && (socket !== this.socket || socket.readyState !== WebSocket.OPEN)) throw new Error("Gua request belongs to an inactive connection.");
    throwIfAborted(signal);
    const remainingTimeoutMs = deadline - Date.now();
    if (remainingTimeoutMs <= 0) {
      throw new Error(`Timed out waiting for Gua bridge command: ${command.type}`);
    }
    const id = this.nextId++;
    const payload = { ...command, id } as BridgeCommand;

    return new Promise<T>((resolve, reject) => {
      const timeoutId = setTimeout(() => {
        this.pending.delete(id);
        signal?.removeEventListener("abort", aborted);
        reject(new Error(`Timed out waiting for Gua bridge command: ${command.type}`));
        if (command.type === "subscribe_observations" || command.type === "poll_observations") socket.close();
      }, remainingTimeoutMs);

      const aborted = () => {
        const pending = this.pending.get(id);
        if (pending === undefined) return;
        clearTimeout(pending.timeoutId);
        this.pending.delete(id);
        signal?.removeEventListener("abort", aborted);
        reject(new RpcFailure(-32800, "MCP request was cancelled."));
        if (command.type === "subscribe_observations" || command.type === "poll_observations") socket.close();
      };

      this.pending.set(id, {
        observeOperation: ["get_observe_snapshot", "subscribe_observations", "poll_observations", "unsubscribe_observations"].includes(command.type) ? command.type : undefined,
        resolve: (value) => {
          signal?.removeEventListener("abort", aborted);
          resolve(value as T);
        },
        reject: (reason) => {
          signal?.removeEventListener("abort", aborted);
          reject(reason);
        },
        timeoutId,
      });

      signal?.addEventListener("abort", aborted, { once: true });
      try {
        if (socket.readyState !== WebSocket.OPEN) throw new Error("Gua bridge connection is inactive.");
        socket.send(JSON.stringify(payload));
        onSent?.(socket, id);
      } catch (error) {
        clearTimeout(timeoutId);
        this.pending.delete(id);
        signal?.removeEventListener("abort", aborted);
        reject(error);
      }
    });
  }

  private connectForRequest(commandType: BridgeCommandInput["type"], timeoutMs: number,
    signal?: AbortSignal): Promise<WebSocket> {
    const connection = this.connect();
    const attempt = this.connectionAttempt?.promise === connection ? this.connectionAttempt : null;
    if (attempt !== null) attempt.waiterCount++;
    return new Promise<WebSocket>((resolve, reject) => {
      let settled = false;
      let timeoutId: ReturnType<typeof setTimeout>;

      const cleanup = () => {
        clearTimeout(timeoutId);
        signal?.removeEventListener("abort", aborted);
      };

      const finishResolve = (socket: WebSocket) => {
        if (settled) return;
        settled = true;
        cleanup();
        this.releaseConnectionWaiter(attempt);
        resolve(socket);
      };

      const finishReject = (error: unknown) => {
        if (settled) return;
        settled = true;
        cleanup();
        this.releaseConnectionWaiter(attempt, error);
        reject(error);
      };

      const aborted = () => finishReject(new RpcFailure(-32800, "MCP request was cancelled."));
      timeoutId = setTimeout(() => finishReject(
        new Error(`Timed out waiting for Gua bridge command: ${commandType}`)), timeoutMs);

      signal?.addEventListener("abort", aborted, { once: true });
      connection.then(finishResolve, finishReject);
      if (signal?.aborted) aborted();
    });
  }

  private connect(): Promise<WebSocket> {
    if (this.socket !== null && this.socket.readyState === WebSocket.OPEN) {
      return Promise.resolve(this.socket);
    }

    if (this.connectionAttempt !== null) {
      return this.connectionAttempt.promise;
    }

    const socket = this.createWebSocket(this.url);
    let resolveAttempt!: (socket: WebSocket) => void;
    let rejectAttempt!: (error: unknown) => void;
    const promise = new Promise<WebSocket>((resolve, reject) => {
      resolveAttempt = resolve;
      rejectAttempt = reject;
    });
    const attempt: ConnectionAttempt = { socket, promise, reject: rejectAttempt, waiterCount: 0 };
    this.connectionAttempt = attempt;

    socket.addEventListener("open", () => {
      if (this.connectionAttempt !== attempt) {
        socket.close();
        return;
      }
      this.socket = socket;
      this.connectionAttempt = null;
      resolveAttempt(socket);
    });

    socket.addEventListener("message", (event) => {
      if (this.socket === socket) this.handleMessage(event.data, socket);
    });

    socket.addEventListener("close", () => {
      for (const [id, owner] of this.observeSubscriptions) if (owner.socket === socket) this.observeSubscriptions.delete(id);
      if (this.connectionAttempt === attempt) {
        this.connectionAttempt = null;
        rejectAttempt(new Error("Gua bridge WebSocket connection closed."));
      }
      if (this.socket === socket) {
        this.socket = null;
        this.rejectAll(new Error("Gua bridge WebSocket connection closed."));
      }
    });

    socket.addEventListener("error", () => {
      const error = new Error(`Failed to connect to Gua bridge at ${this.url}.`);
      if (this.connectionAttempt === attempt) {
        this.connectionAttempt = null;
        rejectAttempt(error);
      }
      if (this.socket === socket) {
        this.rejectAll(error);
      }
    });

    return promise;
  }

  private releaseConnectionWaiter(attempt: ConnectionAttempt | null, error?: unknown): void {
    if (attempt === null) return;
    attempt.waiterCount--;
    if (error === undefined || attempt.waiterCount !== 0 || this.connectionAttempt !== attempt) return;
    this.connectionAttempt = null;
    attempt.reject(error);
    closePendingSocket(attempt.socket);
  }

  private handleMessage(data: unknown, ownerSocket: WebSocket): void {
    if (typeof data !== "string") {
      return;
    }

    let response: GuaBridgeResponse;
    try {
      response = JSON.parse(data) as GuaBridgeResponse;
    } catch {
      return;
    }

    if (!("id" in response)) {
      return;
    }

    const pending = this.pending.get(response.id);
    if (pending === undefined) {
      return;
    }

    clearTimeout(pending.timeoutId);
    this.pending.delete(response.id);

    if (pending.observeOperation) {
      try { pending.resolve(decodeObserveWireResponse(data, response.id, pending.observeOperation)); }
      catch (error) {
        pending.reject(error instanceof ObserveWireRejectionError ? error : new Error("Invalid Observe transport response."));
        if ((pending.observeOperation === "subscribe_observations" || pending.observeOperation === "poll_observations") && !(error instanceof ObserveWireRejectionError)) ownerSocket.close();
      }
    } else if (response.ok) {
      pending.resolve(response.result);
    } else {
      pending.reject(new BridgeRejectionError(response.error));
    }
  }

  private rejectAll(error: Error): void {
    for (const [id, pending] of this.pending) {
      clearTimeout(pending.timeoutId);
      pending.reject(error);
      this.pending.delete(id);
    }
  }
}

type BridgeCommandInput =
  | { type: "get_spatial_info" }
  | { type: "query_spatial_batch"; batch: GuaSpatialBatch }
  | { type: "poll_spatial_batch" | "cancel_spatial_batch"; batchId: number }
  | { type: "get_version" | "get_observe_snapshot" | "subscribe_observations" }
  | { type: "poll_observations" | "unsubscribe_observations"; subscriptionId: number }
  | { type: "get_ui_tree" }
  | { type: "get_world_object_tree" }
  | { type: "query_world_objects"; worldId?: string; kind?: string; label?: string; tag?: string; parentId?: string; directChild?: number; visibleToPlayer?: number; active?: number; stateKey?: string; stateType?: number; stateString?: string; stateNumber?: number; stateBool?: boolean; relativeToObjectId?: string; maxDistance?: number; limit?: number }
  | { type: "get_logs" }
  | { type: "get_screenshot" }
  | { type: "get_context_status" }
  | { type: "poll_events"; requestId: number }
  | { type: "poll_game_input"; requestId: number }
  | { type: "get_game_input_actions_v2" }
  | { type: "find_game_input_actions_v2"; actionId?: string; query?: string; valueType?: number; active?: number; context?: string; category?: string; tags?: string[]; limit?: number }
  | { type: "get_game_input_actions" | "get_game_input_state" }
  | { type: "find_game_input_actions"; actionId?: string; query?: string; valueType?: number; active?: number; context?: string; category?: string; tags?: string[]; limit?: number }
  | { type: "get_clock" | "clock_pause" | "clock_resume" }
  | { type: "clock_install"; initialTimeMs?: number; stepMs?: number }
  | { type: "clock_run_for"; durationMs: number; stepMs?: number }
  | {
      type: "click_node" | "focus_node" | "set_value" | "set_checked" | "select" | "scroll" | "press_key";
      nodeId?: string;
      value?: string;
      checked?: boolean;
      key?: string;
      modifiers?: number;
      deltaX?: number;
      deltaY?: number;
      scrollUnit?: number;
      sensitive?: boolean;
    }
  | GameInputCommandInput;

function filter(value: boolean | undefined): number { return value === undefined ? 0 : value ? 2 : 1; }

type BridgeCommand = BridgeCommandInput & { id: number };

interface PendingRequest {
  observeOperation?: string;
  resolve(value: unknown): void;
  reject(reason: Error): void;
  timeoutId: ReturnType<typeof setTimeout>;
}

interface ConnectionAttempt {
  socket: WebSocket;
  promise: Promise<WebSocket>;
  reject(error: unknown): void;
  waiterCount: number;
}

function closePendingSocket(socket: WebSocket): void {
  const terminate = (socket as WebSocket & { terminate?: () => void }).terminate;
  if (socket.readyState === WebSocket.CONNECTING && typeof terminate === "function") {
    terminate.call(socket);
    return;
  }
  socket.close();
}

interface TestStep {
  action: "wait_for_node" | "click_node";
  nodeId: string;
  timeoutMs?: number;
}

interface TestStepResult {
  index: number;
  action: TestStep["action"];
  nodeId: string;
  ok: true;
}

function readTestSteps(args: Record<string, unknown>): TestStep[] {
  if (!Array.isArray(args.steps) || args.steps.length === 0) {
    throw new Error("run_test requires at least one step.");
  }

  return args.steps.map((step, index) => {
    if (!isRecord(step)) {
      throw new Error(`run_test step ${index} must be an object.`);
    }

    if (step.action !== "wait_for_node" && step.action !== "click_node") {
      throw new Error(`run_test step ${index} has unsupported action.`);
    }

    return {
      action: step.action,
      nodeId: readStringArg(step, "nodeId"),
      timeoutMs: readIntegerArg(step, "timeoutMs", 5000),
    };
  });
}

function readStringArg(args: Record<string, unknown>, name: string, allowEmpty = false): string {
  const value = args[name];
  if (typeof value !== "string" || (!allowEmpty && value.length === 0)) {
    throw new Error(`Expected non-empty string argument: ${name}`);
  }

  return value;
}

function readOptionalStringArg(args: Record<string, unknown>, name: string): string | undefined {
  const value = args[name];
  if (value === undefined) return undefined;
  if (typeof value !== "string" || value.length === 0) throw new Error(`Expected non-empty string argument: ${name}`);
  return value;
}

function readIntegerArg(args: Record<string, unknown>, name: string, fallback: number): number {
  const value = args[name];
  if (value === undefined) {
    return fallback;
  }

  if (typeof value !== "number" || !Number.isInteger(value) || value < 0) {
    throw new Error(`Expected non-negative integer argument: ${name}`);
  }

  return value;
}

function readNumberArg(args: Record<string, unknown>, name: string, fallback: number): number {
  const value = args[name];
  if (value === undefined) return fallback;
  if (typeof value !== "number" || !Number.isFinite(value)) throw new Error(`Expected finite number argument: ${name}`);
  return value;
}

export function parseClockRunForArguments(args: Record<string, unknown>): {
  type: "clock_run_for";
  durationMs: number;
  stepMs?: number;
} {
  if (args.durationMs === undefined) throw new Error("Expected finite number argument: durationMs");
  return {
    type: "clock_run_for",
    durationMs: readNumberArg(args, "durationMs", 0),
    stepMs: readOptionalNumberArg(args, "stepMs"),
  };
}

function readOptionalNumberArg(args: Record<string, unknown>, name: string): number | undefined {
  const value = args[name];
  if (value === undefined) return undefined;
  if (typeof value !== "number" || !Number.isFinite(value)) throw new Error(`Expected finite number argument: ${name}`);
  return value;
}

function readBooleanArg(args: Record<string, unknown>, name: string, fallback: boolean): boolean {
  const value = args[name];
  if (value === undefined) return fallback;
  if (typeof value !== "boolean") throw new Error(`Expected boolean argument: ${name}`);
  return value;
}

function readEnumArg<const T extends string>(
  args: Record<string, unknown>,
  name: string,
  values: readonly T[],
  fallback?: T,
): T {
  const value = args[name];
  if (value === undefined && fallback !== undefined) return fallback;
  if (typeof value !== "string" || !values.includes(value as T))
    throw new Error(`${name} must be one of: ${values.join(", ")}.`);
  return value as T;
}

function readRequiredBooleanArg(args: Record<string, unknown>, name: string): boolean {
  if (args[name] === undefined) throw new Error(`Expected boolean argument: ${name}`);
  return readBooleanArg(args, name, false);
}

function readMasks(value: unknown): Array<{ x: number; y: number; width: number; height: number }> | undefined {
  if (value === undefined) return undefined;
  if (!Array.isArray(value)) throw new Error("Expected masks to be an array.");
  return value.map((mask, index) => {
    if (!isRecord(mask)) throw new Error(`Mask ${index} must be an object.`);
    const x = readIntegerArg(mask, "x", 0);
    const y = readIntegerArg(mask, "y", 0);
    const width = readIntegerArg(mask, "width", 0);
    const height = readIntegerArg(mask, "height", 0);
    if (width === 0 || height === 0) throw new Error(`Mask ${index} width and height must be positive.`);
    return { x, y, width, height };
  });
}

function textResult(value: unknown, isError = false): ToolResult {
  return {
    content: [
      {
        type: "text",
        text: typeof value === "string" ? value : JSON.stringify(value, null, 2),
      },
    ],
    isError,
  };
}

function isJsonRpcRequest(value: unknown): value is JsonRpcRequest {
  return isRecord(value) && typeof value.method === "string";
}

function isJsonRpcId(value: unknown): value is JsonRpcId {
  return typeof value === "string" || typeof value === "number" || value === null;
}

function isGuaMcpTool(value: string): value is GuaMcpTool {
  return (guaMcpTools as readonly string[]).includes(value);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function objectSchema(
  properties: Record<string, unknown>,
  required: string[] = [],
): Record<string, unknown> {
  return {
    type: "object",
    additionalProperties: false,
    properties,
    required,
  };
}

function toMcpToolDefinition(definition: GuaToolDefinition<GuaWebMcpToolName>): McpTool {
  if (definition.name === "set_value") {
    return {
      name: definition.name,
      description: "Set a semantic UI node value. Sensitive MCP values require secretKey and are redacted from recordings and diagnostics.",
      inputSchema: {
        ...objectSchema({
          nodeId: stringProperty("The target Gua node id."),
          value: { type: "string", description: "The value to send to the host." },
          sensitive: { type: "boolean", description: "Redact the value from results, recordings, and diagnostics." },
          secretKey: stringProperty("Stable secret reference required when sensitive is true; never put plaintext in this field."),
        }, ["nodeId", "value"]),
        allOf: [{
          if: { properties: { sensitive: { const: true } }, required: ["sensitive"] },
          then: { required: ["secretKey"] },
        }],
      },
    };
  }
  if (definition.name === "get_screenshot") {
    return {
      ...definition,
      description: "Read the latest screenshot published by the running game bridge; this does not request a fresh capture.",
    };
  }
  return definition;
}

function stringProperty(description: string): Record<string, unknown> {
  return {
    type: "string",
    minLength: 1,
    description,
  };
}

function numberProperty(description: string): Record<string, unknown> {
  return { type: "number", description };
}

function writeResponse(response: JsonRpcResponse): void {
  process.stdout.write(`${JSON.stringify(response)}\n`);
}

function writeLog(level: "info" | "error", message: string): void {
  process.stderr.write(`[gui-mcp] ${level}: ${message}\n`);
}

function jsonRpcError(code: number, message: string, data?: unknown): JsonRpcError {
  return { code, message, data };
}

function toJsonRpcError(error: unknown): JsonRpcError {
  if (error instanceof RpcFailure) {
    return jsonRpcError(error.code, error.message);
  }

  return jsonRpcError(-32603, (error as Error).message);
}

class RpcFailure extends Error {
  constructor(
    readonly code: number,
    message: string,
  ) {
    super(message);
  }
}

function jsonRpcIdKey(id: JsonRpcId): string { return `${typeof id}:${String(id)}`; }

function throwIfAborted(signal?: AbortSignal): void {
  if (signal?.aborted) throw new RpcFailure(-32800, "MCP request was cancelled.");
}

function sleep(ms: number, signal?: AbortSignal): Promise<void> {
  if (signal?.aborted) return Promise.reject(new RpcFailure(-32800, "MCP request was cancelled."));
  return new Promise((resolve, reject) => {
    const timer = setTimeout(finish, ms);
    const aborted = () => {
      clearTimeout(timer);
      signal?.removeEventListener("abort", aborted);
      reject(new RpcFailure(-32800, "MCP request was cancelled."));
    };
    function finish() {
      signal?.removeEventListener("abort", aborted);
      resolve();
    }
    signal?.addEventListener("abort", aborted, { once: true });
  });
}
