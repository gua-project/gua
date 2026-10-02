import { expect, test } from "bun:test";
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import path from "node:path";
import { GuaActionOutcomeError, GuaBridgeClient, replayRecording } from "../src/index";

const tree = { screen: "exit_confirmation", revision: 1, nodes: [] };
const completion = { requestId: 7, action: 1, succeeded: true, error: 0, nodeId: "confirm_exit",
  value: "", sensitive: false, sessionEpoch: 1, frameSequence: 2, revision: 2 };
type Scenario = "normal_disconnect" | "abnormal_disconnect" | "lost_receipt" | "success" | "rejected" | "legacy" | "legacy_disconnect" | "wrong_completion" | "invalid_receipt" | "failed_completion";

function fixture(scenario: Scenario) {
  const commands: any[] = []; let connections = 0;
  const server = Bun.serve({ hostname: "127.0.0.1", port: 0,
    fetch(request, server) { if (server.upgrade(request)) return; return new Response("upgrade", { status: 426 }); },
    websocket: {
      open() { connections++; },
      message(socket, data) {
        const command = JSON.parse(String(data)); commands.push(command);
        const respond = (result: unknown) => socket.send(JSON.stringify({ id: command.id, ok: true, result }));
        if (command.type === "get_ui_tree") {
          if (scenario === "legacy_disconnect" && commands.some(c => c.type === "click_node")) socket.close();
          else respond(tree);
        } else if (command.type === "click_node") {
          if (scenario === "lost_receipt") socket.terminate();
          else if (scenario === "rejected") socket.send(JSON.stringify({ id: command.id, ok: false, error: "Gua action rejected: disabled" }));
          else respond(scenario.startsWith("legacy") ? null : scenario === "invalid_receipt" ? {} : { requestId: 7 });
        } else if (command.type === "poll_events") {
          if (scenario === "normal_disconnect") socket.close(1000, "normal shutdown");
          else if (scenario === "abnormal_disconnect") socket.terminate();
          else { respond(scenario === "wrong_completion" ? { ...completion, requestId: 8 }
            : scenario === "failed_completion" ? { ...completion, succeeded: false, error: -4 } : completion); socket.close(); }
        }
      },
    },
  });
  return { server, commands, url: `ws://127.0.0.1:${server.port}`, connections: () => connections };
}

async function callTool(url: string, name = "click_node", args: unknown = { nodeId: "confirm_exit" }) {
  const child = spawn(process.execPath, [path.resolve(import.meta.dir, "../src/cli.ts"), "mcp"], {
    env: { ...process.env, GUA_BRIDGE_URL: url }, stdio: ["pipe", "pipe", "pipe"],
  });
  const lines = createInterface({ input: child.stdout }); child.stderr.resume();
  try {
    const response = new Promise<any>((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error("MCP test response timed out")), 3000);
      lines.on("line", line => { const rpc = JSON.parse(line); if (rpc.id === 2) { clearTimeout(timeout); resolve(rpc.result); } });
      child.once("error", error => { clearTimeout(timeout); reject(error); });
    });
    child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id: 1, method: "initialize", params: {
      protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "action-outcome-test", version: "1" },
    } }) + "\n");
    child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) + "\n");
    child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id: 2, method: "tools/call", params: { name, arguments: args } }) + "\n");
    const result = await response;
    return { isError: result.isError, body: JSON.parse(result.content[0].text) };
  } finally { lines.close(); child.kill(); }
}

for (const scenario of ["normal_disconnect", "abnormal_disconnect"] as const) {
  test(`${scenario}: accepted exit remains unconfirmed with correlation and no reconnect/retry`, async () => {
    const f = fixture(scenario);
    try {
      const result = await callTool(f.url);
      expect(result).toMatchObject({ isError: true, body: { outcome: "completion_unconfirmed",
        stage: "awaiting_completion", requestSent: true, requestId: 7 } });
      expect(result.body.bridgeCommandId).toBeInteger();
      expect(result.body.error).toContain("Do not retry automatically");
      expect(f.commands.filter(c => c.type === "click_node")).toHaveLength(1);
      expect(f.connections()).toBe(1);
    } finally { f.server.stop(true); }
  });
}

test("initial connection refusal reports that the action was never sent", async () => {
  const f = fixture("success"); f.server.stop(true);
  const result = await callTool(f.url);
  expect(result).toMatchObject({ isError: true, body: { outcome: "not_sent", stage: "before_send", requestSent: false } });
  expect(result.body.requestId).toBeUndefined(); expect(result.body.bridgeCommandId).toBeUndefined();
  expect(f.commands).toHaveLength(0);
});

