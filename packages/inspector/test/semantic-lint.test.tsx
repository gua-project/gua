import { test, expect } from "bun:test";
import { renderToStaticMarkup } from "react-dom/server";
import { SemanticLintFindings, SemanticLintPanel } from "../src/SemanticLintPanel";
import { filterLintFindings, lintReportIsCurrent, parseSemanticLintReport } from "../src/semanticLint";
import fixture from "../../../protocol/fixtures/semantic-lint-v1.json";
import { WebSocketInspectorClient } from "../src/core";
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
test("Inspector requests host lint explicitly and validates native reports", async () => {
  const commands: Record<string, unknown>[] = [];
  let corrupt = false, unsupported = false;
  const server = Bun.serve({ port: 0, fetch(request, server) { return server.upgrade(request) ? undefined : new Response(null, {status:400}); },
    websocket: { message(ws, data) {
      const command = JSON.parse(String(data)); commands.push(command);
      ws.send(JSON.stringify(unsupported ? {id:command.id,ok:false,error:"semantic_lint is not supported by this bridge"} :
        {id:command.id,ok:true,result:corrupt ? {...fixture.report,schemaVersion:2} : {...fixture.report,profile:"player"}}));
    } }
  });
  const client = new WebSocketInspectorClient(`ws://127.0.0.1:${server.port}`);
  try {
    expect(commands.length).toBe(0);
    expect((await client.analyzeSemanticLint()).profile).toBe("player");
    expect(commands[0]).toEqual({id:1,type:"semantic_lint",includeWorld:true});
    await client.analyzeSemanticLint(false);
    expect(commands[1]?.includeWorld).toBe(false);
    expect(commands.every(command => !("profile" in command) && !("uiTree" in command))).toBe(true);
    corrupt = true; await expect(client.analyzeSemanticLint()).rejects.toThrow("Unsupported semantic lint report");
    unsupported = true; await expect(client.analyzeSemanticLint()).rejects.toThrow("not supported");
  } finally { client.close(); server.stop(true); }
});
