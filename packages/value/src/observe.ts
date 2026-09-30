import { createValue, EnumCatalog, type Value } from "./index.js";
import { exactInteger, NumericToken, parseJson } from "./json.js";

export interface ObserveMetadata {
  schemaVersion: 1; sourceId: string; sessionEpoch: number; profile: "debug" | "player";
  sequence: number; revision: number; uiFrame: number; uiRevision: number; worldFrame: number; worldRevision: number;
}
export interface ObserveIdentity { ownerId: number; registrationId: number; source: "ui" | "object" | "world"; runtimeId: string; name: string }
export interface ObserveEntry extends ObserveIdentity { status: "available" | "unavailable"; value?: Value; error?: number }
export interface ObserveChange extends ObserveIdentity, ObserveMetadata {
  kind: "added" | "changed" | "removed" | "unavailable" | "recovered"; boundary: "frame" | "explicit" | "lifecycle";
  before?: Value; after?: Value; beforeStatus?: "available" | "unavailable"; afterStatus?: "available" | "unavailable"; beforeError?: number; afterError?: number;
}
export type ObserveDocument = (ObserveMetadata & { kind: "snapshot"; entries: ObserveEntry[] }) |
  (ObserveMetadata & { kind: "changes"; status: "ok" | "gap" | "stale_session"; events: ObserveChange[] });
export type ObserveCatalog = { schemaVersion: 1; enums: { enumType: string; members: readonly string[] }[] };
export interface ObserveTransport { document: ObserveDocument; catalogs: { value?: ObserveCatalog; before?: ObserveCatalog; after?: ObserveCatalog }[] }
export interface ObserveSubscription { subscriptionId: number; snapshot: ObserveTransport }

// Structural validation mirrors Observe v1; Value validation remains shared.
// Protocol-schema conformance is also checked in tests, without shipping repo paths.
const metadataKeys = ["schemaVersion", "sourceId", "sessionEpoch", "profile", "sequence", "revision", "uiFrame", "uiRevision", "worldFrame", "worldRevision"];
const identityKeys = ["ownerId", "registrationId", "source", "runtimeId", "name"];
const errorCodes = [1,2,3,4,5,6,7,8,9,10,11,100,101,102];
function keys(e: Record<string, unknown>, required: string[], optional: string[] = []): void {
  if (required.some(k => !(k in e)) || Object.keys(e).some(k => !required.includes(k) && !optional.includes(k))) invalid();
}
function integer(n: unknown, minimum: number): void { if (typeof n !== "number" || !Number.isSafeInteger(n) || n < minimum) invalid(); }
function metadata(e: Record<string, unknown>): void {
  if (e.schemaVersion !== 1 || typeof e.sourceId !== "string" || !e.sourceId.length || !["debug", "player"].includes(e.profile as string)) invalid();
  integer(e.sessionEpoch, 1);
  for (const k of metadataKeys.slice(4)) integer(e[k], 0);
}
function identity(e: Record<string, unknown>): void {
  integer(e.ownerId, 1); integer(e.registrationId, 1);
  if (!["ui", "object", "world"].includes(e.source as string) || typeof e.runtimeId !== "string" || typeof e.name !== "string" || !e.name.length) invalid();
}
function state(e: Record<string, unknown>, status: string, value: string, error: string): void {
  if (!(status in e)) { if (value in e || error in e) invalid(); return; }
  if (e[status] === "available") { if (!(value in e) || error in e) invalid(); }
  else if (e[status] === "unavailable") { if (value in e || !errorCodes.includes(e[error] as number)) invalid(); }
  else invalid();
}
function validateDocument(d: Record<string, unknown>): void {
  metadata(d);
  if (d.kind === "snapshot") {
    keys(d, [...metadataKeys, "kind", "entries"]);
    for (const raw of d.entries as unknown[]) {
      const e = object(raw); keys(e, [...identityKeys, "status"], ["value", "error"]); identity(e); state(e, "status", "value", "error");
    }
  } else if (d.kind === "changes") {
    keys(d, [...metadataKeys, "kind", "status", "events"]);
    if (!["ok", "gap", "stale_session"].includes(d.status as string)) invalid();
    for (const raw of d.events as unknown[]) {
      const e = object(raw);
      keys(e, [...metadataKeys, ...identityKeys, "kind", "boundary"], ["before", "after", "beforeStatus", "afterStatus", "beforeError", "afterError"]);
      metadata(e); identity(e);
      if (!["frame", "explicit", "lifecycle"].includes(e.boundary as string)) invalid();
      state(e, "beforeStatus", "before", "beforeError"); state(e, "afterStatus", "after", "afterError");
      switch (e.kind) {
        case "added": if (!("after" in e) || "before" in e) invalid(); break;
        case "changed": if (!("before" in e) || !("after" in e)) invalid(); break;
        case "removed": if (!("beforeStatus" in e) || "afterStatus" in e) invalid(); break;
        case "unavailable": if (!("afterError" in e)) invalid(); break;
        case "recovered": if (!("beforeError" in e) || !("after" in e)) invalid(); break;
        default: invalid();
      }
    }
  } else invalid();
}
function invalid(): never { throw new Error("Invalid Observe transport response."); }
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value) || value instanceof NumericToken) return invalid();
  return value as Record<string, unknown>;
}
function normalize(value: unknown): unknown {
  if (value instanceof NumericToken) { const n = Number(value.text); if (!Number.isFinite(n)) return invalid(); return n; }
  if (Array.isArray(value)) return value.map(normalize);
  if (value && typeof value === "object") return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, normalize(v)]));
  return value;
}
/** Pass original JSON for external wire input. Value integer lexemes are validated
 * before converting metadata; error messages never include source values. */
