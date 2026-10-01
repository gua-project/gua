import { expect, test } from "bun:test";
import Ajv2020 from "ajv/dist/2020";
import fixtures from "../../../protocol/fixtures/input-action-metadata-v1.json";
import metadataSchema from "../../../protocol/schema/input-value-schema-v1.schema.json";
import mapSchema from "../../../protocol/schema/game-input-actions-v2.schema.json";
import searchSchema from "../../../protocol/schema/game-input-action-search-v2.schema.json";
import oldMapSchema from "../../../protocol/schema/game-input-actions.schema.json";
import oldSearchSchema from "../../../protocol/schema/game-input-action-search.schema.json";
import commandsSchema from "../../../protocol/schema/game-input-metadata-commands-v1.schema.json";
import { validInputActionMetadata } from "../../webmcp/src/input-metadata";
import { createGuaInPageBridge, registerGuaWebMcp } from "../../webmcp/src/index";

const ajv = new Ajv2020({ strict: false });
for (const schema of [metadataSchema, mapSchema, searchSchema, oldMapSchema, oldSearchSchema]) ajv.addSchema(schema);
const vector = fixtures.cases[0]!.action;
const map = { schemaVersion: 2, sessionEpoch: 1, revision: 1, context: "play", actions: [vector] };

test("schema validators reject lone surrogates and retain valid astral metadata", () => {
  const validate = ajv.getSchema(metadataSchema.$id)!;
  for (const description of ["\ud800", "\udfff", "a\ud800b"]) {
    expect(validate({ type: "number", description })).toBe(false);
    expect(validate({ ...vector.valueSchema, properties: {
      ...vector.valueSchema.properties, x: { type: "number", description },
    } })).toBe(false);
    expect(ajv.getSchema(mapSchema.$id)!({ ...map, actions: [{
      id: "name", description: "Name", valueType: "text", holdable: false, active: true,
      bindings: [], risk: "safe", requiresConfirmation: false, examples: [description],
    }] })).toBe(false);
  }
  expect(validate({ type: "string", description: "\ud83d\ude80" })).toBe(true);
  expect(ajv.getSchema(mapSchema.$id)!({ ...map, actions: [{
    id: "name", description: "Name", valueType: "text", holdable: false, active: true,
    bindings: [], risk: "safe", requiresConfirmation: false, examples: ["\ud83d\ude80"],
  }] })).toBe(true);
});

test("legacy in-page discovery rejects metadata without changing valid v1 responses", async () => {
  const { valueSchema: _, examples: __, ...action } = vector;
  let addition: Record<string, unknown> = {};
  const bridge = createGuaInPageBridge({ invoke: async command => {
    const result = { ...map, schemaVersion: 1, actions: [{ ...action, ...addition }] };
    return command.type === "find_game_input_actions" ? { ...result, count: 1, truncated: false } : result;
  } }, { gameInput: true });
  expect((await bridge.getGameInputActions!()).actions[0]).toEqual(action);
  expect((await bridge.findGameInputActions!({ id: "move" })).actions[0]).toEqual(action);
  for (addition of [{ valueSchema: { unknown: true } }, { examples: [] }, { examples: ["invalid"] }]) {
    await expect(bridge.getGameInputActions!()).rejects.toThrow("invalid game input action map");
    await expect(bridge.findGameInputActions!({ id: "move" })).rejects.toThrow("invalid game input action search");
  }
});

test("shared declaration fixtures satisfy exactly the supported dialect and combined constraints", () => {
  for (const fixture of fixtures.cases) expect({ name: fixture.name, valid: validInputActionMetadata(fixture.action) })
    .toEqual({ name: fixture.name, valid: fixture.valid });
  expect(ajv.getSchema(mapSchema.$id)!(map)).toBe(true);
  expect(validInputActionMetadata({ ...vector, valueType: "text", valueSchema: undefined, examples: undefined })).toBe(true);
  expect(validInputActionMetadata({ ...vector, valueType: "axis1d", range: { minimum: 0.123456789, maximum: 0.5 },
    valueSchema: { type: "number", minimum: 0.123456789 }, examples: [0.123456789] })).toBe(true);
  expect(ajv.getSchema(searchSchema.$id)!({ ...map, count: 1, truncated: false })).toBe(true);
  for (const fixture of fixtures.cases.filter(f => f.valid)) {
    const schema = fixture.action.valueSchema;
    if (schema) expect(ajv.getSchema(metadataSchema.$id)!(schema)).toBe(true);
  }
});

