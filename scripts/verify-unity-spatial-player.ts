import { mkdir, mkdtemp, writeFile, readFile } from "node:fs/promises";
import { resolve, join } from "node:path";
import { createServer } from "node:net";
const [playerArg, outputArg, consumerArg]=process.argv.slice(2);
const player=resolve(playerArg!), output=resolve(outputArg!), consumer=resolve(consumerArg!);
await mkdir(output,{recursive:true}); const directory=await mkdtemp(join(output,"unity-player-"));
const server=createServer(); await new Promise<void>(r=>server.listen(0,"127.0.0.1",r)); const port=(server.address() as any).port; await new Promise<void>(r=>server.close(()=>r()));
const game=Bun.spawn([player,"-batchmode","-nographics","-logFile",join(directory,"engine.log")],{cwd:directory,env:{...process.env,GUA_BRIDGE_PORT:String(port),GUA_FIXTURE_DIRECTORY:directory},stdout:"pipe",stderr:"pipe"});
const logs=Promise.all([new Response(game.stdout).text(),new Response(game.stderr).text()]);
async function run(command:string[], log:string) {
  const child=Bun.spawn(command,{env:{...process.env,GUA_NATIVE_DIR:"",GUA_RUNTIME_NATIVE_DIR:""},stdout:"pipe",stderr:"pipe"});
  const logs=Promise.all([new Response(child.stdout).text(),new Response(child.stderr).text()]);
  const timer=setTimeout(()=>child.kill(),60000);
  try {const status=await child.exited; const [stdout,stderr]=await logs; await writeFile(join(directory,log),stdout+stderr); if(status) throw Error(`${log}: exit ${status}`);}
  finally {clearTimeout(timer);}
}
try {
  const deadline=performance.now()+20000;
  while(!(await Bun.file(join(directory,"transport-ready.json")).exists())) {
    if(game.exitCode!==null) throw Error(`Unity Player exited before readiness: ${game.exitCode}`);
    if(performance.now()>deadline) throw Error("Unity Player readiness timeout"); await Bun.sleep(25);
  }
  const ready=JSON.parse(await readFile(join(directory,"transport-ready.json"),"utf8"));
  if(process.env.GUA_BUILD_ID && ready.buildId!==process.env.GUA_BUILD_ID) throw Error("Actual spatial Player source identity mismatch");
  await run([process.execPath,"scripts/verify-spatial-route.ts",`ws://127.0.0.1:${port}`,join(directory,"typescript-mcp")],"routes.log");
  await run(["dotnet",consumer,`ws://127.0.0.1:${port}`,join(directory,"package-consumer")],"consumer.log");
  await writeFile(join(directory,"transport-done"),"done");
  let timer:ReturnType<typeof setTimeout>|undefined;
  let status:number;
  try {status=await Promise.race([game.exited,new Promise<never>((_,reject)=>{timer=setTimeout(()=>reject(Error("Unity completion timeout")),15000);})]);}
  finally {clearTimeout(timer);}
  if(status!==0) throw Error(`Unity Player completion failed: ${status}`);
  const evidence=JSON.parse(await readFile(join(directory,"transport-host-evidence.json"),"utf8"));
  if(evidence.physicsBatches<3 || evidence.backend!=="PhysX" || evidence.version!=="6000.5.3f1") throw Error("Required pinned Unity physics execution missing");
  console.log("Real Unity Mono Player: TypeScript/MCP/package consumer routes passed.");
} finally {if(game.exitCode===null) game.kill(); await game.exited; const [stdout,stderr]=await logs; await writeFile(join(directory,"stdio.log"),stdout+stderr);}
