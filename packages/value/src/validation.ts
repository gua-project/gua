export type ValueErrorCode = "structure" | "forbidden_type" | "range" | "non_finite" | "unicode" | "enum_unknown" | "enum_conflict" | "enum_member" | "element_type" | "duplicate" | "internal";
export class ValueError extends Error {
  constructor(readonly code: ValueErrorCode, readonly path: string) { super(`Value error ${code} at ${path}`); }
}
export function unicode(s: string, path: string): void {
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if (c >= 0xd800 && c <= 0xdbff) { const low = s.charCodeAt(++i); if (!(low >= 0xdc00 && low <= 0xdfff)) throw new ValueError("unicode", path); }
    else if (c >= 0xdc00 && c <= 0xdfff) throw new ValueError("unicode", path);
  }
}
