import { expect, test } from "bun:test";
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { GuaBridgeClient } from "../src/index";
import type { GuaSpatialBatch } from "gua-world-tools";
import { assertSpatialRouteResult, spatialMcpResult } from "../../../scripts/spatial-route-assertions";
import fixtureData from "../../../protocol/fixtures/spatial-host-r1.json";

const batch = fixtureData.valid.find(v => v.id === "batch")!.json as GuaSpatialBatch;
const partial = fixtureData.valid.find(v => v.id === "batchResult")!.json;
type Fault = "none" | "stall_accept" | "stall_poll" | "null_poll" | "disconnect_accept" | "disconnect_poll" | "wrong_wire_id";
function peer(result: unknown = partial, fault: Fault = "none") {
  const commands: any[] = []; let connections = 0, owners = 0;
  let reached!: () => void, closed!: () => void;
  const faultReached = new Promise<void>(resolve => { reached = resolve; });
  const ownerClosed = new Promise<void>(resolve => { closed = resolve; });
  const server = Bun.serve({ hostname: "127.0.0.1", port: 0,
    fetch(request, server) { if (server.upgrade(request)) return; return new Response("upgrade", { status: 426 }); },
    websocket: {
      open() { connections++; owners++; },
      close() { owners--; closed(); },
      message(socket, data) {
        const command = JSON.parse(String(data)); commands.push(command);
        const acceptance = command.type === "query_spatial_batch";
        const poll = command.type === "poll_spatial_batch";
        if ((acceptance && fault.endsWith("accept")) || (poll && fault !== "none" && !fault.endsWith("accept"))) {
          reached();
          if (fault.startsWith("stall")) return;
          if (fault.startsWith("disconnect")) { socket.terminate(); return; }
          if (fault === "wrong_wire_id") { socket.send(JSON.stringify({ id: command.id + 100, ok: true, result })); return; }
        }
        socket.send(JSON.stringify({ id: command.id, ok: true, result: poll ? fault === "null_poll" ? null : result : null }));
      },
    },
  });
  return { server, url: `ws://127.0.0.1:${server.port}`, commands, faultReached, ownerClosed,
    connections: () => connections, owners: () => owners };
}

async function mcpCall(f: ReturnType<typeof peer>, cancel = false) {
  const child = spawn(process.execPath, [`${import.meta.dir}/../src/cli.ts`, "mcp"], {
    env: { ...process.env, GUA_BRIDGE_URL: f.url }, stdio: ["pipe", "pipe", "pipe"],
  });
  const lines = createInterface({ input: child.stdout }); child.stderr.resume();
  let timer: ReturnType<typeof setTimeout> | undefined;
  const responses: any[] = [];
  try {
    const response = new Promise<any>((resolve, reject) => {
      timer = setTimeout(() => reject(new Error("MCP spatial test watchdog")), 8000);
      lines.on("line", line => { const rpc = JSON.parse(line); responses.push(rpc); if (rpc.id === 20) resolve(rpc); });
      child.once("error", reject);
    });
    child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id: 20, method: "tools/call",
      params: { name: "query_spatial_batch", arguments: { batch } } }) + "\n");
    if (cancel) {
      await f.faultReached;
      child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method: "notifications/cancelled", params: { requestId: 20 } }) + "\n");
    }
    const rpc = await response;
    clearTimeout(timer);
    expect(responses.filter(r => r.id === 20)).toHaveLength(1);
    // Observe production cleanup while MCP is still alive. Killing the child
    // first would make a missing client close indistinguishable from success.
    if (rpc.result?.isError) {
      await Promise.race([f.ownerClosed, new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error("MCP did not close its failed spatial owner")), 1000);
      })]);
    }
    return rpc;
  } finally { clearTimeout(timer); lines.close(); child.kill(); }
}

function assertOneSubmission(f: ReturnType<typeof peer>) {
  const submissions = f.commands.filter(c => c.type === "query_spatial_batch");
  expect(submissions).toHaveLength(1);
  expect(submissions[0].batch).toEqual(batch);
  expect(new Set(f.commands.map(c => c.id)).size).toBe(f.commands.length);
  expect(f.connections()).toBe(1);
}

test("TypeScript and MCP retain ordered partial results, reasons and absent geometry", async () => {
  for (const viaMcp of [false, true]) {
    const f = peer(); const client = new GuaBridgeClient(f.url);
    try {
      if (viaMcp) {
        const rpc = await mcpCall(f);
        expect(rpc.result.isError).not.toBe(true);
        expect(JSON.parse(rpc.result.content[0].text)).toEqual(partial);
      } else expect(await client.spatialBatch(batch)).toEqual(partial);
      assertOneSubmission(f);
    } finally { client.close(); f.server.stop(true); }
  }
});

