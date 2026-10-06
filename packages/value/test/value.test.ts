import { describe, expect, test } from "bun:test";
import Ajv from "ajv/dist/2020.js";
import { createValue, EnumCatalog, parseValue, serializeValue, ValueError, valuesEqual } from "../src/index.js";
import fixture from "../../../protocol/fixtures/value-v1.json";
import schema from "../../../protocol/schema/value-v1.schema.json";
import catalogSchema from "../../../protocol/schema/enum-catalog-v1.schema.json";
import worldSchema from "../../../protocol/schema/world-object-tree.schema.json";
import world from "../../../protocol/fixtures/world-object-tree-v1.json";
import commands from "../../../protocol/schema/commands.schema.json";
import spatialHost from "../../../protocol/schema/spatial-host-r1.schema.json";
import guardedDispatch from "../../../protocol/schema/guarded-dispatch-v1.schema.json";

const catalog = EnumCatalog.fromJSON(JSON.stringify(fixture.catalog));
const ajv = new Ajv({ strict: false });
ajv.addSchema(spatialHost);
ajv.addSchema(guardedDispatch);
const validate = ajv.compile(schema);
const codes = (fn: () => unknown, code: string) => { try { fn(); throw new Error("Expected Value rejection"); } catch (e) { expect(e).toBeInstanceOf(ValueError); expect((e as ValueError).code).toBe(code); expect((e as ValueError).path.startsWith("$")).toBe(true); expect((e as Error).message).not.toContain("DO_NOT_LOG"); } };
describe("shared Value v1 corpus", () => {
  for (const c of fixture.valid) test(`roundtrip ${c.id}`, () => {
    const value = parseValue(c.json, catalog);
    expect(validate(JSON.parse(c.json))).toBe(true);
    expect(validate(JSON.parse(serializeValue(value, catalog)))).toBe(true);
    expect(valuesEqual(value, parseValue(serializeValue(value, catalog), catalog), catalog)).toBe(true);
  });
  for (const c of fixture.invalid) test(`reject ${c.id}`, () => { codes(() => parseValue(c.json, catalog), c.error); if (c.schemaReject) expect(validate(JSON.parse(c.json))).toBe(false); });
  for (const c of fixture.comparisons) test(`compare ${c.id}`, () => expect(valuesEqual(parseValue(c.left, catalog), parseValue(c.right, catalog), catalog)).toBe(c.equal));
  for (const c of fixture.generators) test(`generate ${c.id}`, () => { const value = c.generate === "NaN" ? NaN : c.generate === "Infinity" ? Infinity : c.generate === "-Infinity" ? -Infinity : BigInt(1); codes(() => createValue({ type: c.type, value }), c.error); });
});
test("catalog schema, snapshot, registration conflict and isolation", () => {
  expect(ajv.compile(catalogSchema)(fixture.catalog)).toBe(true);
  const c = new EnumCatalog(), members = ["Second", "First"];
  c.register("game.Phase", members); members[0] = "mutated";
  c.register("game.Phase", ["First", "Second"]);
  expect(c.members("game.Phase")).toEqual(["Second", "First"]);
  codes(() => c.register("game.Phase", ["Third"]), "enum_conflict");
  codes(() => c.register("game.Other", ["First", "First"]), "duplicate");
  codes(() => new EnumCatalog().members("game.Phase"), "enum_unknown");
  expect(EnumCatalog.fromJSON(JSON.stringify(c.toJSON())).toJSON()).toEqual(c.toJSON());
});
test("copies and freezes inputs; preserves order and NUL", () => {
  const input = { type: "set", elementType: "string", value: ["b\u0000", "a"] };
  const value = createValue(input); input.value[0] = "changed";
  expect(value.value).toEqual(["b\u0000", "a"]);
  expect(Object.isFrozen(value)).toBe(true); expect(Object.isFrozen(value.value)).toBe(true);
  expect(serializeValue(parseValue('{"type":"number","value":-0}'))).not.toContain("-0");
  codes(() => createValue({ type: "string", value: "\ud800" }), "unicode");
  codes(() => createValue({ type: "list", elementType: "string", value: new Array(1) }), "forbidden_type");
});
test("existing World nullable state remains valid", () => {
  const sample = structuredClone(world); sample.objects[0].state["nullable"] = null;
  expect(ajv.compile(worldSchema)(sample)).toBe(true);
});
test("existing Input vector2 remains valid while Value objects are rejected", () => {
  expect(ajv.compile(commands)({ type: "set_game_input_action", actionId: "move", value: { x: 0.5, y: -1 } })).toBe(true);
  codes(() => createValue({ type: "number", value: { x: 0.5, y: -1 } }), "forbidden_type");
});
test("catalog rejects sparse members and rounded schema version", () => {
  codes(() => new EnumCatalog().register("game.Phase", new Array(1)), "structure");
  codes(() => EnumCatalog.fromJSON('{"schemaVersion":1.00000000000000001,"enums":[]}'), "range");
});
