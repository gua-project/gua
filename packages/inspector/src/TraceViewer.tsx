import { useMemo, useRef, useState } from "react";
import { confirmedEpoch, indexObservations, object, pendingRequests, readTraceFiles, snapshotDiff, text, traceScreenshot, traceSteps, type TraceDocument, type TraceEvent } from "./trace";

const pretty = (value: unknown) => JSON.stringify(value, null, 2);
const preStyle = { whiteSpace: "pre-wrap", overflowWrap: "anywhere" } as const;
const boolean = (value: unknown) => value === true ? "true" : value === false ? "false" : "unconfirmed";
function Json({ value }: { value: unknown }) { return <pre style={preStyle}>{pretty(value)}</pre>; }

export function GuaTraceViewer({ trace }: { trace: TraceDocument }) {
  const [selected, select] = useState<string | null>(null);
  const [filter, setFilter] = useState("");
  // One explicit selection owns the aggregate 16,777,216-pixel decode budget.
  const [screenshot, showScreenshot] = useState<number | null>(null);
  const observationIndex = useMemo(() => indexObservations(trace.events), [trace]);
  const steps = traceSteps(trace), visible = steps.filter(s => `${s.label} ${s.kind} ${s.outcome} ${s.id}`.toLowerCase().includes(filter.toLowerCase()));
  const step = visible.find(s => s.id === selected) ?? visible[0];
  const issues = [...new Set([...trace.issues, ...trace.manifest.quality.issues])];
  const qualityLabels = [...issues, ...(!steps.length ? ["no retained steps"] : []),
    ...(trace.manifest.quality.detailStopped ? ["detail collection stopped"] : []),
    ...(trace.manifest.quality.droppedEvents > 0 ? ["events dropped"] : [])];
  const observations = step?.events.filter(e => e.type === "observation") ?? [];
  const pending = step ? pendingRequests(step.events) : [];
  return <section className="gua-trace" aria-label="Gua Trace Viewer" style={{ padding: 20, color: "#e7eaf1", background: "#121926", fontFamily: "system-ui", borderRadius: 12 }}>
    <h1>Gua Trace <small>v1</small></h1>
    <p>Primary result: <strong>{trace.manifest.primaryOutcome}</strong> · Trace: {trace.manifest.finalized ? "finalized" : "unfinalized"} · Profile: {trace.manifest.profile}</p>
    <p>{trace.manifest.captureMode} / {trace.manifest.savePolicy} · {steps.length} retained steps · {trace.manifest.quality.evictedSteps} steps outside correlation window</p>
    <p>Collection clock: {trace.manifest.collectionClock}. Host clocks are separate; clock alignment is unverified.</p>
    <details><summary>Manifest and additional metadata</summary><Json value={trace.manifest} /></details>
    {(issues.length > 0 || trace.manifest.quality.detailStopped || trace.manifest.quality.droppedEvents > 0 || !steps.length) && <div role="alert" style={{ background: "#55341c", padding: 12 }}>
      Recording is incomplete or unverified: {qualityLabels.join(", ")} · {trace.manifest.quality.droppedEvents} dropped events
    </div>}
    <label>Filter steps <input value={filter} onChange={e => setFilter(e.target.value)} /></label> <span>{visible.length} of {steps.length} steps</span>
    <div style={{ display: "grid", gridTemplateColumns: "minmax(180px, 1fr) minmax(0, 3fr)", gap: 20, marginTop: 16 }}>
      <nav aria-label="Trace timeline" style={{ maxHeight: 720, overflow: "auto" }}>
        {visible.map(s => <button key={s.id} onClick={() => { select(s.id); showScreenshot(null); }} aria-pressed={step?.id === s.id} style={{ display: "block", width: "100%", textAlign: "left", padding: 12, marginBottom: 8,
          background: step?.id === s.id ? "#30476c" : "#202b3c", color: s.outcome === "failed" ? "#ffb4a6" : "#e7eaf1", border: "1px solid #536178", borderRadius: 6 }}>
          {s.label}<br /><small>{s.kind} · {s.outcome} · {s.duration === null ? "duration unconfirmed" : `${s.duration.toFixed(1)} ms`}</small><br /><small>Step {s.id}</small>
        </button>)}
      </nav>
      <article>
        <h2>{step?.label ?? (steps.length ? "No matching steps" : "No steps captured")}</h2>
        {step && <>
          <p>Step result: <strong>{step.outcome}</strong>. Enqueue acceptance is not host completion. Host completion does not establish expected game state. Timeout does not cancel side effects.</p>
          <p>Pending requests (completion unconfirmed): {pending.length ? pending.join("; ") : "none recorded"}. Missing correlation is never proof of completion.</p>
          {step.events.map(e => <Event key={e.sequence} event={e} trace={trace} observationIndex={observationIndex}
            showImage={screenshot === e.sequence} toggleImage={() => showScreenshot(screenshot === e.sequence ? null : e.sequence)} />)}
          {observations.length > 1 && <details><summary>Observation interval differences</summary>
            <p>Differences are associated with this interval, not proof of causality. At most 1000 differences per pair are shown. Intermediate continuity is only as recorded.</p>
            {observations.map((after, i) => {
              const target = object(after.data), host = object(target.host);
              if (![target.channel, host.sourceId].every(value => typeof value === "string" && value.length > 0) || !confirmedEpoch(host.sessionEpoch)) return null;
              const before = observations.slice(0, i).reverse().find(e => {
                const candidate = object(e.data), candidateHost = object(candidate.host);
                return candidate.channel === target.channel && candidateHost.sourceId === host.sourceId && candidateHost.sessionEpoch === host.sessionEpoch;
              });
              if (!before) return null;
              const a = text(object(before.data).blob, ""), b = text(target.blob, "");
              const available = object(before.data).availability === "available" && target.availability === "available";
              return <div key={after.eventId}><h4>{text(object(before.data).reason)} → {text(target.reason)}</h4>
                {available && Object.hasOwn(trace.blobs, a) && Object.hasOwn(trace.blobs, b)
                  ? <Json value={snapshotDiff(trace.blobs[a], trace.blobs[b])} />
                  : <p>Snapshot unavailable or unverified; differences cannot be established.</p>}</div>;
            })}
          </details>}
        </>}
      </article>
    </div>
  </section>;
}

