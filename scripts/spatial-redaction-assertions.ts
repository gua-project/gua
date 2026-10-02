// Required responses on the invalidated native connection, independently of
// whether a particular geometry marker happens to appear.
export function assertSpatialRedactionInspection(replies:any[]):void {
  const one=(id:number)=>{
    const matches=replies.filter(reply=>reply.id===id);
    if(matches.length!==1) throw Error("Spatial redaction inspection response count/correlation mismatch");
    return matches[0];
  };
  const version=one(9001), diagnostics=one(9002), info=one(9003);
  for(const [reply,value] of [[version,version.result],[diagnostics,diagnostics.result?.version]]) {
    if(reply.ok!==true || !value || !Array.isArray(value.capabilities) ||
       value.capabilities.includes("spatial_read_r1") || value.spatial!==undefined)
      throw Error("Spatial redaction inspection exposes stale capability/provider metadata");
  }
  if(info.ok!==false || info.error!=="not_authorized") throw Error("Spatial redaction inspection did not safely reject discovery");
}
