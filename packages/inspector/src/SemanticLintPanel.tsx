import { useEffect, useRef, useState } from "react";
import { filterLintFindings, lintReportIsCurrent, type LintSeverity, type LintTreeMetadata, type SemanticLintReport } from "./semanticLint";

export interface SemanticLintClient { analyzeSemanticLint?: (includeWorld?: boolean) => Promise<SemanticLintReport>; }
export function SemanticLintFindings({ report, severity = "all" }: { report: SemanticLintReport; severity?: LintSeverity | "all" }) {
  const findings = filterLintFindings(report, severity);
  return <>
    <p>{report.profile} · epoch {report.uiTree.sessionEpoch} · UI frame {report.uiTree.frameSequence} / revision {report.uiTree.revision}
      {report.worldObjectTree && <> · World frame {report.worldObjectTree.frameSequence} / revision {report.worldObjectTree.revision}</>}</p>
    <p>{report.summary.error} errors · {report.summary.warning} warnings · {report.summary.info} info</p>
    {findings.length === 0 ? <p>{report.summary.total === 0 ? "No findings in this published snapshot." : "No findings match this severity."}</p> :
      <table className="gua-detail"><thead><tr><th>Severity / Rule</th><th>Target / Path</th><th>Reason / Repair</th></tr></thead>
        <tbody>{findings.map((finding, index) => <tr key={index}>
          <td>{finding.severity} / {finding.ruleId}</td><td>{finding.targetKind} / {finding.targetId}<br />{finding.path}</td><td>{finding.message}</td>
        </tr>)}</tbody></table>}
  </>;
}
export function SemanticLintPanel({ client, ui, world }: { client: SemanticLintClient;
  ui: Pick<LintTreeMetadata, "sessionEpoch" | "frameSequence" | "revision">;
  world?: Pick<LintTreeMetadata, "sessionEpoch" | "frameSequence" | "revision"> }) {
  const [report, setReport] = useState<SemanticLintReport | null>(null);
  const [severity, setSeverity] = useState<LintSeverity | "all">("all");
  const [pending, setPending] = useState(false), [error, setError] = useState("");
  const generation = useRef(0);
  useEffect(() => {
    ++generation.current; setReport(null); setError(""); setPending(false);
    return () => { ++generation.current; };
  }, [client]);
  const run = async () => {
    const request = ++generation.current; setPending(true); setError(""); setReport(null);
    try {
      if (!client.analyzeSemanticLint) throw new Error("Semantic lint is unavailable on this connection.");
      const result = await client.analyzeSemanticLint(true);
      if (request === generation.current) setReport(result);
    } catch { if (request === generation.current) setError("Semantic lint failed or is unsupported. Connect to a runtime with semantic_lint_v1 and retry."); }
    finally { if (request === generation.current) setPending(false); }
  };
  return <section className="gua-panel gua-lint-panel">
    <header><h2>Semantic lint</h2><button disabled={pending || !client.analyzeSemanticLint} onClick={() => void run()}>{pending ? "Linting…" : "Run lint"}</button>
      <label>Severity <select value={severity} onChange={event => setSeverity(event.target.value as LintSeverity | "all")}>
        <option value="all">All</option><option value="error">Error</option><option value="warning">Warning</option><option value="info">Info</option>
      </select></label></header>
    {error && <p role="alert">{error}</p>}
    {!report && !error && <p>{client.analyzeSemanticLint ? "Run lint to inspect the runtime's published UI and World snapshots." : "Semantic lint is unavailable on this connection. Connect to a runtime with semantic_lint_v1."}</p>}
    {report && <>{!lintReportIsCurrent(report, ui, world) && <p>Snapshot changed since this report. Run lint again to inspect the current snapshots.</p>}
      <SemanticLintFindings report={report} severity={severity} /></>}
  </section>;
}
