import { describe, expect, test } from "bun:test";
import { DemoRuntime, handleMessage } from "../src/index";

describe("DemoRuntime virtual clock", () => {
  test("both metadata discovery commands return correlated unsupported errors", () => {
    for (const type of ["get_game_input_actions_v2", "find_game_input_actions_v2"]) {
      expect(handleMessage(JSON.stringify({ id: 41, type }), new DemoRuntime(() => 0)))
        .toEqual({ id: 41, ok: false, error: "unsupported game input metadata" });
    }
  });
  test("maps the flat search wire fields without treating correlation id as an action id", () => {
    const runtime = new DemoRuntime(() => 0);
    const all = handleMessage(JSON.stringify({ id: 9, type: "find_game_input_actions", limit: 20 }), runtime);
    expect(all).toMatchObject({ id: 9, ok: true, result: { count: 2, truncated: false } });

    const exact = handleMessage(JSON.stringify({ id: 10, type: "find_game_input_actions", actionId: "jump", valueType: 1, active: 2, limit: 1 }), runtime);
    expect(exact).toMatchObject({ id: 10, ok: true, result: { count: 1, actions: [{ id: "jump" }] } });

    for (const invalid of [
      { actionId: "Invalid" }, { limit: 0 }, { tags: ["same", "same"] }, { query: "before\0after" },
    ]) {
      expect(handleMessage(JSON.stringify({ id: 11, type: "find_game_input_actions", ...invalid }), runtime))
        .toEqual({ id: 11, ok: false, error: "invalid game input selector" });
    }
  });

  test("rejects installation until the current timeline is reset", () => {
    const runtime = new DemoRuntime(() => 0);
    expect(runtime.installClock(100, 10).nowMs).toBe(100);

    expect(() => runtime.installClock(0, 5)).toThrow("invalid_state");
    expect(runtime.getClock().nowMs).toBe(100);
    expect(runtime.getClock().defaultStepMs).toBe(10);
  });

  test("rejects durations that would rewind or overflow the timeline", () => {
    const runtime = new DemoRuntime(() => 0);
    runtime.installClock(100, 10);
    runtime.pauseClock();

    expect(() => runtime.runClockFor(-25)).toThrow("invalid_duration");
    expect(() => runtime.runClockFor(Number.POSITIVE_INFINITY)).toThrow("invalid_duration");
    expect(runtime.getClock().nowMs).toBe(100);
  });

  test("rejects steps and runs that cannot advance a large timeline", () => {
    const runtime = new DemoRuntime(() => 0);
    expect(() => runtime.installClock(1e16, 1)).toThrow("invalid_duration");

    runtime.installClock(1e16, 2);
    runtime.pauseClock();
    expect(() => runtime.runClockFor(1)).toThrow("invalid_duration");
    expect(() => runtime.runClockFor(100, 1)).toThrow("invalid_duration");
    expect(() => runtime.runClockFor(0, 1)).toThrow("invalid_duration");
    expect(runtime.getClock().nowMs).toBe(1e16);
  });

  test("advances the completion frame without inventing a semantic revision", async () => {
    const runtime = new DemoRuntime(() => 0);
    runtime.installClock(0, 10);
    runtime.pauseClock();
    const before = runtime.getUiTree();
    const operation = runtime.runClockFor(10);

    await new Promise((resolve) => setTimeout(resolve, 0));

    const after = runtime.getUiTree();
    expect(after.frameSequence).toBe(before.frameSequence + 1);
    expect(after.revision).toBe(before.revision);
    expect(runtime.getClock().completedOperationSequence).toBe(operation.operationSequence);
    const response = handleMessage(JSON.stringify({ id: 9, type: "get_context_status" }), runtime);
    expect(response.ok).toBe(true);
    if (response.ok) {
      const status = response.result as { sessionEpoch: number; frameSequence: number; revision: number };
      expect(status.sessionEpoch).toBe(1);
      expect(status.frameSequence).toBe(after.frameSequence);
      expect(status.revision).toBe(after.revision);
    }
  });

  test("advances running time from a monotonic source and freezes while paused", () => {
    let monotonicNow = 1_000;
    const runtime = new DemoRuntime(() => monotonicNow);
    runtime.installClock(100, 10);

    monotonicNow += 25;
    expect(runtime.getClock().nowMs).toBe(125);
    runtime.pauseClock();
    monotonicNow += 50;
    expect(runtime.getClock().nowMs).toBe(125);

    runtime.resumeClock();
    monotonicNow += 10;
    expect(runtime.getClock().nowMs).toBe(135);
  });
});
