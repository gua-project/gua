export async function waitForDefaultExecutionContext(client: Pick<ReturnType<typeof createCdpClient>, "send">, timeoutMs: number) {
  const deadline = performance.now() + timeoutMs;
  while (performance.now() < deadline) {
    try {
      const response = await client.send("Runtime.evaluate", {expression: "document.readyState", returnByValue: true},
        Math.max(1, Math.min(1_000, deadline - performance.now()))) as {result?: {value?: string}};
      if (response.result?.value === "interactive" || response.result?.value === "complete") return;
    } catch (error) {
      const probeTimedOut = error instanceof CdpTimeoutError && error.method === "Runtime.evaluate";
      const contextChanging = error instanceof Error &&
        ["Cannot find default execution context", "Execution context was destroyed."].includes(error.message);
      if (!probeTimedOut && !contextChanging) throw error;
    }
    const remainingMs = deadline - performance.now();
    if (remainingMs > 0) await Bun.sleep(Math.min(25, remainingMs));
  }
  throw new Error("Timed out waiting for the browser document execution context.");
}

export class CdpTimeoutError extends Error {
  constructor(readonly method: string) {
    super(`Timed out waiting for Chrome DevTools method ${method}.`);
    this.name = "CdpTimeoutError";
  }
}

export function createCdpClient(url: string) {
  const socket = new WebSocket(url);
  let nextId = 1;
  let terminalError: Error | undefined;
  const pending = new Map<number, {
    method: string;
    started: number;
    resolve(value: unknown): void;
    reject(error: Error): void;
    timer: ReturnType<typeof setTimeout>;
  }>();
  socket.addEventListener("message", (event) => {
      const message = JSON.parse(String(event.data)) as { id?: number; result?: unknown; error?: { message: string } };
      if (message.id === undefined) return;
      const call = pending.get(message.id);
      if (!call) return;
      pending.delete(message.id);
      clearTimeout(call.timer);
      console.log(`Chrome DevTools response ${message.id}: ${call.method}, elapsed=${Math.round(performance.now() - call.started)}ms, error=${message.error?.message ?? "none"}`);
      if (message.error) call.reject(new Error(message.error.message));
      else call.resolve(message.result);
  });
  const failConnection = (error: Error) => {
    terminalError ??= error;
    for (const call of pending.values()) {
      clearTimeout(call.timer);
      call.reject(terminalError);
    }
    pending.clear();
  };
  socket.addEventListener("close", () => failConnection(new Error("Chrome DevTools connection closed.")));
  socket.addEventListener("error", () => failConnection(new Error("Chrome DevTools connection failed.")));
  return {
    open(timeoutMs = 10_000): Promise<void> {
      if (terminalError) return Promise.reject(terminalError);
      if (socket.readyState === WebSocket.OPEN) return Promise.resolve();
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error("Timed out connecting to Chrome DevTools.")), timeoutMs);
        socket.addEventListener("open", () => { clearTimeout(timer); resolve(); }, { once: true });
        socket.addEventListener("error", () => { clearTimeout(timer); reject(new Error("Could not connect to Chrome DevTools.")); }, { once: true });
      });
    },
    send(method: string, params: Record<string, unknown> = {}, timeoutMs = 10_000): Promise<unknown> {
      if (terminalError) return Promise.reject(terminalError);
      if (socket.readyState !== WebSocket.OPEN) return Promise.reject(new Error("Chrome DevTools connection is not open."));
      const id = nextId++;
      const started = performance.now();
      console.log(`Chrome DevTools request ${id}: ${method}, timeout=${timeoutMs}ms`);
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
          pending.delete(id);
          console.error(`Chrome DevTools request ${id}: ${method} timed out after ${Math.round(performance.now() - started)}ms`);
          reject(new CdpTimeoutError(method));
        }, timeoutMs);
        pending.set(id, { resolve, reject, timer, method, started });
        try {
          socket.send(JSON.stringify({ id, method, params }));
        } catch (error) {
          clearTimeout(timer);
          pending.delete(id);
          reject(error instanceof Error ? error : new Error(`Could not send Chrome DevTools method ${method}.`));
        }
      });
    },
    close(): void {
      socket.close();
    },
  };
}
