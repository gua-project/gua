import { expect, test } from "bun:test";
import { createGuaInPageBridge, registerGuaWebMcp, type GuaInPagePort } from "../src/index";
const snapshot = { document: {schemaVersion: 1, sourceId: "page", sessionEpoch: 1, profile: "player", sequence: 0, revision: 0, uiFrame: 0, uiRevision: 0, worldFrame: 0, worldRevision: 0, kind: "snapshot", entries: []}, catalogs: [] };
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
