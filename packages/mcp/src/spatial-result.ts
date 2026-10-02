import { Ajv2020 } from "ajv/dist/2020.js";
import { spatialBatchResultSchema, type GuaSpatialBatchResult } from "gua-world-tools";

// Validate the complete wire document, including nested required fields and
// additionalProperties, without duplicating physics or the native semantics.
const validate = new Ajv2020({ strict: false, strictNumbers: true }).compile(spatialBatchResultSchema);
export function isSpatialBatchResult(value: unknown): value is GuaSpatialBatchResult {
  return validate(value);
}