const violations: [string, (r: any) => void, string][] = [
  ["empty", r => { r.items = []; }, "Spatial correlation mismatch"],
  ["missing", r => { r.items.pop(); }, "Spatial correlation mismatch"],
  ["duplicate", r => { r.items[1] = r.items[0]; }, "Spatial correlation mismatch"],
  ["batch ID", r => { r.batchId++; }, "Spatial correlation mismatch"],
  ["request ID", r => { r.items[0].requestId++; }, "Spatial correlation mismatch"],
  ["query ID", r => { r.items[0].queryId = "wrong"; }, "Spatial correlation mismatch"],
  ["inner ID", r => { r.items[0].result.requestId++; }, "Invalid spatial terminal result"],
  ["nonterminal", r => { r.items[1].state = "queued"; }, "Invalid spatial terminal result"],
  ["missing failure reason", r => { delete r.items[1].reason; }, "Invalid spatial terminal result"],
  ["unsafe notExecuted reason", r => { r.items[1].reason = "backend error: private-wall"; }, "Invalid spatial terminal result"],
  ["unsafe failed reason", r => { r.items[1].state = "failed"; r.items[1].reason = "backend error: private-wall"; }, "Invalid spatial terminal result"],
  ["failed geometry", r => { r.items[1].result = r.items[0].result; }, "Invalid spatial terminal result"],
];
for (const [name, mutate, error] of violations) test(`invalid ${name} is rejected on TypeScript and MCP paths`, async () => {
  const broken = structuredClone(partial); mutate(broken);
  for (const viaMcp of [false, true]) {
    const f = peer(broken); const client = new GuaBridgeClient(f.url);
    try {
      if (viaMcp) {
        const rpc = await mcpCall(f);
        expect(rpc.result.isError).toBe(true);
        expect(JSON.parse(rpc.result.content[0].text)).toEqual({ error: error + "." });
      }
      else await expect(client.spatialBatch(batch)).rejects.toThrow(error);
      await f.ownerClosed; expect(f.owners()).toBe(0); assertOneSubmission(f);
    } finally { client.close(); f.server.stop(true); }
  }
});

for (const fault of ["stall_accept", "stall_poll", "null_poll", "disconnect_accept", "disconnect_poll", "wrong_wire_id"] as const) {
  test(`TypeScript ${fault} expires/closes its owner without reconnect or resend`, async () => {
    const f = peer(partial, fault); const client = new GuaBridgeClient(f.url, 150);
    try {
      const started = performance.now();
      await expect(client.spatialBatch(batch)).rejects.toThrow(fault.startsWith("disconnect") ? "connection closed" : fault === "null_poll" ? "completion timed out" : "Timed out waiting for Gua bridge command");
      await f.faultReached; await f.ownerClosed;
      expect(performance.now() - started).toBeLessThan(1500);
      expect(f.owners()).toBe(0); assertOneSubmission(f);
    } finally { client.close(); f.server.stop(true); }
  });
  test(`MCP ${fault} expires/closes its owner without reconnect or resend`, async () => {
    const f = peer(partial, fault);
    try {
      const rpc = await mcpCall(f); await f.faultReached; await f.ownerClosed;
      expect(rpc.result.isError).toBe(true);
      expect(JSON.parse(rpc.result.content[0].text).error).toContain(fault.startsWith("disconnect") ? "connection closed" : fault === "null_poll" ? "completion timed out" : "Timed out waiting for Gua bridge command");
      expect(f.owners()).toBe(0); assertOneSubmission(f);
    } finally { f.server.stop(true); }
  }, 10000);
}

for (const fault of ["stall_accept", "stall_poll"] as const) test(`abort and MCP cancellation at ${fault} close the original owner`, async () => {
  for (const viaMcp of [false, true]) {
    const f = peer(partial, fault); const client = new GuaBridgeClient(f.url); const abort = new AbortController();
    try {
      if (viaMcp) {
        const rpc = await mcpCall(f, true);
        expect(rpc.result.isError).toBe(true);
        expect(JSON.parse(rpc.result.content[0].text)).toEqual({ error: "MCP request was cancelled." });
      }
      else {
        const result = client.spatialBatch(batch, abort.signal).then(() => null, error => error);
        await f.faultReached; abort.abort();
        expect((await result)?.message).toBe("MCP request was cancelled.");
      }
      await f.ownerClosed; expect(f.owners()).toBe(0); assertOneSubmission(f);
    } finally { client.close(); f.server.stop(true); }
  }
});

// Deliberately corrupt acceptance replies: the exact production verifier must
// reject each fault, even when both routes could return the same broken reply.
test("real-engine route assertions reject incomplete, miscorrelated and noncompleted replies", () => {
  const routeBatch = structuredClone(batch); routeBatch.queries.push({ ...routeBatch.queries[0]!, requestId: 3, queryId: "q3" });
  const good: any = { ...partial, items: routeBatch.queries.map((q, i) => ({ requestId: q.requestId, queryId: q.queryId,
    state: "completed", result: { ...(partial.items[0] as any).result, requestId: q.requestId, queryId: q.queryId, outcome: ["hit", "detected", "blocked"][i] } })) };
  expect(() => assertSpatialRouteResult(routeBatch, good)).not.toThrow();
  for (const [name, mutate] of violations.filter(([name]) => name !== "missing failure reason" && name !== "failed geometry")) {
    const broken = structuredClone(good); mutate(broken);
    expect(() => assertSpatialRouteResult(routeBatch, broken), name).toThrow("Spatial route");
  }
  for (const mutate of [(r: any) => { r.items[0].result.status = "failed"; }, (r: any) => { r.items[0].state = "failed"; r.items[0].reason = "internal"; }]) {
    const broken = structuredClone(good); mutate(broken); expect(() => assertSpatialRouteResult(routeBatch, broken)).toThrow("completion/correlation/outcome");
  }
  const rpc = { id: 3, result: { content: [{ type: "text", text: JSON.stringify(good) }] } };
  expect(spatialMcpResult([rpc], 3)).toEqual(good);
  expect(() => spatialMcpResult([], 3)).toThrow("response count/status");
  expect(() => spatialMcpResult([rpc, rpc], 3)).toThrow("response count/status");
});
