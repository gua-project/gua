import { ObserveWireRejectionError, decodeObserveWireResponse, parseObserveTransport, observeSubscriptionId, type ObserveTransport, type ObserveSubscription } from "gua-value";
export type { ObserveTransport, ObserveSubscription } from "gua-value";

export interface GuaBounds {
  x?: number;
  y?: number;
  w?: number;
  h?: number;
}

export function formatBounds(bounds: GuaBounds): string {
  return [bounds.x, bounds.y, bounds.w, bounds.h]
    .map((value) => value === undefined ? "unknown" : String(value))
    .join(", ");
}

export function hasCompleteBounds(bounds: GuaBounds): bounds is Required<GuaBounds> {
  return typeof bounds.x === "number" && Number.isFinite(bounds.x) &&
    typeof bounds.y === "number" && Number.isFinite(bounds.y) &&
    typeof bounds.w === "number" && Number.isFinite(bounds.w) && bounds.w >= 0 &&
    typeof bounds.h === "number" && Number.isFinite(bounds.h) && bounds.h >= 0;
}

export interface GuaNodeState {
  focused?: boolean;
  hovered?: boolean;
  pressed?: boolean;
  checked?: boolean;
  value?: number | string | boolean | null;
}

export interface GuaNode {
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

export interface GuaUiTree {
  schemaVersion: 2;
  sessionEpoch?: number;
  frameSequence: number;
  revision: number;
  screen: string;
  nodes: GuaNode[];
}
export interface GuaWorldObject { id: string; parentId?: string; kind: string; label?: string; space: "world2d" | "world3d"; position: { x?: number; y?: number; z?: number }; visibleToPlayer: boolean; active: boolean; agentExposure: "auto" | "private"; tags?: string[]; state: Record<string, string | number | boolean | null> }
export interface GuaWorldObjectTree { schemaVersion: 1; sessionEpoch: number; frameSequence: number; revision: number; scene: string; objects: GuaWorldObject[] }

export interface GuaLogEntry {
  sequence: number;
  level: "trace" | "debug" | "info" | "warn" | "error";
  message: string;
}

export interface GuaScreenshot {
  dataUri: string;
  width: number;
  height: number;
}
export interface GuaClockStatus { schemaVersion: 1; installed: boolean; state: "running" | "paused"; nowMs: number; defaultStepMs: number; pendingMs: number; generation: number; completedOperationSequence: number; operationSequence?: number; completionSessionEpoch?: number; completionAfterFrameSequence?: number; }
export interface GuaContextStatus { sessionEpoch: number; frameSequence: number; revision: number; nodeCount: number; pendingRequestCount: number; inFlightRequestCount: number; unconsumedEventCount: number; logCount: number; hasScreenshot: boolean; firstPendingAction: number; firstPendingNodeId: string; firstEventAction: number; firstEventNodeId: string; }
export type GuaGameInputValueType = "button" | "axis1d" | "vector2" | "text";
export interface GuaGameInputAction { id: string; description?: string; valueType: GuaGameInputValueType; range?: { minimum: number; maximum: number }; holdable: boolean; active: boolean; bindings: unknown[]; risk: string; requiresConfirmation: boolean; category?: string; aliases?: string[]; tags?: string[]; agentExposure?: "auto" | "private"; }
export interface GuaGameInputActions { schemaVersion: 1; sessionEpoch: number; revision: number; context: string; actions: GuaGameInputAction[]; }
export interface GuaGameInputActionSelector { id?: string; query?: string; valueType?: GuaGameInputValueType; active?: boolean; context?: string; category?: string; tags?: string[]; limit?: number; }
export interface GuaGameInputActionSearchResult extends GuaGameInputActions { count: number; truncated: boolean; }
export interface GuaHeldGameInput { kind: number; target: string; deviceIndex: number; value: unknown; remainingLeaseMs: number; }
export interface GuaGameInputState { schemaVersion: 1; held: GuaHeldGameInput[]; }
export type GameInputCommandInput =
  | { type: "press_game_input_action"; actionId: string; confirmed?: boolean }
  | { type: "release_game_input_action"; actionId: string }
  | { type: "set_game_input_action"; actionId: string; value: unknown; leaseMs?: number; confirmed?: boolean; sensitive?: boolean; secretKey?: string }
  | { type: "key_down" | "key_up" | "press_physical_key"; code: string; leaseMs?: number }
  | { type: "pointer_move"; mode: "absolute" | "delta"; coordinateSpace?: "viewport_normalized" | "viewport_pixels"; x: number; y: number }
  | { type: "pointer_button_down" | "pointer_button_up"; button: string; leaseMs?: number }
  | { type: "pointer_wheel"; deltaX: number; deltaY: number; wheelUnit?: "pixels" | "lines" }
  | { type: "gamepad_button_down" | "gamepad_button_up"; button: string; gamepadIndex?: number; leaseMs?: number }
  | { type: "set_gamepad_axis"; axis: string; value: number; gamepadIndex?: number; leaseMs?: number }
  | { type: "reset_gamepad"; gamepadIndex?: number }
  | { type: "text_input"; text: string; sensitive?: boolean; secretKey?: string }
  | { type: "release_all_game_inputs" };

export interface InspectorPanel {
  id: "tree" | "node" | "screenshot" | "logs";
  title: string;
}

export interface InspectorSnapshot {
  uiTree: GuaUiTree;
  worldObjectTree: GuaWorldObjectTree;
  logs: GuaLogEntry[];
  screenshot: GuaScreenshot;
}

export interface InspectorState extends InspectorSnapshot {
  selectedNodeId: string | null;
}

export interface GuaInspectorClient {
  getObserveSnapshot?(): Promise<ObserveTransport>;
  subscribeObservations?(): Promise<ObserveSubscription>;
  pollObservations?(subscriptionId: number): Promise<ObserveTransport>;
  unsubscribeObservations?(subscriptionId: number): Promise<void>;
  getUiTree(): Promise<GuaUiTree>;
  getWorldObjectTree(): Promise<GuaWorldObjectTree>;
  getLogs(): Promise<GuaLogEntry[]>;
  getScreenshot(): Promise<GuaScreenshot>;
  getContextStatus(): Promise<GuaContextStatus>;
  performAction(action: SemanticActionInput): Promise<ActionOutcome>;
  clickNode(nodeId: string): Promise<void>;
  focusNode(nodeId: string): Promise<void>;
  getClock(): Promise<GuaClockStatus>;
  installClock(initialTimeMs?: number, stepMs?: number): Promise<GuaClockStatus>;
  pauseClock(): Promise<GuaClockStatus>;
  runClockFor(durationMs: number, stepMs?: number): Promise<GuaClockStatus>;
  resumeClock(): Promise<GuaClockStatus>;
  getGameInputActions(): Promise<GuaGameInputActions>;
  findGameInputActions(selector: GuaGameInputActionSelector): Promise<GuaGameInputActionSearchResult>;
  getGameInputState(): Promise<GuaGameInputState>;
  performGameInput(command: GameInputCommandInput): Promise<{ requestId: number }>;
}

export type GuaInspectorCommand =
  | { id: number; type: "get_ui_tree" }
  | { id: number; type: "get_world_object_tree" }
  | { id: number; type: "get_logs" }
  | { id: number; type: "get_screenshot" }
  | { id: number; type: "get_context_status" }
  | { id: number; type: "poll_events"; requestId: number }
  | { id: number; type: "click_node"; nodeId: string }
  | { id: number; type: "focus_node"; nodeId: string }
  | { id: number; type: "get_clock" | "clock_pause" | "clock_resume" }
  | { id: number; type: "clock_install"; initialTimeMs?: number; stepMs?: number }
  | { id: number; type: "clock_run_for"; durationMs: number; stepMs?: number }
  | { id: number; type: "set_value"; nodeId: string; value: string; sensitive?: boolean }
  | { id: number; type: "set_checked"; nodeId: string; checked: boolean }
  | { id: number; type: "select"; nodeId: string; value: string }
  | { id: number; type: "scroll"; nodeId: string; deltaX: number; deltaY: number; scrollUnit?: number }
  | { id: number; type: "press_key"; nodeId?: string; key: string; modifiers?: number }
  | ({ id: number } & GameInputCommandInput)
  | { id: number; type: "get_game_input_actions" | "get_game_input_state" }
  | { id: number; type: "find_game_input_actions"; actionId?: string; query?: string; valueType?: 1 | 2 | 3 | 4;
      active?: 0 | 1 | 2; context?: string; category?: string; tags?: string[]; limit?: number }
  | { id: number; type: "poll_game_input"; requestId: number };

type GuaInspectorCommandInput =
  | { type: "get_version" }
  | { type: "get_observe_snapshot" | "subscribe_observations" }
  | { type: "poll_observations" | "unsubscribe_observations"; subscriptionId: number }
  | { type: "get_ui_tree" }
  | { type: "get_world_object_tree" }
  | { type: "get_logs" }
  | { type: "get_screenshot" }
  | { type: "get_context_status" }
  | { type: "poll_events"; requestId: number }
  | { type: "click_node"; nodeId: string }
  | { type: "focus_node"; nodeId: string }
  | { type: "get_clock" | "clock_pause" | "clock_resume" }
  | { type: "clock_install"; initialTimeMs?: number; stepMs?: number }
  | { type: "clock_run_for"; durationMs: number; stepMs?: number }
  | { type: "set_value"; nodeId: string; value: string; sensitive?: boolean }
  | { type: "set_checked"; nodeId: string; checked: boolean }
  | { type: "select"; nodeId: string; value: string }
  | { type: "scroll"; nodeId: string; deltaX: number; deltaY: number; scrollUnit?: number }
  | { type: "press_key"; nodeId?: string; key: string; modifiers?: number }
  | GameInputCommandInput
  | { type: "get_game_input_actions" | "get_game_input_state" }
  | ({ type: "find_game_input_actions" } & GuaGameInputActionSelector)
  | { type: "poll_game_input"; requestId: number };

export type GuaInspectorResponse =
  | { id: number; ok: true; result: unknown }
  | { id: number; ok: false; error: string };

export type GuaInspectorNotification =
  | { type: "snapshot"; snapshot: InspectorSnapshot };

export type SnapshotListener = (snapshot: InspectorSnapshot) => void;

export const initialPanels: InspectorPanel[] = [
  { id: "tree", title: "UI Tree" },
  { id: "node", title: "Node Detail" },
  { id: "screenshot", title: "Screenshot" },
  { id: "logs", title: "Logs" },
];

export function createInspectorState(snapshot?: Partial<InspectorSnapshot>): InspectorState {
  const uiTree = snapshot?.uiTree ?? {
    schemaVersion: 2,
    sessionEpoch: 1,
    frameSequence: 0,
    revision: 0,
    screen: "unknown",
    nodes: [],
  };
  return {
    uiTree,
    worldObjectTree: snapshot?.worldObjectTree ?? { schemaVersion: 1, sessionEpoch: 1, frameSequence: 0, revision: 0, scene: "unknown", objects: [] },
    logs: snapshot?.logs ?? [],
    screenshot: snapshot?.screenshot ?? { dataUri: "", width: 0, height: 0 },
    selectedNodeId: uiTree.nodes[0]?.id ?? null,
  };
}

export function parseInspectorSnapshot(input: {
  uiTreeJson: string;
  worldObjectTreeJson?: string;
  logsJson?: string;
  screenshotJson?: string;
}): InspectorSnapshot {
  return {
    uiTree: parseJson<GuaUiTree>(input.uiTreeJson, "Gua UI tree"),
    worldObjectTree: input.worldObjectTreeJson === undefined
      ? { schemaVersion: 1, sessionEpoch: 1, frameSequence: 0, revision: 0, scene: "unknown", objects: [] }
      : parseJson<GuaWorldObjectTree>(input.worldObjectTreeJson, "Gua World Object Tree"),
    logs: input.logsJson === undefined ? [] : parseJson<GuaLogEntry[]>(input.logsJson, "Gua logs"),
    screenshot: input.screenshotJson === undefined
      ? { dataUri: "", width: 0, height: 0 }
      : parseJson<GuaScreenshot>(input.screenshotJson, "Gua screenshot"),
  };
}

export function selectNode(state: InspectorState, nodeId: string | null): InspectorState {
  const selectedNodeId = nodeId !== null && state.uiTree.nodes.some((node) => node.id === nodeId)
    ? nodeId
    : state.uiTree.nodes[0]?.id ?? null;

  return {
    ...state,
    selectedNodeId,
  };
}

export function updateInspectorState(state: InspectorState, snapshot: Partial<InspectorSnapshot>): InspectorState {
  const next: InspectorState = {
    ...state,
    ...snapshot,
  };

  return selectNode(next, state.selectedNodeId);
}

export function getSelectedNode(state: InspectorState): GuaNode | null {
  return state.uiTree.nodes.find((node) => node.id === state.selectedNodeId) ?? null;
}

export function worldObjectDepths(objects: GuaWorldObject[]): Map<string, number> {
  const byId = new Map(objects.map((object) => [object.id, object]));
  const depths = new Map<string, number>();
  const depthOf = (object: GuaWorldObject, visiting = new Set<string>()): number => {
    const cached = depths.get(object.id);
    if (cached !== undefined) return cached;
    if (object.parentId === undefined || !byId.has(object.parentId) || visiting.has(object.id)) return 0;
    visiting.add(object.id);
    const depth = depthOf(byId.get(object.parentId)!, visiting) + 1;
    visiting.delete(object.id);
    depths.set(object.id, depth);
    return depth;
  };
  for (const object of objects) depths.set(object.id, depthOf(object));
  return depths;
}

export async function readSnapshot(client: GuaInspectorClient): Promise<InspectorSnapshot> {
  for (let attempt = 0; attempt < 3; attempt += 1) {
    const [uiTree, worldObjectTree, logs, screenshot] = await Promise.all([
      client.getUiTree(),
      client.getWorldObjectTree(),
      client.getLogs(),
      client.getScreenshot(),
    ]);
    if (uiTree.sessionEpoch === undefined || uiTree.sessionEpoch === worldObjectTree.sessionEpoch)
      return { uiTree, worldObjectTree, logs, screenshot };
  }
  throw new Error("Unable to read UI and World Object Trees from the same session epoch.");
}

export class MockInspectorClient implements GuaInspectorClient {
  private clock: GuaClockStatus = { schemaVersion: 1, installed: false, state: "running", nowMs: 0, defaultStepMs: 1000 / 60, pendingMs: 0, generation: 0, completedOperationSequence: 0 };
  private logs: GuaLogEntry[] = [
    { sequence: 1, level: "info", message: "Inspector connected to mock runtime." },
    { sequence: 2, level: "debug", message: "Title screen snapshot received." },
  ];

