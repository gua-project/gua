import { mkdir, writeFile } from "node:fs/promises";
import { GuaBridgeClient } from "../packages/mcp/src/index";
import type { GuaSpatialBatch } from "gua-world-tools";
const url=process.argv[2] ?? "ws://127.0.0.1:8874", output=process.argv[3] ?? "artifacts/spatial-route-ts";
const client=new GuaBridgeClient(url);
try {
  const info=await client.spatialInfo();
  const common={schemaVersion:"spatial-r1" as const,documentType:"request" as const,sessionEpoch:1,
    spaceId:info.provider.spaceId,spaceEpoch:info.provider.spaceEpoch,queryPolicyId:"solid",deadlineMs:1000,consistency:"samePhysicsSample" as const};
  const batch:GuaSpatialBatch={schemaVersion:"spatial-host-r1",documentType:"batch",batchId:200,consistency:"samePhysicsSample",queries:[
    {...common,requestId:201,queryId:"ray",kind:"raycast",segment:{from:{x:0,y:0,z:0},to:{x:0,y:0,z:4}}},
    {...common,requestId:202,queryId:"overlap",kind:"overlap",shape:{type:"sphere",center:{x:0,y:0,z:2},radius:0.5}},
    {...common,requestId:203,queryId:"sweep",kind:"sweep",shape:{type:"sphere",center:{x:0,y:0,z:0},radius:0.25},delta:{x:0,y:0,z:4}},
  ]};
  const result=await client.spatialBatch(batch);
  const expected=["hit","detected","blocked"];
  await mkdir(output,{recursive:true});
  await writeFile(output+"/bridge-result.json",JSON.stringify(result,null,2));
  if(result.items.some((item,index)=>item.state!=="completed" || item.result?.status!=="completed" || item.result?.outcome!==expected[index])) throw new Error("Real engine did not return ray hit, overlap detection and blocked sweep.");
  const processHandle=Bun.spawn([process.execPath,"packages/mcp/dist/cli.js","mcp"],{
    env:{...process.env,GUA_BRIDGE_URL:url},stdin:"pipe",stdout:"pipe",stderr:"pipe"});
  const requests=[{jsonrpc:"2.0",id:1,method:"tools/list"},
    {jsonrpc:"2.0",id:2,method:"tools/call",params:{name:"get_spatial_info",arguments:{}}},
    {jsonrpc:"2.0",id:3,method:"tools/call",params:{name:"query_spatial_batch",arguments:{batch}}}];
  processHandle.stdin.write(requests.map(request=>JSON.stringify(request)).join("\n")+"\n");
  processHandle.stdin.end();
  const text=await new Response(processHandle.stdout).text();
  const stderr=await new Response(processHandle.stderr).text();
  if(await processHandle.exited) throw new Error("Packaged native MCP failed: "+stderr);
  const responses=text.trim().split("\n").map(line=>JSON.parse(line));
  const response=responses.find(response=>response.id===3);
  if(!response || response.error || response.result.isError) throw new Error("Native MCP query failed.");
  const mcpResult=JSON.parse(response.result.content[0].text);
  if(mcpResult.items.some((item:any,index:number)=>item.result?.outcome!==result.items[index]?.result?.outcome)) throw new Error("Route semantics differ.");
  await mkdir(output,{recursive:true});
  await writeFile(output+"/evidence.json",JSON.stringify({url,info,batch,result,mcpResponses:responses,stderr},null,2));
  console.log("Real engine TypeScript bridge and built native MCP routes passed.");
} finally {client.close();}
