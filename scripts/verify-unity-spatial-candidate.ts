import { readFile, writeFile, readdir, mkdir, mkdtemp, copyFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { createHash } from "node:crypto";
import { assertEngineEvidence } from "./spatial-engine-evidence";

const [rootArg,outputArg,consumerArg,rid]=process.argv.slice(2);
const root=resolve(rootArg!), output=resolve(outputArg!);
await mkdir(output,{recursive:true});
async function executable(kind:string) {
  const target=join(root,kind,"Spatial"+(rid==="win-x64"?".exe":rid!.startsWith("osx-")?".app":""));
  let binary=target;
  if(target.endsWith(".app")) {
    const directory=join(target,"Contents/MacOS"); const files=await readdir(directory,{withFileTypes:true});
    const executables=files.filter(f=>f.isFile());if(executables.length!==1) throw Error("Expected one actual spatial Player executable");
    binary=join(directory,executables[0]!.name);
    const required=rid==="osx-arm64"?"arm64":"x86_64";
    if(Bun.spawnSync(["lipo",binary,"-verify_arch",required]).exitCode!==0) throw Error("Spatial Player missing declared native slice");
  }
  // Actions' raw artifact transfer resets file modes to 644.
  if(process.platform!=="win32" && Bun.spawnSync(["chmod","u+x",binary]).exitCode!==0) throw Error("Cannot restore actual Player execute permission");
  await writeFile(join(output,kind+"-player-provenance.json"),JSON.stringify({rid,sourceCommit:process.env.GUA_BUILD_ID,recipeCommit:process.env.GUA_RECIPE_ID,sha256:createHash("sha256").update(await readFile(binary)).digest("hex")},null,2));
  return binary;
}
const geometry=await mkdtemp(join(output,"geometry-"));
await copyFile("protocol/fixtures/spatial-engine-r1.json",join(geometry,"spatial-engine-r1.json"));
const child=Bun.spawn([await executable("Geometry"),"-batchmode","-nographics","-logFile",join(geometry,"engine.log")],{cwd:geometry,env:{...process.env,GUA_FIXTURE_DIRECTORY:geometry},stdout:"ignore",stderr:"ignore"});
const timer=setTimeout(()=>child.kill(),180000);
try {if(await child.exited!==0) throw Error("Actual Unity geometry Player failed");}
finally {clearTimeout(timer);if(child.exitCode===null) child.kill();await child.exited;}
assertEngineEvidence(JSON.parse(await readFile(join(geometry,"evidence.json"),"utf8")),JSON.parse(await readFile("protocol/fixtures/spatial-engine-r1.json","utf8")));
const route=Bun.spawn([process.execPath,"scripts/verify-unity-spatial-player.ts",await executable("Transport"),join(output,"transport"),resolve(consumerArg!)],{stdout:"inherit",stderr:"inherit"});
if(await route.exited!==0) throw Error("Actual packaged spatial transport routes failed");
console.log("Actual archive-only Unity geometry/profile and TS/MCP/package spatial routes passed: "+rid);
