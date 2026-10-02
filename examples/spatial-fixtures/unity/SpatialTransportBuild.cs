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
}
