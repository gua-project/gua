import { afterEach, describe, expect, test } from "bun:test";

type GodotWebPort = {
  __guaUninstall(): void;
  invoke(command: unknown, options?: { signal?: AbortSignal; timeoutMs?: number }): Promise<unknown>;
};

type GodotResponse = { value?: string | number };
type GodotCallback<TArgs extends unknown[] = []> = (response: GodotResponse, ...args: TArgs) => void;

function godotCallback<TArgs extends unknown[], TResult extends string | number>(
  callback: (...args: TArgs) => TResult,
): GodotCallback<TArgs> {
  return (response, ...args) => { response.value = callback(...args); };
}

const godotGlobals = globalThis as typeof globalThis & {
  __guaGodotWebPort?: GodotWebPort;
  __guaGodotObserve?: GodotCallback<[request: string]>;
  __guaGodotReleaseObserve?: GodotCallback;
  __guaGodotGetTree?: GodotCallback;
  __guaGodotGetWorldTree?: GodotCallback;
  __guaGodotQueryWorld?: GodotCallback<[request: string]>;
  __guaGodotEnqueueAction?: GodotCallback<[request: string]>;
  __guaGodotPollAction?: GodotCallback<[requestId: string]>;
  __guaGodotCancelAction?: GodotCallback<[requestId: string]>;
  __guaGodotGetGameInputCapabilities?: GodotCallback;
  __guaGodotGetGameInputActions?: GodotCallback;
  __guaGodotGetGameInputActionsV2?: GodotCallback;
  __guaGodotFindGameInputActionsV2?: GodotCallback<[request: string]>;
  __guaGodotFindGameInputActions?: GodotCallback<[request: string]>;
  __guaGodotGetGameInputState?: GodotCallback;
  __guaGodotEnqueueGameInput?: GodotCallback<[request: string]>;
  __guaGodotPollGameInput?: GodotCallback<[requestId: string]>;
  __guaGodotReleaseGameInput?: GodotCallback<[recreate?: string]>;
};

afterEach(() => {
  godotGlobals.__guaGodotWebPort?.__guaUninstall();
  delete godotGlobals.__guaGodotWebPort;
  delete godotGlobals.__guaGodotGetTree;
  delete godotGlobals.__guaGodotGetWorldTree;
  delete godotGlobals.__guaGodotQueryWorld;
  delete godotGlobals.__guaGodotEnqueueAction;
  delete godotGlobals.__guaGodotPollAction;
  delete godotGlobals.__guaGodotCancelAction;
  delete godotGlobals.__guaGodotGetGameInputCapabilities;
  delete godotGlobals.__guaGodotGetGameInputActions;
  delete godotGlobals.__guaGodotGetGameInputActionsV2;
  delete godotGlobals.__guaGodotFindGameInputActionsV2;
  delete godotGlobals.__guaGodotFindGameInputActions;
  delete godotGlobals.__guaGodotGetGameInputState;
  delete godotGlobals.__guaGodotEnqueueGameInput;
  delete godotGlobals.__guaGodotPollGameInput;
  delete godotGlobals.__guaGodotReleaseGameInput;
});

