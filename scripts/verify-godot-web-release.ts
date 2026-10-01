import { createCdpClient, waitForDefaultExecutionContext } from "./godot-web-cdp";
import { mkdtemp, rm } from "node:fs/promises";
import { resolve, sep } from "node:path";
import { tmpdir } from "node:os";

declare global {
  var __guaGodotWebPort: undefined | {
    invoke(command: unknown, options?: { timeoutMs?: number }): Promise<any>;
  };
}

const exportRoot = resolve(process.argv[2] ?? "artifacts/godot-web-release");
const chromeExecutable = Bun.env.CHROME_EXECUTABLE ?? ["google-chrome", "google-chrome-stable", "chromium", "chromium-browser"]
  .map((name) => Bun.which(name))
  .find((value): value is string => value !== null);

if (!chromeExecutable) throw new Error("Chrome or Chromium is required for the Godot Web Release smoke test.");
if (!(await Bun.file(resolve(exportRoot, "index.html")).exists())) {
  throw new Error(`Godot Web Release export is missing: ${resolve(exportRoot, "index.html")}`);
}

const server = Bun.serve({
  port: 0,
  async fetch(request) {
    const url = new URL(request.url);
    const relativePath = decodeURIComponent(url.pathname === "/" ? "/index.html" : url.pathname).replace(/^\/+/, "");
    const path = resolve(exportRoot, relativePath);
    if (path !== exportRoot && !path.startsWith(`${exportRoot}${sep}`)) return new Response("Forbidden", { status: 403 });
    const file = Bun.file(path);
    if (!(await file.exists())) return new Response("Not found", { status: 404 });
    return new Response(file, { headers: {
      "Cross-Origin-Opener-Policy": "same-origin",
      "Cross-Origin-Embedder-Policy": "require-corp",
    } });
  },
});

const profileDirectory = await mkdtemp(resolve(tmpdir(), "gua-godot-web-"));
const debuggingPort = 9222;
const pageUrl = `http://127.0.0.1:${server.port}/index.html`;
const chrome = Bun.spawn([
  chromeExecutable,
  "--headless=new",
  "--no-sandbox",
  "--disable-dev-shm-usage",
  "--enable-webgl",
  "--ignore-gpu-blocklist",
  "--use-angle=swiftshader",
  "--enable-unsafe-swiftshader",
  "--remote-allow-origins=*",
  `--remote-debugging-port=${debuggingPort}`,
  `--user-data-dir=${profileDirectory}`,
  pageUrl,
], { stdout: "ignore", stderr: "inherit" });

try {
  const target = await waitForPageTarget(debuggingPort, pageUrl, 30_000);
  const client = createCdpClient(target.webSocketDebuggerUrl);
  await client.open();
  try {
    console.log(`Chrome DevTools browser: ${JSON.stringify(await client.send("Browser.getVersion"))}`);
    console.log("Godot Web smoke phase: enable Runtime");
    await client.send("Runtime.enable", {}, 5_000);
    console.log("Godot Web smoke phase: document readiness (10s total, 1s probes)");
    await waitForDefaultExecutionContext(client, 10_000, pageUrl);
    console.log("Godot Web smoke phase: bridge assertions (30s)");
    const response = await client.send("Runtime.evaluate", {
      expression: `(${runSmoke.toString()})()`,
      awaitPromise: true,
      returnByValue: true,
    }, 30_000) as { exceptionDetails?: unknown; result?: { value?: unknown } };
    if (response.exceptionDetails) throw new Error(`Browser evaluation failed: ${JSON.stringify(response.exceptionDetails)}`);
    const result = response.result?.value as {
      initialScreen?: string;
      requestId?: number;
      gameInputRequestId?: number;
      finalScreen?: string;
    } | undefined;
    if (result?.initialScreen !== "title" || !Number.isSafeInteger(result.requestId) || result.requestId! <= 0 ||
        !Number.isSafeInteger(result.gameInputRequestId) || result.gameInputRequestId! <= 0 || result.finalScreen !== "loading") {
      throw new Error(`Godot Web Release smoke returned an invalid result: ${JSON.stringify(result)}`);
    }
    console.log(`Godot Web Release smoke passed (requestId=${result.requestId}).`);
  } finally {
    client.close();
  }
} finally {
  chrome.kill();
  await chrome.exited.catch(() => undefined);
  server.stop(true);
  await rm(profileDirectory, { recursive: true, force: true });
}

