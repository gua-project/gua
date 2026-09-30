import { expect, test } from "bun:test";
import { WebSocketInspectorClient } from "../../inspector/src/core";
import { GuaBridgeClient } from "../../mcp/src/index";
const envelope = {document:{schemaVersion:1,sourceId:"wire",sessionEpoch:1,profile:"debug",sequence:0,revision:0,uiFrame:0,uiRevision:0,worldFrame:0,worldRevision:0,kind:"snapshot",entries:[{ownerId:1,registrationId:1,source:"object",runtimeId:"enemy",name:"phase",status:"available",value:{type:"integer",value:1}}]},catalogs:[{}]};
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
