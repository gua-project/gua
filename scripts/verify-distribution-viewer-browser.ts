import { readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { createCdpClient, waitForDefaultExecutionContext } from "./godot-web-cdp";

const output = resolve(process.argv[2]!);
const port = Number(process.argv[3]!);
const report = resolve(output, "archive-run/report.html");
const url = pathToFileURL(report).href;
const target = await (await fetch(`http://127.0.0.1:${port}/json/new?about:blank`, { method: "PUT" })).json() as { webSocketDebuggerUrl: string };
const client = createCdpClient(target.webSocketDebuggerUrl);
await client.open();
try {
  await client.send("Page.enable");
  await client.send("Runtime.enable");
  await client.send("Page.addScriptToEvaluateOnNewDocument", { source: `globalThis.__guaErrors=[]; addEventListener('error',e=>__guaErrors.push(e.message)); addEventListener('unhandledrejection',e=>__guaErrors.push(String(e.reason)));` });
  await client.send("Page.navigate", { url });
  await waitForDefaultExecutionContext(client, 10000, url);
  const deadline = performance.now() + 10000;
  let evidence: any;
  do {
    const result = await client.send("Runtime.evaluate", { returnByValue: true, expression: `({ rendered: !!document.querySelector('.gua-trace'), text: document.body.innerText, steps: document.querySelectorAll('nav[aria-label="Trace timeline"] button').length, errors: globalThis.__guaErrors, resources: performance.getEntriesByType('resource').map(e=>e.name) })` }) as { result: { value: any }; exceptionDetails?: unknown };
    if (result.exceptionDetails) throw new Error("Viewer browser evaluation failed");
    evidence = result.result.value;
    if (evidence.rendered) break;
    await Bun.sleep(50);
  } while (performance.now() < deadline);
  if (!evidence?.rendered || !evidence.text.includes("Primary result: passed") || evidence.steps !== 1 || evidence.errors.length || evidence.resources.some((u: string) => /^https?:/.test(u)))
    throw new Error("Packaged offline Viewer failed: " + JSON.stringify(evidence));
  const metadata = JSON.parse(await readFile(resolve(output, "archive-run/viewer-version.json"), "utf8"));
  const screenshot = await client.send("Page.captureScreenshot", { format: "png" }) as { data: string };
  await writeFile(resolve(output, "viewer.png"), Buffer.from(screenshot.data, "base64"));
  await writeFile(resolve(output, "viewer-browser.json"), JSON.stringify({ sourceCommit: metadata.sourceCommit, scriptSha256: metadata.scriptSha256, ...evidence }, null, 2));
  console.log("Packaged offline Viewer rendered without external resources.");
} finally { client.close(); }