async function installGodotWebPort(
  cancelled: string[],
  options: {
    cancellationResult?: number;
    pollAction?: (requestId: string) => string;
    enqueueGameInput?: (request: string) => string;
    pollGameInput?: (requestId: string) => string;
    releaseGameInput?: (recreate?: string) => number;
    findGameInputActions?: (request: string) => string;
    getGameInputActionsV2?: () => string;
    findGameInputActionsV2?: (request: string) => string;
  } = {},
) {
  const source = await Bun.file(new URL(
    "../../../examples/godot-gdscript/addons/gua/gua_webmcp_bridge.gd",
    import.meta.url,
  )).text();
  const match = source.match(/JavaScriptBridge\.eval\("""([\s\S]*?)"""/);
  if (!match) throw new Error("Godot WebMCP install script was not found.");
  godotGlobals.__guaGodotObserve = godotCallback(() => "null");
  godotGlobals.__guaGodotReleaseObserve = godotCallback(() => 1);
  godotGlobals.__guaGodotGetTree = godotCallback(() => JSON.stringify({ screen: "title", nodes: [] }));
  godotGlobals.__guaGodotGetWorldTree = godotCallback(() => JSON.stringify({ schemaVersion: 1, sessionEpoch: 1, frameSequence: 1, revision: 1, scene: "level", objects: [] }));
  godotGlobals.__guaGodotQueryWorld = godotCallback(() => JSON.stringify({ valid: true, matches: [] }));
  godotGlobals.__guaGodotEnqueueAction = godotCallback(() => JSON.stringify({ requestId: 17 }));
  godotGlobals.__guaGodotPollAction = godotCallback(options.pollAction ?? (() => "null"));
  godotGlobals.__guaGodotCancelAction = godotCallback((requestId) => { cancelled.push(requestId); return options.cancellationResult ?? 1; });
  godotGlobals.__guaGodotGetGameInputCapabilities = godotCallback(() => JSON.stringify(["raw_keyboard_input_v1"]));
  if (options.getGameInputActionsV2) godotGlobals.__guaGodotGetGameInputActionsV2 = godotCallback(options.getGameInputActionsV2);
  if (options.findGameInputActionsV2) godotGlobals.__guaGodotFindGameInputActionsV2 = godotCallback(options.findGameInputActionsV2);
  godotGlobals.__guaGodotGetGameInputActions = godotCallback(() => JSON.stringify({ schemaVersion: 1, sessionEpoch: 1, revision: 1, context: "", actions: [] }));
  godotGlobals.__guaGodotFindGameInputActions = godotCallback(options.findGameInputActions ?? ((request: string) => {
    const selector = JSON.parse(request) as { id?: string };
    return JSON.stringify({ schemaVersion: 1, sessionEpoch: 1, revision: 2, context: "gameplay", count: 1,
      truncated: false, actions: [{ id: selector.id, description: "Jump", valueType: "button", holdable: true,
        active: true, bindings: ["Space"], risk: "safe", requiresConfirmation: false, category: "movement",
        aliases: ["hop"], tags: ["gameplay"], agentExposure: "auto" }] });
  }));
  godotGlobals.__guaGodotGetGameInputState = godotCallback(() => JSON.stringify({ schemaVersion: 1, held: [] }));
  godotGlobals.__guaGodotEnqueueGameInput = godotCallback(options.enqueueGameInput ?? (() => JSON.stringify({ requestId: 23 })));
  godotGlobals.__guaGodotPollGameInput = godotCallback(options.pollGameInput ?? (() => "null"));
  godotGlobals.__guaGodotReleaseGameInput = godotCallback(options.releaseGameInput ?? (() => 1));
  new Function(match[1]!.replaceAll("%s", "test-owner"))();
  return godotGlobals.__guaGodotWebPort!;
}

describe("Godot Web same-page port", () => {
  test("v2 discovery rejects extra fields before host dispatch while preserving v1 calls", async () => {
    let calls = 0;
    const response = () => { calls++; return JSON.stringify({ schemaVersion: 2, actions: [] }); };
    const port = await installGodotWebPort([], { getGameInputActionsV2: response, findGameInputActionsV2: response });
    for (const type of ["get_game_input_actions_v2", "find_game_input_actions_v2"]) {
      for (const extra of [{ confirmed: true }, { unknown: true }, { requestId: 1 }])
        await expect(port.invoke({ type, ...extra })).rejects.toMatchObject({ code: "invalid_request" });
    }
    expect(calls).toBe(0);
    await expect(port.invoke({ type: "get_game_input_actions_v2" })).resolves.toMatchObject({ schemaVersion: 2 });
    await expect(port.invoke({ type: "find_game_input_actions_v2", id: "jump", query: "hop", valueType: "button",
      active: true, context: "play", category: "movement", tags: ["gameplay"], limit: 100 })).resolves.toMatchObject({ schemaVersion: 2 });
    expect(calls).toBe(2);
    await expect(port.invoke({ type: "get_game_input_actions", confirmed: true })).resolves.toMatchObject({ schemaVersion: 1 });
  });
  test("v2 discovery preserves structured capability-revocation errors", async () => {
    let revoked = false;
    const response = () => JSON.stringify(revoked
      ? { code: "engine_unsupported", message: "Semantic input was revoked." }
      : { schemaVersion: 2, actions: [] });
    const port = await installGodotWebPort([], { getGameInputActionsV2: response, findGameInputActionsV2: response });
    await expect(port.invoke({ type: "get_game_input_actions_v2" })).resolves.toMatchObject({ schemaVersion: 2 });
    revoked = true;
    for (const type of ["get_game_input_actions_v2", "find_game_input_actions_v2"]) {
      await expect(port.invoke({ type })).rejects.toMatchObject({ code: "engine_unsupported", message: "Semantic input was revoked." });
    }
  });
  test("metadata discovery is routed only by explicit v2 calls", async () => {
    let metadataCalls = 0;
    const port = await installGodotWebPort([], { getGameInputActionsV2: () => {
      metadataCalls++;
      return JSON.stringify({ schemaVersion: 2, actions: [{ id: "axis", examples: [0.5] }] });
    }, findGameInputActionsV2: (request) => {
      metadataCalls++;
      return JSON.stringify({ schemaVersion: 2, actions: [{ id: JSON.parse(request).id, examples: [0.5] }] });
    } });
    await expect(port.invoke({ type: "get_game_input_actions" })).resolves.toMatchObject({ schemaVersion: 1 });
    expect(metadataCalls).toBe(0);
    await expect(port.invoke({ type: "get_game_input_actions_v2" })).resolves.toMatchObject({ schemaVersion: 2 });
    await expect(port.invoke({ type: "find_game_input_actions_v2", id: "axis" })).resolves.toMatchObject({
      schemaVersion: 2, actions: [{ id: "axis", examples: [0.5] }],
    });
    expect(metadataCalls).toBe(2);
  });
  test("detaching removes all observation callback globals", async () => {
    const port = await installGodotWebPort([]);
    expect(typeof godotGlobals.__guaGodotObserve).toBe("function");
    port.__guaUninstall();
    // Uninstall releases client resources; GDScript detach owns global deletion.
    const source = await Bun.file(new URL("../../../examples/godot-gdscript/addons/gua/gua_webmcp_bridge.gd", import.meta.url)).text();
    const scripts = [...source.matchAll(/JavaScriptBridge\.eval\("""([\s\S]*?)"""/g)];
    new Function(scripts[1]![1]!.replaceAll("%s", "test-owner"))();
    expect("__guaGodotObserve" in godotGlobals).toBe(false);
    expect("__guaGodotReleaseObserve" in godotGlobals).toBe(false);
  });
  test("preserves structured selector errors from the Godot binding", async () => {
    const port = await installGodotWebPort([], {
      findGameInputActions: () => JSON.stringify({ code: "invalid_request", message: "Invalid game input selector." }),
    });
    await expect(port.invoke({ type: "find_game_input_actions", limit: 1.5 }))
      .rejects.toMatchObject({ code: "invalid_request" });
  });
  test("routes bounded semantic action search through the Godot callback", async () => {
    const port = await installGodotWebPort([]);
    await expect(port.invoke({ type: "find_game_input_actions", id: "jump", limit: 1 })).resolves.toMatchObject({
      context: "gameplay", count: 1, truncated: false, actions: [{ id: "jump", category: "movement" }],
    });
  });
  test("routes World Object Tree reads and queries through Godot callbacks", async () => {
    const port = await installGodotWebPort([]);
    await expect(port.invoke({ type: "get_world_object_tree" })).resolves.toMatchObject({ scene: "level" });
    await expect(port.invoke({ type: "query_world_objects", worldId: "door" })).resolves.toEqual({ valid: true, matches: [] });
  });
  test("cancels the native request when the bridge signal aborts", async () => {
    const cancelled: string[] = [];
    const port = await installGodotWebPort(cancelled);
    const controller = new AbortController();
    const pending = port
      .invoke({ type: "perform_action", request: { action: "click", nodeId: "start" } }, { signal: controller.signal })
      .catch((error) => error);

    controller.abort();

    await expect(pending).resolves.toMatchObject({ code: "aborted" });
    expect(cancelled).toEqual(["17"]);
  });

  test("uses the per-call action timeout", async () => {
    const cancelled: string[] = [];
    const port = await installGodotWebPort(cancelled);

    await expect(port.invoke(
      { type: "perform_action", request: { action: "click", nodeId: "start" } },
      { timeoutMs: 0 },
    )).rejects.toMatchObject({ code: "timeout" });
    expect(cancelled).toEqual(["17"]);
  });

  test("aborts every correlated game-input call when one call releases the page owner", async () => {
    const released: Array<string | undefined> = [];
    let requestId = 20;
    const port = await installGodotWebPort([], {
      enqueueGameInput: () => JSON.stringify({ requestId: ++requestId }),
      releaseGameInput: (recreate) => { released.push(recreate); return 1; },
    });
    const controller = new AbortController();
    const first = port.invoke(
      { type: "perform_game_input", request: { type: "key_down", code: "KeyA" } },
      { signal: controller.signal },
    ).catch((error) => error);
    const second = port.invoke(
      { type: "perform_game_input", request: { type: "key_down", code: "KeyB" } },
    ).catch((error) => error);

    controller.abort();

    await expect(first).resolves.toMatchObject({ code: "aborted" });
    await expect(second).resolves.toMatchObject({ code: "aborted" });
    expect(released).toEqual(["1"]);
  });

  test("rejects every pending game-input call when the port is uninstalled", async () => {
    const released: Array<string | undefined> = [];
    let requestId = 30;
    const port = await installGodotWebPort([], {
      enqueueGameInput: () => JSON.stringify({ requestId: ++requestId }),
      releaseGameInput: (recreate) => { released.push(recreate); return 1; },
    });
    const first = port.invoke({ type: "perform_game_input", request: { type: "key_down", code: "KeyA" } }).catch((error) => error);
    const second = port.invoke({ type: "perform_game_input", request: { type: "key_down", code: "KeyB" } }).catch((error) => error);

    port.__guaUninstall();

    await expect(first).resolves.toMatchObject({ code: "engine_unsupported" });
    await expect(second).resolves.toMatchObject({ code: "engine_unsupported" });
    expect(released).toEqual([undefined]);
  });

  test("rejects and cancels pending actions when the port is uninstalled", async () => {
    const cancelled: string[] = [];
    const port = await installGodotWebPort(cancelled);
    const pending = port
      .invoke({ type: "perform_action", request: { action: "click", nodeId: "start" } })
      .catch((error) => error);

    port.__guaUninstall();

    await expect(pending).resolves.toMatchObject({ code: "engine_unsupported" });
    expect(cancelled).toEqual(["17"]);
  });

  test("drains an in-flight completion after uninstall rejects the call", async () => {
    const cancelled: string[] = [];
    let polls = 0;
    const port = await installGodotWebPort(cancelled, {
      cancellationResult: -1,
      pollAction: () => JSON.stringify(++polls >= 2
        ? { requestId: 17, action: "click", succeeded: true }
        : null),
    });
    const pending = port
      .invoke({ type: "perform_action", request: { action: "click", nodeId: "start" } })
      .catch((error) => error);

    port.__guaUninstall();

    await expect(pending).resolves.toMatchObject({ code: "engine_unsupported" });
    await waitFor(() => polls >= 2);
    expect(cancelled).toEqual(["17"]);
  });

  test("stops uninstall drain polling when the Godot producer disappears", async () => {
    const cancelled: string[] = [];
    let polls = 0;
    const port = await installGodotWebPort(cancelled, {
      cancellationResult: -1,
      pollAction: () => JSON.stringify(++polls >= 2
        ? { code: "engine_unsupported", message: "The Godot Gua adapter is no longer available." }
        : null),
    });
    const pending = port
      .invoke({ type: "perform_action", request: { action: "click", nodeId: "start" } })
      .catch((error) => error);

    port.__guaUninstall();

    await expect(pending).resolves.toMatchObject({ code: "engine_unsupported" });
    await waitFor(() => polls >= 2);
    await new Promise((resolve) => setTimeout(resolve, 5));
    expect(polls).toBe(2);
    expect(cancelled).toEqual(["17"]);
  });

  test("drains an already-emitted completion when uninstall cancellation reports not found", async () => {
    const cancelled: string[] = [];
    let polls = 0;
    const port = await installGodotWebPort(cancelled, {
      cancellationResult: 0,
      pollAction: () => JSON.stringify(++polls >= 2
        ? { requestId: 17, action: "click", succeeded: true }
        : null),
    });
    const pending = port
      .invoke({ type: "perform_action", request: { action: "click", nodeId: "start" } })
      .catch((error) => error);

    port.__guaUninstall();

    await expect(pending).resolves.toMatchObject({ code: "engine_unsupported" });
    expect(cancelled).toEqual(["17"]);
    expect(polls).toBe(2);
  });

  test("drains an in-flight completion after reporting the abort", async () => {
    const cancelled: string[] = [];
    let polls = 0;
    const port = await installGodotWebPort(cancelled, {
      cancellationResult: -1,
      pollAction: () => JSON.stringify(++polls >= 2
        ? { requestId: 17, action: "click", succeeded: true }
        : null),
    });
    const controller = new AbortController();
    const pending = port
      .invoke({ type: "perform_action", request: { action: "click", nodeId: "start" } }, { signal: controller.signal })
      .catch((error) => error);

    controller.abort();

    await expect(pending).resolves.toMatchObject({ code: "aborted" });
    await waitFor(() => polls >= 2);
    expect(cancelled).toEqual(["17"]);
    expect(polls).toBe(2);
  });

  test("drains an already-emitted completion when cancellation reports not found", async () => {
    const cancelled: string[] = [];
    let polls = 0;
    const port = await installGodotWebPort(cancelled, {
      cancellationResult: 0,
      pollAction: () => JSON.stringify(++polls >= 2
        ? { requestId: 17, action: "click", succeeded: true }
        : null),
    });
    const controller = new AbortController();
    const pending = port
      .invoke({ type: "perform_action", request: { action: "click", nodeId: "start" } }, { signal: controller.signal })
      .catch((error) => error);

    controller.abort();

    await expect(pending).resolves.toMatchObject({ code: "aborted" });
    expect(cancelled).toEqual(["17"]);
    expect(polls).toBe(2);
  });
});

async function waitFor(predicate: () => boolean): Promise<void> {
  const deadline = performance.now() + 100;
  while (!predicate()) {
    if (performance.now() >= deadline) throw new Error("Timed out waiting for the Godot port to drain its result.");
    await new Promise((resolve) => setTimeout(resolve, 1));
  }
}
