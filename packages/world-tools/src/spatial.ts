import { spatialBatchInputSchema } from "./spatial-schema.generated.js";
const failureReasons = new Set<string>(spatialBatchInputSchema.$defs.batchResult.properties.items.items.oneOf[1]!.properties.reason!.enum);
/** Use the protocol-generated safe enum, never backend diagnostic text. */
export function isSpatialFailureReason(value: unknown): value is string {
  return typeof value === "string" && failureReasons.has(value);
}
export interface GuaSpatialVector { x: number; y: number; z: number }
export interface GuaSpatialBasis { x: GuaSpatialVector; y: GuaSpatialVector; z: GuaSpatialVector }
export type GuaSpatialOperation = "raycast" | "overlap" | "sweep";
export type GuaSpatialConsistency = "bestEffort" | "samePhysicsSample";
export interface GuaSpatialProvider {
  schemaVersion: "spatial-r1"; documentType: "provider"; providerId: string;
  spaceId: string; spaceEpoch: number; worldSpace: "world3d";
  basis: GuaSpatialBasis; up: GuaSpatialVector;
  unit: { label: string; metersPerUnit?: number };
  precision: { representation: "binary32" | "binary64" | "unknown"; reason: string; absoluteError?: number };
  operations: GuaSpatialOperation[]; shapes: ("sphere" | "capsule" | "box")[];
  policies: string[]; consistencies: GuaSpatialConsistency[];
  engine: { name: string; version: string; backend: string; backendVersion: string };
  limits: { maxQueriesPerBatch: number; maxHitsPerQuery: number; maxDeadlineMs: number };
}
export interface GuaSpatialAdvertisement {
  schemaVersion: "spatial-host-r1"; documentType: "advertisement";
  provider: GuaSpatialProvider;
  budgets: { maxQueueDepth: number; queryDeadlineMs: number; maxBatchWorkTimeMs: number };
}
export type GuaSpatialShape =
  | { type: "sphere"; center: GuaSpatialVector; radius: number }
  | { type: "capsule"; pointA: GuaSpatialVector; pointB: GuaSpatialVector; radius: number }
  | { type: "box"; center: GuaSpatialVector; halfExtents: GuaSpatialVector; basis: GuaSpatialBasis };
export interface GuaSpatialRequest {
  schemaVersion: "spatial-r1"; documentType: "request"; requestId: number;
  sessionEpoch: number; queryId: string; spaceId: string; spaceEpoch: number;
  queryPolicyId: string; kind: "raycast" | "overlap" | "sweep"; deadlineMs: number;
  consistency: "bestEffort" | "samePhysicsSample"; segment?: { from: GuaSpatialVector; to: GuaSpatialVector };
  shape?: GuaSpatialShape; delta?: GuaSpatialVector; maxHits?: number;
}
export interface GuaSpatialBatch {
  schemaVersion: "spatial-host-r1"; documentType: "batch"; batchId: number;
  consistency: "bestEffort" | "samePhysicsSample"; queries: GuaSpatialRequest[];
}
export interface GuaSpatialBatchResult {
  schemaVersion: "spatial-host-r1"; documentType: "batchResult"; batchId: number;
  items: { requestId: number; queryId: string; state: "completed" | "failed" | "notExecuted";
    reason?: string; result?: Record<string, unknown> }[];
}
export const spatialTools = [
  { name: "get_spatial_info" as const, description: "Read an explicitly enabled Testing/Debug spatial provider and effective limits. Unsupported for Player and browser public agents.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false } },
  { name: "query_spatial_batch" as const, description: "Execute one bounded read batch through the host-owned provider. Does not guarantee movement, change collision policy, or add Recording input.",
    inputSchema: spatialBatchInputSchema },
];
/** Only checks the bounded envelope. Native host is the authoritative validator,
 * including geometry, policy, epoch and backend semantics. No physics here. */
export function spatialBatchArguments(args: Record<string, unknown>): GuaSpatialBatch {
  if (Object.keys(args).length !== 1 || !("batch" in args) || typeof args.batch !== "object" || args.batch === null)
    throw new Error("invalid_request");
  const batch = args.batch as GuaSpatialBatch;
  if (batch.schemaVersion !== "spatial-host-r1" || batch.documentType !== "batch" ||
    !Number.isSafeInteger(batch.batchId) || batch.batchId < 1 || !Array.isArray(batch.queries) ||
    batch.queries.length < 1 || batch.queries.length > 64 || new TextEncoder().encode(JSON.stringify(args)).length > 1048000)
    throw new Error("invalid_request");
  return batch;
}
