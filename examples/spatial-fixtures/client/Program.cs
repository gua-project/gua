using System.Text.Json;
using Gua.Core;
using Gua.Testing;
var url=args[0]; var output=Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
using var remote=new GuaWebSocketContext(url);
var info=remote.GetSpatialInfo();
var provider=info.Provider;
var queries=new[] {
    new GuaSpatialRequest { RequestId=101,QueryId="ray",Kind="raycast",Segment=new(new(0,0,0),new(0,0,4)) },
    new GuaSpatialRequest { RequestId=102,QueryId="overlap",Kind="overlap",Shape=new() {Type="sphere",Center=new(0,0,2),Radius=0.5} },
    new GuaSpatialRequest { RequestId=103,QueryId="sweep",Kind="sweep",Shape=new() {Type="sphere",Center=new(0,0,0),Radius=0.25},Delta=new(0,0,4) },
};
foreach(var q in queries) { q.SessionEpoch=1; q.SpaceId=provider.SpaceId; q.SpaceEpoch=provider.SpaceEpoch; q.QueryPolicyId="solid"; q.DeadlineMs=1000; q.Consistency="samePhysicsSample"; }
using var trace=new GuaTraceSession(new GuaTraceOptions {OutputDirectory=output,SavePolicy=GuaTraceSavePolicy.Always});
var result=remote.QuerySpatialBatch(new() {BatchId=100,Consistency="samePhysicsSample",Queries=queries},TimeSpan.FromSeconds(5),trace:trace);
var expected=new[]{"hit","detected","blocked"};
if(result.Items.Where((i,index)=>i.State!="completed" || i.Result?.Status!="completed" || i.Result.Outcome!=expected[index]).Any()) throw new Exception("Real geometry route did not produce ray hit, overlap detection and blocked sweep.");
if(result.Items.Select(i=>i.Result!.Sample!.PhysicsSampleId).Distinct().Count()!=1) throw new Exception("Sample correlation drift.");
await trace.CompleteAsync(GuaTraceOutcome.Passed);
var directories=Directory.GetDirectories(output).Where(d=>File.Exists(Path.Combine(d,"manifest.json"))).ToArray();
var saved=GuaTraceReader.Read(directories.Single());
if(saved.Events.Any(e=>e.Type=="step.begin" && e.Data.GetProperty("kind").GetString()=="action")) throw new Exception("Read polluted input budget.");
if(!saved.Blobs.Values.Any(b=>b.TryGetProperty("advertisement",out _))) throw new Exception("Spatial attachment missing.");
var report=GuaTraceReport.WriteHtml(directories.Single(),Path.Combine(output,"spatial-report.html"));
if(!report.Succeeded) throw new Exception(report.Error);
File.WriteAllText(Path.Combine(output,"client-evidence.json"),JsonSerializer.Serialize(new {info,result,trace=saved.Manifest,report=report.Path},new JsonSerializerOptions {WriteIndented=true}));
Console.WriteLine("Gua-only real spatial route and offline report passed.");