function Event({ event, trace, observationIndex, showImage, toggleImage }: { event: TraceEvent; trace: TraceDocument;
  observationIndex: ReturnType<typeof indexObservations>; showImage: boolean; toggleImage: () => void }) {
  const data = object(event.data), blob = text(data.blob, ""), result = object(data.result), source = object(data.source);
  return <details open={["request.phase", "assertion.evaluation", "observation", "observation.change", "attachment", "step.begin"].includes(event.type)} style={{ padding: "8px 0", borderBottom: "1px solid #364157" }}>
    <summary>{event.collectedMilliseconds.toFixed(1)} ms · {event.type}{data.phase ? ` · ${text(data.phase)}` : ""}</summary>
    {event.type === "step.begin" && <p>Source: {text(source.file)}{typeof source.line === "number" ? `:${source.line}` : ""}</p>}
    {event.type === "request.phase" && <p>Native phase: <strong>{text(data.phase)}</strong> · host clock: {text(result.hostClockId)} · host elapsed: {typeof result.hostElapsedMilliseconds === "number" ? `${result.hostElapsedMilliseconds} ms` : "unconfirmed"}</p>}
    {event.type === "request.enqueue" && <p>Request accepted: {boolean(data.accepted)} · host completion: {text(data.hostCompletion)}</p>}
    {event.type === "request.completion" && <p>Host completion succeeded: {boolean(data.succeeded)} · expected state: {text(data.expectedState)}</p>}
    {event.type === "assertion.evaluation" && <p>Assertion truth: <strong>{text(data.truth)}</strong> · role: {text(data.role)} · caller execution result: <strong>{text(data.callerOutcome)}</strong></p>}
    {event.type === "observation" && <p>Snapshot: {text(data.channel)} · {text(data.reason)} · availability: <strong>{text(data.availability)}</strong> · continuity: {text(data.continuity)}</p>}
    {event.type === "observation.change" && <p>Intermediate change: {text(data.change)} · channel: {text(data.channel, "property")} · continuity: {text(data.continuity)}</p>}
    {event.type === "attachment" && <p>Attachment schema: {text(data.schema)}. References to lint, comparison and Recording are inert metadata; no external content is loaded.</p>}
    <Json value={event.data} />
    <details><summary>Full event envelope</summary><Json value={event} /></details>
    {blob && (Object.hasOwn(trace.blobs, blob) ? <>
      {event.type === "attachment" && data.schema === "gua.trace.screenshot.v1" && <>
        <button aria-pressed={showImage} onClick={toggleImage}>{showImage ? "Hide screenshot" : "Show screenshot"}</button>
        <p>Pixels decode only on selection. At most one screenshot is mounted, within a total 16,777,216-pixel budget.</p>
        {showImage && <Screenshot key={blob} value={trace.blobs[blob]} trace={trace} observationIndex={observationIndex} />}
      </>}
      <details><summary>{event.type === "observation" ? "Snapshot content" : "Attachment content"}</summary><Json value={trace.blobs[blob]} /></details>
    </> : <p role="alert">Blob unavailable or outside retained range.</p>)}
    {event.type === "observation" && !blob && <p>Snapshot content not captured; availability is {text(data.availability)}.</p>}
  </details>;
}