  private screen: "title" | "loading" = "title";
  private frameSequence = 1;
  private revision = 1;

  async getUiTree(): Promise<GuaUiTree> {
    if (this.screen === "loading") {
      return {
        schemaVersion: 2,
        sessionEpoch: 1,
        frameSequence: this.frameSequence,
        revision: this.revision,
        screen: "loading",
        nodes: [
          {
            id: "root",
            role: "screen",
            label: "Loading Screen",
            visible: true,
            enabled: false,
            bounds: { x: 0, y: 0, w: 1280, h: 720 },
            actions: [],
          },
          {
            id: "loading",
            role: "text",
            label: "Loading...",
            visible: true,
            enabled: false,
            bounds: { x: 544, y: 328, w: 192, h: 48 },
            actions: [],
          },
        ],
      };
    }

    return {
      schemaVersion: 2,
      sessionEpoch: 1,
      frameSequence: this.frameSequence,
      revision: this.revision,
      screen: "title",
      nodes: [
        {
          id: "root",
          role: "screen",
          label: "Title Screen",
          visible: true,
          enabled: false,
          bounds: { x: 0, y: 0, w: 1280, h: 720 },
          actions: [],
        },
        {
          id: "menu",
          parentId: "root",
          role: "panel",
          label: "Main Menu",
          visible: true,
          enabled: false,
          bounds: { x: 448, y: 232, w: 384, h: 256 },
          actions: [],
        },
        {
          id: "start",
          parentId: "menu",
          role: "button",
          label: "Start Game",
          visible: true,
          enabled: true,
          bounds: { x: 512, y: 312, w: 256, h: 56 },
          state: { focused: true },
          actions: ["click", "focus"],
        },
        {
          id: "settings",
          parentId: "menu",
          role: "button",
          label: "Settings",
          visible: true,
          enabled: true,
          bounds: { x: 512, y: 384, w: 256, h: 56 },
          actions: ["click", "focus"],
        },
      ],
    };
  }
  async getWorldObjectTree(): Promise<GuaWorldObjectTree> { return { schemaVersion: 1, sessionEpoch: 1, frameSequence: this.frameSequence, revision: 1, scene: this.screen, objects: [{ id: "door-a", kind: "door", label: "Door A", space: "world2d", position: { x: 640, y: 180 }, visibleToPlayer: true, active: true, agentExposure: "auto", tags: ["east-corridor"], state: { open: false, locked: true } }] }; }