test("unmodified strict legacy validators accept legacy payloads and reject metadata additions", () => {
  const { valueSchema: _schema, examples: _examples, ...legacyAction } = vector;
  const legacy = { ...map, schemaVersion: 1, actions: [legacyAction] };
  expect(ajv.getSchema(oldMapSchema.$id)!(legacy)).toBe(true);
  expect(ajv.getSchema(oldSearchSchema.$id)!({ ...legacy, count: 1, truncated: false })).toBe(true);
  expect(ajv.getSchema(oldMapSchema.$id)!({ ...map, schemaVersion: 1 })).toBe(false);
  expect(ajv.getSchema(oldMapSchema.$id)!(map)).toBe(false);
  const validateCommands = ajv.compile(commandsSchema);
  expect(validateCommands({ type: "get_game_input_actions_v2" })).toBe(true);
  expect(validateCommands({ type: "find_game_input_actions_v2", actionId: "move", limit: 1 })).toBe(true);
  expect(validateCommands({ type: "find_game_input_actions_v2", confirmed: true })).toBe(false);
});

test("v2 validators reject ranges only on metadata-bearing button and text actions", () => {
  const { valueSchema: _schema, examples: _examples, ...legacy } = vector;
  for (const valueType of ["button", "text"] as const) {
    const action = { ...legacy, valueType };
    for (const [validate, document] of [
      [ajv.getSchema(mapSchema.$id)!, map],
      [ajv.getSchema(searchSchema.$id)!, { ...map, count: 1, truncated: false }],
    ] as const) {
      expect(validate({ ...document, actions: [action] })).toBe(true);
      expect(validInputActionMetadata(action)).toBe(true);
      for (const metadata of [
        { valueSchema: { type: valueType === "button" ? "boolean" : "string" } },
        { examples: [] },
        { valueSchema: { type: valueType === "button" ? "boolean" : "string" }, examples: [] },
      ]) {
        expect(validate({ ...document, actions: [{ ...action, ...metadata }] })).toBe(false);
        expect(validInputActionMetadata({ ...action, ...metadata })).toBe(false);
        const { range: _range, ...withoutRange } = action;
        expect(validate({ ...document, actions: [{ ...withoutRange, ...metadata }] })).toBe(true);
      }
    }
    expect(ajv.getSchema(oldMapSchema.$id)!({ ...map, schemaVersion: 1, actions: [action] })).toBe(true);
    expect(ajv.getSchema(oldSearchSchema.$id)!({ ...map, schemaVersion: 1, actions: [action], count: 1, truncated: false })).toBe(true);
  }
});

test("same-page metadata calls are explicit, version-specific and reject inconsistent engine declarations", async () => {
  const requests: string[] = [];
  let invalid = false;
  const bridge = createGuaInPageBridge({ invoke: async command => {
    requests.push(command.type);
    if (command.type === "get_game_input_capabilities") return ["semantic_game_input_v1", "semantic_game_input_metadata_v1"];
    if (command.type === "get_game_input_actions") {
      const { valueSchema: _, examples: __, ...action } = vector;
      return { ...map, schemaVersion: 1, actions: [action] };
    }
    const result = invalid ? { ...map, actions: [{ ...vector, examples: [{ x: 2, y: 0 }] }] } : map;
    return command.type === "find_game_input_actions_v2" ? { ...result, count: 1, truncated: false } : result;
  } }, { gameInput: true });
  expect((await bridge.getGameInputActions!()).schemaVersion).toBe(1);
  expect(requests).toEqual(["get_game_input_actions"]);
  expect((await bridge.getGameInputActionsV2!()).actions[0]!.valueSchema).toEqual(vector.valueSchema);
  expect((await bridge.findGameInputActionsV2!({ id: "move" })).actions[0]!.examples).toEqual(vector.examples);
  invalid = true;
  await expect(bridge.getGameInputActionsV2!()).rejects.toThrow("invalid game input action map");
});

