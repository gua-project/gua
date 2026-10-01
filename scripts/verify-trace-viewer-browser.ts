import { readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { createCdpClient } from "./godot-web-cdp";

const root = resolve(import.meta.dir, "..");
const fixturesRoot = resolve(root, process.argv[2] ?? "artifacts/trace-viewer-qa");
const port = process.argv[3] ?? "9337";
const inspectorUrl = process.argv[4] ?? "http://127.0.0.1:5173";
const fixtures = JSON.parse(await readFile(resolve(fixturesRoot, "fixtures.json"), "utf8")) as { outcome: string; directory: string; report: string }[];
const target = await (await fetch(`http://127.0.0.1:${port}/json/new?about:blank`, { method: "PUT" })).json() as { webSocketDebuggerUrl: string };
const client = createCdpClient(target.webSocketDebuggerUrl);
await client.open();
const observed = new WebSocket(target.webSocketDebuggerUrl);
await new Promise<void>((resolve, reject) => { observed.addEventListener("open", () => resolve(), { once: true }); observed.addEventListener("error", reject, { once: true }); });
const external: string[] = [], exceptions: string[] = [];
observed.addEventListener("message", e => {
  const m = JSON.parse(String(e.data));
  if (m.method === "Network.requestWillBeSent" && /^(https?:|file:)/.test(m.params.request.url) && !m.params.request.url.startsWith(inspectorUrl) && !m.params.request.url.startsWith("file:///" + root.replaceAll("\\", "/"))) external.push(m.params.request.url);
  if (m.method === "Runtime.exceptionThrown") exceptions.push(m.params.exceptionDetails.text);
  if (m.method === "Page.javascriptDialogOpening") exceptions.push("unexpected dialog");
});
for (const [i, method] of ["Network.enable", "Runtime.enable", "Page.enable"].entries()) observed.send(JSON.stringify({ id: i + 1, method }));
async function evaluate(expression: string) {
  const r = await client.send("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true }) as { result?: { value?: any }; exceptionDetails?: unknown };
  if (r.exceptionDetails) throw new Error("Browser evaluation failed: " + JSON.stringify(r.exceptionDetails));
  return r.result?.value;
}
function require(value: unknown, reason: string) { if (!value) throw new Error(reason); }
async function wait(expression: string) {
  const deadline = performance.now() + 10000;
  while (performance.now() < deadline) { if (await evaluate(expression)) return; await Bun.sleep(50); }
  throw new Error("Browser condition timed out: " + expression);
}
async function navigate(url: string) { await client.send("Page.navigate", { url }); await wait(`location.href===${JSON.stringify(new URL(url).href)} && !!document.querySelector('input[type=file]')`); }
async function filter(value: string) {
  await evaluate(`(() => {const input = document.querySelector('.gua-trace input'); Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,${JSON.stringify(value)}); input.dispatchEvent(new Event('input',{bubbles:true})); })()`);
  await Bun.sleep(80);
}
async function openDetails() { await evaluate("document.querySelectorAll('.gua-trace article details').forEach(d=>d.open=true)"); }
async function select(index: number) { await evaluate(`document.querySelectorAll('nav[aria-label="Trace timeline"] button')[${index}].click()`); await Bun.sleep(80); }
async function loadDirectory(path: string) {
  const manifest = JSON.parse(await readFile(resolve(path, "manifest.json"), "utf8"));
  const dom = await client.send("DOM.getDocument") as { root: { nodeId: number } };
  const input = await client.send("DOM.querySelector", { nodeId: dom.root.nodeId, selector: "input[type=file][webkitdirectory]" }) as { nodeId: number };
  await client.send("DOM.setFileInputFiles", { nodeId: input.nodeId, files: [path] });
  await wait(`!!document.querySelector('.gua-trace') && document.querySelector('.gua-trace').textContent.includes(${JSON.stringify(manifest.traceId)})`);
}
async function screenshot(name: string) {
  const shot = await client.send("Page.captureScreenshot", { format: "png" }) as { data: string };
  await writeFile(resolve(fixturesRoot, name + ".png"), Buffer.from(shot.data, "base64"));
}
const evidence: Record<string, unknown>[] = [];
try {
  await client.send("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
  for (const fixture of fixtures) {
    await navigate(pathToFileURL(fixture.report).href);
    await wait("!!document.querySelector('.gua-trace')");
    require(await evaluate(`document.body.innerText.includes('Primary result: ${fixture.outcome}')`), "static primary outcome");
    require(await evaluate("document.querySelectorAll('nav[aria-label=\"Trace timeline\"] button').length===3"), "three retained steps");
    await select(0); await openDetails();
    require(await evaluate("document.body.innerText.includes('Assertion truth: false') && document.body.innerText.includes('external.future.goal') && document.body.innerText.includes('recording-fixture') && !document.body.innerText.includes('SECRET-MARKER') && !document.body.innerText.includes('C:/private/Fixture.cs')"), "generic/truth/redaction display");
    require(await evaluate("document.body.innerText.includes('hold-pending') && document.body.innerText.includes('hold-started') && document.body.innerText.includes('release-requested') && document.body.innerText.includes('release-confirmed') && document.body.innerText.includes('late-completion')"), "distinct hold/release/late phases");
    require(await evaluate("document.body.innerText.includes('First') && document.body.innerText.includes('Second') && document.body.innerText.includes('Third') && document.body.innerText.includes('before → result-decision')"), "intermediate/difference UI");
    require(await evaluate("!window.__hostileExecuted && !document.querySelector('img[src^=http],a[href^=http]')"), "inert hostile data");
    // Collapse bulky JSON before visual QA; retain the recorded image and its selector.
    await evaluate("document.querySelectorAll('article pre').forEach(p=>p.style.display='none');document.querySelector('article').querySelectorAll('details').forEach(d=>{if(d.querySelector('figure'))d.open=true})");
    await evaluate("(() => { const s=document.querySelector('figure select'); s.value='player';s.dispatchEvent(new Event('change',{bubbles:true})); })()");
    await wait("!!document.querySelector('[aria-label=\"Bounds overlay for player\"]')");
    require(await evaluate("(() => {const img=document.querySelector('figure img'), b=document.querySelector('[aria-label=\"Bounds overlay for player\"]'); const r=img.getBoundingClientRect(), o=b.getBoundingClientRect();return img.complete&&img.naturalWidth===640&&Math.abs((o.left-r.left)/r.width-0.25)<0.01&&Math.abs(o.width/r.width-0.1875)<0.01})()"), "scaled pixel overlay");
    await evaluate("(() => { const s=document.querySelector('figure select'); s.value='unknown-bounds';s.dispatchEvent(new Event('change',{bubbles:true})); })()");
    await wait("document.body.innerText.includes('Selected node bounds unavailable or unverified')");
    require(await evaluate("!document.querySelector('[aria-label^=\"Bounds overlay\"]')"), "partial bounds never create guessed overlay");
    await evaluate("(() => { const s=document.querySelector('figure select'); s.value='player';s.dispatchEvent(new Event('change',{bubbles:true})); })()");
    await client.send("Emulation.setDeviceMetricsOverride", { width: 680, height: 900, deviceScaleFactor: 1, mobile: false });
    require(await evaluate("(() => {const img=document.querySelector('figure img'), b=document.querySelector('[aria-label=\"Bounds overlay for player\"]'); const r=img.getBoundingClientRect(), o=b.getBoundingClientRect();return r.width<640 && Math.abs((o.top-r.top)/r.height-60/360)<0.01 && Math.abs(o.width/r.width-120/640)<0.01})()"), "responsive pixel overlay");
    await client.send("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
    await evaluate("document.querySelector('figure').scrollIntoView({block:'center'})");
    await screenshot("static-" + fixture.outcome);
    await select(1); await openDetails();
    require(await evaluate("document.querySelector('article').innerText.includes('request 8') && !document.querySelector('article').innerText.includes('late-completion')"), "repeated label retains separate request");
    await filter("cleanup"); require(await evaluate("document.querySelectorAll('nav button').length===1 && document.querySelector('article h2').innerText==='Runner cleanup'"), "filtered selection follows visible step");
    await filter("no-match"); require(await evaluate("document.querySelector('article').innerText==='No matching steps'"), "empty filter hides old detail");
    await filter(""); await select(2); await openDetails();
    require(await evaluate(`document.body.innerText.includes('Primary result: ${fixture.outcome}') && document.querySelector('article').innerText.includes('after-cleanup') && document.querySelector('article').innerText.includes('Step result: failed')`), "cleanup doesn't overwrite primary");
    require(await evaluate("document.querySelector('article').innerText.includes('Screenshot unavailable:') && document.querySelector('article').innerText.includes('bounds overlay unavailable') && ['failed','partial','stale','gap','outsideRetention'].every(s=>document.querySelector('article').innerText.includes('missing-'+s)) && !document.querySelector('img[src^=http]')"), "missing observations and malicious screenshot never become complete records");
    evidence.push({ surface: "static .NET report", outcome: fixture.outcome, checks: "timeline, repeated actions, filtering, truth/result, phases, differences, overlay, unknown records, inert hostile data, cleanup" });
  }
  await navigate(pathToFileURL(resolve(fixturesRoot, "missing.html")).href); await wait("!!document.querySelector('.gua-trace')");
  require(await evaluate("document.body.innerText.includes('Recording is incomplete or unverified') && document.body.innerText.includes('incomplete-tail') && document.body.innerText.includes('No steps captured')"), "missing trace is unverified");
  await screenshot("static-missing");
  // The shipped standalone picker works with generated files alone, including error/recovery.
  await navigate(pathToFileURL(resolve(root, "artifacts/trace-viewer/index.html")).href);
  require(await evaluate("document.body.innerText.includes('No Trace loaded')"), "standalone no-trace state");
  await loadDirectory(fixtures[0]!.directory);
  await evaluate("(() => {const input=document.querySelector('input[type=file]'); input.files=new DataTransfer().files;input.dispatchEvent(new Event('change',{bubbles:true}));})()");
  require(await evaluate("!!document.querySelector('.gua-trace')"), "cancelled picker preserves loaded trace");
  await filter("cleanup"); await loadDirectory(fixtures[1]!.directory);
  require(await evaluate("document.querySelector('.gua-trace input').value==='' && document.querySelector('article h2').innerText==='Repeated hold'"), "load resets selection/filter");
  // Synthetic user-selected Files exercise picker failures without external reads.
  await evaluate("(() => {const dt=new DataTransfer();dt.items.add(new File(['bad'],'manifest.json'));dt.items.add(new File([''],'events.jsonl'));const input=document.querySelector('input[type=file]');input.files=dt.files;input.dispatchEvent(new Event('change',{bubbles:true}));})()");
  await wait("document.body.innerText.includes('Trace could not be read')");
  require(await evaluate("!document.querySelector('.gua-trace')"), "bad load clears stale content");
  await loadDirectory(fixtures[0]!.directory);
  // Slow older selection must never replace a newer selection. Input is deliberately enabled.
  const contents = await Promise.all(fixtures.slice(0, 2).map(async f => ({ manifest: await readFile(resolve(f.directory, "manifest.json"), "utf8"), events: await readFile(resolve(f.directory, "events.jsonl"), "utf8") })));
  await evaluate(`(async()=>{const fixtures=${JSON.stringify(contents)}; const original=File.prototype.arrayBuffer;File.prototype.arrayBuffer=function(){if(this.name==='manifest.json'&&this.__slow)return new Promise(r=>setTimeout(()=>original.call(this).then(r),350));return original.call(this)};const input=document.querySelector('input[type=file]');for(let i=0;i<2;i++){const dt=new DataTransfer(),m=new File([fixtures[i].manifest],'manifest.json');m.__slow=i===0;dt.items.add(m);dt.items.add(new File([fixtures[i].events],'events.jsonl'));input.files=dt.files;input.dispatchEvent(new Event('change',{bubbles:true}));}await new Promise(r=>setTimeout(r,600));File.prototype.arrayBuffer=original;})()`);
  require(await evaluate("document.body.innerText.includes('Primary result: failed')"), "last selection wins");
  evidence.push({ surface: "standalone picker", checks: "no trace, generated assets only, directory picker, error/recovery, reset, selection race" });
  for (const fixture of fixtures) {
    await navigate(inspectorUrl);
    await evaluate("[...document.querySelectorAll('summary')].find(s=>s.textContent==='Open Gua Trace (offline)').parentElement.open=true");
    await loadDirectory(fixture.directory); await select(0); await openDetails();
    require(await evaluate(`document.body.innerText.includes('Primary result: ${fixture.outcome}') && document.body.innerText.includes('Assertion truth: false') && document.body.innerText.includes('external.future.goal')`), "Inspector/static shared semantics");
    await filter("cleanup"); require(await evaluate("document.querySelector('.gua-trace article h2').innerText==='Runner cleanup'"), "Inspector filtered selection");
    evidence.push({ surface: "Inspector offline panel", outcome: fixture.outcome, checks: "same truth/primary/unknown semantics and filtering as static report" });
  }
  require(external.length === 0, "Unexpected external read: " + external.join(","));
  require(exceptions.length === 0, "Browser errors: " + exceptions.join(","));
  await writeFile(resolve(fixturesRoot, "browser-evidence.json"), JSON.stringify({ browser: await client.send("Browser.getVersion"), evidence, externalRequests: external, exceptions }, null, 2));
  console.log(`Trace browser acceptance passed (${evidence.length} surfaces/cases). Evidence: ${fixturesRoot}`);
} finally { client.close(); observed.close(); }
