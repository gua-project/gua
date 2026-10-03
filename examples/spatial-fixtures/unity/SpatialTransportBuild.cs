using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
public static class SpatialTransportBuild
{
    public static void Build()
    {
        var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        new GameObject("explicit-spatial-testing-host").AddComponent<SpatialTransportHost>();
        EditorSceneManager.SaveScene(scene,"Assets/Transport.unity");
        PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone,ScriptingImplementation.Mono2x);
        var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions {scenes=new[]{"Assets/Transport.unity"},
            locationPathName="Player/SpatialTransport.exe",target=BuildTarget.StandaloneWindows64,options=BuildOptions.Development});
        EditorApplication.Exit(report.summary.result==BuildResult.Succeeded ? 0 : 1);
    }
    public static void BuildCandidates()
    {
        var args=System.Environment.GetCommandLineArgs();
        string Arg(string key) {var i=System.Array.IndexOf(args,key);if(i<0||i+1>=args.Length) throw new System.ArgumentException(key);return args[i+1];}
        var target=(BuildTarget)System.Enum.Parse(typeof(BuildTarget),Arg("-guaTarget"));
        var architecture=Arg("-guaArchitecture");
        var output=Arg("-guaOutput");
        PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone,ScriptingImplementation.Mono2x);
        if(target==BuildTarget.StandaloneOSX) PlayerSettings.SetArchitecture(UnityEditor.Build.NamedBuildTarget.Standalone,architecture=="arm64"?1:0);
        foreach(var kind in new[]{"Geometry","Transport"}) {
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var host=new GameObject("explicit-spatial-testing-host");
            if(kind=="Geometry") host.AddComponent<SpatialGeometryHost>();else host.AddComponent<SpatialTransportHost>();
            var path="Assets/"+kind+".unity";EditorSceneManager.SaveScene(scene,path);
            var suffix=target==BuildTarget.StandaloneWindows64?".exe":target==BuildTarget.StandaloneOSX?".app":"";
            var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions {scenes=new[]{path},locationPathName=System.IO.Path.Combine(output,kind,"Spatial"+suffix),target=target,options=BuildOptions.Development});
            if(report.summary.result!=BuildResult.Succeeded) {EditorApplication.Exit(1);return;}
        }
        EditorApplication.Exit(0);
    }
}
