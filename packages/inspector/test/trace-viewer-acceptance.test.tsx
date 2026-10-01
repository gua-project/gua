import { describe, expect, test } from "bun:test";
import { renderToStaticMarkup } from "react-dom/server";
import { GuaTraceViewer } from "../src/TraceViewer";
import { parseTrace, pendingRequests, readTraceFiles, traceScreenshot } from "../src/trace";

export const png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1cAAAAASUVORK5CYII=";
const id = "a".repeat(32), step = "b".repeat(32);
const manifest = { schemaVersion: 1, traceId: id, captureMode: "recent", savePolicy: "always", profile: "player", primaryOutcome: "passed", finalized: true, collectionClock: "test", startedAt: "test", lastSequence: 0, quality: { detailStopped: false, evictedSteps: 0, droppedEvents: 0, issues: [] } };
const event = (sequence: number, type: string, data: unknown) => ({ schemaVersion: 1 as const, traceId: id, stepId: step, sequence, eventId: `e${sequence}`, type, collectedMilliseconds: sequence, data });
const lines = (...events: unknown[]) => events.map(e => JSON.stringify(e)).join("\n") + "\n";
const screenshot = { schemaVersion: 1, profile: "player", pixelPolicy: "caller-authorized", observationId: "o1", screenshot: { dataUri: png, width: 1, height: 1 } };

describe("Trace Viewer acceptance contracts", () => {
  test("PNG only: external, SVG, policy/profile mismatch, size and IHDR mismatch are unavailable", () => {
    expect(traceScreenshot(screenshot, "player")).not.toBeNull();
    for (const value of [
      { ...screenshot, profile: "debug" }, { ...screenshot, pixelPolicy: "implicit" }, { ...screenshot, observationId: "" },
      ...["https://attacker.invalid", "file:///C:/private", "data:image/svg+xml,<svg/>", "data:image/png;base64,AAAA"].map(dataUri => ({ ...screenshot, screenshot: { ...screenshot.screenshot, dataUri } })),
      { ...screenshot, screenshot: { ...screenshot.screenshot, width: 2 } },
      { ...screenshot, screenshot: { ...screenshot.screenshot, width: 8192, height: 8192 } },
    ]) expect(traceScreenshot(value, "player")).toBeNull();
  });
  test("pending identity distinguishes sources/epochs, host completion and late completion", () => {
    const request = { sourceId: "native:game:input:1", sessionEpoch: "1", requestId: "7" };
    const events = [event(1, "request.phase", { request, phase: "enqueue" }),
      event(2, "request.phase", { request: { ...request, sessionEpoch: "2" }, phase: "completion" }),
      event(3, "request.phase", { request: { ...request, sourceId: "native:other:input:1" }, phase: "completion" })];
    expect(pendingRequests(events)).toHaveLength(1);
    expect(pendingRequests([...events, event(4, "request.phase", { request, phase: "late-completion" })])).toEqual([]);
    expect(pendingRequests([event(1, "request.enqueue", { sourceId: "client", requestId: "7", accepted: true }),
      event(2, "request.completion", { sourceId: "client", sessionEpoch: "1", requestId: "7", succeeded: false })])).toEqual([]);
  });
  test("passed primary and failed step never convert false assertion truth into true", () => {
    const trace = parseTrace(JSON.stringify({ ...manifest, lastSequence: 3 }), lines(event(1, "step.begin", { label: "truth", source: { file: "Test.cs", line: 42 } }),
      event(2, "assertion.evaluation", { truth: "false", role: "goal", callerOutcome: "passed" }), event(3, "step.end", { outcome: "failed" })));
    const html = renderToStaticMarkup(<GuaTraceViewer trace={trace} />);
    expect(html).toContain("Assertion truth: <strong>false</strong>"); expect(html).toContain("caller execution result: <strong>passed</strong>");
    expect(html).toContain("Step result: <strong>failed</strong>"); expect(html).toContain("Test.cs:42");
  });
  test("stale snapshots with blobs are not treated as verified differences", () => {
    const paths = ["snapshots/" + "1".repeat(64) + ".json", "snapshots/" + "2".repeat(64) + ".json"];
    const events = paths.map((blob, i) => event(i + 1, "observation", { observationId: `o${i}`, channel: "ui", host: { sourceId: "game", sessionEpoch: "1" }, reason: `r${i}`, availability: i ? "stale" : "available", blob }));
    const trace = parseTrace(JSON.stringify({ ...manifest, lastSequence: 2 }), lines(...events), { [paths[0]!]: { x: 1 }, [paths[1]!]: { x: 2 } });
    expect(renderToStaticMarkup(<GuaTraceViewer trace={trace} />)).not.toContain('"path":');
  });
  test("no retained steps and truncated detail are visibly unverified even with passed primary", () => {
    for (const quality of [manifest.quality, { ...manifest.quality, detailStopped: true }]) {
      const html = renderToStaticMarkup(<GuaTraceViewer trace={parseTrace(JSON.stringify({ ...manifest, quality }), "")} />);
      expect(html).toContain("Recording is incomplete or unverified"); expect(html).toContain("No steps captured");
    }
  });
  test("browser directory reader never substitutes nested/outside-root blob with matching basename", async () => {
    const bytes = new TextEncoder().encode('{"secret":"OUTSIDE-ROOT"}');
    const hash = [...new Uint8Array(await crypto.subtle.digest("SHA-256", bytes))].map(b => b.toString(16).padStart(2, "0")).join("");
    const blob = `attachments/${hash}.json`;
    const file = (content: BlobPart, name: string, relative: string) => {
      const f = new File([content], name); Object.defineProperty(f, "webkitRelativePath", { value: relative }); return f;
    };
    const manifestFile = file(JSON.stringify({ ...manifest, lastSequence: 1 }), "manifest.json", "trace/manifest.json");
    const eventsFile = file(lines(event(1, "attachment", { schema: "future", blob })), "events.jsonl", "trace/events.jsonl");
    const nested = file(bytes, `${hash}.json`, `trace/extra/${blob}`);
    const read = await readTraceFiles([manifestFile, eventsFile, nested]);
    expect(read.issues).toContain("blob-unavailable"); expect(Object.keys(read.blobs)).toHaveLength(0);
    await expect(readTraceFiles([manifestFile, file("", "events.jsonl", "other/events.jsonl")])).rejects.toThrow("Mixed Trace roots");
    const valid = file(bytes, `${hash}.json`, `trace/${blob}`);
    await expect(readTraceFiles([manifestFile, eventsFile, valid, valid])).rejects.toThrow("Duplicate Trace blob");
  });
});
