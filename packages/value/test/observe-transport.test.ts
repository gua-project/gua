import { expect, test } from "bun:test";
import { decodeObserveWireResponse, parseObserveTransport } from "../src/index";
const metadata = { schemaVersion: 1, sourceId: "fixture", sessionEpoch: 1, profile: "debug", sequence: 1, revision: 1, uiFrame: 1, uiRevision: 1, worldFrame: 2, worldRevision: 2 };
const entry = { ownerId: 1, registrationId: 2, source: "object", runtimeId: "enemy", name: "phase", status: "available" };
const catalog = { schemaVersion: 1, enums: [{ enumType: "game.Phase", members: ["First", "Second"] }] };
function envelope(value: unknown, candidates: unknown = {}) { return { document: { ...metadata, kind: "snapshot", entries: [{ ...entry, value }] }, catalogs: [candidates] }; }
test("transport preserves candidates, enum aliases and empty collection element type", () => {
  const parsed = parseObserveTransport(JSON.stringify(envelope({ type: "set", elementType: "enum", enumType: "game.Phase", value: [] }, { value: catalog })));
  expect(parsed.document.kind).toBe("snapshot");
  if (parsed.document.kind === "snapshot") expect(parsed.document.entries[0]!.value).toEqual({ type: "set", elementType: "enum", enumType: "game.Phase", value: [] });
  expect(parsed.catalogs[0]!.value).toEqual(catalog);
});
test("wire integer and metadata lexemes cannot round into valid integers", () => {
  const valid = JSON.stringify(envelope({ type: "integer", value: 1 }));
  expect(() => parseObserveTransport(valid.replace('"value":1', '"value":1.00000000000000001'))).toThrow("Invalid Observe");
  expect(() => parseObserveTransport(valid.replace('"sessionEpoch":1', '"sessionEpoch":1.00000000000000001'))).toThrow("Invalid Observe");
});
test("missing candidates, misaligned catalogs, gaps with events and unknown fields are rejected safely", () => {
  expect(() => parseObserveTransport(envelope({ type: "enum", enumType: "game.Phase", value: "First" }))).toThrow("Invalid Observe");
  expect(() => parseObserveTransport({ ...envelope({ type: "bool", value: false }), catalogs: [] })).toThrow("Invalid Observe");
  expect(() => parseObserveTransport({ ...envelope({ type: "string", value: "SECRET_MARKER" }), SECRET_MARKER: 1 })).toThrow("Invalid Observe transport response.");
  const gap = { document: { ...metadata, kind: "changes", status: "gap", events: [] }, catalogs: [] };
  expect(parseObserveTransport(gap).document).toEqual(gap.document);
  expect(() => parseObserveTransport({ ...gap, document: { ...gap.document, events: [{}] }, catalogs: [{}] })).toThrow("Invalid Observe");
});
import fixtures from "../../../protocol/fixtures/observe-v1.json";
import observeSchema from "../../../protocol/schema/observe-v1.schema.json";
import valueSchema from "../../../protocol/schema/value-v1.schema.json";
import transportSchema from "../../../protocol/schema/observe-transport-v1.schema.json";
import enumSchema from "../../../protocol/schema/enum-catalog-v1.schema.json";
import { Ajv2020 } from "ajv/dist/2020.js";
test("transport parser and protocol schema agree on Observe fixture documents", () => {
  const ajv = new Ajv2020({strict:false});
  ajv.addSchema(valueSchema); ajv.addSchema(observeSchema); ajv.addSchema(enumSchema);
  const validate = ajv.compile(transportSchema);
  for (const fixture of fixtures.valid) {
    const d = fixture.document as any;
    const env = {document:d, catalogs:(d.entries ?? d.events ?? []).map(() => ({}))};
    if (d.kind === "snapshot" || d.kind === "changes") {
      expect(validate(env)).toBe(true);
      expect(parseObserveTransport(env).document).toEqual(d);
    }
  }
  for (const fixture of fixtures.invalid) {
    const d = fixture.document as any;
    if (d.kind !== "snapshot" && d.kind !== "changes") continue;
    const env = {document:d, catalogs:(d.entries ?? d.events ?? []).map(() => ({}))};
    expect(validate(env)).toBe(false);
    expect(() => parseObserveTransport(env)).toThrow("Invalid Observe");
  }
});

test("only the Value's used enum definition may cross the transport", () => {
  const extra = {schemaVersion:1, enums:[...catalog.enums, {enumType:"game.Secret", members:["SECRET_MARKER"]}]};
  expect(() => parseObserveTransport(envelope({type:"enum", enumType:"game.Phase", value:"First"}, {value:extra}))).toThrow("Invalid Observe transport response.");
  expect(() => parseObserveTransport(envelope({type:"integer", value:1}, {value:catalog}))).toThrow("Invalid Observe transport response.");
  expect(() => parseObserveTransport(envelope({type:"list", elementType:"integer", value:[]}, {value:catalog}))).toThrow("Invalid Observe transport response.");
});

test("operations cannot exchange Snapshot and Changes documents", () => {
  const snapshot = envelope({type:"bool",value:true});
  const changes = {document:{...metadata,kind:"changes",status:"ok",events:[]},catalogs:[]};
  for (const [operation,result] of [["get_observe_snapshot",changes],["subscribe_observations",{subscriptionId:1,snapshot:changes}],["poll_observations",snapshot]] as const)
    expect(() => decodeObserveWireResponse(JSON.stringify({id:1,ok:true,result}),1,operation)).toThrow("Invalid Observe");
});
