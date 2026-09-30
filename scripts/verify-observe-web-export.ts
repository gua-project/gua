import { mkdtemp, rm } from "node:fs/promises";
import { resolve, sep } from "node:path";
import { tmpdir } from "node:os";
const engine = process.argv[2] ?? "unity";
if (!["unity", "godot"].includes(engine)) throw new Error("Expected unity or godot.");
const exportRoot = resolve(process.argv[3] ?? `artifacts/issue120-${engine}-web`);
const chromeExecutable = Bun.env.CHROME_EXECUTABLE ?? Bun.which("google-chrome") ?? "C:/Program Files/Google/Chrome/Application/chrome.exe";
const bundle = await Bun.build({entrypoints:["packages/webmcp/src/index.ts"], target:"browser", format:"esm"});
if (!bundle.success) throw new Error("Could not bundle WebMCP SDK.");
const sdkUrl = "data:text/javascript;base64," + Buffer.from(await bundle.outputs[0]!.arrayBuffer()).toString("base64");
const server = Bun.serve({port:0, async fetch(request) {
 const relative = decodeURIComponent(new URL(request.url).pathname).replace(/^\/+/, "") || "index.html";
 const path = resolve(exportRoot, relative);
 if (!path.startsWith(exportRoot + sep)) return new Response(null,{status:403});
 const file = Bun.file(path); if (!await file.exists()) return new Response(null,{status:404});
 return new Response(file,{headers:{"Cross-Origin-Opener-Policy":"same-origin","Cross-Origin-Embedder-Policy":"require-corp"}});
}});
const profileDirectory = await mkdtemp(resolve(tmpdir(), "gua-observe-web-"));
const debugProbe = Bun.serve({port:0,fetch:()=>new Response(null)});
const debuggingPort = debugProbe.port; debugProbe.stop(true);
const pageUrl = `http://127.0.0.1:${server.port}/index.html`;
const chrome = Bun.spawn([chromeExecutable,"--headless=new","--disable-background-timer-throttling","--disable-renderer-backgrounding","--disable-backgrounding-occluded-windows","--no-sandbox","--disable-dev-shm-usage","--enable-webgl","--ignore-gpu-blocklist","--use-angle=swiftshader","--enable-unsafe-swiftshader","--remote-allow-origins=*",`--remote-debugging-port=${debuggingPort}`,`--user-data-dir=${profileDirectory}`,pageUrl],{stdout:"ignore",stderr:"pipe"});
try {
 const target = await waitForPageTarget(debuggingPort,pageUrl,30000);
 const client = createCdpClient(target.webSocketDebuggerUrl); await client.open();
 await client.send("Runtime.enable"); await Bun.sleep(1000);
 try {
  const response = await client.send("Runtime.evaluate",{expression:`(async () => { const sdk = await import(${JSON.stringify(sdkUrl)}); return (${runSmoke.toString()})(sdk,${JSON.stringify(engine)}); })()`,awaitPromise:true,returnByValue:true},60000) as any;
  if (response.exceptionDetails) throw new Error(JSON.stringify(response.exceptionDetails));
  if (response.result?.value?.ok !== true) throw new Error("Invalid Web Observe result.");
  console.log(`${engine} real Web export -> WebMCP Observe passed: ${JSON.stringify(response.result.value)}`);
 } finally {client.close();}
} finally {
 chrome.kill(); await chrome.exited.catch(()=>undefined); server.stop(true);
 if (!profileDirectory.startsWith(resolve(tmpdir())+sep)) throw new Error("Invalid temporary profile path.");
 await rm(profileDirectory,{recursive:true,force:true});
}
async function runSmoke(sdk: any, engine: string) {
 const deadline = performance.now()+50000;
 const globals = globalThis as any;
 const name = engine === "unity" ? "__guaUnityWebPort" : "__guaGodotWebPort";
 while (!globals[name]) {if(performance.now()>deadline) throw new Error("Engine port not installed.");await new Promise(r=>setTimeout(r,25));}
 const tools = new Map<string,any>();
 const bridge = sdk.createGuaInPageBridge(globals[name],{world:true});
 // Registration host captures SDK tools; all data and actions use the real engine.
 const registration = await sdk.registerGuaWebMcp(bridge,{document:{modelContext:{registerTool:(tool:any)=>tools.set(tool.name,tool)}},defaultTimeoutMs:10000});
 const call = async (name:string,args:any={}) => {const result=await tools.get(name).execute(args);if(result?.isError)throw new Error(JSON.stringify(result));return result;};
 let snapshot:any;
 while(performance.now()<deadline){snapshot=await call("get_observe_snapshot");if(snapshot.document.entries.some((e:any)=>e.name==="phase" && e.status==="available"))break;await new Promise(r=>setTimeout(r,25));}
 const phaseIndex = snapshot.document.entries.findIndex((e:any)=>e.name==="phase");
 if(phaseIndex<0 || snapshot.document.profile!=="player" || JSON.stringify(snapshot).includes("SECRET_MARKER")) throw new Error("Player observations invalid.");
 const entry = snapshot.document.entries[phaseIndex];
 if(!entry.value) throw new Error("Phase unavailable.");
 if(entry.value.enumType!=="game.Phase" || snapshot.catalogs[phaseIndex].value.enums[0].members.join(",")!=="First,Second") throw new Error("Enum metadata lost.");
 const empty = snapshot.document.entries.find((e:any)=>e.name==="empty").value;
 if(empty.elementType!=="enum" || empty.value.length!==0) throw new Error("Empty collection type lost.");
 const world = await call("get_world_object_tree");
 const object = world.objects.find((o:any)=>o.id===entry.runtimeId);
 if(!object || object.position.x!==(engine==="unity"?640:2) || snapshot.document.entries.some((e:any)=>e.name==="worldPosition")) throw new Error("World/Observe association invalid.");
 const one = await call("subscribe_observations"); const two = await call("subscribe_observations");
 const tree = await call("get_ui_tree"); const button = tree.nodes.find((n:any)=>n.label===(engine==="unity"?"Settings":"Advance"));
 await call("click_node",{nodeId:button.id});
 let changed=false;
 while(performance.now()<deadline){const changes=await call("poll_observations",{subscriptionId:one.subscriptionId});if(changes.document.events.some((e:any)=>e.after?.value==="Second")){changed=true;break;}await new Promise(r=>setTimeout(r,25));}
 const changes = await call("poll_observations",{subscriptionId:two.subscriptionId});
 if(!changed || !changes.document.events.some((e:any)=>e.after?.value==="Second")) throw new Error("Independent change delivery failed.");
 await call("unsubscribe_observations",{subscriptionId:one.subscriptionId}); registration.unregister();
 await new Promise(r=>setTimeout(r,100));
 let rejected=false; try{await bridge.pollObservations(two.subscriptionId);}catch{rejected=true;}
 if(!rejected)throw new Error("Unregister did not release subscription.");
 return {ok:true,profile:snapshot.document.profile,runtimeId:entry.runtimeId,enumType:entry.value.enumType,emptyType:empty.type,changed,cleanup:rejected};
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
  throw new Error("Timed out waiting for the headless Chrome engine page.");
}

