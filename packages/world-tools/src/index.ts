export type WorldPrimitive = string | number | boolean | null;
export * from "./spatial.js";
export interface GuaWorldObject {
  id: string; parentId?: string; kind: string; label?: string; description?: string;
  space: "world2d" | "world3d"; position: { x?: number; y?: number; z?: number };
  visibleToPlayer: boolean; active: boolean; agentExposure: "auto" | "private";
  domainId?: string; relatedUiNodeId?: string; tags?: string[]; state: Record<string, WorldPrimitive>;
}
export interface GuaWorldObjectTree { schemaVersion: 1; sessionEpoch: number; frameSequence: number; revision: number; scene: string; objects: GuaWorldObject[] }
export interface GuaWorldSelector { id?: string; kind?: string; label?: string; tag?: string; parentId?: string; directChild?: boolean; visibleToPlayer?: boolean; active?: boolean; state?: { key: string; value: WorldPrimitive }; near?: { relativeToObjectId: string; maxDistance: number }; limit?: number }
export interface GuaWorldDistance { objectId: string; distance: number }
export interface GuaWorldSpatialResult { relativeToObjectId: string; truncated: boolean; distances: GuaWorldDistance[] }
export interface GuaWorldQueryResult { valid: boolean; matches: GuaWorldObject[]; error?: string; sessionEpoch?: number; frameSequence?: number; revision?: number; spatial?: GuaWorldSpatialResult }
export interface GuaWorldProvider {
  getWorldObjectTree(): Promise<GuaWorldObjectTree>;
  findWorldObjects(selector: GuaWorldSelector): Promise<GuaWorldQueryResult>;
  waitForWorldObject(selector: GuaWorldSelector, timeoutMs: number): Promise<GuaWorldObject>;
}

export function parseWorldObjectTree(value: unknown): GuaWorldObjectTree {
  const parsed = parseJson(value);
  const tree = record(parsed);
  if (!tree || !only(tree, treeKeys) || tree.schemaVersion !== 1 || !positiveInteger(tree.sessionEpoch) ||
      !nonNegativeInteger(tree.frameSequence) || !nonNegativeInteger(tree.revision) ||
      !nonEmptyString(tree.scene) || !Array.isArray(tree.objects) || !tree.objects.every(worldObject)) {
    throw new TypeError("The engine returned an invalid protocol World Object Tree.");
  }
  return parsed as GuaWorldObjectTree;
}

export function parseWorldQueryResult(value: unknown, selector?: GuaWorldSelector): GuaWorldQueryResult {
  const parsed = parseJson(value);
  const result = record(parsed);
  const spatial = record(result?.spatial);
  const matches = Array.isArray(result?.matches) ? result.matches : undefined;
  const distances = Array.isArray(spatial?.distances) ? spatial.distances : undefined;
  const validSpatial = spatial === undefined || (only(spatial, spatialKeys) && nonEmptyString(spatial.relativeToObjectId) &&
    typeof spatial.truncated === "boolean" && distances !== undefined && distances.every((item) => {
      const distance = record(item);
      return !!distance && only(distance, distanceKeys) && nonEmptyString(distance.objectId) && finiteNumber(distance.distance) && distance.distance >= 0;
    }) && distances.every((item, index) => {
      if (index === 0) return true;
      const previous = record(distances[index - 1])!;
      const current = record(item)!;
      return (previous.distance as number) < (current.distance as number) ||
        (previous.distance === current.distance && utf8OrdinalLess(
          previous.objectId as string, current.objectId as string));
    }) && matches !== undefined && distances.length === matches.length &&
    distances.every((item, index) => record(item)?.objectId === record(matches[index])?.id));
  const selectorSpatial = !result?.valid || selector === undefined || (selector.near === undefined
    ? spatial === undefined
    : spatial !== undefined && distances !== undefined && matches !== undefined &&
      spatial.relativeToObjectId === selector.near.relativeToObjectId &&
      distances.every((item) => (record(item)?.distance as number) <= selector.near!.maxDistance) &&
      matches.every((item) => {
        const match = record(item)!;
        const position = record(match.position)!;
        return match.id !== selector.near!.relativeToObjectId && finiteNumber(position.x) &&
          finiteNumber(position.y) && (match.space === "world2d" || finiteNumber(position.z));
      }) &&
      (selector.limit === undefined
        ? spatial.truncated === false
        : matches !== undefined && matches.length <= selector.limit && (!spatial.truncated || matches.length === selector.limit)));
  if (!result || !only(result, queryKeys) || typeof result.valid !== "boolean" ||
      !Array.isArray(result.matches) || !result.matches.every(worldObject) ||
      (result.error !== undefined && typeof result.error !== "string") || !validSpatial || !selectorSpatial ||
      (result.valid && (!positiveInteger(result.sessionEpoch) || !nonNegativeInteger(result.frameSequence) ||
        !nonNegativeInteger(result.revision) || result.error !== undefined)) ||
      (result.valid && distances !== undefined && new Set(distances.map((item) => record(item)!.objectId)).size !== distances.length) ||
      (!result.valid && (typeof result.error !== "string" || result.sessionEpoch !== undefined || result.frameSequence !== undefined ||
        result.revision !== undefined || result.spatial !== undefined))) {
    throw new TypeError("The engine returned an invalid world query result.");
  }
  return parsed as GuaWorldQueryResult;
}

