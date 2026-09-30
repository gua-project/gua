import { expect, test } from "bun:test";
import { WebSocketInspectorClient } from "../../inspector/src/core";
import { GuaBridgeClient } from "../../mcp/src/index";
const envelope = {document:{schemaVersion:1,sourceId:"wire",sessionEpoch:1,profile:"debug",sequence:0,revision:0,uiFrame:0,uiRevision:0,worldFrame:0,worldRevision:0,kind:"snapshot",entries:[{ownerId:1,registrationId:1,source:"object",runtimeId:"enemy",name:"phase",status:"available",value:{type:"integer",value:1}}]},catalogs:[{}]};
test("Inspector ignores delayed close/error events from a timed-out Observe socket", async () => {
  const original=globalThis.WebSocket, sockets:ControlledSocket[]=[];
  class ControlledSocket extends EventTarget {
    static OPEN=1; static CONNECTING=0; static CLOSING=2; static CLOSED=3;
    readyState=0;
    constructor(_url:string) {super();sockets.push(this);queueMicrotask(()=>{this.readyState=1;this.dispatchEvent(new Event("open"));});}
    send(payload:string) {
      const command=JSON.parse(payload);
      if(command.type==="subscribe_observations" && this===sockets[0]) return;
      const result=command.type==="get_version" ? {capabilities:["observe_v1"]} : command.type==="subscribe_observations" ? {subscriptionId:1,snapshot:envelope} : {document:{...envelope.document,kind:"changes",entries:undefined,events:[],status:"ok"},catalogs:[]};
      queueMicrotask(()=>this.dispatchEvent(new MessageEvent("message",{data:JSON.stringify({id:command.id,ok:true,result})})));
    }
    close() {this.readyState=2;}
    finishClose() {this.readyState=3;this.dispatchEvent(new Event("close"));this.dispatchEvent(new Event("error"));}
  }
  globalThis.WebSocket=ControlledSocket as unknown as typeof WebSocket;
  const inspector=new WebSocketInspectorClient("ws://controlled",20);
  try {
    await expect(inspector.subscribeObservations()).rejects.toThrow("Timed out");
    const fresh=await inspector.subscribeObservations();sockets[0]!.finishClose();
    expect((await inspector.pollObservations(fresh.subscriptionId)).document.kind).toBe("changes");
  } finally {inspector.close();globalThis.WebSocket=original;}
});
for (const kind of ["inspector", "mcp"] as const) {
  test(`${kind} stale handles cannot poll or remove a reused cursor after reconnect`, async () => {
    const peers = new Set<any>(), operations: string[] = [];
    const server = Bun.serve({port:0, fetch(request, server) {return server.upgrade(request) ? undefined : new Response(null,{status:400});}, websocket:{
      open(ws) {peers.add(ws);}, close(ws) {peers.delete(ws);},
      message(ws,data) {
        const command = JSON.parse(String(data));
        let result: unknown;
        if(command.type === "get_version") result={capabilities:["observe_v1"]};
        else if(command.type === "subscribe_observations") result={subscriptionId:1,snapshot:envelope};
        else {
          operations.push(`${command.type}:${command.subscriptionId}`);
          result=command.type === "unsubscribe_observations" ? null : {document:{...envelope.document,kind:"changes",entries:undefined,events:[],status:"ok"},catalogs:[]};
        }
        ws.send(JSON.stringify({id:command.id,ok:true,result}));
      }
    }});
    const url=`ws://127.0.0.1:${server.port}`, inspector=new WebSocketInspectorClient(url), mcp=new GuaBridgeClient(url);
    const subscribe=()=>kind === "inspector" ? inspector.subscribeObservations() : mcp.observeCommand("subscribe_observations") as Promise<{subscriptionId:number}>;
    const poll=(id:number)=>kind === "inspector" ? inspector.pollObservations(id) : mcp.observeCommand("poll_observations",id);
    const unsubscribe=(id:number)=>kind === "inspector" ? inspector.unsubscribeObservations(id) : mcp.observeCommand("unsubscribe_observations",id);
    try {
      const old=await subscribe(); for(const ws of peers) ws.close();
      for(let i=0;i<100&&peers.size;i++) await Bun.sleep(5);
      expect(peers.size).toBe(0); await Bun.sleep(5);
      const fresh=await subscribe();
      await expect(poll(old.subscriptionId)).rejects.toThrow("inactive connection");
      await unsubscribe(old.subscriptionId); expect(operations).toEqual([]);
      await poll(fresh.subscriptionId); await unsubscribe(fresh.subscriptionId);
      expect(operations).toEqual(["poll_observations:1","unsubscribe_observations:1"]);
    } finally {inspector.close();mcp.close();for(const ws of peers) ws.close();server.stop(true);}
  });
}
for (const mutation of ["epoch", "value", "id", "duplicate"] as const) {
  test(`Inspector and MCP reject malformed original ${mutation} integer/correlation wire`, async () => {
    const peers = new Set<any>();
    const server = Bun.serve({port:0, fetch(request, server) {return server.upgrade(request) ? undefined : new Response(null,{status:400});}, websocket:{
      open(ws) {peers.add(ws);}, close(ws) {peers.delete(ws);},
      message(ws, data) {
        const command = JSON.parse(String(data));
        if(command.type === "get_version") {ws.send(JSON.stringify({id:command.id,ok:true,result:{capabilities:["observe_v1"]}}));return;}
        let result = JSON.stringify({id:command.id,ok:true,result:envelope});
        if(mutation === "epoch") result = result.replace('"sessionEpoch":1', '"sessionEpoch":1.00000000000000001');
        if(mutation === "value") result = result.replace('"value":1}', '"value":1.00000000000000001}');
        if(mutation === "id") result = result.replace(`"id":${command.id}`, `"id":${command.id}.00000000000000001`);
        if(mutation === "duplicate") result = result.replace('{"id":', `{"id":${command.id},"id":`);
        ws.send(result);
      }
    }});
    const mcp = new GuaBridgeClient(`ws://127.0.0.1:${server.port}`);
    try {
      await expect(new WebSocketInspectorClient(`ws://127.0.0.1:${server.port}`).getObserveSnapshot()).rejects.toThrow("Invalid Observe transport response.");
      await expect(mcp.observeCommand("get_observe_snapshot")).rejects.toThrow("Invalid Observe transport response.");
    } finally {mcp.close(); for(const ws of peers) ws.close(); server.stop(true);}
  });
}
for (const mode of ["inspector-timeout", "mcp-timeout", "mcp-cancel"] as const) {
  test(`${mode} closes the owner connection after lost subscribe completion`, async () => {
    let accepted = false, closed = false;
    const peers = new Set<any>();
    const server = Bun.serve({port:0, fetch(request, server) {return server.upgrade(request) ? undefined : new Response(null,{status:400});}, websocket:{
      open(ws) {peers.add(ws);}, close(ws) {peers.delete(ws);closed = true;},
      message(ws,data) {
        const command = JSON.parse(String(data));
        if(command.type === "get_version") ws.send(JSON.stringify({id:command.id,ok:true,result:{capabilities:["observe_v1"]}}));
        if(command.type === "subscribe_observations") accepted = true;
      }
    }});
    const url = `ws://127.0.0.1:${server.port}`;
    const mcp = new GuaBridgeClient(url, mode === "mcp-cancel" ? 1000 : 40);
    try {
      const controller = new AbortController();
      const pending = mode === "inspector-timeout" ? new WebSocketInspectorClient(url,40).subscribeObservations() : mcp.observeCommand("subscribe_observations",undefined,controller.signal);
      if(mode === "mcp-cancel") {while(!accepted) await Bun.sleep(5);controller.abort();}
      await expect(pending).rejects.toThrow();
      for(let i=0;i<40&&!closed;i++) await Bun.sleep(5);
      expect(accepted).toBe(true);expect(closed).toBe(true);
    } finally {mcp.close();for(const ws of peers) ws.close();server.stop(true);}
  });
}
for (const kind of ["inspector", "mcp"] as const) for (const reply of ["malformed-success", "rejection", "malformed-rejection"] as const) {
  test(`${kind} subscribe ${reply} preserves only a verified rejection connection`, async () => {
    let closed = false, subscriptions = 0;
    const peers = new Map<any, Set<number>>();
    const server = Bun.serve({port:0, fetch(request, server) {return server.upgrade(request) ? undefined : new Response(null,{status:400});}, websocket:{
      open(ws) {peers.set(ws,new Set());}, close(ws) {peers.delete(ws);closed = true;},
      message(ws,data) {
        const command = JSON.parse(String(data));
        if(command.type === "get_version") {ws.send(JSON.stringify({id:command.id,ok:true,result:{capabilities:["observe_v1"]}}));return;}
        if(command.type === "subscribe_observations") {
          subscriptions++;
          if(subscriptions > 1 && reply !== "malformed-success") {
            ws.send(JSON.stringify({id:command.id,ok:false,error:"observe_rejected",...(reply === "malformed-rejection" ? {unknown:1} : {})}));return;
          }
          const id = 6+subscriptions; peers.get(ws)!.add(id);
          ws.send(JSON.stringify({id:command.id,ok:true,result:{subscriptionId:id,snapshot:envelope,...(subscriptions>1?{unknown:1}:{})}}));return;
        }
        if(command.type === "poll_observations") ws.send(JSON.stringify({id:command.id,ok:true,result:{document:{...envelope.document,kind:"changes",entries:undefined,events:[],status:"ok"},catalogs:[]}}));
      }
    }});
    const url=`ws://127.0.0.1:${server.port}`;
    const inspector=new WebSocketInspectorClient(url), mcp=new GuaBridgeClient(url);
    const subscribe=()=>kind === "inspector" ? inspector.subscribeObservations() : mcp.observeCommand("subscribe_observations");
    try {
      const first = await subscribe() as {subscriptionId:number};
      await expect(subscribe()).rejects.toThrow(reply === "rejection" ? "Observe request rejected." : "Invalid Observe transport response.");
      if(reply === "rejection") {
        expect(closed).toBe(false);expect(peers.size).toBe(1);
        const changes=kind === "inspector" ? await inspector.pollObservations(first.subscriptionId) : await mcp.observeCommand("poll_observations",first.subscriptionId);
        expect((changes as any).document.status).toBe("ok");
      } else {
        for(let i=0;i<40&&!closed;i++) await Bun.sleep(5);
        expect(closed).toBe(true);expect(peers.size).toBe(0);
      }
    } finally {mcp.close();for(const ws of peers.keys()) ws.close();server.stop(true);}
  });
}
