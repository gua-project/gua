import { test, expect } from "bun:test";
import Ajv from "ajv/dist/2020.js";
import schema from "../../../protocol/schema/spatial-r1.schema.json";
import fixture from "../../../protocol/fixtures/spatial-r1.json";
import version from "../../../protocol/schema/version.schema.json";
import hostSchema from "../../../protocol/schema/spatial-host-r1.schema.json";
import hostFixture from "../../../protocol/fixtures/spatial-host-r1.json";
import legacyVersion from "../../../protocol/fixtures/version-v1.json";
const ajv = new Ajv({ strict: false, strictNumbers: true });
ajv.addSchema(hostSchema);
const validate = ajv.compile(schema);
for (const c of fixture.valid) test(`Spatial schema valid ${c.id}`, () => {
  expect(validate(JSON.parse(c.json))).toBe(true);
});
for (const c of fixture.invalid.filter(c => c.schemaReject)) test(`Spatial schema rejects ${c.id}`, () => {
  let document: unknown;
  try { document = JSON.parse(c.json); } catch { return; }
  expect(validate(document)).toBe(false);
});
test("Spatial uses the existing additive capability list without rewriting legacy version", () => {
  const check = ajv.compile(version);
  expect(check(legacyVersion)).toBe(true);
  expect(check({ ...legacyVersion, capabilities: [...legacyVersion.capabilities, "spatial_read_r1"] })).toBe(true);
  expect(legacyVersion.capabilities.includes("spatial_read_r1")).toBe(false);
  const advertisement = { schemaVersion: "spatial-host-r1", documentType: "advertisement",
    provider: hostFixture.valid[0]!.json.provider,
    budgets: { maxQueueDepth: 4, queryDeadlineMs: 1000, maxBatchWorkTimeMs: 1000 } };
  expect(check({ ...legacyVersion, capabilities: [...legacyVersion.capabilities, "spatial_read_r1"], spatial: advertisement })).toBe(true);
  expect(check({ ...legacyVersion, spatial: { ...advertisement, budgets: { maxQueueDepth: 0 } } })).toBe(false);
});
