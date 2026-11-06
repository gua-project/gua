using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

if(args.Length!=2 || args[1] is not ("testing" or "player")) throw new ArgumentException("<loopback-ws-url> <testing|player>");
using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
using var socket=new ClientWebSocket();
await socket.ConnectAsync(new Uri(args[0]),deadline.Token);
var clock=await Command(1,"clock_install",new{initialTimeMs=0,stepMs=10});
if(clock.GetProperty("ok").GetBoolean()!=(args[1]=="testing")) throw new Exception("Compiled Player clock ceiling violated.");
var tree=await Command(2,"get_ui_tree");
if(!tree.GetProperty("ok").GetBoolean()) throw new Exception("Real engine UI tree unavailable.");
var buttons=Buttons(tree).ToArray();
if(buttons.Length!=1) throw new Exception("Expected exactly one public fixture button, got "+buttons.Length);
var click=await Command(3,"click_node",new{nodeId=buttons[0]});
if(!click.GetProperty("ok").GetBoolean()) throw new Exception("Allowed public UI action rejected.");
Console.WriteLine(JsonSerializer.Serialize(new{publicUiActionAccepted=true,clockAllowed=args[1]=="testing"}));
socket.Abort();

async Task<JsonElement> Command(int id,string type,object? extra=null) {
    var request=new Dictionary<string,object?>{{"id",id},{"type",type}};
    if(extra!=null) foreach(var item in JsonSerializer.SerializeToElement(extra).EnumerateObject()) request[item.Name]=item.Value;
    await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)),WebSocketMessageType.Text,true,deadline.Token);
    while(true) {
        using var message=new MemoryStream(); WebSocketReceiveResult received;
        do {var buffer=new byte[8192]; received=await socket.ReceiveAsync(new ArraySegment<byte>(buffer),deadline.Token); message.Write(buffer,0,received.Count);} while(!received.EndOfMessage);
        using var parsed=JsonDocument.Parse(message.ToArray());
        if(parsed.RootElement.TryGetProperty("id",out var replyId) && replyId.ValueKind==JsonValueKind.Number && replyId.GetInt32()==id) return parsed.RootElement.Clone();
    }
}
static IEnumerable<string> Buttons(JsonElement element) {
    if(element.ValueKind==JsonValueKind.Object) {
        if(element.TryGetProperty("role",out var role) && role.GetString()=="button" && element.TryGetProperty("id",out var id)) yield return id.GetString()!;
        foreach(var property in element.EnumerateObject()) foreach(var found in Buttons(property.Value)) yield return found;
    } else if(element.ValueKind==JsonValueKind.Array) foreach(var child in element.EnumerateArray()) foreach(var found in Buttons(child)) yield return found;
}
