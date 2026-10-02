import type { GuaSpatialBatch, GuaSpatialBatchResult } from "gua-world-tools";

/** Acceptance assertions for the three-query real-engine fixture, on each route
 * independently. Comparing two equally incomplete replies is not evidence. */
export function assertSpatialRouteResult(batch: GuaSpatialBatch, value: unknown): asserts value is GuaSpatialBatchResult {
  const result = value as GuaSpatialBatchResult | null;
  if (!result || result.schemaVersion !== "spatial-host-r1" || result.documentType !== "batchResult" ||
      result.batchId !== batch.batchId || !Array.isArray(result.items) || result.items.length !== batch.queries.length ||
      batch.queries.length !== 3) throw new Error("Spatial route batch count/correlation mismatch.");
  const expected = ["hit", "detected", "blocked"];
  for (const [index, item] of result.items.entries()) {
    const query = batch.queries[index]!;
    const geometry = item?.result;
    if (!item || item.requestId !== query.requestId || item.queryId !== query.queryId ||
        item.state !== "completed" || item.reason !== undefined || !geometry ||
        geometry.schemaVersion !== "spatial-host-r1" || geometry.documentType !== "result" ||
        geometry.requestId !== query.requestId || geometry.queryId !== query.queryId ||
        geometry.sessionEpoch !== query.sessionEpoch || geometry.spaceId !== query.spaceId || geometry.spaceEpoch !== query.spaceEpoch ||
        geometry.kind !== query.kind ||
        geometry.status !== "completed" || geometry.outcome !== expected[index])
      throw new Error(`Spatial route item ${index} completion/correlation/outcome mismatch.`);
  }
}

export function spatialMcpResult(responses: any[], id: number): unknown {
  const matches = responses.filter(response => response.id === id);
  if (matches.length !== 1 || matches[0].error || matches[0].result?.isError ||
      matches[0].result?.content?.length !== 1 || matches[0].result.content[0].type !== "text")
    throw new Error("Native MCP query response count/status mismatch.");
  return JSON.parse(matches[0].result.content[0].text);
}
