#if UNITY_EDITOR_WIN
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEditor;
using Gua.Runtime;
using Gua.Core;

// A disposable, explicitly started batch Editor only. Exit is normal Editor
// shutdown after tests, never Process.Kill/TerminateProcess or another Editor.
[InitializeOnLoad]
public static class GameIntegrityEditorFixture
{
    private static int frames;
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    static GameIntegrityEditorFixture() { EditorApplication.update += Tick; }
    public static void Run()
    {
        SessionState.SetBool("GameIntegrityFixture.Active",true);
        SessionState.SetBool("GameIntegrityFixture.SawPlay",false);
        SessionState.SetInt("GameIntegrityFixture.PlayCount",0);
        EditorApplication.isPlaying=true;
    }
    private static void Tick()
    {
        if(!SessionState.GetBool("GameIntegrityFixture.Active",false)) return;
        bool negative=Environment.GetEnvironmentVariable("GUA_EDITOR_EXPECT_REFUSAL")=="1";
        if(EditorApplication.isPlaying) {
            if(negative) {Finish(false,"Replaced SDK entered Play."); return;}
            if(++frames<5) return;
            try {CheckLocalRuntime();} catch(Exception e) {Finish(false,e.GetType().Name+": "+e.Message);return;}
            SessionState.SetBool("GameIntegrityFixture.SawPlay",true);
            EditorApplication.isPlaying=false;
            return;
        }
        if(EditorApplication.isPlayingOrWillChangePlaymode) return;
        if(SessionState.GetBool("GameIntegrityFixture.SawPlay",false)) {
            int count=SessionState.GetInt("GameIntegrityFixture.PlayCount",0)+1;
            SessionState.SetInt("GameIntegrityFixture.PlayCount",count);
            if(count==2) {Finish(true,"actual-play-local-api-and-return-to-edit-twice"); return;}
            SessionState.SetBool("GameIntegrityFixture.SawPlay",false);
            frames=0;
            EditorApplication.isPlaying=true;
            return;
        }
        if(GameIntegrityEditor.Attempted) {
            bool none=GetModuleHandle("gua.dll")==IntPtr.Zero && GetModuleHandle("gua_runtime.dll")==IntPtr.Zero;
            Finish(negative && !GameIntegrityEditor.Accepted && GameIntegrityEditor.LastStatus==78 && none,"play-cancelled; nativeLoaded="+(!none));
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckLocalRuntime()
    {
        using(var runtime=new GuaRuntime()) {
            runtime.BeginFrame("editor");
            runtime.RegisterNode(new GuaNodeDescriptor("local","button","Editor local API",new GuaBounds(0,0,10,10)));
            runtime.EndFrame();
            if(!runtime.GetUiTreeJson().Contains("Editor local API")) throw new Exception("Editor local API regression.");
        }
    }
    private static void Finish(bool passed,string detail)
    {
        SessionState.SetBool("GameIntegrityFixture.Active",false);
        File.WriteAllText("editor-policy-result.txt",(passed?"PASS: ":"FAIL: ")+detail);
        EditorApplication.Exit(passed?0:1);
    }
}
#endif
