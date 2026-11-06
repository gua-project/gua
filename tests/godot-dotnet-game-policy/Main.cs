using System;
using System.IO;
using System.Text.Json;
using Godot;
using Gua.Core;
using Gua.Runtime;
using Gua.Godot;

[Tool]
public partial class Main : Control
{
    private GuaGodotRuntime? host;
    private int clicks, framesAfterClick;
    private string output="";
    private static string Mode=>GuaGameNativePolicy.RemoteMode;
    public override void _Ready()
    {
        if(Engine.IsEditorHint()){GD.Print("GUA_DOTNET_EDITOR_READY");return;}
        output=System.Environment.GetEnvironmentVariable("GUA_GODOT_TEST_RESULT")??throw new InvalidOperationException("Missing fixture output.");
        try {RunReady();}
        catch(InvalidOperationException error) when(System.Environment.GetEnvironmentVariable("GUA_GODOT_EXPECT_PROFILE_REFUSAL")=="1" && error.Message=="Failed to create a Gua runtime.") {
            File.WriteAllText(output,JsonSerializer.Serialize(new{ready=false,remote=false,consumed=0,profileRefused=true}));
            GetTree().Quit(72);
        }
    }
    private void RunReady()
    {
        host=new GuaGodotRuntime();
        var button=new Button{Name="GuardedButton",Text="Guarded",Size=new Vector2(100,50)};
        button.Pressed+=()=>clicks++;
        AddChild(button);
        host.Attach(this);host.SyncAttachedTree("guarded");
        if(!host.GetUiTreeJson().Contains("Guarded"))throw new Exception("Local reflected UI missing.");
        if(Mode=="player") {
            using var local=new GuaRuntime();bool refused=false;
            try{local.SetObservationProfile(GuaObservationProfile.Debug);}catch(InvalidOperationException){refused=true;}
            if(!refused)throw new Exception("Player ceiling escaped.");
        }
        int port=int.Parse(System.Environment.GetEnvironmentVariable("GUA_GODOT_TEST_PORT")??"19587");
        bool started=host.StartInspectorBridge(port);
        if(started!=(GuaGameNativePolicy.RemoteMode!="off"))throw new Exception("Compiled remote mode mismatch.");
        WriteResult(started);
        GD.Print("GUA_DOTNET_GAME_READY ",GuaGameNativePolicy.RemoteMode);
        if(!started)GetTree().Quit(0);
    }
    public override void _Process(double delta)
    {
        if(Engine.IsEditorHint())return;
        if(host==null)return;
        host.SyncAttachedTree("guarded");
        if(clicks>0 && ++framesAfterClick>=5){WriteResult(true);GetTree().Quit(clicks==1?0:1);}
    }
    private void WriteResult(bool remote)
    {
        var loader=System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(Main).Assembly);
        File.WriteAllText(output+".tmp",JsonSerializer.Serialize(new{mode=GuaGameNativePolicy.RemoteMode,ready=true,remote,consumed=clicks,gameAssembly=typeof(Main).Assembly.Location,gameLoader=loader?.GetType().AssemblyQualifiedName,collectible=loader?.IsCollectible}));
        File.Move(output+".tmp",output,true);
    }
    public override void _ExitTree(){host?.Dispose();}
}
