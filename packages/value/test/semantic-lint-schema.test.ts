import { test, expect } from "bun:test";
import Ajv from "ajv/dist/2020.js";
import lint from "../../../protocol/schema/semantic-lint.schema.json";
import ui from "../../../protocol/schema/ui-tree.schema.json";
import world from "../../../protocol/schema/world-object-tree.schema.json";
import commands from "../../../protocol/schema/commands.schema.json";
import spatialHost from "../../../protocol/schema/spatial-host-r1.schema.json";
import fixture from "../../../protocol/fixtures/semantic-lint-v1.json";
const ajv = new Ajv({ strict: false });
ajv.addSchema(spatialHost);
const report = ajv.compile(lint), uiTree = ajv.compile(ui), worldTree = ajv.compile(world), command = ajv.compile(commands);
test("published lint fixtures retain valid source shapes", () => {
  for (const item of fixture.cases) { expect(uiTree(item.uiTree)).toBe(true); expect(worldTree(item.worldObjectTree)).toBe(true); }
  expect(report(fixture.report)).toBe(true);
  expect(report({ ...fixture.report, schemaVersion: 2 })).toBe(false);
  expect(report({ ...fixture.report, findings: [{ ruleId: "x", severity: "fatal" }] })).toBe(false);
});
test("lint command cannot select profile or supply raw trees", () => {
  expect(command({ type: "semantic_lint" })).toBe(true);
  expect(command({ type: "semantic_lint", includeWorld: false })).toBe(true);
  expect(command({ type: "semantic_lint", profile: "debug" })).toBe(false);
  expect(command({ type: "semantic_lint", uiTree: fixture.cases[0]?.uiTree })).toBe(false);
});
