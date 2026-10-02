// Native-host integration driver. A wire proxy supplies an observable fault;
// the NUnit host checks the actual native lease/owner after this process exits.
import { expect } from "bun:test";
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { GuaBridgeClient } from "../packages/mcp/src/index";
import fixture from "../protocol/fixtures/spatial-host-r1.json";
import type { GuaSpatialBatch } from "gua-world-tools";
const [url, route, fault] = process.argv.slice(2);
const batch = fixture.valid.find(v => v.id === "batch")!.json as GuaSpatialBatch;
const commands: any[] = []; let connections = 0;
let pollReady!: () => void, fire!: () => void;
const ready = new Promise<void>(r => { pollReady = r; });
const fired = new Promise<void>(r => { fire = r; });
const input = createInterface({ input: process.stdin }); input.once("line", () => fire());
const upstreams = new Set<WebSocket>();
const proxy = Bun.serve<{ upstream: WebSocket; pending: string[] }>({ hostname: "127.0.0.1", port: 0,
  fetch(request, server) {
    const upstream = new WebSocket(url!); upstreams.add(upstream);
    if (server.upgrade(request, { data: { upstream, pending: [] } })) return;
    upstream.close(); return new Response("upgrade", { status: 426 });
  },
  websocket: {
    open(socket) {
      connections++; const upstream = socket.data.upstream;
      upstream.addEventListener("open", () => { for (const command of socket.data.pending) upstream.send(command); socket.data.pending = []; });
      upstream.addEventListener("message", event => { socket.send(String(event.data)); });
      upstream.addEventListener("close", () => socket.close());
      upstream.addEventListener("error", () => socket.close());
    },
    close(socket) { socket.data.upstream.close(); },
    async message(socket, wire) {
      const command = JSON.parse(String(wire)); commands.push(command);
      if (command.type === "poll_spatial_batch") {
        if (!commands.slice(0,-1).some(c => c.type === "poll_spatial_batch")) { console.log("poll"); pollReady(); }
        await fired;
        if (fault === "disconnect") { socket.terminate(); return; }
        if (fault === "correlation") {
          socket.send(JSON.stringify({ id: command.id, ok: true, result: { schemaVersion: "spatial-host-r1", documentType: "batchResult", batchId: 99, items: [] } })); return;
        }
        // Stall the poll: cancellation or the client's deadline closes upstream.
        return;
      }
      if (socket.data.upstream.readyState === WebSocket.OPEN) socket.data.upstream.send(String(wire));
      else socket.data.pending.push(String(wire));
    },
  },
});
const proxyUrl = `ws://127.0.0.1:${proxy.port}`;
const expected = fault === "disconnect" ? "connection closed" : fault === "correlation" ? "Spatial correlation mismatch" : fault === "cancel" ? "MCP request was cancelled" : "Timed out waiting for Gua bridge command";
try {
  if (route === "typescript") {
    const client = new GuaBridgeClient(proxyUrl, 2000); const abort = new AbortController();
    try {
      const response = client.spatialBatch(batch, abort.signal).then(() => null, error => error);
      await ready; await fired; if (fault === "cancel") abort.abort();
      expect((await response)?.message).toContain(expected);
    } finally { client.close(); }
  } else {
    const child = spawn(process.execPath, ["packages/mcp/dist/cli.js", "mcp"], { env: { ...process.env, GUA_BRIDGE_URL: proxyUrl }, stdio: ["pipe", "pipe", "pipe"] });
    const lines = createInterface({ input: child.stdout }); child.stderr.resume();
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      const response = new Promise<any>((resolve, reject) => {
        timer = setTimeout(() => reject(new Error("native MCP driver watchdog")), 8000);
        lines.on("line", line => { const rpc = JSON.parse(line); if (rpc.id === 10) resolve(rpc); }); child.once("error", reject);
      });
      child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id: 10, method: "tools/call", params: { name: "query_spatial_batch", arguments: { batch } } }) + "\n");
      await ready; await fired;
      if (fault === "cancel") child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method: "notifications/cancelled", params: { requestId: 10 } }) + "\n");
      const rpc = await response;
      expect(rpc.result.isError).toBe(true); expect(JSON.parse(rpc.result.content[0].text).error).toContain(expected);
    } finally { clearTimeout(timer); lines.close(); child.kill(); }
  }
  expect(commands.filter(c => c.type === "query_spatial_batch")).toHaveLength(1);
  expect(commands.find(c => c.type === "query_spatial_batch").batch).toEqual(batch);
  expect(connections).toBe(1);
  expect(new Set(commands.map(c => c.id)).size).toBe(commands.length);
  console.log(JSON.stringify({ route, fault, submissions: 1, connections, batchId: batch.batchId, error: expected }));
} finally { input.close(); for (const upstream of upstreams) upstream.close(); proxy.stop(true); }