async function runSmoke() {
  const deadline = performance.now() + 20_000;
  while (!globalThis.__guaGodotWebPort) {
    if (performance.now() >= deadline) throw new Error("Godot did not install __guaGodotWebPort.");
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  const port = globalThis.__guaGodotWebPort;
  let initial;
  while (performance.now() < deadline) {
    initial = await port.invoke({ type: "get_ui_tree" });
    if (initial?.screen === "title") break;
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  if (initial?.screen !== "title" || !initial.nodes?.some((node: any) => node.id === "start" && node.actions?.includes("click"))) {
    throw new Error(`Godot Player UI tree did not expose the allowed start action: ${JSON.stringify(initial)}`);
  }
  const capabilities = await port.invoke({ type: "get_game_input_capabilities" });
  const actionMap = await port.invoke({ type: "get_game_input_actions" });
  if (!capabilities?.includes("semantic_game_input_v1") || !actionMap?.actions?.some((action: any) => action.id === "jump")) {
    throw new Error(`Godot Release did not expose its Player-authorized game action: ${JSON.stringify({ capabilities, actionMap })}`);
  }
  const search = await port.invoke({ type: "find_game_input_actions", id: "jump", limit: 1 });
  if (search?.count !== 1 || search.actions?.[0]?.id !== "jump") {
    throw new Error(`Godot JSON game-input search failed: ${JSON.stringify(search)}`);
  }
  for (const limit of [0, 1.5, 101, "1", null]) {
    let code;
    try { await port.invoke({ type: "find_game_input_actions", limit }); }
    catch (error: any) { code = error.code; }
    if (code !== "invalid_request") throw new Error(`Godot accepted invalid selector limit: ${limit}`);
  }
  const gameInputCompletion = await port.invoke(
    { type: "perform_game_input", request: { type: "press_game_input_action", actionId: "jump" } },
    { timeoutMs: 5_000 },
  );
  if (!gameInputCompletion?.completed || !gameInputCompletion?.succeeded ||
      !Number.isSafeInteger(gameInputCompletion.requestId) || gameInputCompletion.requestId <= 0) {
    throw new Error(`Godot did not return a correlated game-input completion: ${JSON.stringify(gameInputCompletion)}`);
  }
  const completion = await port.invoke(
    { type: "perform_action", request: { action: "click", nodeId: "start" } },
    { timeoutMs: 5_000 },
  );
  if (!completion?.succeeded || !Number.isSafeInteger(completion.requestId) || completion.requestId <= 0) {
    throw new Error(`Godot did not return a correlated click completion: ${JSON.stringify(completion)}`);
  }
  let finalTree;
  while (performance.now() < deadline) {
    finalTree = await port.invoke({ type: "get_ui_tree" });
    if (finalTree?.screen === "loading") break;
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  if (finalTree?.screen !== "loading") throw new Error(`Godot click did not publish the loading screen: ${JSON.stringify(finalTree)}`);
  return {
    initialScreen: initial.screen,
    requestId: completion.requestId,
    gameInputRequestId: gameInputCompletion.requestId,
    finalScreen: finalTree.screen,
  };
}

type PageTarget = { url: string; type: string; webSocketDebuggerUrl: string };

async function waitForPageTarget(port: number, expectedUrl: string, timeoutMs: number): Promise<PageTarget> {
  const deadline = performance.now() + timeoutMs;
  while (performance.now() < deadline) {
    try {
      const remainingMs = Math.max(1, Math.ceil(deadline - performance.now()));
      const targets = await fetch(`http://127.0.0.1:${port}/json/list`, {
        signal: AbortSignal.timeout(Math.min(remainingMs, 1_000)),
      }).then((response) => response.json()) as PageTarget[];
      const target = targets.find((candidate) => candidate.type === "page" && candidate.url.startsWith(expectedUrl));
      if (target?.webSocketDebuggerUrl) return target;
    } catch {
      // Chrome has not opened its DevTools endpoint yet.
    }
    await Bun.sleep(100);
  }
  throw new Error("Timed out waiting for the headless Chrome Godot page.");
}
