import { test, expect } from "bun:test";
import Ajv from "ajv/dist/2020.js";
import diagnostics from "../../../protocol/schema/trace-diagnostics.schema.json";
import recording from "../../../protocol/schema/trace-recording.schema.json";
import originalRecording from "../../../protocol/schema/recording.schema.json";
const ajv = new Ajv({ strict: false });
const validDiagnostics = ajv.compile(diagnostics), validRecording = ajv.compile(recording);
const rawRecording = ajv.compile(originalRecording);
const sensitive = { schemaVersion: 1, steps: [{ redacted: true }] };
test("Trace recording envelope accepts redacted steps without claiming original format", () => {
  expect(validRecording({ schemaVersion: 1, recording: sensitive })).toBe(true);
  expect(rawRecording(sensitive)).toBe(false);
});
test("Trace recording envelope rejects missing data and unknown major", () => {
  expect(validRecording({ schemaVersion: 1 })).toBe(false);
  expect(validRecording({ schemaVersion: 2, recording: sensitive })).toBe(false);
});
test("Trace diagnostics projection requires metadata and rejects pixels", () => {
  const data = { schemaVersion: 1, sessionEpoch: 1, frameSequence: 0, revision: 0, historyLimit: 0,
    pendingRequestCount: 0, inFlightRequestCount: 0, unconsumedEventCount: 0, environment: {}, version: {},
    uiTree: { nodes: [{ redacted: true }] }, pendingRequests: [], operations: [], events: [], logs: [] };
  expect(validDiagnostics(data)).toBe(true);
  expect(validDiagnostics({ ...data, screenshot: null })).toBe(false);
  expect(validDiagnostics({})).toBe(false);
});