const treeKeys = new Set(["schemaVersion", "sessionEpoch", "frameSequence", "revision", "scene", "objects"]);
const queryKeys = new Set(["valid", "matches", "error", "sessionEpoch", "frameSequence", "revision", "spatial"]);
const spatialKeys = new Set(["relativeToObjectId", "truncated", "distances"]);
const distanceKeys = new Set(["objectId", "distance"]);
const objectKeys = new Set([
  "id", "parentId", "kind", "label", "description", "space", "position", "visibleToPlayer", "active",
  "agentExposure", "domainId", "relatedUiNodeId", "tags", "state",
]);
const position2dKeys = new Set(["x", "y"]);
const position3dKeys = new Set(["x", "y", "z"]);
const kindPattern = /^[a-z][a-z0-9_.-]*$/;

function worldObject(value: unknown): value is GuaWorldObject {
  const object = record(value);
  const position = record(object?.position);
  const state = record(object?.state);
  if (!object || !only(object, objectKeys) || !nonEmptyString(object.id) || !optionalNonEmptyString(object.parentId) ||
      typeof object.kind !== "string" || !kindPattern.test(object.kind) ||
      (object.label !== undefined && typeof object.label !== "string") ||
      (object.description !== undefined && typeof object.description !== "string") ||
      (object.space !== "world2d" && object.space !== "world3d") || !position ||
      !only(position, object.space === "world2d" ? position2dKeys : position3dKeys) ||
      !optionalFiniteNumber(position.x) || !optionalFiniteNumber(position.y) ||
      (object.space === "world3d" && !optionalFiniteNumber(position.z)) ||
      typeof object.visibleToPlayer !== "boolean" || typeof object.active !== "boolean" ||
      (object.agentExposure !== "auto" && object.agentExposure !== "private") ||
      !optionalString(object.domainId) || !optionalString(object.relatedUiNodeId) ||
      (object.tags !== undefined && (!Array.isArray(object.tags) || !object.tags.every(nonEmptyString) || new Set(object.tags).size !== object.tags.length)) ||
      !state || Object.keys(state).some((key) => key.length === 0) || !Object.values(state).every(worldPrimitive)) return false;
  return true;
}

function parseJson(value: unknown): unknown {
  if (typeof value !== "string") return value;
  try { return JSON.parse(value); }
  catch { throw new TypeError("The engine returned malformed world JSON."); }
}
function record(value: unknown): Record<string, unknown> | undefined {
  return value !== null && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}

