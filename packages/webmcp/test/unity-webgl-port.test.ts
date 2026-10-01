import { afterEach, describe, expect, test } from "bun:test";

type UnityWebLibrary = {
  GuaUnityWebInstall(hostName: string, ownerId: string, timeoutMs: number): void;
  GuaUnityWebUninstall(ownerId: string): void;
  GuaUnityWebResolve(ownerId: string, callId: number, payload: string, failed: number): void;
};

type UnityWebPort = { invoke(command: unknown, options?: { signal?: AbortSignal; timeoutMs?: number }): Promise<unknown> };

const unityGlobals = globalThis as typeof globalThis & {
  __guaUnityWebPort?: UnityWebPort;
  __guaUnityWebState?: unknown;
  __guaUnityWebResolveInternal?: unknown;
  __guaUnityWebNextCallId?: number;
};

afterEach(() => {
  delete unityGlobals.__guaUnityWebPort;
  delete unityGlobals.__guaUnityWebState;
  delete unityGlobals.__guaUnityWebResolveInternal;
  delete unityGlobals.__guaUnityWebNextCallId;
});

async function loadUnityWebLibrary() {
  const library: Record<string, unknown> = {};
  const messages: Array<{ hostName: string; methodName: string; payload: string }> = [];
  const source = await Bun.file(new URL(
    "../../../bindings/unity/Runtime/Plugins/WebGL/GuaWebMcp.jslib",
    import.meta.url,
  )).text();
  const evaluate = new Function("LibraryManager", "mergeInto", "UTF8ToString", "SendMessage", source);
  evaluate(
    { library },
    (target: Record<string, unknown>, entries: Record<string, unknown>) => Object.assign(target, entries),
    (value: string) => value,
    (hostName: string, methodName: string, payload: string) => messages.push({ hostName, methodName, payload }),
  );
  return { library: library as unknown as UnityWebLibrary, messages };
}

