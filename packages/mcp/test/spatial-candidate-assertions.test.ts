import { expect, test } from "bun:test";
import { assertCandidateLibraries } from "../../../scripts/spatial-candidate-assertions";

test("candidate consumer rejects missing, newer and source fallback libraries", () => {
  const candidate = { "Gua.Core/0.0.0-ci": { type: "package" }, "Gua.Testing/0.0.0-ci": { type: "package" } };
  expect(() => assertCandidateLibraries(candidate)).not.toThrow();
  expect(() => assertCandidateLibraries({})).toThrow("Missing candidate package");
  expect(() => assertCandidateLibraries({ "Gua.Core/0.0.0-ci": { type: "package" }, "Gua.Testing/0.1.0": { type: "package" } })).toThrow("Missing candidate package");
  expect(() => assertCandidateLibraries({ ...candidate, "Gua.Core/0.1.0": { type: "package" } })).toThrow("Unexpected Gua package version");
  expect(() => assertCandidateLibraries({ ...candidate, "Gua.Testing/0.0.0-ci": { type: "project" } })).toThrow("project reference");
});
