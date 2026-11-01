using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;

namespace Gua.Unity.Editor
{

public static class GuaUnityEditorCommands
{
    public static void PrepareProject()
    {
        if (AssetDatabase.LoadAssetAtPath<TMP_Settings>("Assets/TextMesh Pro/Resources/TMP Settings.asset") != null) return;
        TMP_PackageResourceImporter.ImportResources(true, false, false);
    }

    public static void BuildPlayer()
    {
        // This command builds the repository's development fixture, not a shipping game.
        var buildVariant = Argument("-guaVariant") ?? "development";
        Environment.SetEnvironmentVariable("GUA_BUILD_VARIANT", buildVariant);
        PrepareProject();
        var scene = Argument("-guaScene") ?? throw new ArgumentException("-guaScene is required.");
        var output = Argument("-guaOutput") ?? throw new ArgumentException("-guaOutput is required.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var platform = (Argument("-guaPlatform") ?? "windows-x64") switch
        {
            "WindowsX64" => "windows-x64",
            "LinuxX64" => "linux-x64",
            "MacOSX64" => "macos-x64",
            "MacOSArm64" => "macos-arm64",
            "MacOSUniversal" => "macos-universal",
            var value => value,
        };
        var target = platform switch
        {
            "windows-x64" => BuildTarget.StandaloneWindows64,
            "linux-x64" => BuildTarget.StandaloneLinux64,
            "macos-x64" or "macos-arm64" or "macos-universal" => BuildTarget.StandaloneOSX,
            _ => throw new ArgumentException($"Unsupported -guaPlatform '{platform}'."),
        };
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
        if (target == BuildTarget.StandaloneOSX)
        {
            var architecture = platform == "macos-arm64" ? 1 : platform == "macos-universal" ? 2 : 0;
            PlayerSettings.SetArchitecture(NamedBuildTarget.Standalone, architecture);
        }
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { scene }, locationPathName = output,
            target = target, options = buildVariant == "development" ? BuildOptions.Development : BuildOptions.None,
        });
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            throw new InvalidOperationException($"Gua Unity player build failed: {report.summary.result} ({report.summary.totalErrors} errors).");
    }

    public static void StartPlayMode()
    {
        var scene = Argument("-guaScene") ?? throw new ArgumentException("-guaScene is required.");
        EditorSceneManager.OpenScene(scene);
        EditorApplication.isPlaying = true;
    }

    private static string? Argument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.Ordinal));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
}
