import { test, expect } from "bun:test";
import { renderToStaticMarkup } from "react-dom/server";
import { SemanticLintFindings, SemanticLintPanel } from "../src/SemanticLintPanel";
import { filterLintFindings, lintReportIsCurrent, parseSemanticLintReport } from "../src/semanticLint";
import fixture from "../../../protocol/fixtures/semantic-lint-v1.json";
test("versioned lint reports render metadata and severity filters", () => {
  const report = parseSemanticLintReport({ ...fixture.report, findings: [
    { ruleId: "missing-accessible-name", severity: "warning", targetKind: "ui", targetId: "blank", path: "$.uiTree.nodes[0].label", message: "Provide a label." },
    { ruleId: "missing-parent", severity: "error", targetKind: "world", targetId: "orphan", path: "$.worldObjectTree.objects[0].parentId", message: "Repair parent." }
  ], summary: { error: 1, warning: 1, info: 0, total: 2 } });
  expect(filterLintFindings(report, "error").length).toBe(1);
  const markup = renderToStaticMarkup(<SemanticLintFindings report={report} severity="error" />);
  expect(markup).toContain("orphan"); expect(markup).not.toContain("blank"); expect(markup).toContain("epoch 1");
  expect(lintReportIsCurrent(report, report.uiTree, report.worldObjectTree!)).toBe(true);
  expect(lintReportIsCurrent(report, { ...report.uiTree, sessionEpoch: 2 }, report.worldObjectTree!)).toBe(false);
  expect(lintReportIsCurrent(report, report.uiTree, { ...report.worldObjectTree!, revision: 8 })).toBe(false);
});
test("consumer rejects corrupt summaries and unsupported versions", () => {
  expect(() => parseSemanticLintReport({ ...fixture.report, schemaVersion: 2 })).toThrow();
  expect(() => parseSemanticLintReport({ ...fixture.report, summary: { ...fixture.report.summary, total: 1 } })).toThrow();
});
test("panel never executes lint while rendering", () => {
  let calls = 0;
  const report = parseSemanticLintReport(fixture.report);
  const markup = renderToStaticMarkup(<SemanticLintPanel client={{ analyzeSemanticLint: async () => { ++calls; return report; } }} ui={report.uiTree} />);
  expect(calls).toBe(0); expect(markup).toContain("Run lint"); expect(markup).toContain("Severity");
});
