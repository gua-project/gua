import { createRoot } from "react-dom/client";
import { GuaTraceFileViewer } from "./TraceViewer";
import { parseTrace, type TraceDocument } from "./trace";

const element = document.getElementById("gua-trace-data");
let initial: TraceDocument | undefined;
try {
  if (element?.textContent) {
    const document = JSON.parse(element.textContent);
    initial = parseTrace(JSON.stringify(document.manifest), document.events.map((e: unknown) => JSON.stringify(e)).join("\n") + (document.events.length ? "\n" : ""), document.blobs);
    initial.issues = [...new Set([...initial.issues, ...document.issues])];
  }
} catch { /* File picker remains available if the embedded trace is invalid. */ }
createRoot(document.getElementById("root")!).render(<GuaTraceFileViewer initial={initial} />);
