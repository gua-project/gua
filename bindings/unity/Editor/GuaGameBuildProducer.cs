using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Gua.Unity.Editor
{
// Build-agent inputs only. Engine reference assemblies are never SDK payloads.
public static class GuaGameBuildProducer
{
    public static void BuildPlayerAndCaptureReferences()
    {
        GuaUnityEditorCommands.BuildPlayer();
        CaptureReferences();
    }
    public static void CaptureReferences()
    {
        string[] args=Environment.GetCommandLineArgs();
        int index=Array.IndexOf(args,"-guaGameBuildReferences");
        if(index<0 || index+1>=args.Length) throw new ArgumentException("-guaGameBuildReferences is required.");
        string output=Path.GetFullPath(args[index+1]);
        if(Directory.Exists(output)) throw new InvalidOperationException("Capture references into a new build-only directory.");
        string engine=Path.Combine(output,"managed/UnityEngine");
        string scripts=Path.Combine(output,"script-assemblies");
        Directory.CreateDirectory(engine);Directory.CreateDirectory(scripts);
        foreach(string file in Directory.GetFiles(Path.Combine(EditorApplication.applicationContentsPath,"Managed/UnityEngine"),"*.dll"))
            File.Copy(file,Path.Combine(engine,Path.GetFileName(file)));
        foreach(string name in new[]{"Unity.InputSystem.dll","UnityEngine.UI.dll"})
            File.Copy(Path.Combine("Library/ScriptAssemblies",name),Path.Combine(scripts,name));
        File.WriteAllText(Path.Combine(output,"unity-version.txt"),Application.unityVersion);
        Debug.Log("Captured private build references; do not distribute this directory.");
    }
}
}