  async getLogs(): Promise<GuaLogEntry[]> {
    return this.logs;
  }

  async getScreenshot(): Promise<GuaScreenshot> {
    return {
      dataUri: "",
      width: 1280,
      height: 720,
    };
  }

  async getContextStatus(): Promise<GuaContextStatus> {
    return {
      sessionEpoch: 1, frameSequence: this.frameSequence, revision: this.revision,
      nodeCount: (await this.getUiTree()).nodes.length, pendingRequestCount: 0,
      inFlightRequestCount: 0, unconsumedEventCount: 0, logCount: this.logs.length,
      hasScreenshot: false, firstPendingAction: 0, firstPendingNodeId: "",
      firstEventAction: 0, firstEventNodeId: "",
    };
  }

  async performAction(action: SemanticActionInput): Promise<ActionOutcome> {
    if (action.action === "click") await this.clickNode(action.nodeId as string);
    else if (action.action === "focus") await this.focusNode(action.nodeId as string);
    else {
      this.logs = [...this.logs, {
        sequence: this.logs.length + 1,
        level: "info",
        message: `${action.action}(${action.nodeId ?? "current-focus"})`,
      }];
    }
    return {};
  }

  async clickNode(nodeId: string): Promise<void> {
    this.logs = [
      ...this.logs,
      {
        sequence: this.logs.length + 1,
        level: "info",
        message: `click_node(${nodeId})`,
      },
    ];

    if (nodeId === "start") {
      this.screen = "loading";
      this.frameSequence += 1;
      this.revision += 1;
    }
  }

