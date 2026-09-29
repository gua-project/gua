import { useState } from "react";
import { object, readTraceFiles, snapshotDiff, text, traceSteps, type TraceDocument, type TraceEvent } from "./trace";

const pretty = (value: unknown) => JSON.stringify(value, null, 2);
export function GuaTraceViewer({ trace }: { trace: TraceDocument }) {
  const [selected, select] = useState<string | null>(null);
  const [filter, setFilter] = useState("");
  const steps = traceSteps(trace), step = steps.find(s => s.id === selected) ?? steps[0];
  const issues = [...trace.issues, ...trace.manifest.quality.issues];
  const observations = step?.events.filter(e => e.type === "observation") ?? [];
  return <section className="gua-trace" aria-label="Gua Trace Viewer" style={{ padding: 20, color: "#e7eaf1", background: "#121926", fontFamily: "system-ui", borderRadius: 12 }}>
    <h1>Gua Trace <small>v1</small></h1>
    <p>Primary result: <strong>{trace.manifest.primaryOutcome}</strong> · Trace: {trace.manifest.finalized ? "finalized" : "unfinalized"} · Profile: {trace.manifest.profile}</p>
    <p>{trace.manifest.captureMode} / {trace.manifest.savePolicy} · {steps.length} retained steps · {trace.manifest.quality.evictedSteps} steps outside correlation window</p>
    <p>Collection clock: {trace.manifest.collectionClock}. Host clocks are separate; clock alignment is unverified.</p>
    <details><summary>Manifest and additional metadata</summary><pre style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>{pretty(trace.manifest)}</pre></details>
    {(issues.length > 0 || trace.manifest.quality.detailStopped || trace.manifest.quality.droppedEvents > 0) && <div role="alert" style={{ background: "#55341c", padding: 12 }}>
      Recording is incomplete: {issues.join(", ")} · {trace.manifest.quality.droppedEvents} dropped events
    </div>}
    <label>Filter steps <input value={filter} onChange={e => setFilter(e.target.value)} /></label>
    <div style={{ display: "grid", gridTemplateColumns: "minmax(180px, 1fr) minmax(0, 3fr)", gap: 20, marginTop: 16 }}>
      <nav aria-label="Trace timeline" style={{ maxHeight: 720, overflow: "auto" }}>
        {steps.filter(s => s.label.toLowerCase().includes(filter.toLowerCase())).map(s => <button key={s.id} onClick={() => select(s.id)}
          aria-pressed={step?.id === s.id} style={{ display: "block", width: "100%", textAlign: "left", padding: 12, marginBottom: 8,
            background: step?.id === s.id ? "#30476c" : "#202b3c", color: s.outcome === "failed" ? "#ffb4a6" : "#e7eaf1", border: "1px solid #536178", borderRadius: 6 }}>
          {s.label}<br /><small>{s.kind} · {s.outcome} · {s.duration === null ? "duration unconfirmed" : `${s.duration.toFixed(1)} ms`}</small>
        </button>)}
      </nav>
      <article>
        <h2>{step?.label ?? "No steps captured"}</h2>
        <p>Enqueue acceptance is not host completion. Host completion does not establish expected game state. Timeout does not cancel side effects.</p>
        {step?.events.map(e => <Event key={e.eventId} event={e} trace={trace} />)}
        {observations.length > 1 && <details><summary>Observation interval differences</summary>
          <p>Differences are associated with this interval, not proof of causality. At most 1000 differences per pair are shown. Intermediate continuity is only as recorded.</p>
          {observations.slice(1).map((after, i) => {
            const before = observations[i]!, a = text(object(before.data).blob, ""), b = text(object(after.data).blob, "");
            return <div key={after.eventId}><h4>{text(object(before.data).reason)} → {text(object(after.data).reason)}</h4>
              {Object.hasOwn(trace.blobs, a) && Object.hasOwn(trace.blobs, b)
                ? <pre style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>{pretty(snapshotDiff(trace.blobs[a], trace.blobs[b]))}</pre>
                : <p>Snapshot unavailable; differences cannot be established.</p>}</div>;
          })}
        </details>}
      </article>
    </div>
  </section>;
}
function Event({ event, trace }: { event: TraceEvent; trace: TraceDocument }) {
  const data = object(event.data), blob = text(data.blob, "");
  return <details open={event.type === "request.phase" || event.type === "assertion.evaluation"} style={{ padding: "8px 0", borderBottom: "1px solid #364157" }}>
    <summary>{event.collectedMilliseconds.toFixed(1)} ms · {event.type}{data.phase ? ` · ${text(data.phase)}` : ""}</summary>
    <pre style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>{pretty(event.data)}</pre>
    <details><summary>Full event envelope</summary><pre style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>{pretty(event)}</pre></details>
    {blob && (Object.hasOwn(trace.blobs, blob) ? <pre style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>{pretty(trace.blobs[blob])}</pre> : <p role="alert">Blob unavailable or outside retained range.</p>)}
  </details>;
}
export function GuaTraceFileViewer({ initial }: { initial?: TraceDocument }) {
  const [trace, setTrace] = useState(initial), [error, setError] = useState("");
  return <div><label>Open Trace directory <input type="file" multiple {...{ webkitdirectory: "" }} onChange={async e => {
    try { const next = await readTraceFiles(Array.from(e.target.files ?? [])); setTrace(next); setError(""); }
    catch { setTrace(undefined); setError("Trace could not be read. Select one supported Trace directory within the reader limits."); }
  }} /></label>{error && <p role="alert">{error}</p>}{trace ? <GuaTraceViewer trace={trace} /> : <p>No Trace loaded.</p>}</div>;
}
