export function assertEngineEvidence(evidence:any, fixture:any) {
  const cases=fixture.cases as {id:string}[];
  if(!Array.isArray(evidence.results) || evidence.results.length!==cases.length*2+1 ||
     !Array.isArray(evidence.leaseRaces) || evidence.leaseRaces.length!==cases.length+1 ||
     !Array.isArray(evidence.profile) || evidence.profile.length!==360) throw Error("Incomplete real engine geometry/profile/race evidence");
  const id=(item:any)=>item.case ?? item.id;
  for(const item of cases) {
    const count=evidence.results.filter((r:any)=>id(r)===item.id).length;
    const expected=item.id==="door-transition" && evidence.results.some((r:any)=>r.case===item.id) ? 3 : 2;
    if(count!==expected) throw Error("Required geometry case evidence missing: "+item.id);
    if(evidence.leaseRaces.filter((r:any)=>id(r)===item.id).length!==(item.id===cases[0]!.id?2:1)) throw Error("Required lease race evidence missing: "+item.id);
  }
  if(evidence.results.some((r:any)=>!cases.some(c=>c.id===id(r)) && id(r)!=="door-transition-open")) throw Error("Unexpected geometry case evidence");
  for(const size of [0,1,16]) {
    const samples=evidence.profile.filter((r:any)=>r.batchSize===size);
    if(samples.length!==120 || samples.filter((r:any)=>r.phaseMainThreadCpuUs!==null && r.phaseMainThreadCpuUs!==undefined).length!==1) throw Error("Required physics callback phase missing: "+size);
    for(const sample of samples) {
      if(!["GetThreadTimes","clock_gettime(CLOCK_THREAD_CPUTIME_ID)"].includes(sample.threadCpuClock) ||
         !Number.isFinite(sample.threadCpuUs) || sample.threadCpuUs<0 ||
         !Number.isFinite(sample.callbackWallUs) || sample.callbackWallUs<0 ||
         (sample.threadCpuClock!=="GetThreadTimes" && sample.threadCpuCycles!==null)) throw Error("Missing or fabricated actual thread CPU evidence");
    }
  }
}
