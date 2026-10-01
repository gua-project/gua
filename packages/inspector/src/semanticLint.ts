export type LintSeverity = "error" | "warning" | "info";
export interface LintTreeMetadata {
  schemaVersion: number; sessionEpoch: number; frameSequence: number; revision: number;
  screen?: string; scene?: string;
}
export interface LintFinding {
  ruleId: string; severity: LintSeverity; message: string;
  targetKind: "ui" | "world"; targetId: string; path: string;
}
export interface SemanticLintReport {
  schemaVersion: 1; profile: "debug" | "player";
  uiTree: LintTreeMetadata; worldObjectTree: LintTreeMetadata | null;
  summary: { error: number; warning: number; info: number; total: number };
  findings: LintFinding[];
}
const object = (value: unknown): value is Record<string, unknown> => !!value && typeof value === "object" && !Array.isArray(value);
const count = (value: unknown): value is number => typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
function metadata(value: unknown, world: boolean): value is LintTreeMetadata {
  return object(value) && value.schemaVersion === (world ? 1 : 2) && count(value.sessionEpoch) && value.sessionEpoch > 0 &&
    count(value.frameSequence) && count(value.revision) && typeof value[world ? "scene" : "screen"] === "string";
}
export function parseSemanticLintReport(value: unknown): SemanticLintReport {
  if (!object(value) || value.schemaVersion !== 1 || (value.profile !== "debug" && value.profile !== "player") ||
      !metadata(value.uiTree, false) || (value.worldObjectTree !== null && !metadata(value.worldObjectTree, true)) ||
      !object(value.summary) || !Array.isArray(value.findings)) throw new Error("Unsupported semantic lint report.");
  const counts = { error: 0, warning: 0, info: 0 };
  for (const finding of value.findings) {
    if (!object(finding) || (finding.severity !== "error" && finding.severity !== "warning" && finding.severity !== "info") ||
        (finding.targetKind !== "ui" && finding.targetKind !== "world") ||
        !["ruleId", "message", "targetId", "path"].every(key => typeof finding[key] === "string" && (finding[key] as string).length > 0) ||
        !(finding.path as string).startsWith(finding.targetKind === "ui" ? "$.uiTree.nodes[" : "$.worldObjectTree.objects["))
      throw new Error("Invalid semantic lint finding.");
    counts[finding.severity]++;
  }
  const summary = value.summary;
  if (!count(summary.total) || summary.total !== value.findings.length ||
      !["error", "warning", "info"].every(key => count(summary[key]) && summary[key] === counts[key as LintSeverity]))
    throw new Error("Invalid semantic lint summary.");
  return value as unknown as SemanticLintReport;
}
export function filterLintFindings(report: SemanticLintReport, severity: LintSeverity | "all"): LintFinding[] {
  return severity === "all" ? report.findings : report.findings.filter(f => f.severity === severity);
}
export function lintReportIsCurrent(report: SemanticLintReport, ui: Pick<LintTreeMetadata, "sessionEpoch" | "frameSequence" | "revision">,
  world?: Pick<LintTreeMetadata, "sessionEpoch" | "frameSequence" | "revision">): boolean {
  const equal = (a: typeof ui, b: typeof ui) => a.sessionEpoch === b.sessionEpoch && a.frameSequence === b.frameSequence && a.revision === b.revision;
  return equal(report.uiTree, ui) && (!report.worldObjectTree || (!!world && equal(report.worldObjectTree, world)));
}
