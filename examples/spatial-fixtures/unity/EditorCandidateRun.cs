// Fixture-only Editor entry point inside the existing licensed GameCI Linux image.
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class EditorCandidateRun
{
    static Process consumer;
    static double deadline;
    public static void Run() {
        var args=Environment.GetCommandLineArgs();
        string Arg(string key) {var i=Array.IndexOf(args,key);if(i<0||i+1>=args.Length) throw new ArgumentException(key);return args[i+1];}
        SessionState.SetString("gua-candidate-client",Arg("-guaClient"));
        SessionState.SetString("gua-candidate-output",Arg("-guaEvidence"));
        SessionState.SetString("gua-candidate-source",Arg("-guaSource"));
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        Environment.SetEnvironmentVariable("GUA_BRIDGE_PORT",port.ToString());
        SessionState.SetInteger("gua-candidate-port",port);
        SessionState.SetBool("gua-candidate-pending",true);
        EditorSceneManager.OpenScene("Assets/Scenes/GuaUnityFixture.unity");
        EditorApplication.EnterPlaymode();
    }
    [InitializeOnLoadMethod] static void Resume() {EditorApplication.update+=Poll;deadline=EditorApplication.timeSinceStartup+300;}
    static void Poll() {
        if(!SessionState.GetBool("gua-candidate-pending",false)) return;
        if(EditorApplication.timeSinceStartup>deadline) {consumer?.Kill();EditorApplication.Exit(1);return;}
        if(consumer==null && EditorApplication.isPlaying && Application.isPlaying) {
            var start=new ProcessStartInfo {FileName=SessionState.GetString("gua-candidate-client",""),UseShellExecute=false};
            start.Arguments="attach ws://127.0.0.1:"+SessionState.GetInteger("gua-candidate-port",0)+" . "+SessionState.GetString("gua-candidate-output","")+" "+SessionState.GetString("gua-candidate-source","");
            consumer=Process.Start(start);
        }
        if(consumer!=null && consumer.HasExited) {
            SessionState.SetBool("gua-candidate-pending",false);
            EditorApplication.Exit(consumer.ExitCode);
        }
    }
}