function Screenshot({ value, trace, observationIndex }: { value: unknown; trace: TraceDocument; observationIndex: ReturnType<typeof indexObservations> }) {
  const [selected, select] = useState("");
  const [decoded, setDecoded] = useState(false), [failed, setFailed] = useState(false);
  const image = traceScreenshot(value, trace.manifest.profile);
  if (!image) return <p role="alert">Screenshot unavailable: unsupported pixels, dimensions, profile or pixel policy.</p>;
  const observation = observationIndex.get(image.observationId), d = object(observation?.data), host = object(d.host);
  const snapshot = object(trace.blobs[text(d.blob, "")]);
  const nodes = d.channel === "ui" && d.availability === "available" && text(host.sourceId, "") && confirmedEpoch(host.sessionEpoch) && Array.isArray(snapshot.nodes)
    ? snapshot.nodes.map(object).filter(n => typeof n.id === "string") : [];
  const node = nodes.find(n => n.id === selected), bounds = object(node?.bounds);
  const validBounds = [bounds.x, bounds.y, bounds.w, bounds.h].every(n => typeof n === "number" && Number.isFinite(n)) && Number(bounds.w) >= 0 && Number(bounds.h) >= 0;
  return <figure style={{ margin: "12px 0" }}>
    <figcaption>Caller-authorized screenshot · {image.width} × {image.height} · Observation {image.observationId}</figcaption>
    <label>Overlay node <select value={selected} onChange={e => select(e.target.value)}><option value="">No node selected</option>{nodes.map(n => <option key={text(n.id)} value={text(n.id)}>{text(n.id)} · {text(n.label, text(n.role))}</option>)}</select></label>
    {!nodes.length && <p>Matching UI snapshot unavailable or unverified; bounds overlay unavailable.</p>}
    {node && !validBounds && <p role="alert">Selected node bounds unavailable or unverified.</p>}
    <div style={{ position: "relative", width: image.width, maxWidth: "100%", overflow: "hidden" }}>
      {!failed && <img src={image.dataUri} alt="Recorded caller-authorized game screenshot" width={image.width} height={image.height} style={{ display: "block", width: "100%", height: "auto" }} onLoad={() => setDecoded(true)} onError={() => { setDecoded(false); setFailed(true); }} />}
      {failed && <p role="alert">Screenshot decode failed; pixels unavailable.</p>}
      {decoded && node && validBounds && <div aria-label={`Bounds overlay for ${selected}`} style={{ position: "absolute", boxSizing: "border-box", pointerEvents: "none", border: "2px solid #ffdd55", left: `${Number(bounds.x) / image.width * 100}%`, top: `${Number(bounds.y) / image.height * 100}%`, width: `${Number(bounds.w) / image.width * 100}%`, height: `${Number(bounds.h) / image.height * 100}%` }} />}
    </div>
  </figure>;
}

export function GuaTraceFileViewer({ initial }: { initial?: TraceDocument }) {
  const [trace, setTrace] = useState(initial), [error, setError] = useState(""), [loading, setLoading] = useState(false);
  const generation = useRef(0);
  return <div><label>Open Trace directory <input type="file" multiple {...{ webkitdirectory: "" }} onChange={async e => {
    const files = Array.from(e.target.files ?? []);
    if (!files.length) return;
    const current = ++generation.current;
    setLoading(true); setTrace(undefined); setError("");
    try { const next = await readTraceFiles(files); if (current === generation.current) { setTrace(next); setError(""); } }
    catch { if (current === generation.current) { setTrace(undefined); setError("Trace could not be read. Select one supported Trace directory within the reader limits."); } }
    finally { if (current === generation.current) setLoading(false); }
  }} /></label>{error && <p role="alert">{error}</p>}{loading ? <p role="status">Reading Trace…</p> : trace ? <GuaTraceViewer key={`${trace.manifest.traceId}:${generation.current}`} trace={trace} /> : <p>No Trace loaded.</p>}</div>;
}
