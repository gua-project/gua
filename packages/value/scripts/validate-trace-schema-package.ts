import Ajv from "ajv/dist/2020.js";
import { readdirSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import fixture from "../../../protocol/fixtures/trace-observations.json";

// Run against an extracted Gua.Testing nupkg, never against repository schemas.
const directory = process.argv[2];
if (!directory) throw new Error("Pass the extracted NuGet trace directory");
const ajv = new Ajv({ strict: false });
for (const file of readdirSync(directory).filter(file => file.endsWith(".schema.json")))
  ajv.addSchema(await Bun.file(join(directory, file)).json());
const structure = ajv.getSchema("https://gua.orizika.com/schema/trace-v1.json");
if (!structure) throw new Error("Packaged Trace schema is missing");
const { validateTraceObserveSemantics } = await import(pathToFileURL(join(directory, "trace-observe-semantics.mjs")).href);
const validate = (record: unknown) => structure(record) && validateTraceObserveSemantics(record);
const received = { schemaVersion: 1, sourceId: "game", profile: "debug", sessionEpoch: "1", sequence: "1",
  revision: "1", uiFrame: "0", uiRevision: "0", worldFrame: "0", worldRevision: "0", ownerId: "1", registrationId: "1",
  source: "ui", runtimeId: "n", name: "value", boundary: "frame", ...fixture.transitions[0] };
const record = { schemaVersion: 1, traceId: fixture.traceId, stepId: fixture.stepId, sequence: 1, eventId: "e1",
  collectedMilliseconds: 0, type: "observation.change", data: { channel: "observe", reason: "offline", intervalId: fixture.intervalId,
    continuity: "continuous", change: received.kind, host: fixture.host, received, catalogs: {} } };
if (!validate(record)) throw new Error(JSON.stringify(structure.errors));
record.data.catalogs = { after: { schemaVersion: 1, enums: [] } };
if (validate(record)) throw new Error("Packaged catalog constraints were lost");
const enumRecord = structuredClone(record) as any;
enumRecord.data.received.after = { type: "enum", enumType: "game.Phase", value: "First" };
enumRecord.data.catalogs = { after: { schemaVersion: 1, enums: [{ enumType: "game.Phase", members: ["First", "Second"] }] } };
if (!validate(enumRecord)) throw new Error("Valid paired enum must pass");
enumRecord.data.catalogs.after.enums[0].enumType = "game.Other";
if (validate(enumRecord)) throw new Error("Mismatched enum type must fail");
enumRecord.data.catalogs.after.enums[0] = { enumType: "game.Phase", members: ["Second"] };
if (validate(enumRecord)) throw new Error("Missing enum member must fail");
console.log("Packaged Trace schema compiles and validates Observe offline");
