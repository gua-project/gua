import {expect,test} from "bun:test";
import {assertSpatialRedactionInspection} from "../../../scripts/spatial-redaction-assertions";
const valid=[{id:9001,ok:true,result:{capabilities:[]}},{id:9002,ok:true,result:{version:{capabilities:[]}}},{id:9003,ok:false,error:"not_authorized"}];
test("invalidation inspection requires all correlated replies and rejects stale provider declarations",()=>{
  expect(()=>assertSpatialRedactionInspection(valid)).not.toThrow();
  for(const id of [9001,9002,9003]) {
    expect(()=>assertSpatialRedactionInspection(valid.filter(reply=>reply.id!==id))).toThrow("response count/correlation");
    expect(()=>assertSpatialRedactionInspection([...valid,structuredClone(valid.find(reply=>reply.id===id)!)])).toThrow("response count/correlation");
  }
  expect(()=>assertSpatialRedactionInspection([])).toThrow("response count/correlation");
  const stale=structuredClone(valid); (stale[1]!.result!.version as any).spatial={providerId:"stale-provider"};
  expect(()=>assertSpatialRedactionInspection(stale)).toThrow("stale capability/provider metadata");
  const duplicated=[...valid,{id:9001,ok:true,result:{capabilities:["spatial_read_r1"],spatial:{providerId:"stale-provider"}}}];
  expect(()=>assertSpatialRedactionInspection(duplicated)).toThrow("response count/correlation");
});
