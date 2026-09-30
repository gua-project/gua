export interface TraceEvent {
  schemaVersion: 1; traceId: string; sequence: number; eventId: string; stepId: string;
  type: string; collectedMilliseconds: number; data: unknown;
}
export interface TraceManifest {
  schemaVersion: 1; traceId: string; captureMode: "recent" | "streaming";
  savePolicy: "onFailure" | "always"; profile: "debug" | "player";
  primaryOutcome: "unknown" | "passed" | "failed" | "interrupted"; finalized: boolean;
  collectionClock: string; startedAt: string; lastSequence: number;
  quality: { detailStopped: boolean; evictedSteps: number; droppedEvents: number; issues: string[] };
}
export interface TraceDocument {
  manifest: TraceManifest; events: TraceEvent[]; blobs: Record<string, unknown>; issues: string[];
}
export const blobPattern = /^(snapshots|attachments)\/[a-f0-9]{64}\.json$/;
export function object(value: unknown): Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : {};
}
export function text(value: unknown, fallback = "unconfirmed"): string {
  return typeof value === "string" ? value : fallback;
}
export function parseTrace(manifestText: string, eventLines: string, blobs: Record<string, unknown> = {}): TraceDocument {
  const utf8 = new TextEncoder();
  if (manifestText.length > 65536 || eventLines.length > 256 * 1024 * 1024 ||
      utf8.encode(manifestText).byteLength > 65536 || utf8.encode(eventLines).byteLength > 256 * 1024 * 1024) throw new Error("Trace reader limit");
  const m = object(JSON.parse(manifestText)), quality = object(m.quality);
  if (m.schemaVersion !== 1 || !/^[a-f0-9]{32}$/.test(text(m.traceId, "")) ||
      !["recent", "streaming"].includes(text(m.captureMode)) || !["onFailure", "always"].includes(text(m.savePolicy)) ||
      !["debug", "player"].includes(text(m.profile)) || !["unknown", "passed", "failed", "interrupted"].includes(text(m.primaryOutcome)) ||
      typeof m.finalized !== "boolean" || typeof m.collectionClock !== "string" || typeof m.startedAt !== "string" ||
      !Number.isSafeInteger(m.lastSequence) || Number(m.lastSequence) < 0 || typeof quality.detailStopped !== "boolean" ||
      !Number.isSafeInteger(quality.evictedSteps) || Number(quality.evictedSteps) < 0 || !Number.isSafeInteger(quality.droppedEvents) || Number(quality.droppedEvents) < 0 ||
      !Array.isArray(quality.issues) || quality.issues.length > 64 || !quality.issues.every(x => typeof x === "string")) throw new Error("Unsupported or invalid Trace manifest");
  const manifest = m as unknown as TraceManifest;
  const events: TraceEvent[] = [], issues: string[] = manifest.finalized ? [] : ["unfinalized"];
  let previous = 0, previousCollected = 0;
  const lines = eventLines.split("\n");
  const tail = lines.pop();
  for (const line of lines) {
    if (line.length > 4 * 1024 * 1024 || utf8.encode(line).byteLength > 4 * 1024 * 1024 || events.length >= 100000) { issues.push("reader-limit"); break; }
    try {
      const e = object(JSON.parse(line));
      if (e.schemaVersion !== 1 || e.traceId !== manifest.traceId || !Number.isSafeInteger(e.sequence) ||
          Number(e.sequence) <= previous || typeof e.eventId !== "string" || e.eventId.length === 0 || !/^[a-f0-9]{32}$/.test(text(e.stepId, "")) ||
          typeof e.type !== "string" || e.type.length < 1 || e.type.length > 64 || !Object.hasOwn(e, "data") || typeof e.collectedMilliseconds !== "number" ||
          !Number.isFinite(e.collectedMilliseconds) || e.collectedMilliseconds < previousCollected) throw new Error("invalid-record");
      if ((manifest.captureMode === "streaming" || manifest.quality.evictedSteps === 0) && Number(e.sequence) !== previous + 1) issues.push("sequence-gap");
      previous = Number(e.sequence); previousCollected = e.collectedMilliseconds; events.push(e as unknown as TraceEvent);
    } catch { issues.push("invalid-record"); break; }
  }
  if (tail) issues.push("incomplete-tail");
  if (manifest.finalized && previous !== manifest.lastSequence) issues.push("sequence-incomplete");
  const safeBlobs: Record<string, unknown> = Object.create(null);
  const observations = new Set(events.filter(e => e.type === "observation").map(e => object(e.data).observationId));
  const steps = new Set(events.filter(e => e.type === "step.begin").map(e => e.stepId));
  for (const e of events) {
    const availability = object(e.data).availability;
    if (e.type === "observation" && typeof availability === "string" && ["gap", "stale", "failed", "partial", "outsideRetention"].includes(availability))
      issues.push(`observation-${availability}`);
    const parent = object(e.data).parentStepId;
    if (e.type === "step.begin" && parent != null && (typeof parent !== "string" || !steps.has(parent))) issues.push("step-outside-retention");
    const refs = object(e.data).observations;
    if (e.type === "assertion.evaluation" && Array.isArray(refs) && refs.some(r => typeof r !== "string" || !observations.has(r))) issues.push("observation-outside-retention");
    if (e.type !== "observation" && e.type !== "attachment") continue;
    const path = object(e.data).blob;
    if (path == null) continue;
    if (typeof path !== "string" || !blobPattern.test(path) || !Object.hasOwn(blobs, path)) { issues.push("blob-unavailable"); continue; }
    safeBlobs[path] = blobs[path];
  }
  return { manifest, events, blobs: safeBlobs, issues: [...new Set(issues)] };
}

