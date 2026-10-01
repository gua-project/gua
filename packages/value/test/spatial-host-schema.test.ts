import { test, expect } from "bun:test";
import Ajv from "ajv/dist/2020.js";
import schema from "../../../protocol/schema/spatial-host-r1.schema.json";
import legacy from "../../../protocol/schema/spatial-r1.schema.json";
import fixture from "../../../protocol/fixtures/spatial-host-r1.json";
const ajv = new Ajv({ strict: false, strictNumbers: true });
const validate = ajv.compile(schema), validateLegacy = ajv.compile(legacy);
for (const c of fixture.valid) test(`Spatial host schema ${c.id}`, () => expect(validate(c.json)).toBe(true));
for (const c of fixture.invalid.filter(c => c.schemaReject)) test(`Spatial host schema rejects ${c.id}`, () => expect((c.type <= 3 ? validateLegacy : validate)(c.json)).toBe(false));
