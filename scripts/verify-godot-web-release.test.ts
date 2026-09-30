import { expect, test } from "bun:test";
import { CdpTimeoutError, createCdpClient, waitForDefaultExecutionContext } from "./godot-web-cdp";

// Exercise the actual CDP transport: a busy renderer delays all queued replies,
// including an already timed-out probe. Its late response must not complete the next probe.
async function withCdpServer(
  respond: (request: { id: number; method: string; params: Record<string, unknown> }, reply: (payload: unknown) => void, close: () => void) => void,
  run: (client: ReturnType<typeof createCdpClient>) => Promise<void>,
) {
  const timers = new Set<ReturnType<typeof setTimeout>>();
  const server = Bun.serve({
    port: 0,
    fetch(request, server) { return server.upgrade(request) ? undefined : new Response("WebSocket required", { status: 400 }); },
    websocket: {
      message(socket, message) {
        const request = JSON.parse(String(message));
        respond(request, (payload) => {
          const timer = setTimeout(() => { timers.delete(timer); socket.send(JSON.stringify({ id: request.id, ...payload as object })); }, 0);
          timers.add(timer);
        }, () => socket.terminate());
      },
    },
  });
  const client = createCdpClient(`ws://127.0.0.1:${server.port}`);
  try { await client.open(); await run(client); }
  finally { client.close(); for (const timer of timers) clearTimeout(timer); await server.stop(true); }
}

test("readiness survives a renderer blocked beyond the 1s probe inside its 10s deadline", async () => {
  const started = performance.now();
  const queued: Array<() => void> = [];
  const release = setTimeout(() => { for (const reply of queued) reply(); queued.length = 0; }, 1_350);
  try {
    await withCdpServer((request, reply) => {
      expect(request.method).toBe("Runtime.evaluate");
      expect(request.params.expression).toBe("document.readyState");
      const respond = () => reply({ result: { result: { value: "complete" } } });
      if (performance.now() - started < 1_350) queued.push(respond); else respond();
    }, async client => {
      await waitForDefaultExecutionContext(client, 10_000);
      expect(performance.now() - started).toBeGreaterThanOrEqual(1_300);
      expect(performance.now() - started).toBeLessThan(3_000);
    });
  } finally { clearTimeout(release); }
}, 5_000);

test("permanently unresponsive CDP fails at the total deadline", async () => {
  await withCdpServer(() => {}, async client => {
    const started = performance.now();
    await expect(waitForDefaultExecutionContext(client, 1_200)).rejects.toThrow("Timed out waiting for the browser document execution context.");
    const elapsed = performance.now() - started;
    expect(elapsed).toBeGreaterThanOrEqual(1_150);
    expect(elapsed).toBeLessThan(1_700);
  });
}, 3_000);

test("late responses remain correlated after a CDP request timeout", async () => {
  await withCdpServer((request, reply) => {
    if (request.id === 1) setTimeout(() => reply({ result: { marker: "late" } }), 60);
    else setTimeout(() => reply({ result: { marker: "current" } }), 100);
  }, async client => {
    await expect(client.send("Runtime.evaluate", {}, 20)).rejects.toBeInstanceOf(CdpTimeoutError);
    expect(await client.send("Runtime.evaluate", {}, 500)).toEqual({ marker: "current" });
  });
});

test("readiness retries changing execution contexts without masking a protocol error", async () => {
  let attempts = 0;
  await withCdpServer((_request, reply) => {
    attempts++;
    if (attempts === 1) reply({ error: { message: "Cannot find default execution context" } });
    else if (attempts === 2) reply({ error: { message: "Execution context was destroyed." } });
    else reply({ error: { message: "Permission denied" } });
  }, async client => {
    await expect(waitForDefaultExecutionContext(client, 1_000)).rejects.toThrow("Permission denied");
    expect(attempts).toBe(3);
  });
});

test("readiness does not swallow unrelated timeout or connection failures", async () => {
  for (const error of [new CdpTimeoutError("Runtime.enable"), new Error("Chrome DevTools connection closed.")]) {
    await expect(waitForDefaultExecutionContext({ send: async () => { throw error; } }, 1_000)).rejects.toBe(error);
  }
});

test("a CDP socket closed between probes fails immediately as a disconnect", async () => {
  await withCdpServer((_request, reply, close) => {
    reply({ result: {} });
    setTimeout(close, 10);
  }, async client => {
    await client.send("Runtime.enable");
    await Bun.sleep(100);
    await expect(waitForDefaultExecutionContext(client, 300)).rejects.toThrow("Chrome DevTools connection closed.");
  });
});

test("a responsive loading document still observes the total readiness deadline", async () => {
  await withCdpServer((_request, reply) => reply({ result: { result: { value: "loading" } } }), async client => {
    const started = performance.now();
    await expect(waitForDefaultExecutionContext(client, 150)).rejects.toThrow("Timed out waiting for the browser document execution context.");
    expect(performance.now() - started).toBeLessThan(350);
  });
});
