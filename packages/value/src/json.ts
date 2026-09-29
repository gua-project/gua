import { ValueError, unicode } from "./validation.js";
export class NumericToken { constructor(readonly text: string) {} }
export function parseJson(text: string): unknown {
  unicode(text, "$");
  let pos = 0;
  const error = (): never => { throw new ValueError("structure", "$"); };
  const ws = () => { while (/[\x20\t\r\n]/.test(text[pos] ?? "x")) pos++; };
  const take = (c: string) => { ws(); if (text[pos] === c) { pos++; return true; } return false; };
  const string = (): string => {
    ws(); const start = pos;
    if (text[pos++] !== '"') return error();
    while (pos < text.length) {
      const c = text[pos++];
      if (c === "\\") pos++;
      else if (c === '"') {
        let value: string;
        try { value = JSON.parse(text.slice(start, pos)); } catch { return error(); }
        unicode(value, "$"); return value;
      }
    }
    return error();
  };
  const read = (depth: number): unknown => {
    if (depth > 64) return error();
    ws();
    if (text[pos] === '"') return string();
    if (take("{")) {
      const obj: Record<string, unknown> = Object.create(null);
      if (take("}")) return obj;
      do { const key = string(); if (Object.hasOwn(obj, key) || !take(":")) return error(); obj[key] = read(depth + 1); } while (take(","));
      if (!take("}")) return error(); return obj;
    }
    if (take("[")) { const arr: unknown[] = []; if (take("]")) return arr; do { arr.push(read(depth + 1)); } while (take(",")); if (!take("]")) return error(); return arr; }
    for (const [literal, value] of [["true", true], ["false", false], ["null", null]] as const) { if (text.startsWith(literal, pos)) { pos += literal.length; return value; } }
    const match = /^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/.exec(text.slice(pos));
    if (!match) return error(); pos += match[0].length; return new NumericToken(match[0]);
  };
  const value = read(0); ws(); if (pos !== text.length) return error(); return value;
}
export function exactInteger(text: string, path: string): number {
  const m = /^(-?)([0-9]+)(?:\.([0-9]+))?(?:[eE]([+-]?[0-9]+))?$/.exec(text)!;
  let digits = (m[2] + (m[3] ?? "")).replace(/^0+/, "");
  if (!digits) return 0;
  let exp = Number(m[4] ?? "0") - (m[3]?.length ?? 0);
  const trim = digits.length; digits = digits.replace(/0+$/, ""); exp += trim - digits.length;
  if (exp < 0 || exp > 16 || digits.length + exp > 16) throw new ValueError("range", path);
  digits += "0".repeat(exp);
  if (digits.length === 16 && digits > "9007199254740991") throw new ValueError("range", path);
  return Number(m[1] + digits);
}
