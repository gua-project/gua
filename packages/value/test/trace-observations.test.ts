import { expect, test } from "bun:test";
import Ajv from "ajv/dist/2020.js";
import value from "../../../protocol/schema/value-v1.schema.json";
import trace from "../../../protocol/schema/trace.schema.json";
import observeSchema from "../../../protocol/schema/observe-v1.schema.json";
import transportSchema from "../../../protocol/schema/observe-transport-v1.schema.json";
import enumSchema from "../../../protocol/schema/enum-catalog-v1.schema.json";
import observe from "../../../protocol/fixtures/observe-v1.json";
import fixture from "../../../protocol/fixtures/trace-observations.json";

const ajv = new Ajv({ strict: false });
ajv.addSchema(value); ajv.addSchema(observeSchema); ajv.addSchema(enumSchema); ajv.addSchema(transportSchema);
const validate = ajv.compile(trace);
test("decimal uint64 limits match native range including every boundary prefix", () => {
  const check = ajv.compile({ $ref: `${trace.$id}#/$defs/uint64` });
  const maximum = 18446744073709551615n;
  for (let i = 0; i <= 20; i++) {
    const value = maximum - (10n ** BigInt(i));
    if (value >= 0n) expect(check(String(value))).toBe(true);
  }
  for (const valid of ["0", "00000000000000000000", String(maximum), "10000000000000000000"]) expect(check(valid)).toBe(true);
  for (const invalid of [String(maximum + 1n), "99999999999999999999", "184467440737095516150", "-1", "1.0"]) expect(check(invalid)).toBe(false);
});

test("Observe host references require typed source and bounded decimal metadata", () => {
  for (const [key, values] of Object.entries({ sourceId: [null, {}, ""], sessionEpoch: [{}, "0", "18446744073709551616"], revision: [[], 1, "99999999999999999999"], frame: [null, "-1"] })) {
    for (const value of values) {
      const data = { ...transition(fixture.transitions[1]!), host: { ...fixture.host, [key]: value } };
      expect(validate(event("observation.change", data))).toBe(false);
    }
  }
  expect(validate(event("observation.change", { ...transition(fixture.transitions[1]!),
    host: { sourceId: "game", sessionEpoch: "18446744073709551615", revision: "0", frame: "0" } }))).toBe(true);
});
const event = (type: string, data: unknown) => ({ schemaVersion: 1, traceId: fixture.traceId, stepId: fixture.stepId,
  sequence: 1, eventId: "e1", collectedMilliseconds: 1, type, data });
