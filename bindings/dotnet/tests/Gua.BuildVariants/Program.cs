using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Gua.Core;
using Gua.Runtime;

static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
Check(args.Length == 4, "core directory, runtime directory, expected flavor, expected remote on/off are required");
Environment.SetEnvironmentVariable("GUA_NATIVE_DIR", Path.GetFullPath(args[0]));
Environment.SetEnvironmentVariable("GUA_RUNTIME_NATIVE_DIR", Path.GetFullPath(args[1]));
Environment.SetEnvironmentVariable("GUA_OBSERVATION_PROFILE", null);
bool remote=args[3]=="on", player=args[2]=="ai-player";
using var runtime=new GuaRuntime();
using var identity=JsonDocument.Parse(runtime.GetBuildInfoJson());
Check(identity.RootElement.GetProperty("flavor").GetString()==args[2],"wrong flavor");
Check(identity.RootElement.GetProperty("remoteEnabled").GetBoolean()==remote,"wrong compiled availability");
Check(runtime.GetBuildInfoJson()==GuaBuildInfo.GetJson(),"core/runtime mismatch");
runtime.BeginFrame("wire");
runtime.RegisterNode(new("public","button","Public",new(0,0,10,10)));
runtime.RegisterNode(new("secret","button","Hidden secret",new(0,0,10,10),AgentPolicy:new(GuaAgentExposure.Private)));
runtime.EndFrame();
if (player) {
    Check(runtime.ObservationProfile==GuaObservationProfile.Player,"AI Player not initially Player");
    bool rejected=false;
    try { runtime.SetObservationProfile(GuaObservationProfile.Debug); } catch(InvalidOperationException) { rejected=true; }
    Check(rejected,"managed API elevated AI Player");
    Check(!runtime.GetUiTreeJson().Contains("Hidden secret"),"managed legacy tree leaked secret");
}
const int port=19462;
Check(runtime.StartInspectorBridge(port)==remote,"bridge availability mismatch");
if(!remote) { Check(!runtime.InspectorBridgeRunning && runtime.InspectorBridgeUrl=="","Shipping bridge is active"); return; }
using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
using var socket=new ClientWebSocket();
await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"),deadline.Token);
int nextId=0;
async Task<JsonElement> Command(string type, object? extra=null) {
    int id=++nextId;
    var request=new Dictionary<string,object?> { ["id"]=id,["type"]=type };
    if(extra!=null) foreach(var property in JsonSerializer.SerializeToElement(extra).EnumerateObject()) request[property.Name]=property.Value;
    await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)),WebSocketMessageType.Text,true,deadline.Token);
    while(true) {
        using var message=new MemoryStream();
        WebSocketReceiveResult received;
        do { var buffer=new byte[8192]; received=await socket.ReceiveAsync(new ArraySegment<byte>(buffer),deadline.Token); message.Write(buffer,0,received.Count); } while(!received.EndOfMessage);
        using var document=JsonDocument.Parse(message.ToArray());
        if(document.RootElement.TryGetProperty("id",out var responseId) && responseId.ValueKind==JsonValueKind.Number && responseId.GetInt32()==id) return document.RootElement.Clone();
    }
}
var tree=await Command("get_ui_tree");
Check(tree.GetProperty("ok").GetBoolean(),"wire tree failed");
Check(tree.GetRawText().Contains("Public"),"public tree missing");
Check(tree.GetRawText().Contains("Hidden secret")!=player,"wire projection incorrect");
runtime.EnableVirtualClockAdapter();
var version=await Command("get_version");
Check(version.GetProperty("ok").GetBoolean(),"version failed");
Check(version.GetRawText().Contains("virtual_clock_v1")!=player,"clock advertisement ignores compiled ceiling");
var clock=await Command("clock_install",new {initialTimeMs=0,stepMs=10});
Check(clock.GetProperty("ok").GetBoolean()!=player,"privileged clock dispatch violates variant");
var secret=await Command("click_node",new {nodeId="secret"});
Check(secret.GetProperty("ok").GetBoolean()!=player,"private UI authorization incorrect");
var click=await Command("click_node",new {nodeId="public"});
Check(click.GetProperty("ok").GetBoolean(),"public click not accepted");
Check(runtime.TryConsumeAction(GuaActionType.Click,"public",out var action),"public click did not reach host");
runtime.EmitActionResult(action,true);
socket.Abort();
runtime.StopInspectorBridge();
Console.WriteLine($"{args[2]} managed + real WebSocket acceptance passed");