  async focusNode(nodeId: string): Promise<void> {
    this.logs = [
      ...this.logs,
      {
        sequence: this.logs.length + 1,
        level: "debug",
        message: `focus_node(${nodeId})`,
      },
    ];
  }
  async getClock() { return this.clock; }
  async installClock(initialTimeMs = 0, stepMs = 1000 / 60) { this.clock = { ...this.clock, installed: true, state: "running", nowMs: initialTimeMs, defaultStepMs: stepMs, generation: this.clock.generation + 1 }; return this.clock; }
  async pauseClock() { if (!this.clock.installed) throw new Error("not_installed"); this.clock = { ...this.clock, state: "paused" }; return this.clock; }
  async runClockFor(durationMs: number, stepMs?: number) { if (this.clock.state !== "paused") throw new Error("invalid_state"); if (stepMs !== undefined && stepMs <= 0) throw new Error("invalid_duration"); this.clock = { ...this.clock, nowMs: this.clock.nowMs + durationMs }; return this.clock; }
  async resumeClock() { if (!this.clock.installed) throw new Error("not_installed"); this.clock = { ...this.clock, state: "running" }; return this.clock; }
  async getGameInputActions(): Promise<GuaGameInputActions> { return { schemaVersion: 1, sessionEpoch: 1, revision: 1, context: "gameplay", actions: [
    { id: "jump", description: "Jump", valueType: "button", holdable: true, active: true, bindings: ["Space"], risk: "safe", requiresConfirmation: false },
    { id: "move", description: "Move", valueType: "vector2", range: { minimum: -1, maximum: 1 }, holdable: true, active: true, bindings: ["Gamepad.leftStick"], risk: "safe", requiresConfirmation: false },
  ] }; }
  async findGameInputActions(selector: GuaGameInputActionSelector): Promise<GuaGameInputActionSearchResult> {
    const map = await this.getGameInputActions();
    const actions = map.actions.filter((action) => (!selector.id || action.id === selector.id) &&
      (!selector.query || action.id.includes(selector.query) || action.description?.includes(selector.query) || action.aliases?.some((alias) => alias.includes(selector.query!))) &&
      (!selector.valueType || action.valueType === selector.valueType) && (selector.active === undefined || action.active === selector.active) &&
      (!selector.context || selector.context === map.context) && (!selector.category || action.category === selector.category) &&
      (!selector.tags || selector.tags.every((tag) => action.tags?.includes(tag)))).sort((left, right) => left.id < right.id ? -1 : left.id > right.id ? 1 : 0);
    const limit = selector.limit ?? 20;
    return { ...map, count: Math.min(actions.length, limit), truncated: actions.length > limit, actions: actions.slice(0, limit) };
  }
  async getGameInputState(): Promise<GuaGameInputState> { return { schemaVersion: 1, held: [] }; }
  async performGameInput(command: GameInputCommandInput): Promise<{ requestId: number }> { this.logs = [...this.logs, { sequence: this.logs.length + 1, level: "info", message: `game_input(${command.type})` }]; return { requestId: this.logs.length }; }
}

function actionCommand(action: SemanticActionInput): GuaInspectorCommandInput {
  switch (action.action) {
    case "click": return { type: "click_node", nodeId: requiredNodeId(action) };
    case "focus": return { type: "focus_node", nodeId: requiredNodeId(action) };
    case "set_value": return {
      type: "set_value", nodeId: requiredNodeId(action), value: action.value ?? "", sensitive: action.sensitive,
    };
    case "set_checked": return { type: "set_checked", nodeId: requiredNodeId(action), checked: action.checked === true };
    case "select": return { type: "select", nodeId: requiredNodeId(action), value: action.value ?? "" };
    case "scroll": return {
      type: "scroll", nodeId: requiredNodeId(action), deltaX: action.deltaX ?? 0,
      deltaY: action.deltaY ?? 0, scrollUnit: action.scrollUnit,
    };
    case "press_key": return {
      type: "press_key", nodeId: action.nodeId, key: action.key ?? "", modifiers: action.modifiers,
    };
    case "game_input": throw new Error("Use performGameInput for game_input actions.");
  }
}

function requiredNodeId(action: SemanticActionInput): string {
  if (action.nodeId === undefined || action.nodeId.length === 0) throw new Error(`${action.action} requires nodeId.`);
  return action.nodeId;
}

interface PendingRequest {
  observeOperation?: string;
  resolve(value: unknown): void;
  reject(reason: Error): void;
  timeoutId: ReturnType<typeof setTimeout>;
}

const clockPauseResponseTimeoutMs = 11_000;

export function createCoalescedAsyncRunner(task: () => Promise<void>): () => Promise<void> {
  let active: Promise<void> | null = null;
  let rerunRequested = false;

  return () => {
    rerunRequested = true;
    if (active !== null) return active;

    active = (async () => {
      do {
        rerunRequested = false;
        await task();
      } while (rerunRequested);
    })().finally(() => {
      active = null;
    });
    return active;
  };
}

export async function findGameInputActionsCompatible(
  client: GuaInspectorClient,
  selector: GuaGameInputActionSelector,
): Promise<GuaGameInputActionSearchResult> {
  try {
    return await client.findGameInputActions(selector);
  } catch (error) {
    if (!/unsupported|unknown command/i.test((error as Error).message)) throw error;
    const map = await client.getGameInputActions();
    const limit = selector.limit ?? 20;
    const matches = map.actions.filter((action) => (!selector.id || action.id === selector.id) &&
      (!selector.query || action.id.includes(selector.query) || action.description?.includes(selector.query) || action.aliases?.some((alias) => alias.includes(selector.query!))) &&
      (!selector.valueType || action.valueType === selector.valueType) && (selector.active === undefined || action.active === selector.active) &&
      (!selector.context || selector.context === map.context) && (!selector.category || action.category === selector.category) &&
      (!selector.tags || selector.tags.every((tag) => action.tags?.includes(tag))))
      .sort((left, right) => left.id < right.id ? -1 : left.id > right.id ? 1 : 0);
    const actions = matches.slice(0, limit);
    return { ...map, count: actions.length, truncated: matches.length > actions.length, actions };
  }
}

export class WebSocketInspectorClient implements GuaInspectorClient {
  private socket: WebSocket | null = null;
  private connectPromise: Promise<WebSocket> | null = null;
  private nextId = 1;
  private nextObserveHandle = 1;
  private observeSubscriptions = new Map<number, { socket: WebSocket; remoteId: number }>();
  private pending = new Map<number, PendingRequest>();
  private snapshotListeners = new Set<SnapshotListener>();