/** Read only the files explicitly selected by the user. Never fetch a trace-provided path or URL. */
export async function readTraceFiles(files: File[]): Promise<TraceDocument> {
  if (files.reduce((n, f) => n + f.size, 0) > 256 * 1024 * 1024 + 65536 || files.length > 10000)
    throw new Error("Trace reader limit");
  const manifest = files.filter(f => f.name === "manifest.json"), events = files.filter(f => f.name === "events.jsonl");
  if (manifest.length !== 1 || events.length !== 1) throw new Error("Select one Trace directory with manifest.json and events.jsonl");
  const blobs: Record<string, unknown> = Object.create(null);
  for (const file of files) {
    const relative = (file.webkitRelativePath || file.name).split("/").slice(-2).join("/");
    if (!blobPattern.test(relative)) continue;
    if (file.size > 4 * 1024 * 1024) continue;
    const bytes = await file.arrayBuffer();
    const hash = [...new Uint8Array(await crypto.subtle.digest("SHA-256", bytes))].map(b => b.toString(16).padStart(2, "0")).join("");
    if (!relative.endsWith(`/${hash}.json`)) continue;
    try { blobs[relative] = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)); } catch { /* reported missing */ }
  }
  const manifestText = new TextDecoder("utf-8", { fatal: true }).decode(await manifest[0]!.arrayBuffer());
  const eventBytes = new Uint8Array(await events[0]!.arrayBuffer());
  const decoded: string[] = [];
  let start = 0, invalid = false, limited = false;
  for (let i = 0; i < eventBytes.length; i++) {
    if (i - start > 4 * 1024 * 1024 || decoded.length >= 100000) { limited = true; break; }
    if (eventBytes[i] !== 10) continue;
    try { decoded.push(new TextDecoder("utf-8", { fatal: true }).decode(eventBytes.subarray(start, i)) + "\n"); }
    catch { invalid = true; break; }
    start = i + 1;
  }
  // Do not decode the unfinished suffix; no complete record was committed there.
  const trace = parseTrace(manifestText, decoded.join("") + (!invalid && !limited && start < eventBytes.length ? "{" : ""), blobs);
  if (invalid) trace.issues.push("invalid-record");
  if (limited) trace.issues.push("reader-limit");
  return trace;
}

export function traceSteps(trace: TraceDocument): { id: string; label: string; kind: string; outcome: string; duration: number | null; events: TraceEvent[] }[] {
  const groups = new Map<string, TraceEvent[]>();
  for (const event of trace.events) {
    const events = groups.get(event.stepId) ?? []; events.push(event); groups.set(event.stepId, events);
  }
  return [...groups].map(([id, events]) => {
    const begin = events.find(e => e.type === "step.begin"), end = events.find(e => e.type === "step.end");
    return { id, label: text(object(begin?.data).label, "Step outside retained range"), kind: text(object(begin?.data).kind),
      outcome: text(object(end?.data).outcome), duration: begin && end ? end.collectedMilliseconds - begin.collectedMilliseconds : null, events };
  });
}

/** Structural difference; explicitly does not claim causality or continuity. Position/bounds are retained. */
export function snapshotDiff(before: unknown, after: unknown): { path: string; before?: unknown; after?: unknown }[] {
  const differences: { path: string; before?: unknown; after?: unknown }[] = [];
  function visit(a: unknown, b: unknown, path: string, depth: number) {
    if (JSON.stringify(a) === JSON.stringify(b)) return;
    if (differences.length >= 1000) return;
    if (depth < 16 && a && b && typeof a === "object" && typeof b === "object" && !Array.isArray(a) && !Array.isArray(b)) {
      const aa = object(a), bb = object(b);
      for (const key of new Set([...Object.keys(aa), ...Object.keys(bb)])) visit(aa[key], bb[key], `${path}/${key}`, depth + 1);
    } else differences.push({ path, ...(a === undefined ? {} : { before: a }), ...(b === undefined ? {} : { after: b }) });
  }
  visit(before, after, "", 0); return differences;
}
