import { expect, test } from "bun:test";
import { findGameInputActionsMetadataCompatible, type GuaInspectorClient } from "../src/core";

test("Inspector metadata selection falls back on an old client without re-registering descriptions", async () => {
  const action = { id: "jump", description: "Jump", valueType: "button", holdable: false, active: true, bindings: [], risk: "safe", requiresConfirmation: false };
  const legacy = { schemaVersion: 1, sessionEpoch: 1, revision: 1, context: "play", actions: [action], count: 1, truncated: false };
  let requests = 0;
  const client = { findGameInputActions: async () => { requests++; return legacy; }, findGameInputActionsV2: async () => { throw new Error("unsupported metadata"); } } as unknown as GuaInspectorClient;
  expect(await findGameInputActionsMetadataCompatible(client, { id: "jump" })).toEqual(legacy);
  expect(requests).toBe(1);
  client.findGameInputActionsV2 = async () => ({ ...legacy, schemaVersion: 2, actions: [{ ...action, valueSchema: { type: "boolean" }, examples: [] }] } as never);
  expect((await findGameInputActionsMetadataCompatible(client, { id: "jump" })).actions[0]?.examples).toEqual([]);
  expect(requests).toBe(1);
});
