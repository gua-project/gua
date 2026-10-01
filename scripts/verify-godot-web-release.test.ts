import { expect, test } from "bun:test";
import { CdpTimeoutError, createCdpClient, waitForDefaultExecutionContext } from "./godot-web-cdp";

const pageUrl = "http://127.0.0.1:8123/index.html";
function evaluateDocument(request: { params: Record<string, unknown> }, url: string, readyState: string) {
  // Evaluate the actual probe against controlled documents, rather than making a
  // server that claims every expression succeeded at the requested URL.
  return new Function("location", "document", `return (${request.params.expression});`)({ href: url }, { readyState });
}

// Exercise the actual CDP transport: a busy renderer delays all queued replies,
// including an already timed-out probe. Its late response must not complete the next probe.
async function withCdpServer(
  respond: (request: { id: number; method: string; params: Record<string, unknown> }, reply: (payload: unknown) => void, close: () => void) => void,
  run: (client: ReturnType<typeof createCdpClient>) => Promise<void>,
) {
  const timers = new Set<ReturnType<typeof setTimeout>>();
  let serverDisconnected = false;
  const server = Bun.serve({
    port: 0,
    fetch(request, server) { return server.upgrade(request) ? undefined : new Response("WebSocket required", { status: 400 }); },
    websocket: {
      message(socket, message) {
        const request = JSON.parse(String(message));
        respond(request, (payload) => {
          const timer = setTimeout(() => { timers.delete(timer); socket.send(JSON.stringify({ id: request.id, ...payload as object })); }, 0);
          timers.add(timer);
        }, () => { serverDisconnected = true; socket.terminate(); });
      },
    },
  });
  const client = createCdpClient(`ws://127.0.0.1:${server.port}`);
  try { await client.open(); await run(client); }
  finally {
    client.close();
    for (const timer of timers) clearTimeout(timer);
    // Bun 1.3.14 leaves the drain promise pending after a server-initiated
    // disconnect. Force-stop the listener, then verify it no longer serves.
    const url = server.url;
    const stopped = server.stop(true);
    if (!serverDisconnected) await stopped;
    await expect(fetch(url, { signal: AbortSignal.timeout(500) })).rejects.toThrow();
  }
}

test("readiness survives a renderer blocked beyond the 1s probe inside its 10s deadline", async () => {
  const started = performance.now();
  const queued: Array<() => void> = [];
  const release = setTimeout(() => { for (const reply of queued) reply(); queued.length = 0; }, 1_350);
  try {
    await withCdpServer((request, reply) => {
      expect(request.method).toBe("Runtime.evaluate");
      expect(evaluateDocument(request, pageUrl, "complete")).toBe("complete");
      const respond = () => reply({ result: { result: { value: "complete" } } });
      if (performance.now() - started < 1_350) queued.push(respond); else respond();
    }, async client => {
      await waitForDefaultExecutionContext(client, 10_000, pageUrl);
      expect(performance.now() - started).toBeGreaterThanOrEqual(1_300);
      expect(performance.now() - started).toBeLessThan(3_000);
    });
  } finally { clearTimeout(release); }
}, 5_000);

test("permanently unresponsive CDP fails at the total deadline", async () => {
  await withCdpServer(() => {}, async client => {
    const started = performance.now();
    await expect(waitForDefaultExecutionContext(client, 1_200, pageUrl)).rejects.toThrow("Timed out waiting for the browser document execution context.");
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
    await expect(waitForDefaultExecutionContext(client, 1_000, pageUrl)).rejects.toThrow("Permission denied");
    expect(attempts).toBe(3);
  });
});

test("readiness does not swallow unrelated timeout or connection failures", async () => {
  for (const error of [new CdpTimeoutError("Runtime.enable"), new Error("Chrome DevTools connection closed.")]) {
    await expect(waitForDefaultExecutionContext({ send: async () => { throw error; } }, 1_000, pageUrl)).rejects.toBe(error);
  }
});

test("a CDP socket closed between probes fails immediately as a disconnect", async () => {
  await withCdpServer((_request, reply, close) => {
    reply({ result: {} });
    setTimeout(close, 10);
  }, async client => {
    await client.send("Runtime.enable");
    await Bun.sleep(100);
    await expect(waitForDefaultExecutionContext(client, 300, pageUrl)).rejects.toThrow("Chrome DevTools connection closed.");
  });
});

test("a responsive loading document still observes the total readiness deadline", async () => {
  await withCdpServer((_request, reply) => reply({ result: { result: { value: "loading" } } }), async client => {
    const started = performance.now();
    await expect(waitForDefaultExecutionContext(client, 150, pageUrl)).rejects.toThrow("Timed out waiting for the browser document execution context.");
    expect(performance.now() - started).toBeLessThan(350);
  });
});

test("readiness does not accept complete about:blank before the target document commits", async () => {
  let probes = 0;
  await withCdpServer((request, reply) => {
    probes++;
    if (probes === 2) { reply({ error: { message: "Cannot find default execution context" } }); return; }
    const url = probes === 1 ? "about:blank" : pageUrl;
    const readyState = probes === 3 ? "loading" : "complete";
    reply({ result: { result: { value: evaluateDocument(request, url, readyState) } } });
  }, async client => {
    await waitForDefaultExecutionContext(client, 1_000, pageUrl);
    expect(probes).toBe(4);
  });
});

test("readiness accepts interactive and complete at the exact target URL", async () => {
  for (const readyState of ["interactive", "complete"]) {
    // Quotes in a requested URL must be represented as data in the JS probe.
    const target = `${pageUrl}?label="quoted"`;
    await withCdpServer((request, reply) => {
      reply({ result: { result: { value: evaluateDocument(request, target, readyState) } } });
    }, async client => { await waitForDefaultExecutionContext(client, 1_000, target); });
  }
});

test("readiness refuses other complete URLs including target-prefix matches", async () => {
  for (const url of ["about:blank", `${pageUrl}/extra`, `${pageUrl}?other=1`, `${pageUrl}#other`, "http://127.0.0.1:8124/index.html"]) {
    await withCdpServer((request, reply) => {
      reply({ result: { result: { value: evaluateDocument(request, url, "complete") } } });
    }, async client => {
      await expect(waitForDefaultExecutionContext(client, 100, pageUrl)).rejects.toThrow("Timed out waiting for the browser document execution context.");
    });
  }
});

test("delayed navigation retains the original overall readiness deadline", async () => {
  const started = performance.now();
  await withCdpServer((request, reply) => {
    const url = performance.now() - started < 200 ? "about:blank" : pageUrl;
    reply({ result: { result: { value: evaluateDocument(request, url, "complete") } } });
  }, async client => {
    await expect(waitForDefaultExecutionContext(client, 150, pageUrl)).rejects.toThrow("Timed out waiting for the browser document execution context.");
    expect(performance.now() - started).toBeLessThan(350);
  });
});
