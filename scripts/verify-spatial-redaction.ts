// Holds the actual native poll until the host completes identifiable geometry
// and invalidates its authorization/scene. No reply is fabricated by this proxy.
import { expect } from "bun:test";
import { createInterface } from "node:readline";
import { GuaBridgeClient } from "../packages/mcp/src/index";
import fixture from "../protocol/fixtures/spatial-host-r1.json";
import { assertSpatialRedactionInspection } from "./spatial-redaction-assertions";
const [url, route, transition] = process.argv.slice(2);
const batch = fixture.valid.find(v => v.id === "batch")!.json as any;
let release!: () => void;
const released = new Promise<void>(r => { release = r; });
let finish!: () => void; const finished = new Promise<void>(r=>{finish=r;});
const input = createInterface({ input: process.stdin }); input.on("line", line=>line==="fire" ? release() : finish());
const replies: any[] = [], commands: any[] = [];
let held = false;
const upstreams = new Set<WebSocket>();
const proxy = Bun.serve<{ upstream: WebSocket; pending: string[] }>({ hostname: "127.0.0.1", port: 0,
  fetch(request, server) {
    const upstream = new WebSocket(url!); upstreams.add(upstream);
    if (server.upgrade(request, { data: { upstream, pending: [] } })) return;
    upstream.close(); return new Response("upgrade", { status: 426 });
  }, websocket: {
    open(socket) {
      const upstream = socket.data.upstream;
      upstream.addEventListener("open", () => { for (const command of socket.data.pending) upstream.send(command); socket.data.pending = []; });
      upstream.addEventListener("message", event => {
        const reply = JSON.parse(String(event.data)); replies.push(reply);
        if (reply.id < 9000) socket.send(String(event.data));
      });
      upstream.addEventListener("close", () => socket.close());
    }, close(socket) { socket.data.upstream.close(); },
    async message(socket, wire) {
      const command = JSON.parse(String(wire)); commands.push(command);
      if (command.type === "poll_spatial_batch" && !held) {
        held = true; console.log("poll"); await released;
        for (const [id, type] of [[9001,"get_version"],[9002,"get_diagnostics"],[9003,"get_spatial_info"]] as const)
          socket.data.upstream.send(JSON.stringify({id,type}));
      }
      if (socket.data.upstream.readyState === WebSocket.OPEN) socket.data.upstream.send(String(wire));
      else socket.data.pending.push(String(wire));
    }
  }
});
let clientResult: any;
try {
  if (route === "trace") {
    console.log(`ws://127.0.0.1:${proxy.port}`); await finished;
    const terminal = replies.filter(r=>r.id<9000).at(-1);
    clientResult = terminal.ok ? terminal.result : {error:terminal.error};
  } else if (route === "typescript") {
    const client = new GuaBridgeClient(`ws://127.0.0.1:${proxy.port}`, 5000);
    try { clientResult = await client.spatialBatch(batch).catch(error => ({error:error.message})); }
    finally { client.close(); }
  } else {
    const child = Bun.spawn([process.execPath,"packages/mcp/dist/cli.js","mcp"], {
      env:{...process.env,GUA_BRIDGE_URL:`ws://127.0.0.1:${proxy.port}`},stdin:"pipe",stdout:"pipe",stderr:"pipe"});
    // Bun streams are web streams; consume the one RPC response without closing
    // stdin before the host-driven invalidation has fired.
    child.stdin.write(JSON.stringify({jsonrpc:"2.0",id:10,method:"tools/call",params:{name:"query_spatial_batch",arguments:{batch}}})+"\n");
    const stdout = child.stdout.getReader(); let text = "";
    try {
      while (!text.includes("\n")) { const chunk = await stdout.read(); if(chunk.done) throw Error("MCP exited before reply"); text += new TextDecoder().decode(chunk.value); }
      clientResult = JSON.parse(text.trim());
    } finally { stdout.releaseLock(); child.kill(); await child.exited; }
  }
  expect(held).toBe(true);
  expect(commands.filter(c=>c.type==="query_spatial_batch")).toHaveLength(1);
  assertSpatialRedactionInspection(replies);
  const evidence = {route,transition,replies,clientResult,submissions:1};
  expect(JSON.stringify(evidence)).not.toContain("PRIVATE_SPATIAL_SENTINEL");
  expect(JSON.stringify(evidence)).not.toContain("0.314159265358979");
  if (transition === "scene") {
    const result = route === "mcp" ? JSON.parse(clientResult.result.content[0].text) : clientResult;
    expect(result.batchId).toBe(batch.batchId); expect(result.items).toHaveLength(batch.queries.length);
    result.items.forEach((item:any,index:number)=>{
      expect(item.requestId).toBe(batch.queries[index].requestId); expect(item.queryId).toBe(batch.queries[index].queryId);
      expect(item.state).toBe("failed"); expect(item.reason).toBe("provider_unregistered"); expect(item.result).toBeUndefined();
    });
  } else {
    if(route==="mcp") expect(clientResult.result.isError).toBe(true);
    const result = route === "mcp" ? JSON.parse(clientResult.result.content[0].text) : clientResult;
    expect(result.error).toContain("not_authorized");
  }
  console.log(JSON.stringify(evidence));
} finally {input.close(); for(const upstream of upstreams) upstream.close(); proxy.stop(true);}