describe("Unity WebGL same-page port", () => {
  test("v2 discovery rejects extra fields before sending to Unity while preserving v1 calls", async () => {
    const { library, messages } = await loadUnityWebLibrary();
    library.GuaUnityWebInstall("Host", "owner-1", 100);
    const port = unityGlobals.__guaUnityWebPort!;
    for (const type of ["get_game_input_actions_v2", "find_game_input_actions_v2"]) {
      for (const extra of [{ confirmed: true }, { unknown: true }, { requestId: 1 }])
        await expect(port.invoke({ type, ...extra })).rejects.toMatchObject({ code: "invalid_request" });
    }
    expect(messages).toHaveLength(0);
    for (const command of [{ type: "get_game_input_actions_v2" },
      { type: "find_game_input_actions_v2", id: "jump", query: "hop", valueType: "button", active: true,
        context: "play", category: "movement", tags: ["gameplay"], limit: 100 },
      { type: "get_game_input_actions", confirmed: true }]) {
      const pending = port.invoke(command);
      const envelope = JSON.parse(messages.at(-1)!.payload) as { callId: number; command: unknown };
      expect(envelope.command).toEqual(command);
      library.GuaUnityWebResolve("owner-1", envelope.callId, "{}", 0);
      await expect(pending).resolves.toEqual({});
    }
  });
  test("routes calls to the installed runtime object and resolves by owner", async () => {
    const { library, messages } = await loadUnityWebLibrary();
    library.GuaUnityWebInstall("Custom Gua Host", "owner-1", 100);
    const result = unityGlobals.__guaUnityWebPort!.invoke({ type: "get_ui_tree" });

    expect(messages).toHaveLength(1);
    expect(messages[0]!.hostName).toBe("Custom Gua Host");
    expect(messages[0]!.methodName).toBe("HandleWebRequest");
    const envelope = JSON.parse(messages[0]!.payload) as { callId: number };
    library.GuaUnityWebResolve("wrong-owner", envelope.callId, JSON.stringify({ ignored: true }), 0);
    library.GuaUnityWebResolve("owner-1", envelope.callId, JSON.stringify({ screen: "title" }), 0);

    await expect(result).resolves.toEqual({ screen: "title" });
  });

  test("rejects incomplete or conflicting world state selectors before reaching Unity", async () => {
    const { library, messages } = await loadUnityWebLibrary();
    library.GuaUnityWebInstall("World Host", "owner-1", 100);
    for (const command of [
      { type: "query_world_objects", stateKey: "locked", stateType: 3 },
      { type: "query_world_objects", stateKey: "locked", stateType: 2, stateNumber: 0, stateBool: false },
      { type: "query_world_objects", stateKey: "locked", stateType: 3, stateBool: "false" },
      { type: "query_world_objects", stateType: 0 },
    ]) {
      await expect(unityGlobals.__guaUnityWebPort!.invoke(command)).rejects.toMatchObject({ code: "invalid_request" });
    }
    expect(messages).toEqual([]);
  });

  test("rejects incomplete spatial selectors before reaching Unity", async () => {
    const { library, messages } = await loadUnityWebLibrary();
    library.GuaUnityWebInstall("World Host", "owner-1", 100);
    for (const command of [
      { type: "query_world_objects", relativeToObjectId: "player" },
      { type: "query_world_objects", maxDistance: 5 },
      { type: "query_world_objects", relativeToObjectId: "player", maxDistance: -1 },
      { type: "query_world_objects", relativeToObjectId: "player", maxDistance: 5, limit: 0 },
    ]) await expect(unityGlobals.__guaUnityWebPort!.invoke(command)).rejects.toMatchObject({ code: "invalid_request" });
    expect(messages).toEqual([]);
  });

  test("preserves false world state values and sends raw field presence to Unity", async () => {
    const { library, messages } = await loadUnityWebLibrary();
    library.GuaUnityWebInstall("World Host", "owner-1", 100);
    const pending = unityGlobals.__guaUnityWebPort!.invoke({
      type: "query_world_objects", stateKey: "locked", stateType: 3, stateBool: false,
    });
    const envelope = JSON.parse(messages[0]!.payload) as { callId: number; commandFields: string[] };
    expect(envelope.commandFields).toEqual(["type", "stateKey", "stateType", "stateBool"]);
    library.GuaUnityWebResolve("owner-1", envelope.callId, JSON.stringify({ valid: true, matches: [] }), 0);
    await expect(pending).resolves.toEqual({ valid: true, matches: [] });
  });

  test("rejects pending calls when the runtime is replaced or destroyed", async () => {
    const { library } = await loadUnityWebLibrary();
    library.GuaUnityWebInstall("First Host", "owner-1", 100);
    const replaced = unityGlobals.__guaUnityWebPort!.invoke({ type: "get_ui_tree" }).catch((error) => error);

    library.GuaUnityWebInstall("Second Host", "owner-2", 100);
    await expect(replaced).resolves.toMatchObject({ code: "engine_unsupported" });

    const destroyed = unityGlobals.__guaUnityWebPort!.invoke({ type: "get_ui_tree" }).catch((error) => error);
    library.GuaUnityWebUninstall("owner-1");
    expect(unityGlobals.__guaUnityWebPort).toBeDefined();
    library.GuaUnityWebUninstall("owner-2");
    await expect(destroyed).resolves.toMatchObject({ code: "engine_unsupported" });
    expect(unityGlobals.__guaUnityWebPort).toBeUndefined();
  });

  test("times out and removes calls that Unity never resolves", async () => {
    const { library, messages } = await loadUnityWebLibrary();
    library.GuaUnityWebInstall("Missing Host", "owner-1", 0);
    await expect(unityGlobals.__guaUnityWebPort!.invoke({ type: "get_ui_tree" }))
      .rejects.toMatchObject({ code: "timeout" });
    expect(messages.at(-1)).toMatchObject({ hostName: "Missing Host", methodName: "HandleWebCancellation" });
  });

  test("uses the per-call timeout instead of the installed default", async () => {
    const { library, messages } = await loadUnityWebLibrary();
    library.GuaUnityWebInstall("Slow Host", "owner-1", 0);
    const pending = unityGlobals.__guaUnityWebPort!.invoke({ type: "get_ui_tree" }, { timeoutMs: 100 });
    await new Promise((resolve) => setTimeout(resolve, 5));
    const envelope = JSON.parse(messages.find((message) => message.methodName === "HandleWebRequest")!.payload) as { callId: number };
    library.GuaUnityWebResolve("owner-1", envelope.callId, JSON.stringify({ screen: "title", nodes: [] }), 0);

    await expect(pending).resolves.toEqual({ screen: "title", nodes: [] });
  });

  test("cancels the Unity host call when the bridge signal aborts", async () => {
    const { library, messages } = await loadUnityWebLibrary();
    library.GuaUnityWebInstall("Abort Host", "owner-1", 1000);
    const controller = new AbortController();
    const pending = unityGlobals.__guaUnityWebPort!
      .invoke({ type: "perform_action", request: { action: "click", nodeId: "start" } }, { signal: controller.signal })
      .catch((error) => error);

    controller.abort();

    await expect(pending).resolves.toMatchObject({ code: "aborted" });
    expect(messages.map((message) => message.methodName)).toEqual(["HandleWebRequest", "HandleWebCancellation"]);
  });
});
