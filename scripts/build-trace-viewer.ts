import { mkdir, copyFile, readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";

const root = resolve(import.meta.dir, "..");
const output = resolve(root, "artifacts/trace-viewer");
await mkdir(output, { recursive: true });
const result = await Bun.build({ entrypoints: [resolve(root, "packages/inspector/src/trace-main.tsx")],
  target: "browser", format: "iife", minify: true, define: { "process.env.NODE_ENV": '"production"' } });
if (!result.success) throw new Error(result.logs.join("\n"));
const script = (await result.outputs[0]!.text()).replace(/<\/script/gi, "<\\/script");
if (/<\/script/i.test(script)) throw new Error("Viewer bundle contains an unsafe HTML script terminator");
await writeFile(resolve(output, "viewer.js"), script);
await copyFile(resolve(root, "protocol/schema/trace.schema.json"), resolve(output, "trace.schema.json"));
await writeFile(resolve(output, "index.html"), `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'none'; img-src 'none'; base-uri 'none'; form-action 'none'"><title>Gua Trace Viewer v1</title></head><body style="background:#121926;color:#e7eaf1;font-family:system-ui"><div id="root"></div><script>${script}</script></body></html>`);
await writeFile(resolve(output, "version.json"), JSON.stringify({ schemaVersion: 1, component: "GuaTraceViewer", bundleFormat: 1 }));
console.log(`Trace Viewer built: ${output}`);