const utf8Encoder = new TextEncoder();
function utf8OrdinalLess(left: string, right: string): boolean {
  const leftBytes = utf8Encoder.encode(left);
  const rightBytes = utf8Encoder.encode(right);
  const length = Math.min(leftBytes.length, rightBytes.length);
  for (let index = 0; index < length; index++) {
    if (leftBytes[index] !== rightBytes[index]) return leftBytes[index]! < rightBytes[index]!;
  }
  return leftBytes.length < rightBytes.length;
}
function only(value: Record<string, unknown>, keys: Set<string>): boolean { return Object.keys(value).every((key) => keys.has(key)); }
function nonEmptyString(value: unknown): value is string { return typeof value === "string" && value.length > 0; }
function optionalNonEmptyString(value: unknown): boolean { return value === undefined || nonEmptyString(value); }
function optionalString(value: unknown): boolean { return value === undefined || typeof value === "string"; }
function finiteNumber(value: unknown): value is number { return typeof value === "number" && Number.isFinite(value); }
function optionalFiniteNumber(value: unknown): boolean { return value === undefined || finiteNumber(value); }
function positiveInteger(value: unknown): boolean { return Number.isInteger(value) && (value as number) >= 1; }
function nonNegativeInteger(value: unknown): boolean { return Number.isInteger(value) && (value as number) >= 0; }
function worldPrimitive(value: unknown): value is WorldPrimitive {
  return value === null || typeof value === "string" || typeof value === "boolean" || finiteNumber(value);
}
const string = (description: string) => ({ type: "string", minLength: 1, description });
const selectorProperties = {
  id: string("Exact stable object id."), kind: string("Exact semantic object kind."), label: string("Exact object label."),
  tag: string("Required object tag."), parentId: string("Scope parent id."), directChild: { type: "boolean" },
  visibleToPlayer: { type: "boolean" }, active: { type: "boolean" },
  stateKey: string("Primitive state key."),
  stateValue: { anyOf: [
    { type: ["string", "boolean", "null"] },
    { type: "number", not: { type: "integer" } },
    { type: "integer", minimum: Number.MIN_SAFE_INTEGER, maximum: Number.MAX_SAFE_INTEGER },
  ], description: "Exact primitive state value; integers must be safely distinguishable." },
  relativeToObjectId: string("Stable observable object id used as the spatial reference."),
  maxDistance: { type: "number", minimum: 0, description: "Inclusive radius in host world units." },
  limit: { type: "integer", minimum: 1, maximum: 4294967295, description: "Maximum spatial matches after distance ordering." },
};
const selectorSchema = (properties: Record<string, unknown>) => ({
  type: "object", additionalProperties: false, properties,
  dependentRequired: { stateKey: ["stateValue"], stateValue: ["stateKey"], relativeToObjectId: ["maxDistance"], maxDistance: ["relativeToObjectId"], limit: ["relativeToObjectId", "maxDistance"] },
  allOf: [{ if: { required: ["directChild"], properties: { directChild: { const: true } } }, then: { required: ["parentId"] } }],
});
export const worldObservationTools = [
  { name: "get_world_object_tree", description: "Read the host-authorized World Object Tree.", inputSchema: { type: "object", additionalProperties: false, properties: {} } },
  { name: "find_world_objects", description: "Find observable world objects with semantic criteria.", inputSchema: selectorSchema(selectorProperties) },
  { name: "wait_for_world_object", description: "Wait until an observable world object matches.", inputSchema: selectorSchema({ ...selectorProperties, timeoutMs: { type: "integer", minimum: 1, maximum: 300000 } }) },
] as const;
export type WorldObservationToolName = (typeof worldObservationTools)[number]["name"];

