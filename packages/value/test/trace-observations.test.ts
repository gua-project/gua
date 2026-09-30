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
  const withCatalogs = (catalogs: unknown) => event("observation.change", { ...transition(fixture.transitions[1]!), catalogs });
  for (const side of ["before", "after", "value"]) expect(validate(withCatalogs({ [side]: catalog }))).toBe(true);
  for (const catalogs of [
    { extra: catalog }, { after: {} }, { after: { ...catalog, schemaVersion: 2 } },
    { after: { ...catalog, enums: [] } }, { after: { ...catalog, enums: [...catalog.enums, ...catalog.enums] } },
    { after: { schemaVersion: 1, enums: [{ enumType: "bad", members: ["First"] }] } },
    { after: { schemaVersion: 1, enums: [{ enumType: "game.Phase", members: ["First", "First"] }] } },
    { after: { ...catalog, extra: true } },
  ]) expect(validate(withCatalogs(catalogs))).toBe(false);
});