test("v2 JSON text enforces raw example byte boundaries on both discovery paths", async () => {
  const numberAction = { ...vector, valueType: "axis1d", valueSchema: { type: "number" }, examples: [0] };
  for (const search of [false, true]) {
    let literal = "0";
    const document = { ...map, actions: [numberAction], ...(search ? { count: 1, truncated: false } : {}) };
    const bridge = createGuaInPageBridge({ invoke: async () => JSON.stringify(document).replace('"examples":[0]', `"exampl\\u0065s":[${literal}]`) }, { gameInput: true });
    const read = () => search ? bridge.findGameInputActionsV2!({}) : bridge.getGameInputActionsV2!();
    literal = `0.${"0".repeat(508)}1`;
    expect(new TextEncoder().encode(literal).length).toBe(511);
    await expect(read()).resolves.toMatchObject({ actions: [{ examples: [0] }] });
    for (const zeroes of [509, 600]) {
      literal = `0.${"0".repeat(zeroes)}1`;
      await expect(read()).rejects.toMatchObject({ code: "invalid_request" });
    }
    const vectorDocument = { ...document, actions: [vector] };
    for (const [padding, valid] of [[498, true], [499, false], [600, false]] as const) {
      const example = `{"x":0,${" ".repeat(padding)}"y":0}`;
      const raw = JSON.stringify({ ...vectorDocument, actions: [{ ...vector, examples: [null] }] }).replace('[null]', `[${example}]`);
      const port = createGuaInPageBridge({ invoke: async () => raw }, { gameInput: true });
      const result = search ? port.findGameInputActionsV2!({}) : port.getGameInputActionsV2!();
      if (valid) await expect(result).resolves.toMatchObject({ actions: [{ examples: [{ x: 0, y: 0 }] }] });
      else await expect(result).rejects.toMatchObject({ code: "invalid_request" });
    }
    const raw = JSON.stringify({ ...vectorDocument, actions: [{ ...vector, examples: [null] }] }).replace('[null]', `[${" ".repeat(600)}{"x":0,"y":0}${" ".repeat(600)}]`);
    const port = createGuaInPageBridge({ invoke: async () => raw }, { gameInput: true });
    await expect(search ? port.findGameInputActionsV2!({}) : port.getGameInputActionsV2!()).resolves.toMatchObject({ schemaVersion: 2 });
  }
});

test("WebMCP metadata tools require the capability and never run published examples", async () => {
  const tools = new Map<string, { execute(input: Record<string, unknown>): Promise<unknown> }>();
  const document = { modelContext: { registerTool: (tool: { name: string; execute(input: Record<string, unknown>): Promise<unknown> }) => tools.set(tool.name, tool), unregisterTool: (name: string) => tools.delete(name) } } as unknown as Document;
  let capable = false, executed = 0;
  const bridge = createGuaInPageBridge({ invoke: async command => {
    if (command.type === "get_game_input_capabilities") return capable ? ["semantic_game_input_v1", "semantic_game_input_metadata_v1"] : ["semantic_game_input_v1"];
    if (command.type === "get_game_input_actions_v2") return map;
    if (command.type === "find_game_input_actions_v2") return { ...map, count: 1, truncated: false };
    if (command.type === "perform_game_input" && command.request.type === "release_all_game_inputs") return { completed: true, requestId: 1, succeeded: true };
    executed++; throw new Error("must not execute examples");
  } }, { gameInput: true });
  const legacy = await registerGuaWebMcp(bridge, { document });
  expect(tools.has("get_game_input_actions_v2")).toBe(false);
  legacy.unregister(); capable = true;
  const registration = await registerGuaWebMcp(bridge, { document });
  expect(tools.has("get_game_input_actions_v2")).toBe(true);
  expect(await tools.get("get_game_input_actions_v2")!.execute({})).toEqual(map);
  expect(await tools.get("find_game_input_actions_v2")!.execute({ id: "move" })).toEqual({ ...map, count: 1, truncated: false });
  expect(executed).toBe(0); registration.unregister();
});
