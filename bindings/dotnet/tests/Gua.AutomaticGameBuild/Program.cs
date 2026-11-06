using Gua.Runtime;
using Gua.Core;
using System.Text.Json;

using var runtime = new GuaRuntime();
using var build = JsonDocument.Parse(runtime.GetBuildInfoJson());
#if GUA_REMOTE_AUTOMATION
if (!build.RootElement.GetProperty("remoteEnabled").GetBoolean()) throw new Exception("Wrong selected prebuilt mode.");
#else
if (build.RootElement.GetProperty("remoteEnabled").GetBoolean()) throw new Exception("OFF game loaded a remote-capable DLL.");
#endif
runtime.BeginFrame("game");
runtime.RegisterNode(new("local", "button", "Local APIs remain available", new(0, 0, 10, 10)));
runtime.EndFrame();
if (!runtime.GetUiTreeJson().Contains("Local APIs remain available")) throw new Exception("Local API regression.");
#if GUA_REMOTE_AUTOMATION
if (!runtime.StartInspectorBridge(19467)) throw new Exception("Explicit remote mode failed.");
#if GUA_PLAYER_ONLY
try { runtime.SetObservationProfile(GuaObservationProfile.Debug); throw new Exception("Player ceiling escaped."); }
catch (InvalidOperationException) { }
#endif
runtime.StopInspectorBridge();
#else
if (runtime.InspectorBridgeRunning) throw new Exception("OFF game started a listener.");
#endif
Console.WriteLine("game-owned policy: " + GuaGameNativePolicy.RemoteMode);
