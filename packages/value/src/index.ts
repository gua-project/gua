import { exactInteger, NumericToken, parseJson } from "./json.js";
export * from "./observe.js";
import { unicode, ValueError } from "./validation.js";
export { ValueError } from "./validation.js";
export type { ValueErrorCode } from "./validation.js";
export type ScalarType = "bool" | "integer" | "number" | "string" | "enum";
export type ValueType = ScalarType | "list" | "set";
type ScalarMap = { bool: boolean; integer: number; number: number; string: string; enum: string };
export type Value = { [K in ScalarType]: Readonly<{ type: K; value: ScalarMap[K] } & (K extends "enum" ? { enumType: string } : {})> }[ScalarType]
  | { [K in ScalarType]: Readonly<{ type: "list" | "set"; elementType: K; value: readonly ScalarMap[K][] } & (K extends "enum" ? { enumType: string } : {})> }[ScalarType];
const types = ["bool", "integer", "number", "string", "enum", "list", "set"];
const bad = (code: ConstructorParameters<typeof ValueError>[0], path = "$"): never => { throw new ValueError(code, path); };
function object(x: unknown): Record<string, unknown> { if (!x || typeof x !== "object" || Array.isArray(x) || x instanceof NumericToken) return bad("structure"); return x as Record<string, unknown>; }
function keys(x: Record<string, unknown>, expected: string[]): void { const actual = Reflect.ownKeys(x); if (actual.length !== expected.length || expected.some(k => !Object.hasOwn(x, k))) bad("structure"); }
function type(x: unknown, path: string): ValueType { if (typeof x !== "string") return bad("structure", path); if (!types.includes(x)) return bad("forbidden_type", path); return x as ValueType; }
function enumId(id: unknown): asserts id is string { if (typeof id !== "string") bad("structure", "$.enumType"); unicode(id as string, "$.enumType"); if (!/^[A-Za-z_][A-Za-z_0-9]*(?:\.[A-Za-z_][A-Za-z_0-9]*)+$/.test(id as string)) bad("structure", "$.enumType"); }
export class EnumCatalog {
  readonly #enums = new Map<string, readonly string[]>();
  register(enumType: string, members: readonly string[]): void {
    enumId(enumType);
    if (!Array.isArray(members) || members.length === 0) bad("structure", "$.members");
    const seen = new Set<string>();
    for (let i = 0; i < members.length; i++) { const m = members[i], p = `$.members[${i}]`; if (typeof m !== "string" || !m.length) bad("structure", p); unicode(m, p); if (seen.has(m)) bad("duplicate", p); seen.add(m); }
    const old = this.#enums.get(enumType);
    if (old) { if (old.length !== seen.size || old.some(m => !seen.has(m))) bad("enum_conflict", "$.enumType"); return; }
    this.#enums.set(enumType, Object.freeze([...members]));
  }
  members(enumType: string): readonly string[] { enumId(enumType); return this.#enums.get(enumType) ?? bad("enum_unknown", "$.enumType"); }
  toJSON(): { schemaVersion: 1; enums: { enumType: string; members: readonly string[] }[] } { return { schemaVersion: 1, enums: [...this.#enums].sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0).map(([enumType, members]) => ({ enumType, members })) }; }
  static fromJSON(text: string): EnumCatalog {
    const j = object(parseJson(text)); keys(j, ["schemaVersion", "enums"]);
    if (!(j.schemaVersion instanceof NumericToken) || exactInteger(j.schemaVersion.text, "$.schemaVersion") !== 1 || !Array.isArray(j.enums)) bad("structure");
    const cat = new EnumCatalog(); for (const entry of j.enums as unknown[]) { const e = object(entry); keys(e, ["enumType", "members"]); cat.register(e.enumType as string, e.members as string[]); } return cat;
  }
}
function scalar(t: ScalarType, raw: unknown, cat: EnumCatalog | undefined, id: string | undefined, path: string): boolean | number | string {
  if (raw === null || typeof raw === "bigint" || typeof raw === "undefined" || typeof raw === "symbol" || typeof raw === "function" || (typeof raw === "object" && !(raw instanceof NumericToken))) return bad("forbidden_type", path);
  if (t === "bool") { if (typeof raw !== "boolean") return bad("element_type", path); return raw; }
  if (t === "number" || t === "integer") {
    if (!(raw instanceof NumericToken) && typeof raw !== "number") return bad("element_type", path);
    const n = raw instanceof NumericToken ? (t === "integer" ? exactInteger(raw.text, path) : Number(raw.text)) : raw;
    if (!Number.isFinite(n)) return bad("non_finite", path);
    if (t === "integer" && !Number.isSafeInteger(n)) return bad("range", path);
    return n === 0 ? 0 : n;
  }
  if (typeof raw !== "string") return bad("element_type", path);
  unicode(raw, path);
  if (t === "enum" && !cat!.members(id!).includes(raw)) return bad("enum_member", path);
  return raw;
}
/** Validate and copy; returned values and collections are immutable. */
export function createValue(input: unknown, catalog?: EnumCatalog): Value {
  const j = object(input); const t = type(j.type, "$.type"); const collection = t === "list" || t === "set";
  const element = collection ? type(j.elementType, "$.elementType") : undefined;
  if (element === "list" || element === "set") return bad("forbidden_type", "$.elementType");
  const en = t === "enum" || element === "enum";
  let id: string | undefined;
  if (en) { enumId(j.enumType); id = j.enumType; if (!catalog) return bad("enum_unknown", "$.enumType"); catalog.members(id); }
  keys(j, ["type", "value", ...(collection ? ["elementType"] : []), ...(en ? ["enumType"] : [])]);
  let value: unknown;
  if (collection) {
    if (!Array.isArray(j.value)) return bad("structure", "$.value");
    const seen = new Set<unknown>(); const values: unknown[] = [];
    for (let i = 0; i < j.value.length; i++) { const p = `$.value[${i}]`; const v = scalar(element!, j.value[i], catalog, id, p); if (t === "set" && seen.has(v)) return bad("duplicate", p); seen.add(v); values.push(v); }
    value = Object.freeze(values);
  } else value = scalar(t, j.value, catalog, id, "$.value");
  return Object.freeze({ type: t, ...(collection ? { elementType: element } : {}), ...(en ? { enumType: id } : {}), value }) as Value;
}
export function parseValue(text: string, catalog?: EnumCatalog): Value { return createValue(parseJson(text), catalog); }
export function serializeValue(value: Value, catalog?: EnumCatalog): string { return JSON.stringify(createValue(value, catalog)); }
export function valuesEqual(left: Value, right: Value, catalog?: EnumCatalog): boolean {
  const a = createValue(left, catalog), b = createValue(right, catalog);
  if (a.type !== b.type || ("enumType" in a ? a.enumType : undefined) !== ("enumType" in b ? b.enumType : undefined)) return false;
  if ("elementType" in a) {
    if (!("elementType" in b) || a.elementType !== b.elementType || a.value.length !== b.value.length) return false;
    return a.value.every((v, i) => a.type === "set" ? (b.value as readonly unknown[]).includes(v) : v === b.value[i]);
  }
  return a.value === b.value;
}