export function selectorFromArguments(args: Record<string, unknown>): GuaWorldSelector {
  const knownKeys = new Set(["id", "kind", "label", "tag", "parentId", "directChild", "visibleToPlayer", "active", "stateKey", "stateValue", "relativeToObjectId", "maxDistance", "limit", "timeoutMs"]);
  for (const key of Object.keys(args)) if (!knownKeys.has(key)) throw new TypeError(`Unknown world selector argument: ${key}.`);
  const result: Record<string, unknown> = {};
  for (const key of ["id", "kind", "label", "tag", "parentId"] as const) {
    if (!Object.prototype.hasOwnProperty.call(args, key)) continue;
    if (typeof args[key] !== "string" || args[key].length === 0) throw new TypeError(`${key} must be a non-empty string.`);
    result[key] = args[key];
  }
  for (const key of ["directChild", "visibleToPlayer", "active"] as const) {
    if (!Object.prototype.hasOwnProperty.call(args, key)) continue;
    if (typeof args[key] !== "boolean") throw new TypeError(`${key} must be a boolean.`);
    result[key] = args[key];
  }
  if (result.directChild === true && result.parentId === undefined) throw new TypeError("parentId is required when directChild is true.");
  const hasStateKey = Object.prototype.hasOwnProperty.call(args, "stateKey");
  const hasStateValue = Object.prototype.hasOwnProperty.call(args, "stateValue");
  if (hasStateKey !== hasStateValue) throw new TypeError("stateKey and stateValue must be supplied together.");
  if (hasStateKey) {
    if (typeof args.stateKey !== "string" || args.stateKey.length === 0) throw new TypeError("stateKey must be a non-empty string.");
    const value = args.stateValue;
    if (value !== null && typeof value !== "string" && typeof value !== "boolean" &&
        (typeof value !== "number" || !Number.isFinite(value)))
      throw new TypeError("stateValue must be a primitive JSON value.");
    if (typeof value === "number" && Number.isInteger(value) && !Number.isSafeInteger(value))
      throw new TypeError("Integer stateValue must be a safely distinguishable JSON number.");
    result.state = { key: args.stateKey, value: value as WorldPrimitive };
  }
  const hasRelative = Object.prototype.hasOwnProperty.call(args, "relativeToObjectId");
  const hasMaxDistance = Object.prototype.hasOwnProperty.call(args, "maxDistance");
  if (hasRelative !== hasMaxDistance) throw new TypeError("relativeToObjectId and maxDistance must be supplied together.");
  if (hasRelative) {
    if (typeof args.relativeToObjectId !== "string" || args.relativeToObjectId.length === 0)
      throw new TypeError("relativeToObjectId must be a non-empty string.");
    if (typeof args.maxDistance !== "number" || !Number.isFinite(args.maxDistance) || args.maxDistance < 0)
      throw new TypeError("maxDistance must be a finite non-negative number.");
    result.near = { relativeToObjectId: args.relativeToObjectId, maxDistance: args.maxDistance };
  }
  if (Object.prototype.hasOwnProperty.call(args, "limit")) {
    if (!hasRelative) throw new TypeError("relativeToObjectId and maxDistance are required when limit is supplied.");
    if (typeof args.limit !== "number" || !Number.isInteger(args.limit) || args.limit < 1 || args.limit > 4294967295)
      throw new TypeError("limit must be an integer from 1 through 4294967295.");
    result.limit = args.limit;
  }
  return result as GuaWorldSelector;
}

export interface WebMcpModelContext { registerTool(definition: { name: string; description: string; inputSchema: Record<string, unknown>; execute(args: Record<string, unknown>): Promise<unknown> }): void }
export function registerWorldWebMcpTools(modelContext: WebMcpModelContext | undefined, provider: GuaWorldProvider): boolean {
  if (modelContext === undefined) return false;
  for (const tool of worldObservationTools) modelContext.registerTool({ ...tool, execute: async (args) => {
    if (tool.name === "get_world_object_tree") {
      if (Object.keys(args).length !== 0) throw new TypeError(`Unknown get_world_object_tree argument: ${Object.keys(args)[0]}.`);
      return provider.getWorldObjectTree();
    }
    if (tool.name === "find_world_objects" && Object.prototype.hasOwnProperty.call(args, "timeoutMs"))
      throw new TypeError("Unknown find_world_objects argument: timeoutMs.");
    const selector = selectorFromArguments(args);
    if (tool.name === "find_world_objects") return parseWorldQueryResult(await provider.findWorldObjects(selector), selector);
    if (Object.prototype.hasOwnProperty.call(args, "timeoutMs") &&
        (typeof args.timeoutMs !== "number" || !Number.isInteger(args.timeoutMs) || args.timeoutMs < 1 || args.timeoutMs > 300000))
      throw new TypeError("timeoutMs must be an integer from 1 through 300000.");
    const timeoutMs = typeof args.timeoutMs === "number" ? args.timeoutMs : 5000;
    return provider.waitForWorldObject(selector, timeoutMs);
  }});
  return true;
}
