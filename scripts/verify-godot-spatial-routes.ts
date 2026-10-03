import { cp, mkdir, readFile, writeFile, mkdtemp } from "node:fs/promises";
import { resolve, join } from "node:path";
import { tmpdir } from "node:os";
import { createServer } from "node:net";
import { assertCandidateLibraries } from "./spatial-candidate-assertions";
import { assertEngineEvidence } from "./spatial-engine-evidence";
import { createHash } from "node:crypto";
const [executableArg, addonArg, outputArg, packageArg] = process.argv.slice(2);
const executable=resolve(executableArg!), output=resolve(outputArg!);
let addon=resolve(addonArg!);
await mkdir(output,{recursive:true});
async function run(command:string[], log:string, env:Record<string,string|undefined>={}) {
  const child=Bun.spawn(command,{cwd:process.cwd(),env:{...process.env,...env},stdout:"pipe",stderr:"pipe"});
  const logs=Promise.all([new Response(child.stdout).text(),new Response(child.stderr).text()]);
  const timer=setTimeout(()=>child.kill(),120000);
  try {const status=await child.exited; const [stdout,stderr]=await logs; await writeFile(join(output,log),stdout+stderr); if(status) throw Error(`${command[0]} exited ${status}; see ${log}`);}
  finally {clearTimeout(timer);}
}
let consumer:string|undefined;
if(addon.endsWith(".zip")) {
  const archive=addon;
  const extracted=await mkdtemp(join(tmpdir(),"gua-godot-archive-"));
  await run(["pwsh","-NoProfile","-File","scripts/expand-godot-candidate-archive.ps1","-ArchivePath",archive,"-OutputDirectory",extracted],"archive-extract.log");
  addon=join(extracted,"addons/gua");
  await writeFile(join(output,"archive-provenance.json"),JSON.stringify({archive,sha256:createHash("sha256").update(await readFile(archive)).digest("hex"),sourceCommit:process.env.GUA_BUILD_ID,recipeCommit:process.env.GUA_RECIPE_ID},null,2));
}
if(packageArg) {
  consumer=await mkdtemp(join(tmpdir(),"gua-spatial-consumer-"));
  await cp("examples/spatial-fixtures/client/Program.cs",join(consumer,"Program.cs"));
  await cp("examples/spatial-fixtures/client/SpatialClient.csproj",join(consumer,"SpatialClient.csproj"));
  const feed=resolve(packageArg), config=join(consumer,"NuGet.Config");
  await writeFile(config,`<configuration><packageSources><clear/><add key="local" value="${feed.replaceAll("&","&amp;")}"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><packageSource key="local"><package pattern="Gua.*"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping></configuration>`);
  const env={NUGET_PACKAGES:join(consumer,"cache"),GUA_NATIVE_DIR:"",GUA_RUNTIME_NATIVE_DIR:""};
  await run(["dotnet","build",join(consumer,"SpatialClient.csproj"),"-c","Release",`-p:RestoreConfigFile=${config}`,"-p:GuaUsePackages=true","-p:GuaPackageVersion=[0.0.0-ci]"],"consumer-build.log",env);
  const assets=JSON.parse(await readFile(join(consumer,"obj/project.assets.json"),"utf8"));
  assertCandidateLibraries(assets.libraries);
  await writeFile(join(output,"consumer-assets.json"),JSON.stringify(assets,null,2));
  await writeFile(join(output,"consumer-provenance.json"),JSON.stringify({project:consumer,feed,assembly:join(consumer,"bin/Release/net10.0/SpatialClient.dll"),nativeDirectoryOverrides:"cleared",projectReferences:0},null,2));
}
for(const backend of ["GodotPhysics3D","Jolt Physics"]) {
  const directory=await mkdtemp(join(output,backend.replaceAll(" ","-")+"-")); await mkdir(join(directory,".godot"),{recursive:true});
  await cp("examples/spatial-fixtures/godot",directory,{recursive:true});
  await cp(addon,join(directory,"addons/gua"),{recursive:true});
  await cp("protocol/fixtures/spatial-engine-r1.json",join(directory,"spatial-engine-r1.json"));
  let project=await readFile(join(directory,"project.godot"),"utf8");
  await writeFile(join(directory,"project.godot"),project.replace("GodotPhysics3D",backend));
  const scene=await readFile(join(directory,"fixture.tscn"),"utf8");
  await writeFile(join(directory,"fixture.tscn"),scene.replace("res://fixture.gd","res://transport.gd"));
  // Minimal deterministic import metadata for this source-only fixture. No
  // editor execution or geometry/result cache is needed or claimed.
  await writeFile(join(directory,".godot/extension_list.cfg"),"res://addons/gua/gua.gdextension\n");
  await writeFile(join(directory,".godot/global_script_class_cache.cfg"),'list=[{\n"base": &"RefCounted",\n"class": &"GuaSpatialReader",\n"icon": "",\n"is_abstract": false,\n"is_tool": false,\n"language": &"GDScript",\n"path": "res://addons/gua/gua_spatial.gd"\n}]\n');
  await run([executable,"--headless","--path",directory,"--max-fps","60"],backend+"-geometry.log");
  const geometry=JSON.parse(await readFile(join(directory,"evidence.json"),"utf8"));
  assertEngineEvidence(geometry,JSON.parse(await readFile("protocol/fixtures/spatial-engine-r1.json","utf8")));
  const server=createServer(); await new Promise<void>(r=>server.listen(0,"127.0.0.1",r)); const port=(server.address() as any).port; await new Promise<void>(r=>server.close(()=>r()));
  const game=Bun.spawn([executable,"--headless","--path",directory],{env:{...process.env,GUA_BRIDGE_PORT:String(port)},stdout:"pipe",stderr:"pipe"});
  const logs=Promise.all([new Response(game.stdout).text(),new Response(game.stderr).text()]);
  try {
    const deadline=performance.now()+15000;
    while(!(await Bun.file(join(directory,"transport-ready.json")).exists())) {
      if(game.exitCode!==null) throw Error(`Godot ${backend} exited before readiness: ${game.exitCode}`);
      if(performance.now()>deadline) throw Error(`Godot ${backend} readiness timeout`);
      await Bun.sleep(25);
    }
    const url=`ws://127.0.0.1:${port}`;
    const ready=JSON.parse(await readFile(join(directory,"transport-ready.json"),"utf8"));
    if(process.env.GUA_BUILD_ID && ready.buildId!==process.env.GUA_BUILD_ID) throw Error("Actual Godot archive source identity mismatch");
    await run([process.execPath,"scripts/verify-spatial-route.ts",url,join(directory,"typescript-mcp")],backend+"-routes.log");
    if(consumer) await run(["dotnet",join(consumer,"bin/Release/net10.0/SpatialClient.dll"),url,join(directory,"package-consumer")],backend+"-consumer.log",{GUA_NATIVE_DIR:"",GUA_RUNTIME_NATIVE_DIR:""});
    await writeFile(join(directory,"transport-done"),"done");
    let timer:ReturnType<typeof setTimeout>|undefined;
    let status:number;
    try {status=await Promise.race([game.exited,new Promise<never>((_,reject)=>{timer=setTimeout(()=>reject(Error("Godot completion timeout")),10000);})]);}
    finally {clearTimeout(timer);}
    if(status!==0) throw Error(`Godot ${backend} completion failed: ${status}`);
    const evidence=JSON.parse(await readFile(join(directory,"transport-host-evidence.json"),"utf8"));
    if(evidence.physicsBatches < (consumer ? 3 : 2) || evidence.backend!==backend) throw Error("Host did not execute required real physics batches");
    console.log(`Godot ${backend}: actual TypeScript/MCP${consumer ? "/package consumer" : ""} routes passed.`);
  } finally {
    if(game.exitCode===null) game.kill(); await game.exited; const [stdout,stderr]=await logs;
    await writeFile(join(directory,"engine.log"),stdout+stderr);
  }
}
