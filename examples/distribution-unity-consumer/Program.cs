using System.Text.Json;
using Gua.Core;
using Gua.Testing;
using Gua.Testing.Unity;

if (args.Length is < 5 or > 6) throw new ArgumentException("mode target project output sourceCommit [unityExecutable]");
var mode=args[0]; var output=Path.GetFullPath(args[3]); Directory.CreateDirectory(output);
foreach(var key in new[]{"GUA_NATIVE_DIR","GUA_RUNTIME_NATIVE_DIR"})
    if(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))) throw new Exception("Native override forbidden: "+key);
var options=new UnitySceneTestHostOptions {
    UnityExecutablePath=args.Length==6 ? args[5] : Environment.GetEnvironmentVariable("UNITY_EXECUTABLE"),
    ProjectPath=Path.GetFullPath(args[2]), ConnectTimeout=TimeSpan.FromSeconds(150),
    AdditionalArguments=mode=="editor" ? new[]{"-batchmode","-disable-assembly-updater"} : new[]{"-batchmode"}
};
using var host=mode=="editor" ? UnitySceneTestHost.LoadEditor(args[1],options) :
    mode=="player" ? UnitySceneTestHost.LoadRenderedPlayer(args[1],options) : throw new Exception("Unknown mode");
var remote=host.RemoteContext; var version=remote.GetVersion();
if(version.BuildId!=args[4] || !version.AdapterVersions!.ContainsKey("unity")) throw new Exception("Candidate engine provenance mismatch");
version.EnsureCompatible(abiVersion:1,protocolSchemaVersion:"2");
File.WriteAllText(Path.Combine(output,"version.json"),JsonSerializer.Serialize(version));
var deadline=DateTime.UtcNow+TimeSpan.FromSeconds(20);
GuaRemoteTree? initial=null;
while(DateTime.UtcNow<deadline) { initial=remote.GetRemoteTree(); if(initial.Screen=="title" && initial.Nodes.Any(n=>n.Id=="start" && n.Role=="button" && n.Visible && n.Enabled && n.Actions.Contains("click"))) break; Thread.Sleep(50); }
if(initial?.Screen!="title") throw new Exception("Required initial title UI missing");
File.WriteAllText(Path.Combine(output,"initial-ui.json"),remote.GetUiTreeJson());
var starts=initial.Nodes.Where(n=>n.Id=="start" && n.Role=="button" && n.Visible && n.Enabled && n.Actions.Contains("click")).ToArray();
if(starts.Length!=1) throw new Exception("Required unique actionable Start Game control missing");
var before=remote.GetRemoteTree();
var rejected=remote.EnqueueAction(new(GuaActionType.Click,"missing-artifact-target"),out _);
if(rejected!=GuaActionError.NodeNotFound || remote.GetRemoteTree().Screen!="title") throw new Exception("Missing-target rejection or no-side-effect assertion failed");
using var trace=new GuaTraceSession(new GuaTraceOptions {OutputDirectory=Path.Combine(output,"trace"),SavePolicy=GuaTraceSavePolicy.Always});
var step=trace.BeginStep(GuaTraceStepKind.Action,"artifact Unity "+mode+" click");
GuaTraceCapture.Tree(trace,step,"ui","unity-"+mode,"before",()=>remote.GetUiTreeJson());
if(remote.EnqueueAction(new(GuaActionType.Click,starts[0].Id),out var request)!=GuaActionError.None || request==0) throw new Exception("Candidate click was not accepted");
GuaActionEvent? completion=null; deadline=DateTime.UtcNow+TimeSpan.FromSeconds(10);
while(DateTime.UtcNow<deadline) { if(remote.TryPollActionEvent(request,out var fact)) {completion=fact;break;} Thread.Sleep(20); }
if(completion is not {Succeeded:true,Error:GuaActionError.None} || completion.Value.RequestId!=request || completion.Value.NodeId!=starts[0].Id) throw new Exception("Correlated successful click completion missing");
deadline=DateTime.UtcNow+TimeSpan.FromSeconds(10); var final=remote.GetRemoteTree();
while(final.Screen!="loading" && DateTime.UtcNow<deadline) {Thread.Sleep(20);final=remote.GetRemoteTree();}
if(final.Screen!="loading" || !final.Nodes.Any(n=>n.Text=="Loading..." && n.Visible)) throw new Exception("Completed click did not produce observed loading UI");
GuaTraceCapture.Tree(trace,step,"ui","unity-"+mode,"after",()=>remote.GetUiTreeJson());
trace.Record(step,"caller.fact",JsonSerializer.SerializeToElement(new {requestId=request.ToString(),completion,observedScreen=final.Screen}));
trace.EndStep(step,GuaTraceOutcome.Passed);
if(!await trace.CompleteAsync(GuaTraceOutcome.Passed)) throw new Exception("Trace finalization failed");
var saved=GuaTraceReader.Read(trace.ArtifactPath);
if(!saved.Manifest.Finalized || saved.Issues.Count!=0 || saved.Blobs.Count<2) throw new Exception("Real UI Trace observation round trip failed");
var report=GuaTraceReport.WriteHtml(trace.ArtifactPath,Path.Combine(output,"report.html"));
if(!report.Succeeded) throw new Exception("Packaged Viewer report failed");
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {mode,sourceCommit=args[4],version,requestId=request.ToString(),completion,missingTarget=rejected.ToString(),initialScreen=before.Screen,finalScreen=final.Screen,trace=trace.ArtifactPath,report=report.Path},new JsonSerializerOptions {WriteIndented=true}));
Console.WriteLine("Actual artifact Unity "+mode+" route passed: "+output);




