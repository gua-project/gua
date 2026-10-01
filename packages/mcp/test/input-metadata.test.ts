import { expect, test } from "bun:test";
import { GuaBridgeClient, guaMcpToolDefinitions } from "../src/index";

test("native MCP explicitly negotiates metadata and retains the old default command", async () => {
  const requests: string[] = []; let capable = false;
  const map = { schemaVersion: 2, sessionEpoch: 1, revision: 1, context: "play", actions: [{ id: "jump", examples: [true] }] };
  const server = Bun.serve({ port: 0, fetch(request, server) { if (server.upgrade(request)) return; return new Response("upgrade", { status: 426 }); },
    websocket: { message(socket, text) {
      const command = JSON.parse(String(text)); requests.push(command.type);
      const result = command.type === "get_version" ? { capabilities: capable ? ["semantic_game_input_metadata_v1"] : [] }
        : command.type === "get_game_input_actions" ? { ...map, schemaVersion: 1, actions: [] }
        : command.type === "find_game_input_actions_v2" ? { ...map, count: 1, truncated: false } : map;
      socket.send(JSON.stringify({ id: command.id, ok: true, result }));
    } } });
  const client = new GuaBridgeClient(`ws://127.0.0.1:${server.port}`);
  try {
    expect(await client.getGameInputActions()).toMatchObject({ schemaVersion: 1 });
    expect(requests).toEqual(["get_game_input_actions"]);
    await expect(client.getGameInputActionsV2()).rejects.toThrow("unsupported");
    expect(requests).not.toContain("get_game_input_actions_v2");
    capable = true;
    expect(await client.getGameInputActionsV2()).toEqual(map);
    expect(await client.findGameInputActionsV2({ actionId: "jump", limit: 1 })).toMatchObject({ schemaVersion: 2, count: 1 });
    expect(guaMcpToolDefinitions.some(tool => tool.name === "get_game_input_actions_v2")).toBe(true);
    expect(requests).not.toContain("set_game_input_action");
  } finally { client.close(); server.stop(true); }
});
