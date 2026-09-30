import { useEffect, useState } from "react";
import type { GuaInspectorClient, ObserveTransport } from "./core";

export function ObservePanel({ client }: { client: GuaInspectorClient }) {
  const [snapshot, setSnapshot] = useState<ObserveTransport | null>(null);
  const [changes, setChanges] = useState<ObserveTransport | null>(null);
  const [status, setStatus] = useState("Connecting");
  const [generation, setGeneration] = useState(0);
  useEffect(() => {
    let disposed = false, subscription = 0;
    let timer: ReturnType<typeof setTimeout> | undefined;
    setSnapshot(null); setChanges(null); setStatus("Connecting");
    const poll = async () => {
      try {
        const changes = await client.pollObservations!(subscription);
        if (disposed) return;
        setChanges(changes);
        if (changes.document.kind !== "changes") throw new Error("Unexpected Observe response.");
        setStatus(changes.document.status);
        if (changes.document.status !== "ok") { setSnapshot(null); return; }
        const current = await client.getObserveSnapshot!();
        if (disposed) return;
        setSnapshot(current);
        timer = setTimeout(poll, 500);
      } catch { if (!disposed) { setStatus("Observation connection failed; resubscribe."); setSnapshot(null); } }
    };
    void (async () => {
      if (!client.subscribeObservations || !client.pollObservations || !client.unsubscribeObservations || !client.getObserveSnapshot) {
        setStatus("observe_v1 unsupported"); return;
      }
      try {
        const initial = await client.subscribeObservations(); subscription = initial.subscriptionId;
        if (disposed) { await client.unsubscribeObservations(subscription); return; }
        setSnapshot(initial.snapshot); setStatus("ok"); await poll();
      } catch { if (!disposed) setStatus("observe_v1 unavailable; resubscribe after connecting."); }
    })();
    return () => { disposed = true; if (timer) clearTimeout(timer); if (subscription) void client.unsubscribeObservations?.(subscription).catch(() => undefined); };
  }, [client, generation]);
  const document = snapshot?.document;
  return <section className="gua-panel gua-world-panel">
    <header><h2>Additional observations</h2><span>{status}</span> <button onClick={() => setGeneration(n => n + 1)}>Resubscribe</button></header>
    {document && <p>{document.profile} · epoch {document.sessionEpoch} · UI frame {document.uiFrame} · World frame {document.worldFrame} · revision {document.revision}</p>}
    {document?.kind === "snapshot" && <table className="gua-detail"><thead><tr><th>Owner / Runtime ID</th><th>Name</th><th>Value / availability</th></tr></thead><tbody>
      {document.entries.map(entry => <tr key={entry.registrationId}><td>{entry.source} / {entry.runtimeId || "World"} ({entry.ownerId})</td><td>{entry.name}</td>
        <td>{entry.status === "available" ? JSON.stringify(entry.value) : `unavailable (${entry.error})`}</td></tr>)}
    </tbody></table>}
    {changes?.document.kind === "changes" && changes.document.status !== "ok" && <p>Continuity lost: {changes.document.status}. Resubscribe to read current values; missed history cannot be recovered.</p>}
    {snapshot && <details><summary>Type definitions and latest changes</summary><pre>{JSON.stringify({ catalogs: snapshot.catalogs, changes }, null, 2)}</pre></details>}
  </section>;
}