const uint64 = new Set(["sessionEpoch", "sequence", "revision", "uiFrame", "uiRevision", "worldFrame", "worldRevision", "ownerId", "registrationId"]);
function changeData(document: object) {
  const received = Object.fromEntries(Object.entries(document).map(([key, value]) => [key, uint64.has(key) ? String(value) : value]));
  return { channel: "observe", reason: "intermediate", intervalId: fixture.intervalId, continuity: "continuous",
    host: fixture.host, change: received.kind, received, catalogs: {} };
}
function transition(document: object) {
  const base = { ...(observe.valid.find(e => e.id === "added")!.document as { events: object[] }).events[0] } as Record<string, unknown>;
  for (const key of ["before", "after", "beforeStatus", "afterStatus", "beforeError", "afterError"]) delete base[key];
  return changeData({ ...base, ...document });
}
test("Trace accepts published Observe transitions with decimal identities and common Values", () => {
  // Shared G-02 fixtures remain the source for before/after availability semantics.
  for (const entry of observe.valid) {
    if (!("events" in entry.document)) continue;
    for (const received of entry.document.events)
      expect(validate(event("observation.change", changeData(received)))).toBe(true);
  }
  for (const received of fixture.transitions) expect(validate(event("observation.change", transition(received)))).toBe(true);
});
test("Trace rejects missing/dummy values and imprecise identities", () => {
  const original = transition(fixture.transitions[1]!);
  for (const invalid of fixture.invalidChanges) {
    const data = structuredClone(original);
    if (invalid === "missing-after") delete data.received.after;
    if (invalid === "null-after") data.received.after = null;
    if (invalid === "numeric-registration-id") data.received.registrationId = 3;
    if (invalid === "unsupported-kind") data.received.kind = "empty-success";
    if (invalid === "missing-after-status") delete data.received.afterStatus;
    if (invalid === "missing-before-status") delete data.received.beforeStatus;
    if (invalid === "error-with-value") data.received.afterError = 100;
    expect(validate(event("observation.change", data))).toBe(false);
  }
});
test("Trace rejects unknown fields in the closed Observe change contract", () => {
  for (const received of fixture.transitions) {
    const data = transition(received);
    expect(validate(event("observation.change", data))).toBe(true);
    data.received.unexpected = true;
    expect(validate(event("observation.change", data))).toBe(false);
  }
});
test("Trace keeps explicit unavailable observations without a fabricated snapshot blob", () => {
  for (const unavailable of fixture.unavailable)
    expect(validate(event("observation", { observationId: fixture.intervalId, channel: "observe", ...unavailable,
      host: fixture.host, continuity: "unverified" }))).toBe(true);
});
test("Trace rejects conflicting duplicated change kinds for every transition", () => {
  const kinds = ["added", "changed", "removed", "unavailable", "recovered"];
  for (const received of fixture.transitions) {
    for (const kind of kinds) {
      const data = transition(received); data.change = kind;
      expect(validate(event("observation.change", data))).toBe(kind === received.kind);
    }
  }
});
test("Trace catalogs follow the Observe transport single-enum contract", () => {
  const catalog = { schemaVersion: 1, enums: [{ enumType: "game.Phase", members: ["First", "Second"] }] };
  const original = transition(fixture.transitions[1]!);
  const withCatalogs = (catalogs: unknown) => event("observation.change", { ...original, catalogs });
  for (const side of ["before", "after", "value"]) expect(validate(withCatalogs({ [side]: catalog }))).toBe(false);
  for (const side of ["before", "after"] as const) {
    for (const typed of [
      { type: "enum", enumType: "game.Phase", value: "First" },
      { type: "list", elementType: "enum", enumType: "game.Phase", value: [] },
      { type: "set", elementType: "enum", enumType: "game.Phase", value: [] },
    ]) {
      const data = structuredClone(original); data.received[side] = typed;
      expect(validate(event("observation.change", data))).toBe(false);
      expect(validate(event("observation.change", { ...data, catalogs: { [side]: catalog } }))).toBe(true);
      expect(validate(event("observation.change", { ...data, catalogs: { [side]: catalog, value: catalog } }))).toBe(false);
    }
  }
  for (const received of fixture.transitions) {
    const data = transition(received);
    for (const side of ["before", "after", "value"])
      expect(validate(event("observation.change", { ...data, catalogs: { [side]: catalog } }))).toBe(false);
  }
  original.received.after = { type: "enum", enumType: "game.Phase", value: "First" };
  for (const catalogs of [
    { extra: catalog }, { after: {} }, { after: { ...catalog, schemaVersion: 2 } },
    { after: { ...catalog, enums: [] } }, { after: { ...catalog, enums: [...catalog.enums, ...catalog.enums] } },
    { after: { schemaVersion: 1, enums: [{ enumType: "bad", members: ["First"] }] } },
    { after: { schemaVersion: 1, enums: [{ enumType: "game.Phase", members: ["First", "First"] }] } },
    { after: { ...catalog, extra: true } },
  ]) expect(validate(withCatalogs(catalogs))).toBe(false);
});
test("Observe identities stay positive while publication counters may be zero", () => {
  for (const key of ["sessionEpoch", "ownerId", "registrationId"]) {
    for (const invalid of ["0", "00", "00000000000000000000", "-1", "18446744073709551616", "99999999999999999999"]) {
      const data = transition(fixture.transitions[1]!); data.received[key] = invalid;
      expect(validate(event("observation.change", data))).toBe(false);
    }
    for (const positive of ["1", "18446744073709551615"]) {
      const data = transition(fixture.transitions[1]!); data.received[key] = positive;
      expect(validate(event("observation.change", data))).toBe(true);
    }
  }
  const data = transition(fixture.transitions[1]!);
  for (const key of ["sequence", "revision", "uiFrame", "uiRevision", "worldFrame", "worldRevision"]) data.received[key] = "0";
  expect(validate(event("observation.change", data))).toBe(true);
});
