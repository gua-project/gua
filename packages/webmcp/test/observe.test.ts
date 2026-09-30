import { expect, test } from "bun:test";
import { createGodotWebBridge, createUnityWebGlBridge, createGuaInPageBridge, registerGuaWebMcp, type GuaInPagePort } from "../src/index";
const snapshot = { document: {schemaVersion: 1, sourceId: "page", sessionEpoch: 1, profile: "player", sequence: 0, revision: 0, uiFrame: 0, uiRevision: 0, worldFrame: 0, worldRevision: 0, kind: "snapshot", entries: []}, catalogs: [] };
for (const mode of ["timeout","cancel"] as const) {
  test(`same-page poll ${mode} invalidates the consumed cursor immediately`, async () => {
    const {entries,...metadata}=snapshot.document;
    const changes={document:{...metadata,kind:"changes",status:"ok",events:[]},catalogs:[]};
    let polls=0, released=0, finish!:(value:unknown)=>void, accepted!:()=>void;
    const polled=new Promise<void>(resolve=>{accepted=resolve;});
    const port:GuaInPagePort={capabilities:["observe_v1"],invoke:async command=> {
      if(command.type==="subscribe_observations") return {subscriptionId:1,snapshot};
      if(command.type==="unsubscribe_observations") {released++;return null;}
      if(command.type==="poll_observations" && ++polls===1) {accepted();return await new Promise(resolve=>{finish=resolve;});}
      return changes;
    }};
    const bridge=createGuaInPageBridge(port), tools=new Map<string,any>();
    const document={modelContext:{registerTool:(tool:any)=>tools.set(tool.name,tool)}} as unknown as Document;
    const registration=await registerGuaWebMcp(bridge,{document,defaultTimeoutMs:mode==="timeout"?10:1000});
    try {
      const sub=await bridge.subscribeObservations!();const controller=new AbortController();
      const pending=tools.get("poll_observations").execute({subscriptionId:sub.subscriptionId},{signal:controller.signal});
      if(mode==="cancel"){await polled;controller.abort();}
      expect((await pending).isError).toBe(true);
      await expect(bridge.pollObservations!(sub.subscriptionId)).rejects.toThrow("inactive connection");
      expect(polls).toBe(1);expect(released).toBe(1);
      finish(changes);await Bun.sleep(0);expect(released).toBe(1);
    } finally {finish?.(changes);registration.unregister();await bridge.disposeObservations!();}
  });
}
test("partial tool registration failure releases observations before returning", async () => {
  const cursors=new Set<number>();
  const port:GuaInPagePort={capabilities:["observe_v1"],invoke:async command=> {
    if(command.type==="subscribe_observations") {cursors.add(1);return {subscriptionId:1,snapshot};}
    if(command.type==="unsubscribe_observations") cursors.delete(command.subscriptionId);
    return null;
  }};
  const document={modelContext:{registerTool:async(tool:any)=> {
    if(tool.name==="subscribe_observations") await tool.execute({});
    if(tool.name==="poll_observations") throw new Error("registration rejected");
  }}} as unknown as Document;
  const bridge=createGuaInPageBridge(port);
  try {
    const registration=await registerGuaWebMcp(bridge,{document});
    expect(registration.supported).toBe(false);expect(registration.error?.message).toBe("registration rejected");
    expect(cursors.size).toBe(0);
  } finally {await bridge.disposeObservations!();}
});
for (const create of [createGodotWebBridge, createUnityWebGlBridge]) {
  test(`${create.name} releases a late subscription when its global port disappears`, async () => {
    const name="__guaObserveLateDetachTest", released:number[]=[];
    let finish!:(value:unknown)=>void;
    const port:GuaInPagePort={capabilities:["observe_v1"],invoke:async command=> {
      if(command.type==="subscribe_observations") return await new Promise(resolve=>{finish=resolve;});
      if(command.type==="unsubscribe_observations") released.push(command.subscriptionId);
      return null;
    }};
    const globals=globalThis as Record<string,unknown>; globals[name]=port;
    const bridge=create(name,{observe:true});
    try {
      const pending=bridge.subscribeObservations!();delete globals[name];finish({subscriptionId:7,snapshot});
      await expect(pending).rejects.toThrow();expect(released).toEqual([7]);
    } finally {await bridge.disposeObservations!();delete globals[name];}
  });
  test(`${create.name} keeps reused subscription IDs bound to their original engine port`, async () => {
    const name="__guaObserveReplacementTest", calls:number[]=[];
    const port=(generation:number):GuaInPagePort=>({capabilities:["observe_v1"],invoke:async command=> {
      if(command.type==="subscribe_observations") return {subscriptionId:1,snapshot};
      if(command.type==="poll_observations" || command.type==="unsubscribe_observations") calls.push(generation);
      const {entries,...metadata}=snapshot.document;
      return command.type==="unsubscribe_observations" ? null : {document:{...metadata,kind:"changes",status:"ok",events:[]},catalogs:[]};
    }});
    const globals=globalThis as Record<string,unknown>; globals[name]=port(1);
    const bridge=create(name,{observe:true});
    try {
      const old=await bridge.subscribeObservations!(); globals[name]=port(2);
      const fresh=await bridge.subscribeObservations!();
      await expect(bridge.pollObservations!(old.subscriptionId)).rejects.toThrow("inactive connection");
      await bridge.unsubscribeObservations!(old.subscriptionId); expect(calls).not.toContain(2);
      await bridge.pollObservations!(fresh.subscriptionId); await bridge.unsubscribeObservations!(fresh.subscriptionId);
      expect(calls.filter(g=>g===2)).toEqual([2,2]);
    } finally {await bridge.disposeObservations!();delete globals[name];}
  });
}
test("observe tools require capability and release subscriptions on unregister", async () => {
  const calls: string[] = [];
  const port: GuaInPagePort = { capabilities: ["observe_v1"], invoke: async c => { calls.push(c.type); return c.type === "subscribe_observations" ? {subscriptionId: 1, snapshot} : snapshot; } };
  const tools = new Map<string, {execute(input: Record<string, unknown>): Promise<unknown>}>();
  const document = {modelContext: {registerTool: (t: any) => tools.set(t.name, t)}} as unknown as Document;
  const registration = await registerGuaWebMcp(createGuaInPageBridge(port), {document});
  expect(tools.has("subscribe_observations")).toBe(true);
  for (const args of [null,0,false,[],"", "SECRET_MARKER"]) {
    const malformed = await tools.get("get_observe_snapshot")!.execute(args as any) as {isError:boolean};
    expect(malformed.isError).toBe(true); expect(JSON.stringify(malformed)).not.toContain("SECRET_MARKER");
  }
  const rejection = await tools.get("get_observe_snapshot")!.execute({SECRET_MARKER:1}) as {isError:boolean};
  expect(rejection.isError).toBe(true); expect(JSON.stringify(rejection)).not.toContain("SECRET_MARKER");
  await tools.get("subscribe_observations")!.execute({});
  registration.unregister();
  await new Promise(r => setTimeout(r, 0));
  expect(calls).toContain("unsubscribe_observations");
  const absent = createGuaInPageBridge({invoke: port.invoke});
  expect(absent.subscribeObservations).toBeUndefined();
});
test("late subscription after tool timeout is released and never returned", async () => {
  const released: number[] = [];
  let resolve!: (v: unknown) => void;
  const port: GuaInPagePort = { capabilities: ["observe_v1"], invoke: async c => {
    if (c.type === "subscribe_observations") return await new Promise(r => {resolve = r;});
    if (c.type === "unsubscribe_observations") released.push(c.subscriptionId);
    return null;
  }};
  const tools = new Map<string, {execute(input: Record<string, unknown>): Promise<unknown>}>();
  const document = {modelContext: {registerTool: (t: any) => tools.set(t.name, t)}} as unknown as Document;
  const registration = await registerGuaWebMcp(createGuaInPageBridge(port), {document, defaultTimeoutMs: 1});
  const failed = await tools.get("subscribe_observations")!.execute({}) as {isError: boolean; content: {text:string}[]};
  expect(failed.isError).toBe(true);
  expect(JSON.parse(failed.content[0]!.text).error.code).toBe("timeout");
  resolve({subscriptionId: 2, snapshot});
  await new Promise(r => setTimeout(r, 5));
  expect(released).toEqual([2]);
  registration.unregister();
});
test("a custom bridge without observation disposal cannot expose subscription tools", async () => {
  const port:GuaInPagePort={capabilities:["observe_v1"],invoke:async()=>snapshot};
  const bridge=createGuaInPageBridge(port);
  delete bridge.disposeObservations;
  const tools=new Map<string,unknown>();
  const document={modelContext:{registerTool:(tool:any)=>tools.set(tool.name,tool)}} as unknown as Document;
  const registration=await registerGuaWebMcp(bridge,{document});
  for(const name of ["get_observe_snapshot","subscribe_observations","poll_observations","unsubscribe_observations"]) expect(tools.has(name)).toBe(false);
  registration.unregister();
});
