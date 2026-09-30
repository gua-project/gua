import { describe, test, expect } from "bun:test";
import { renderToStaticMarkup } from "react-dom/server";
import { GuaTraceViewer } from "../src/TraceViewer";
import { parseTrace, readTraceFiles, snapshotDiff, traceSteps } from "../src/trace";

const traceId = "a".repeat(32), stepId = "b".repeat(32);
const manifest = { schemaVersion: 1, traceId, captureMode: "recent", savePolicy: "always", profile: "player", primaryOutcome: "failed",
  finalized: true, collectionClock: "collector:a", startedAt: "2026-09-29T00:00:00Z", lastSequence: 2,
  quality: { detailStopped: false, evictedSteps: 0, droppedEvents: 0, issues: [] } };
function event(sequence: number, type: string, data: unknown) { return { schemaVersion: 1, traceId, sequence, eventId: `e${sequence}`, stepId, type, collectedMilliseconds: sequence, data }; }
const lines = (...events: unknown[]) => events.map(e => JSON.stringify(e)).join("\n") + "\n";
describe("Trace v1 offline reader/viewer", () => {
  test("missing complete streaming record is reported even when the tail is intact", () => {
    const trace = parseTrace(JSON.stringify({ ...manifest, captureMode: "streaming", lastSequence: 3 }),
      lines(event(1, "step.begin", {}), event(3, "step.end", { outcome: "passed" })));
    expect(trace.events.length).toBe(2); expect(trace.issues).toContain("sequence-gap");
    expect(renderToStaticMarkup(<GuaTraceViewer trace={trace} />)).toContain("Recording is incomplete");
  });
  test("recent eviction is a normal range change but missing parent is explicit", () => {
    const trace = parseTrace(JSON.stringify({ ...manifest, lastSequence: 4, quality: { ...manifest.quality, evictedSteps: 1 } }),
      lines(event(3, "step.begin", { parentStepId: "c".repeat(32) }), event(4, "step.end", { outcome: "passed" })));
    expect(trace.issues).toContain("step-outside-retention"); expect(trace.issues).not.toContain("sequence-gap");
    const retained = parseTrace(JSON.stringify({ ...manifest, lastSequence: 4, quality: { ...manifest.quality, evictedSteps: 1 } }),
      lines(event(3, "step.begin", {}), event(4, "step.end", { outcome: "passed" })));
    expect(retained.issues).toEqual([]);
  });
  test("unknown annotations and hostile HTML remain inert text", () => {
    const hostile = '</script><img src="https://attacker.invalid/x" onerror="alert(1)">';
    const trace = parseTrace(JSON.stringify(manifest), lines(event(1, "step.begin", { kind: "assertion", label: hostile }),
      event(2, "annotation", { name: "gua-playtest.unknown", value: { type: "string", value: hostile } })));
    const html = renderToStaticMarkup(<GuaTraceViewer trace={trace} />);
    expect(html).toContain("gua-playtest.unknown"); expect(html).not.toContain("<img"); expect(html).not.toContain("<script");
    expect(html).toContain("&lt;/script&gt;"); expect(html).toContain("unconfirmed");
  });
  test("unfinalized and incomplete tail are independent of caller passed", () => {
    const trace = parseTrace(JSON.stringify({ ...manifest, primaryOutcome: "passed", finalized: false }), lines(event(1, "step.begin", { kind: "mark", label: "hello" })) + '{"cut":');
    expect(trace.events.length).toBe(1); expect(trace.issues).toContain("unfinalized"); expect(trace.issues).toContain("incomplete-tail");
    expect(renderToStaticMarkup(<GuaTraceViewer trace={trace} />)).toContain("Recording is incomplete");
  });
  test("traversal and external URLs are unavailable, never links", () => {
    for (const blob of ["../../secret", "https://attacker.invalid/file", "file:///C:/private"]) {
      const trace = parseTrace(JSON.stringify(manifest), lines(event(1, "step.begin", { label: "x" }), event(2, "attachment", { blob })));
      expect(trace.issues).toContain("blob-unavailable");
      expect(renderToStaticMarkup(<GuaTraceViewer trace={trace} />)).not.toContain('href=');
    }
  });
  test("late phases do not change confirmed duration or primary result", () => {
    const trace = parseTrace(JSON.stringify({ ...manifest, lastSequence: 3 }), lines(event(1, "step.begin", { kind: "action", label: "hold" }),
      event(2, "step.end", { outcome: "unknown" }), event(3, "request.phase", { phase: "late-completion" })));
    expect(traceSteps(trace)[0]?.outcome).toBe("unknown"); expect(traceSteps(trace)[0]?.duration).toBe(1); expect(trace.manifest.primaryOutcome).toBe("failed");
  });
  test("position differences and absence are preserved", () => {
    expect(snapshotDiff({ position: { x: 1 }, state: null }, { position: { x: 2 } })).toEqual([
      { path: "/position/x", before: 1, after: 2 }, { path: "/state", before: null },
    ]);
  });
  test("future versions and invalid envelopes are not complete traces", () => {
    expect(() => parseTrace(JSON.stringify({ ...manifest, schemaVersion: 2 }), "")).toThrow();
    const trace = parseTrace(JSON.stringify(manifest), lines(event(2, "mark", {}), event(1, "mark", {})));
    expect(trace.issues).toContain("invalid-record"); expect(trace.events.length).toBe(1);
  });
  test("missing data is rejected", () => {
    const { data, ...incomplete } = event(1, "mark", {});
    expect(parseTrace(JSON.stringify(manifest), lines(incomplete)).issues).toContain("invalid-record");
  });
  test("file reader rejects invalid UTF-8", async () => {
    const encoded = new TextEncoder().encode(lines(event(1, "mark", { text: "marker" })));
    encoded[new TextDecoder().decode(encoded).indexOf("marker")] = 255;
    const files = [new File([JSON.stringify(manifest)], "manifest.json"), new File([encoded], "events.jsonl")];
    const result = await readTraceFiles(files);
    expect(result.issues).toContain("invalid-record");
  });
  test("unknown envelope fields are displayed generically", () => {
    const trace = parseTrace(JSON.stringify({ ...manifest, futureManifest: "manifest-marker" }),
      lines({ ...event(1, "mark", {}), futureEvent: "event-marker" }));
    const html = renderToStaticMarkup(<GuaTraceViewer trace={trace} />);
    expect(html).toContain("manifest-marker"); expect(html).toContain("event-marker");
  });
  test("reversed collector timestamps do not become negative durations", () => {
    const trace = parseTrace(JSON.stringify(manifest), lines({ ...event(1, "step.begin", {}), collectedMilliseconds: 10 },
      { ...event(2, "step.end", {}), collectedMilliseconds: 5 }));
    expect(trace.issues).toContain("invalid-record"); expect(trace.events.length).toBe(1);
  });
  test("record limit counts UTF-8 bytes rather than UTF-16 characters", async () => {
    const source = lines(event(1, "mark", { text: "あ".repeat(1500000) }));
    expect(parseTrace(JSON.stringify(manifest), source).issues).toContain("reader-limit");
    const fromFiles = await readTraceFiles([new File([JSON.stringify(manifest)], "manifest.json"), new File([source], "events.jsonl")]);
    expect(fromFiles.issues).toContain("reader-limit");
  });
});
