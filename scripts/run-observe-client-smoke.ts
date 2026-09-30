// Real Inspector and MCP clients against an actual engine fixture, no fake transport.
// Start Godot with scripts/gua_observe_fixture.gd and set GUA_OBSERVE_BRIDGE_URL.
import { WebSocketInspectorClient } from "../packages/inspector/src/core";
import { fileURLToPath } from "node:url";
import { parseObserveTransport } from "../packages/value/src/index";
import { strict as assert } from "node:assert";

// Inspector action polling uses browser timers; provide the same timers in Bun.
(globalThis as unknown as { window: unknown }).window = globalThis;
const url = process.env.GUA_OBSERVE_BRIDGE_URL ?? "ws://127.0.0.1:8765";
const inspector = new WebSocketInspectorClient(url);
const server = Bun.spawn([process.execPath, fileURLToPath(new URL("../packages/mcp/src/cli.ts", import.meta.url)), "mcp"], {
  env: {...process.env, GUA_BRIDGE_URL: url}, stdin: "pipe", stdout: "pipe", stderr: "inherit",
});
let nextId = 0;
const responses = new Map<number, (value: any) => void>();
void (async () => {
  let buffer = ""; const decoder = new TextDecoder();
  for await (const chunk of server.stdout) {
    buffer += decoder.decode(chunk, {stream:true});
    let newline: number;
    while ((newline = buffer.indexOf("\n")) >= 0) {
      const response = JSON.parse(buffer.slice(0, newline)); buffer = buffer.slice(newline + 1);
      responses.get(response.id)?.(response); responses.delete(response.id);
    }
  }
})();
const mcp = {observeCommand: async (name: string, subscriptionId?: number, extra: Record<string,unknown> = {}, expectError = false): Promise<unknown> => {
  const id = ++nextId;
  const response = new Promise<any>((resolve, reject) => {
    const timer = setTimeout(() => {responses.delete(id); reject(new Error("MCP response timed out."));}, 5000);
    responses.set(id, result => {clearTimeout(timer); resolve(result);});
  });
  server.stdin.write(JSON.stringify({jsonrpc:"2.0", id, method:"tools/call", params:{name, arguments:{...extra, ...(subscriptionId === undefined ? {} : {subscriptionId})}}}) + "\n");
  const result = await response;
  assert.equal(result.error, undefined);
  if (expectError) assert.equal(result.result.isError, true); else assert.notEqual(result.result.isError, true);
  return JSON.parse(result.result.content[0].text);
}};
try {
  for (const name of ["get_observe_snapshot", "subscribe_observations"]) {
    const rejection = await mcp.observeCommand(name, undefined, {profile:"debug", SECRET_MARKER:1}, true);
    assert.deepEqual(rejection, {error:"Invalid Observe arguments."});
  }
  const initial = await inspector.subscribeObservations();
  const other = await mcp.observeCommand("subscribe_observations") as { subscriptionId: number; snapshot: unknown };
  for (const name of ["poll_observations", "unsubscribe_observations"]) {
    const rejection = await mcp.observeCommand(name, other.subscriptionId, {profile:"debug"}, true);
    assert.deepEqual(rejection, {error:"Invalid Observe arguments."});
  }
  const snapshot = parseObserveTransport(other.snapshot);
  assert.equal(snapshot.document.kind, "snapshot");
  if (snapshot.document.kind !== "snapshot") throw new Error("Expected snapshot");
  assert.equal(snapshot.document.entries[0]!.runtimeId, "enemy-1");
  assert.deepEqual(snapshot.catalogs[0]!.value!.enums[0]!.members, ["First", "Second"]);
  const tree = await inspector.getUiTree();
  const advance = tree.nodes.find(n => n.label === "Advance")!;
  await inspector.clickNode(advance.id);
  const deadline = performance.now() + 3000;
  let changed = false;
  while (performance.now() < deadline) {
    const changes = await inspector.pollObservations(initial.subscriptionId);
    if (changes.document.kind === "changes" && changes.document.events.some(e => e.after?.type === "enum" && e.after.value === "Second")) { changed = true; break; }
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  assert.equal(changed, true);
  const mcpChanges = parseObserveTransport(await mcp.observeCommand("poll_observations", other.subscriptionId));
  assert.equal(mcpChanges.document.kind, "changes");
  assert.ok(JSON.stringify(mcpChanges).includes("Second"));
  await inspector.unsubscribeObservations(initial.subscriptionId);
  await mcp.observeCommand("unsubscribe_observations", other.subscriptionId);
  console.log("Real Godot -> Inspector/MCP Observe client smoke passed.");
  server.kill(); process.exit(0);
} catch (error) { console.error(error); server.kill(); process.exit(1); }