test("lost acceptance response preserves send evidence without inventing a host request ID", async () => {
  const f = fixture("lost_receipt");
  try {
    const result = await callTool(f.url);
    expect(result).toMatchObject({ isError: true, body: { outcome: "completion_unconfirmed", stage: "awaiting_receipt", requestSent: true } });
    expect(result.body.bridgeCommandId).toBeInteger(); expect(result.body.requestId).toBeUndefined();
    expect(f.commands.filter(c => c.type === "click_node")).toHaveLength(1);
  } finally { f.server.stop(true); }
});

test("correlated completion stays successful when the runtime closes immediately afterwards", async () => {
  const f = fixture("success");
  try {
    expect(await callTool(f.url)).toEqual({ isError: false, body: { ok: true, requestId: 7, completion } });
    expect(f.commands.filter(c => c.type === "get_ui_tree")).toHaveLength(1);
  } finally { f.server.stop(true); }
});

test("explicit bridge rejection keeps its existing error", async () => {
  const f = fixture("rejected");
  try { expect(await callTool(f.url)).toEqual({ isError: true, body: { error: "Gua action rejected: disabled" } }); }
  finally { f.server.stop(true); }
});

test("correlated host failure keeps its existing error", async () => {
  const f = fixture("failed_completion");
  try { expect(await callTool(f.url)).toEqual({ isError: true, body: { error: "Gua action click failed with error -4." } }); }
  finally { f.server.stop(true); }
});

test("malformed acceptance cannot fabricate a host request ID", async () => {
  const f = fixture("invalid_receipt");
  try {
    const result = await callTool(f.url);
    expect(result).toMatchObject({ isError: true, body: { outcome: "completion_unconfirmed", stage: "awaiting_receipt", requestSent: true } });
    expect(result.body.requestId).toBeUndefined();
    expect(f.commands.some(c => c.type === "poll_events")).toBe(false);
  } finally { f.server.stop(true); }
});

test("legacy null receipt keeps the existing successful response", async () => {
  const f = fixture("legacy");
  try { expect(await callTool(f.url)).toEqual({ isError: false, body: { ok: true } }); }
  finally { f.server.stop(true); }
});

test("legacy observation failure is sent but unconfirmed without a fabricated request ID", async () => {
  const f = fixture("legacy_disconnect");
  try {
    const result = await callTool(f.url);
    expect(result).toMatchObject({ isError: true, body: { outcome: "completion_unconfirmed", stage: "awaiting_observation", requestSent: true } });
    expect(result.body.requestId).toBeUndefined();
  } finally { f.server.stop(true); }
});

test("a mismatched completion cannot confirm the exit action", async () => {
  const f = fixture("wrong_completion");
  try { expect(await callTool(f.url)).toMatchObject({ isError: true, body: { outcome: "completion_unconfirmed", requestId: 7 } }); }
  finally { f.server.stop(true); }
});

test("run_test stops on an uncertain exit without sending its next step", async () => {
  const f = fixture("normal_disconnect");
  try {
    expect(await callTool(f.url, "run_test", { steps: [{ action: "click_node", nodeId: "confirm_exit" },
      { action: "click_node", nodeId: "other" }] })).toMatchObject({ isError: true, body: { outcome: "completion_unconfirmed", requestId: 7 } });
    expect(f.commands.filter(c => c.type === "click_node")).toHaveLength(1);
  } finally { f.server.stop(true); }
});

test("replay completion timeout is bounded and retains acceptance evidence", async () => {
  const server = Bun.serve({ port: 0, hostname: "127.0.0.1",
    fetch(req, server) { if (server.upgrade(req)) return; return new Response("upgrade", { status: 426 }); },
    websocket: { message(socket, data) { const command = JSON.parse(String(data));
      if (command.type !== "poll_events") socket.send(JSON.stringify({ id: command.id, ok: true,
        result: command.type === "get_ui_tree" ? tree : { requestId: 7 } })); } },
  });
  const bridge = new GuaBridgeClient(`ws://127.0.0.1:${server.port}`, 5000);
  try {
    const start = performance.now();
    let failure: unknown;
    try { await replayRecording({ schemaVersion: 1, steps: [{ action: "click", target: { id: "confirm_exit" },
      relativeMilliseconds: 0, preRevision: 1, postRevision: 2, sensitive: false }] }, bridge, {}, "preserve_delays", 75); }
    catch (error) { failure = error; }
    expect(failure).toBeInstanceOf(GuaActionOutcomeError);
    expect((failure as GuaActionOutcomeError).diagnostics()).toMatchObject({ outcome: "completion_unconfirmed", stage: "awaiting_completion", requestId: 7 });
    expect(performance.now() - start).toBeLessThan(1000);
  } finally { bridge.close(); server.stop(true); }
});