  constructor(
    private readonly url: string,
    private readonly requestTimeoutMs = 5000,
  ) {
  }

  async getUiTree(): Promise<GuaUiTree> {
    return this.request<GuaUiTree>({ type: "get_ui_tree" });
  }
  async getWorldObjectTree(): Promise<GuaWorldObjectTree> { return this.request({ type: "get_world_object_tree" }); }
  private async requireObserve(): Promise<void> {
    const version = await this.request<{ capabilities?: string[] }>({ type: "get_version" });
    if (!version.capabilities?.includes("observe_v1")) throw new Error("The runtime does not support observe_v1.");
  }
  async getObserveSnapshot(): Promise<ObserveTransport> { await this.requireObserve(); return parseObserveTransport(await this.request({ type: "get_observe_snapshot" })); }
  async subscribeObservations(): Promise<ObserveSubscription> {
    await this.requireObserve();
    const socket = await this.connect();
    const result = await this.request<ObserveSubscription>({ type: "subscribe_observations" }, this.requestTimeoutMs, socket);
    const remoteId = observeSubscriptionId(result.subscriptionId), snapshot = parseObserveTransport(result.snapshot);
    if (socket !== this.socket || socket.readyState !== WebSocket.OPEN) throw new Error("Observe subscription belongs to an inactive connection.");
    const subscriptionId = observeSubscriptionId(this.nextObserveHandle++);
    this.observeSubscriptions.set(subscriptionId, { socket, remoteId });
    return { subscriptionId, snapshot };
  }
  async pollObservations(subscriptionId: number): Promise<ObserveTransport> {
    const owner = this.observeSubscriptions.get(observeSubscriptionId(subscriptionId));
    if (!owner) throw new Error("Observe subscription belongs to an inactive connection.");
    return parseObserveTransport(await this.request({ type: "poll_observations", subscriptionId: owner.remoteId }, this.requestTimeoutMs, owner.socket));
  }
  async unsubscribeObservations(subscriptionId: number): Promise<void> {
    const owner = this.observeSubscriptions.get(observeSubscriptionId(subscriptionId));
    if (!owner || owner.socket !== this.socket || owner.socket.readyState !== WebSocket.OPEN) { this.observeSubscriptions.delete(subscriptionId); return; }
    await this.request({ type: "unsubscribe_observations", subscriptionId: owner.remoteId }, this.requestTimeoutMs, owner.socket);
    this.observeSubscriptions.delete(subscriptionId);
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

  async getClock(): Promise<GuaClockStatus> { return this.request({ type: "get_clock" }); }
  async installClock(initialTimeMs = 0, stepMs?: number): Promise<GuaClockStatus> { return this.request({ type: "clock_install", initialTimeMs, stepMs }); }
  async pauseClock(): Promise<GuaClockStatus> {
    return this.request({ type: "clock_pause" }, Math.max(this.requestTimeoutMs, clockPauseResponseTimeoutMs));
  }
  async runClockFor(durationMs: number, stepMs?: number): Promise<GuaClockStatus> {
    let status = await this.request<GuaClockStatus>({ type: "clock_run_for", durationMs, stepMs });
    const completionEpoch = status.completionSessionEpoch;
    const completionFrame = status.completionAfterFrameSequence;
    const operationSequence = status.operationSequence;
    if (completionEpoch === undefined || completionFrame === undefined || operationSequence === undefined) throw new Error("unsupported");
    const started = Date.now();
    while (Date.now() - started < this.requestTimeoutMs) {
      const context = await this.getContextStatus();
      if (context.sessionEpoch !== completionEpoch) throw new Error("stale_session");
      if (status.completedOperationSequence >= operationSequence && context.frameSequence > completionFrame) return status;
      await new Promise((resolve) => window.setTimeout(resolve, 5));
      status = await this.getClock();
    }
    throw new Error("Timed out waiting for Gua clock run_for host completion.");
  }
  async resumeClock(): Promise<GuaClockStatus> { return this.request({ type: "clock_resume" }); }
  async getGameInputActions(): Promise<GuaGameInputActions> { return this.request({ type: "get_game_input_actions" }); }
  async findGameInputActions(selector: GuaGameInputActionSelector): Promise<GuaGameInputActionSearchResult> {
    const valueType = selector.valueType === undefined ? undefined : ({ button: 1, axis1d: 2, vector2: 3, text: 4 } as const)[selector.valueType];
    const { id, ...rest } = selector;
    return this.request({ type: "find_game_input_actions", ...rest, actionId: id, valueType,
      active: selector.active === undefined ? undefined : selector.active ? 2 : 1 } as unknown as GuaInspectorCommandInput);
  }
  async getGameInputState(): Promise<GuaGameInputState> { return this.request({ type: "get_game_input_state" }); }
  async performGameInput(command: GameInputCommandInput): Promise<{ requestId: number }> {
    const receipt = await this.request<{ requestId: number }>(command);
    const started = Date.now();
    while (Date.now() - started <= 10000) {
      const result = await this.request<{ completed: boolean; succeeded?: boolean; errorCode?: number }>({ type: "poll_game_input", requestId: receipt.requestId });
      if (result.completed) {
        if (!result.succeeded) throw new Error(`Gua game input failed with error ${result.errorCode ?? "unknown"}.`);
        return receipt;
      }
      await new Promise((resolve) => window.setTimeout(resolve, 25));
    }
    throw new Error(`Timed out waiting for Gua game input request ${receipt.requestId}.`);
  }

  async performAction(action: SemanticActionInput): Promise<ActionOutcome> {
    const command = actionCommand(action);
    const receipt = await this.request<{ requestId: number } | null>(command);
    if (receipt === null) return {};
    const started = Date.now();
    while (Date.now() - started <= 10000) {
      const completion = await this.request<NonNullable<ActionOutcome["completion"]> | null>({ type: "poll_events", requestId: receipt.requestId });
      if (completion !== null) {
        if (!completion.succeeded) throw new Error(`Gua action failed with error ${completion.error}.`);
        return { requestId: receipt.requestId, completion };
      }
      await new Promise((resolve) => window.setTimeout(resolve, 25));
    }
    throw new Error(`Timed out waiting for Gua action request ${receipt.requestId}.`);
  }

  async clickNode(nodeId: string): Promise<void> {
    await this.performAction({ action: "click", nodeId });
  }

  async focusNode(nodeId: string): Promise<void> {
    await this.performAction({ action: "focus", nodeId });
  }

  close(): void {
    this.observeSubscriptions.clear();
    this.rejectAll(new Error("Gua Inspector WebSocket client closed."));
    this.socket?.close();
    this.socket = null;
    this.connectPromise = null;
    this.snapshotListeners.clear();
  }

  subscribeSnapshots(listener: SnapshotListener): () => void {
    this.snapshotListeners.add(listener);
    void this.connect().catch(() => {
      this.snapshotListeners.delete(listener);
    });
    return () => {
      this.snapshotListeners.delete(listener);
    };
  }

  private async request<T>(command: GuaInspectorCommandInput, timeoutMs = this.requestTimeoutMs, ownerSocket?: WebSocket): Promise<T> {
    const socket = ownerSocket ?? await this.connect();
    if (ownerSocket && (socket !== this.socket || socket.readyState !== WebSocket.OPEN)) throw new Error("Observe subscription belongs to an inactive connection.");
    const id = this.nextId++;
    const payload = { ...command, id } as GuaInspectorCommand;

    return new Promise<T>((resolve, reject) => {
      const timeoutId = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`Timed out waiting for ${command.type}.`));
        if (command.type === "subscribe_observations" || command.type === "poll_observations") socket.close();
      }, timeoutMs);

