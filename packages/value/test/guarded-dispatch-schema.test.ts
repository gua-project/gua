import { expect, test } from "bun:test";
import Ajv from "ajv/dist/2020.js";
import commands from "../../../protocol/schema/commands.schema.json";
import guarded from "../../../protocol/schema/guarded-dispatch-v1.schema.json";
import spatial from "../../../protocol/schema/spatial-host-r1.schema.json";

const ajv = new Ajv({ strict: false });
ajv.addSchema(guarded); ajv.addSchema(spatial);
const validate = ajv.compile(commands);
const guard = { expectedSessionEpoch: 1, expectedProfile: 1, expectedRevision: 3 };
test("guarded commands compose with existing protocol without widening legacy verbs", () => {
  for (const payload of [
    { type: "guarded_click_node", nodeId: "buy" },
    { type: "guarded_press_key", nodeId: null, key: "Enter" },
    { type: "guarded_press_game_input_action", actionId: "jump" },
    { type: "guarded_pointer_move", mode: "absolute", coordinateSpace: "viewport_pixels", x: 20, y: 30 },
    { type: "guarded_pointer_move", mode: "delta", x: 2, y: -3 },
    { type: "guarded_poll_action", requestId: 4 },
    { type: "guarded_poll_game_input", requestId: 5 },
  ]) expect(validate({ ...guard, ...payload })).toBe(true);
  expect(validate({ type: "click_node", nodeId: "buy" })).toBe(true);
  expect(validate({ type: "click_node", nodeId: "buy", ...guard })).toBe(false);
});
test("guarded schema rejects missing and unauthorized metadata or lifecycle verbs", () => {
  const click = { ...guard, type: "guarded_click_node", nodeId: "buy" };
  for (const invalid of [
    { ...click, expectedSessionEpoch: 0 }, { ...click, expectedProfile: 2 },
    { ...click, expectedRevision: 1.5 }, { ...click, profile: "debug" },
    { type: "guarded_click_node", nodeId: "buy" },
    { ...guard, type: "guarded_poll_action" },
    { ...guard, type: "guarded_release_all_game_inputs" },
    { ...guard, type: "guarded_reset_context" },
    { ...guard, type: "guarded_pointer_move", mode: "delta", coordinateSpace: "viewport_pixels", x: 1, y: 2 },
  ]) expect(validate(invalid)).toBe(false);
});
