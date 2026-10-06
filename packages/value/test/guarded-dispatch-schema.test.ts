import { expect, test } from "bun:test";
import Ajv from "ajv/dist/2020.js";
import commands from "../../../protocol/schema/commands.schema.json";
import guarded from "../../../protocol/schema/guarded-dispatch-v1.schema.json";
import spatial from "../../../protocol/schema/spatial-host-r1.schema.json";

const ajv = new Ajv({ strict: false });
ajv.addSchema(guarded); ajv.addSchema(spatial);
const validate = ajv.compile(commands);
const guard = { id: 1, expectedSessionEpoch: 1, expectedProfile: 1, expectedRevision: 3 };
test("guarded commands compose with existing protocol without widening legacy verbs", () => {
  for (const payload of [
    { type: "guarded_click_node", nodeId: "buy" },
    { type: "guarded_set_checked", nodeId: "buy", checked: false },
    { type: "guarded_set_value", nodeId: "name", value: "" },
    { type: "guarded_select", nodeId: "choice", value: "a" },
    { type: "guarded_scroll", nodeId: "list", deltaX: 0, deltaY: 1 },
    { type: "guarded_press_key", key: "Enter" },
    { type: "guarded_press_game_input_action", actionId: "jump" },
    { type: "guarded_pointer_move", mode: "absolute", coordinateSpace: "viewport_pixels", x: 20, y: 30 },
    { type: "guarded_pointer_move", mode: "delta", x: 2, y: -3 },
    { type: "guarded_poll_action", requestId: 4 },
    { type: "guarded_poll_game_input", requestId: 5 },
  ]) expect(validate({ ...guard, ...payload })).toBe(true);
  expect(validate({ type: "click_node", nodeId: "buy" })).toBe(true);
  expect(validate({ type: "click_node", nodeId: "buy", ...guard })).toBe(false);
});
test("guarded UI preserves required legacy payload fields and their types", () => {
  const examples = [
    { type: "set_checked", nodeId: "buy", checked: false },
    { type: "set_value", nodeId: "name", value: "" },
    { type: "select", nodeId: "choice", value: "a" },
    { type: "scroll", nodeId: "list", deltaX: 0, deltaY: 1 },
    { type: "press_key", key: "Enter" },
  ];
  const definitions = {set_checked: "checkedAction", set_value: "valueAction", select: "selectAction", scroll: "scrollAction", press_key: "pressKey"} as const;
  for (const example of examples) {
    const required = commands.$defs[definitions[example.type as keyof typeof definitions]].required;
    for (const field of required.filter(f => f !== "type")) {
      const incomplete = { ...example } as Record<string, unknown>; delete incomplete[field];
      expect(validate(incomplete)).toBe(false);
      expect(validate({ ...incomplete, type: "guarded_" + example.type, ...guard })).toBe(false);
    }
  }
  for (const invalid of [
    {type:"guarded_set_checked", nodeId:"buy", checked:null},
    {type:"guarded_set_checked", nodeId:"buy", checked:"false"},
    {type:"guarded_set_value", nodeId:"buy", value:42},
    {type:"guarded_select", nodeId:"choice", value:""},
    {type:"guarded_scroll", nodeId:"list", deltaX:0, deltaY:"1"},
    {type:"guarded_press_key", key:null},
  ]) expect(validate({...guard, ...invalid})).toBe(false);
});
test("guarded input payloads preserve legacy required fields, enums, bounds and exclusions", () => {
  const valid = [
    {type:"press_game_input_action", actionId:"jump"}, {type:"set_game_input_action", actionId:"move", value:0.5, leaseMs:5000},
    {type:"release_game_input_action", actionId:"move"},
    ...["key_down","key_up","press_physical_key"].map(type => ({type, code:"Space"})),
    {type:"pointer_move", mode:"absolute", coordinateSpace:"viewport_normalized", x:0.5, y:1},
    {type:"pointer_move", mode:"delta", x:2, y:-3},
    ...["pointer_button_down","pointer_button_up"].map(type => ({type, button:"primary"})),
    {type:"pointer_wheel", deltaY:1, wheelUnit:"lines"},
    ...["gamepad_button_down","gamepad_button_up"].map(type => ({type, button:"south", gamepadIndex:3})),
    {type:"set_gamepad_axis", axis:"left_stick_x", value:-1, gamepadIndex:0}, {type:"text_input", text:"Hello"},
    {type:"text_input", text:""},
  ];
  const invalid = [
    {type:"set_gamepad_axis", axis:"left_stick_x"}, {type:"set_gamepad_axis", axis:"left_stick_x", value:1.1},
    {type:"set_gamepad_axis", axis:"unknown", value:0}, {type:"set_gamepad_axis", axis:"left_stick_x", value:0, gamepadIndex:4},
    {type:"gamepad_button_down", button:"primary"}, {type:"gamepad_button_down", button:"south", axis:"left_stick_x"},
    {type:"key_down", code:"Invalid"}, {type:"key_down", code:"Space", leaseMs:0},
    {type:"pointer_button_down", button:"middle"}, {type:"pointer_move", mode:"relative", x:0, y:0},
    {type:"pointer_move", mode:"absolute", coordinateSpace:"viewport_normalized", x:2, y:0},
    {type:"pointer_wheel", wheelUnit:"lines"}, {type:"pointer_wheel", deltaY:1, leaseMs:100},
    {type:"set_game_input_action", actionId:"move"}, {type:"press_game_input_action", actionId:"jump", value:false},
    {type:"release_game_input_action", actionId:"move", confirmed:false}, {type:"press_game_input_action", actionId:"Invalid"},
    {type:"text_input", text:"x".repeat(41)},
  ];
  for (const payload of valid) {
    expect(validate(payload)).toBe(true); expect(validate({...guard,...payload,type:"guarded_"+payload.type})).toBe(true);
  }
  for (const payload of invalid) {
    expect(validate(payload)).toBe(false); expect(validate({...guard,...payload,type:"guarded_"+payload.type})).toBe(false);
  }
});
test("guarded schema rejects missing and unauthorized metadata or lifecycle verbs", () => {
  const click = { ...guard, type: "guarded_click_node", nodeId: "buy" };
  for (const invalid of [
    {...click,id:undefined}, {...click,id:0}, {...click,id:-1}, {...click,id:1.5}, {...click,id:"1"}, {...click,id:2147483648},
    { ...click, expectedSessionEpoch: 0 }, { ...click, expectedProfile: 2 },
    { ...click, expectedRevision: 1.5 }, { ...click, profile: "debug" },
    { type: "guarded_click_node", nodeId: "buy" },
    { ...guard, type: "guarded_poll_action" },
    { ...guard, type: "guarded_release_all_game_inputs" },
    { ...guard, type: "guarded_reset_context" },
    { ...guard, type: "guarded_pointer_move", mode: "delta", coordinateSpace: "viewport_pixels", x: 1, y: 2 },
  ]) expect(validate(invalid)).toBe(false);
});
