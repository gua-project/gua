/** Validate host-published metadata; this never grants execution authority. */
type RecordValue = Record<string, unknown>;
const record = (value: unknown): RecordValue | undefined => value !== null && typeof value === "object" && !Array.isArray(value) ? value as RecordValue : undefined;
const finite = (value: unknown): value is number => typeof value === "number" && Number.isFinite(value);
const unicode = (text: string) => [...text].every(c => { const cp = c.codePointAt(0)!; return cp < 0xd800 || cp > 0xdfff; });
const points = (text: string) => [...text].length;

/** Keep original JSON spans for byte limits that decoding/re-encoding loses. */
export function parseInputActionMetadataJson(source: string): unknown {
  const parsed: unknown = JSON.parse(source); // Grammar validation precedes the span walk.
  let cursor = 0;
  const encoder = new TextEncoder();
  const space = () => { while (/\s/.test(source[cursor] ?? "")) cursor++; };
  const string = () => {
    cursor++;
    while (source[cursor] !== '"') cursor += source[cursor] === "\\" ? 2 : 1;
    cursor++;
  };
  const value = () => {
    if (source[cursor] === '"') { string(); return; }
    if (source[cursor] === "{" || source[cursor] === "[") {
      let depth = 1; cursor++;
      // Skip arbitrary nested values iteratively; only the fixed map/action path
      // is visited below, so unrelated nesting cannot grow the JS call stack.
      while (depth) {
        const token = source[cursor];
        if (token === '"') { string(); continue; }
        if (token === "{" || token === "[") depth++;
        if (token === "}" || token === "]") depth--;
        cursor++;
      }
    } else while (cursor < source.length && !/[\s,}\]]/.test(source[cursor]!)) cursor++;
  };
  const bounded = (start: number, maximum: number, exclusive = false) => {
    const limit = maximum + (exclusive ? 0 : 1);
    if (cursor - start >= limit || encoder.encode(source.slice(start, cursor)).length >= limit)
      throw new Error("Oversized game input metadata literal.");
  };
  const array = (item: () => void) => {
    cursor++; space();
    while (source[cursor] !== "]") {
      item(); space();
      if (source[cursor] !== ",") break;
      cursor++; space();
    }
    cursor++;
  };
  const object = (property: (key: string) => boolean) => {
    cursor++; space();
    while (source[cursor] !== "}") {
      const start = cursor; string();
      const key = JSON.parse(source.slice(start, cursor)) as string;
      space(); cursor++; space(); // colon
      if (!property(key)) value();
      space();
      if (source[cursor] !== ",") break;
      cursor++; space();
    }
    cursor++;
  };
  space();
  if (source[cursor] === "{") object(key => {
    if (key !== "actions" || source[cursor] !== "[") return false;
    array(() => {
      if (source[cursor] !== "{") { value(); return; }
      object(field => {
        if (field === "valueSchema") { const start = cursor; value(); bounded(start, 16384); return true; }
        if (field !== "examples" || source[cursor] !== "[") return false;
        const start = cursor;
        array(() => { const exampleStart = cursor; value(); bounded(exampleStart, 512, true); });
        bounded(start, 16384);
        return true;
      });
    });
    return true;
  });
  return parsed;
}

export function validInputActionMetadata(action: RecordValue): boolean {
  if (action.valueSchema === undefined && action.examples === undefined) return true;
  try {
    const kind = action.valueType;
    const range = record(action.range);
    const low = range?.minimum as number | undefined, high = range?.maximum as number | undefined;
    if (range && kind !== "axis1d" && kind !== "vector2") return false;
    const schema = action.valueSchema === undefined ? undefined : record(action.valueSchema);
    if (action.valueSchema !== undefined && !schema) return false;
    const schemaValid = (s: RecordValue, type: string, root: boolean): boolean => {
      if (s.type !== type) return false;
      for (const [key, value] of Object.entries(s)) {
        if (key === "type") continue;
        if (key === "description") { if (typeof value !== "string" || !unicode(value) || points(value) > 1024) return false; continue; }
        if (root && key === "$schema") { if (value !== "https://json-schema.org/draft/2020-12/schema") return false; continue; }
        if (type === "number" && (key === "minimum" || key === "maximum")) { if (!finite(value)) return false; continue; }
        if (type === "string" && (key === "minLength" || key === "maxLength")) { if (!finite(value) || !Number.isInteger(value) || value < 0 || value > 40) return false; continue; }
        if (type === "object" && ["properties", "required", "additionalProperties"].includes(key)) continue;
        return false;
      }
      if (type === "number") {
        const min = s.minimum as number | undefined ?? low ?? -Number.MAX_VALUE;
        const max = s.maximum as number | undefined ?? high ?? Number.MAX_VALUE;
        return min <= max && (low === undefined || min >= low) && (high === undefined || max <= high);
      }
      if (type === "string") return (s.minLength as number | undefined ?? 0) <= (s.maxLength as number | undefined ?? 40);
      if (type === "object") {
        const p = record(s.properties), required = s.required;
        return !!p && Object.keys(p).length === 2 && !!record(p.x) && !!record(p.y) &&
          Array.isArray(required) && required.length === 2 && required.includes("x") && required.includes("y") && s.additionalProperties === false &&
          schemaValid(record(p.x)!, "number", false) && schemaValid(record(p.y)!, "number", false);
      }
      return true;
    };
    const type = ({ button: "boolean", axis1d: "number", vector2: "object", text: "string" } as Record<string, string>)[String(kind)];
    if (!type || (schema && !schemaValid(schema, type, true))) return false;
    if (schema && new TextEncoder().encode(JSON.stringify(schema)).length > 16384) return false;
    if (action.examples === undefined) return true;
    if (!Array.isArray(action.examples) || action.examples.length > 16 ||
        new TextEncoder().encode(JSON.stringify(action.examples)).length > 16384 ||
        (kind === "button" && action.holdable === false && action.examples.length !== 0)) return false;
    const valueValid = (value: unknown, t: string, s?: RecordValue): boolean => {
      if (t === "boolean") return typeof value === "boolean";
      if (t === "number") return finite(value) && value >= (low ?? -Number.MAX_VALUE) && value <= (high ?? Number.MAX_VALUE) &&
        value >= (s?.minimum as number | undefined ?? -Number.MAX_VALUE) && value <= (s?.maximum as number | undefined ?? Number.MAX_VALUE);
      if (t === "string") return typeof value === "string" && unicode(value) && points(value) <= 40 &&
        points(value) >= (s?.minLength as number | undefined ?? 0) && points(value) <= (s?.maxLength as number | undefined ?? 40);
      const v = record(value), p = record(s?.properties);
      return !!v && Object.keys(v).length === 2 && valueValid(v.x, "number", record(p?.x)) && valueValid(v.y, "number", record(p?.y));
    };
    return action.examples.every(value => valueValid(value, type, schema));
  } catch { return false; }
}