export function parseObserveTransport(input: string | unknown, expectedKind?: "snapshot" | "changes"): ObserveTransport {
  try {
    const raw = object(typeof input === "string" ? parseJson(input) : input);
    if (Object.keys(raw).length !== 2 || !Array.isArray(raw.catalogs)) return invalid();
    const document = object(raw.document);
    if (expectedKind !== undefined && document.kind !== expectedKind) return invalid();
    const entries = document.kind === "snapshot" ? document.entries : document.events;
    if (!Array.isArray(entries) || entries.length !== raw.catalogs.length) return invalid();
    if (document.kind === "changes" && document.status !== "ok" && entries.length !== 0) return invalid();
    for (const entry of [document, ...entries.map(object)]) {
      for (const key of ["schemaVersion", "sessionEpoch", "sequence", "revision", "uiFrame", "uiRevision", "worldFrame", "worldRevision", "ownerId", "registrationId"])
        if (entry[key] instanceof NumericToken) exactInteger((entry[key] as NumericToken).text, "$.");
    }
    entries.forEach((entry, i) => {
      const e = object(entry), catalogs = object((raw.catalogs as unknown[])[i]);
      if (Object.keys(catalogs).some(k => !["value", "before", "after"].includes(k))) return invalid();
      for (const key of ["value", "before", "after"]) {
        const c = catalogs[key];
        if (c !== undefined && e[key] === undefined) return invalid();
        if (c !== undefined) {
          const version = object(c).schemaVersion;
          if (version instanceof NumericToken) exactInteger(version.text, "$.schemaVersion");
        }
        const catalog = c === undefined ? undefined : EnumCatalog.fromJSON(JSON.stringify(normalize(c)));
        if (e[key] !== undefined) {
          const value = createValue(e[key], catalog);
          const enumType = value.type === "enum" ? value.enumType :
            (value.type === "list" || value.type === "set") && value.elementType === "enum" ? value.enumType : undefined;
          if (c !== undefined) {
            const definitions = object(normalize(c)).enums as {enumType: string}[];
            if (enumType === undefined || definitions.length !== 1 || definitions[0]!.enumType !== enumType) return invalid();
          }
          e[key] = value;
        }
      }
    });
    const result = normalize(raw) as ObserveTransport;
    validateDocument(object(result.document));
    for (const entry of [result.document, ...entries.map(e => normalize(e) as Record<string, unknown>)]) {
      for (const key of ["schemaVersion", "sessionEpoch", "sequence", "revision", "uiFrame", "uiRevision", "worldFrame", "worldRevision", "ownerId", "registrationId"])
        if (key in entry && !Number.isSafeInteger((entry as unknown as Record<string, unknown>)[key])) return invalid();
    }
    return result;
  } catch { return invalid(); }
}
export function observeSubscriptionId(value: unknown): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value <= 0) return invalid(); return value;
}
export const observeTools = ([
  { name: "get_observe_snapshot", description: "Read additional observations associated with UI/World owners. Standard position remains in the World tree." },
  { name: "subscribe_observations", description: "Atomically read additional observations and start a connection-owned change cursor." },
  { name: "poll_observations", description: "Read changes for this subscription. gap/stale_session require a new subscription; missed changes cannot be recovered." },
  { name: "unsubscribe_observations", description: "Release this connection's observation subscription." },
] as const).map(tool => ({ ...tool, inputSchema: { type: "object", properties: tool.name.startsWith("poll_") || tool.name.startsWith("unsubscribe_") ?
  { subscriptionId: { type: "integer", minimum: 1, maximum: Number.MAX_SAFE_INTEGER } } : {},
  required: tool.name.startsWith("poll_") || tool.name.startsWith("unsubscribe_") ? ["subscriptionId"] : [], additionalProperties: false } }));

/** Decode the original WebSocket response before JSON.parse can round integers. */
export class ObserveWireRejectionError extends Error {
  constructor() { super("Observe request rejected."); }
}
export function decodeObserveWireResponse(input: string, expectedId: number, operation: string): unknown {
  try {
    const raw = object(parseJson(input));
    keys(raw, raw.ok === true ? ["id", "ok", "result"] : ["id", "ok", "error"]);
    if (!(raw.id instanceof NumericToken) || exactInteger(raw.id.text, "$.id") !== expectedId) return invalid();
    if (raw.ok === false && typeof raw.error === "string") throw new ObserveWireRejectionError();
    if (raw.ok !== true) return invalid();
    if (operation === "unsubscribe_observations") { if (raw.result !== null) return invalid(); return null; }
    if (operation === "subscribe_observations") {
      const result = object(raw.result); keys(result, ["subscriptionId", "snapshot"]);
      if (!(result.subscriptionId instanceof NumericToken)) return invalid();
      return {subscriptionId: observeSubscriptionId(exactInteger(result.subscriptionId.text, "$.subscriptionId")), snapshot: parseObserveTransport(result.snapshot, "snapshot")};
    }
    if (operation !== "get_observe_snapshot" && operation !== "poll_observations") return invalid();
    return parseObserveTransport(raw.result, operation === "get_observe_snapshot" ? "snapshot" : "changes");
  } catch (error) { if (error instanceof ObserveWireRejectionError) throw error; return invalid(); }
}