function createCdpClient(url: string) {
  const socket = new WebSocket(url);
  let nextId = 1;
  const pending = new Map<number, {
    resolve(value: unknown): void;
    reject(error: Error): void;
    timer: ReturnType<typeof setTimeout>;
  }>();
  socket.addEventListener("message", (event) => {
      const message = JSON.parse(String(event.data)) as { id?: number; result?: unknown; error?: { message: string } };
      if (message.id === undefined) return;
      const call = pending.get(message.id);
      if (!call) return;
      pending.delete(message.id);
      clearTimeout(call.timer);
      if (message.error) call.reject(new Error(message.error.message));
      else call.resolve(message.result);
  });
  socket.addEventListener("close", () => {
    for (const call of pending.values()) {
      clearTimeout(call.timer);
      call.reject(new Error("Chrome DevTools connection closed."));
    }
    pending.clear();
  });
  return {
    open(timeoutMs = 10_000): Promise<void> {
      if (socket.readyState === WebSocket.OPEN) return Promise.resolve();
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error("Timed out connecting to Chrome DevTools.")), timeoutMs);
        socket.addEventListener("open", () => { clearTimeout(timer); resolve(); }, { once: true });
        socket.addEventListener("error", () => { clearTimeout(timer); reject(new Error("Could not connect to Chrome DevTools.")); }, { once: true });
      });
    },
    send(method: string, params: Record<string, unknown> = {}, timeoutMs = 10_000): Promise<unknown> {
      const id = nextId++;
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
          pending.delete(id);
          reject(new Error(`Timed out waiting for Chrome DevTools method ${method}.`));
        }, timeoutMs);
        pending.set(id, { resolve, reject, timer });
        try {
          socket.send(JSON.stringify({ id, method, params }));
        } catch (error) {
          clearTimeout(timer);
          pending.delete(id);
          reject(error instanceof Error ? error : new Error(`Could not send Chrome DevTools method ${method}.`));
        }
      });
    },
    close(): void {
      socket.close();
    },
  };
}
