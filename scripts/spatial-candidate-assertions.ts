export function assertCandidateLibraries(libraries: Record<string, { type: string }>) {
  if (Object.values(libraries).some(library => library.type === "project"))
    throw Error("Consumer used a project reference");
  for (const required of ["Gua.Core/0.0.0-ci", "Gua.Testing/0.0.0-ci"])
    if (libraries[required]?.type !== "package") throw Error(`Missing candidate package ${required}`);
  for (const name of Object.keys(libraries))
    if (name.startsWith("Gua.") && name.split("/")[1] !== "0.0.0-ci")
      throw Error(`Unexpected Gua package version ${name}`);
}