      this.pending.set(id, {
        observeOperation: ["get_observe_snapshot", "subscribe_observations", "poll_observations", "unsubscribe_observations"].includes(command.type) ? command.type : undefined,
        resolve: (value) => resolve(value as T),
        reject,
        timeoutId,
      });

      socket.send(JSON.stringify(payload));
    });
  }

  private async connect(): Promise<WebSocket> {
    if (this.socket !== null && this.socket.readyState === WebSocket.OPEN) {
      return this.socket;
    }

    if (this.connectPromise !== null) {
      return this.connectPromise;
    }

    const socket = new WebSocket(this.url);
    const attempt = new Promise<WebSocket>((resolve, reject) => {

      socket.addEventListener("open", () => {
        if (this.connectPromise !== attempt) { socket.close(); reject(new Error("Gua Inspector connection attempt ended.")); return; }
        this.socket = socket;
        this.connectPromise = null;
        resolve(socket);
      });

      socket.addEventListener("message", (event) => {
        if (this.socket === socket) this.handleMessage(event.data, socket);
      });

      socket.addEventListener("close", () => {
        for (const [id, owner] of this.observeSubscriptions) if (owner.socket === socket) this.observeSubscriptions.delete(id);
        const error = new Error("Gua Inspector WebSocket connection closed.");
        if (this.connectPromise === attempt) { this.connectPromise = null; reject(error); }
        if (this.socket === socket) { this.socket = null; this.rejectAll(error); }
      });

      socket.addEventListener("error", () => {
        const error = new Error(`Failed to connect to Gua bridge at ${this.url}.`);
        if (this.connectPromise === attempt) { this.connectPromise = null; reject(error); }
        if (this.socket === socket) this.rejectAll(error);
      });
    });

    this.connectPromise = attempt;
    return attempt;
  }

  private handleMessage(data: unknown, ownerSocket: WebSocket): void {
    if (typeof data !== "string") {
      return;
    }

    let parsed: GuaInspectorResponse | GuaInspectorNotification;
    try {
      parsed = JSON.parse(data) as GuaInspectorResponse | GuaInspectorNotification;
    } catch {
      return;
    }

    if (isNotification(parsed)) {
      for (const listener of this.snapshotListeners) {
        listener(parsed.snapshot);
      }
      return;
    }

    const response = parsed;
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
      pending.reject(new Error(response.error));
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

function parseJson<T>(json: string, description: string): T {
  try {
    return JSON.parse(json) as T;
  } catch (error) {
    throw new Error(`Invalid ${description} JSON: ${(error as Error).message}`);
  }
}

function isNotification(value: GuaInspectorResponse | GuaInspectorNotification): value is GuaInspectorNotification {
  return "type" in value && value.type === "snapshot";
}
import type { ActionOutcome, SemanticActionInput } from "./automation";
