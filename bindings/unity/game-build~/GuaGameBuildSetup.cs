using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using Gua.GameBuild;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Gua.Unity.Editor
{
// Provider-side setup/build code only. Runtime trust is in generated GAME files.
public sealed class GuaGameBuildSetup : IPreprocessBuildWithReport, IPostprocessBuildWithReport
{
    private const string GameDirectory="Assets/GameSecurity";
    private const string SettingsFile=GameDirectory+"/build-settings.json";
    private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
    public int callbackOrder => -20000;
    [Serializable] private sealed class Settings {public string id="";}
    [Serializable] private sealed class Manifest {public Build build;public Entry[] files;}
    [Serializable] private sealed class Build {public string sourceCommit,buildId,flavor,configuration,remotePermissionCeiling;public bool remoteEnabled;}
    [Serializable] private sealed class Entry {public string path,sha256;}
    private static string PackageRoot {
        get {
            var package=UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(GuaGameBuildSetup).Assembly);
            if(package==null) throw new BuildFailedException("Cannot locate the Gua game-build SDK package.");
            return package.resolvedPath;
        }
    }
    private static string Mode {
        get {
            var flags=PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone).Split(';');
            bool remote=flags.Contains("GUA_REMOTE_AUTOMATION"),player=flags.Contains("GUA_PLAYER_ONLY");
            if(player&&!remote) throw new BuildFailedException("GUA_PLAYER_ONLY requires GUA_REMOTE_AUTOMATION.");
            return !remote?"off":player?"player":"testing";
        }
    }
    private static uint ModeValue(string mode) => mode=="off"?0u:mode=="testing"?1u:2u;
    private static bool HasLoadedSession() {
        if(GetModuleHandle("gua.dll")!=IntPtr.Zero || GetModuleHandle("gua_runtime.dll")!=IntPtr.Zero) return true;
        return File.Exists(SettingsFile) && GetModuleHandle("EditorSecurity_"+ReadSettings().id+".dll")!=IntPtr.Zero;
    }
    private static string Literal(string text) => "\""+text.Replace("\\","\\\\").Replace("\"","\\\"")+"\"";
    private static Settings ReadSettings() {
        if(!File.Exists(SettingsFile)) throw new BuildFailedException("First select Gua/Game build/Prepare game verification, then let Unity compile the generated game code.");
        var settings=JsonUtility.FromJson<Settings>(File.ReadAllText(SettingsFile));
        if(settings==null || !Guid.TryParseExact(settings.id,"N",out _)) throw new BuildFailedException("Invalid game-owned verification settings.");
        return settings;
    }
    private static string Hash(string file) {using(var input=File.OpenRead(file)) using(var hash=SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(input)).Replace("-","").ToLowerInvariant();}
    private static Manifest ValidateProvider(string mode) {
        string folder=Path.Combine(PackageRoot,"Provider~/windows",mode);
        var manifest=JsonUtility.FromJson<Manifest>(File.ReadAllText(Path.Combine(folder,"gua-build-manifest.json")));
        var build=manifest?.build;
        string flavor=mode=="off"?"shipping":mode=="player"?"ai-player":"qa";
        string ceiling=mode=="off"?"none":mode=="player"?"player":"debug";
        if(build==null || build.configuration!="Release" || build.flavor!=flavor || build.remoteEnabled!=(mode!="off") || build.remotePermissionCeiling!=ceiling || build.sourceCommit==null || build.sourceCommit.Length!=40 || string.IsNullOrEmpty(build.buildId)) throw new BuildFailedException("The selected provider mode is missing or has incompatible build identity.");
        foreach(string name in new[]{"gua.dll","gua_runtime.dll"}) {
            var entries=manifest.files.Where(e=>e.path==name).ToArray();
            if(entries.Length!=1 || Hash(Path.Combine(folder,name))!=entries[0].sha256) throw new BuildFailedException("The selected prebuilt native SDK differs from its provider manifest.");
        }
        return manifest;
    }
    [MenuItem("Gua/Game build/Normal distribution (remote off)")] public static void ConfigureOff() {Configure("off");}
    [MenuItem("Gua/Game build/Development and testing")] public static void ConfigureTesting() {Configure("testing");}
    [MenuItem("Gua/Game build/AI player distribution")] public static void ConfigurePlayer() {Configure("player");}
    private static void Configure(string mode) {
        if(Application.platform!=RuntimePlatform.WindowsEditor || HasLoadedSession()) throw new BuildFailedException("Select the game SDK mode in a Windows Editor before loading its native verifier/SDK; restart the Editor if already loaded. No compile flags were changed.");
        var flags=PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone).Split(';').Where(f=>f!="GUA_REMOTE_AUTOMATION"&&f!="GUA_PLAYER_ONLY"&&!string.IsNullOrWhiteSpace(f)).ToList();
        if(mode!="off") flags.Add("GUA_REMOTE_AUTOMATION"); if(mode=="player") flags.Add("GUA_PLAYER_ONLY");
        PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Standalone,string.Join(";",flags));
        Prepare();
    }
    [MenuItem("Gua/Game build/Prepare game verification")]
    public static void Prepare() {
        if(Application.platform!=RuntimePlatform.WindowsEditor) throw new BuildFailedException("Game verification currently requires a Windows Editor.");
        // Explicit developer setup only: never regenerate expectations on launch,
        // domain reload, Play, or after a runtime SDK integrity failure.
        string mode=Mode;
        var manifest=ValidateProvider(mode);
        if(HasLoadedSession()) throw new BuildFailedException("Restart the Editor before changing the game SDK policy; its loaded native verifier/files are held for this session.");
        if(!File.Exists(SettingsFile) && Directory.Exists(GameDirectory) && Directory.EnumerateFileSystemEntries(GameDirectory).Any()) throw new BuildFailedException("Assets/GameSecurity already contains files not owned by this setup. No files were overwritten.");
        Directory.CreateDirectory(GameDirectory);
        var settings=File.Exists(SettingsFile)?ReadSettings():new Settings{id=Guid.NewGuid().ToString("N")};
        File.WriteAllText(SettingsFile,JsonUtility.ToJson(settings));
        string library="GameSecurity_"+settings.id;
        string player=GameDirectory+"/Plugins/x86_64/"+library+".dll";
        Directory.CreateDirectory(Path.GetDirectoryName(player));
        File.Copy(Path.Combine(PackageRoot,"Provider~/GamePolicyTemplate.dll"),player,true);
        if(!File.Exists(player+".meta")) File.WriteAllText(player+".meta",File.ReadAllText(Path.Combine(PackageRoot,"Provider~/player-policy.meta.txt")).Replace("/*GUID*/",Guid.NewGuid().ToString("N")));
        string source=File.ReadAllText(Path.Combine(PackageRoot,"Provider~/GameBootstrap.cs.txt")).Replace("/*LIBRARY*/",Literal(library)).Replace("/*FILENAME*/",Literal(library+".dll"));
        File.WriteAllText(GameDirectory+"/GameIntegrityBootstrap.cs",source);
        string editor=GameDirectory+"/Editor";Directory.CreateDirectory(editor);
        string checker=Path.GetFullPath(editor+"/Native~/EditorSecurity_"+settings.id+".dll");
        var files=SdkFiles(mode);
        NativePolicyWriter.Write(Path.Combine(PackageRoot,"Provider~/EditorPolicyTemplate.dll"),checker,3,ModeValue(mode),ProjectRoot,manifest.build.sourceCommit,manifest.build.buildId,files);
        string editorSource=File.ReadAllText(Path.Combine(PackageRoot,"Provider~/GameIntegrityEditor.cs.txt")).Replace("/*CHECKER_PATH*/",Literal(checker)).Replace("/*MODE*/",ModeValue(mode).ToString());
        File.WriteAllText(editor+"/GameIntegrityEditor.cs",editorSource);
        AssetDatabase.Refresh();
        Debug.Log("Game-owned Gua verification prepared. Allow scripts to compile before building or entering Play.");
    }
    private static List<NativePolicyWriter.FileEntry> SdkFiles(string mode) {
        string native=Path.Combine(PackageRoot,"Provider~/windows",mode);
        var files=new List<NativePolicyWriter.FileEntry>();
        string project=Path.GetFullPath(ProjectRoot)+Path.DirectorySeparatorChar;
        foreach(string file in new[]{Path.Combine(native,"gua.dll"),Path.Combine(native,"gua_runtime.dll")}.Concat(Directory.GetFiles(Path.Combine(PackageRoot,"Runtime/Plugins/Managed"),"*.dll")).Concat(new[]{Path.Combine(PackageRoot,"Runtime/Plugins/Managed",mode,"Gua.Unity.dll")})) {
            // Native policy paths are project relative; package cache must lie
            // inside the project. External local package paths fail explicitly.
            string full=Path.GetFullPath(file);
            if(!full.StartsWith(project,StringComparison.OrdinalIgnoreCase)) throw new BuildFailedException("Embed this game SDK in Packages before preparing Editor verification; external package paths are not supported.");
            files.Add(new NativePolicyWriter.FileEntry(full.Substring(project.Length),full,file==Path.Combine(native,"gua.dll")||file==Path.Combine(native,"gua_runtime.dll")));
        }
        foreach(string assembly in new[]{"Gua.Unity.Bootstrap.dll","Gua.Unity.TMP.dll"}) {
            string file=Path.Combine(ProjectRoot,"Library/ScriptAssemblies",assembly);
            if(!File.Exists(file)) throw new BuildFailedException("Allow Unity to compile the SDK adapters, then prepare game verification again.");
            files.Add(new NativePolicyWriter.FileEntry("Library/ScriptAssemblies/"+assembly,file,false));
        }
        return files;
    }
    public void OnPreprocessBuild(BuildReport report) {
        try {Preflight(report);}
        catch(BuildFailedException) {throw;}
        catch(Exception e) {throw new BuildFailedException("Gua game-build preflight failed: "+e.Message);}
    }
    private static void Preflight(BuildReport report) {
        if(report.summary.platform==BuildTarget.StandaloneWindows64 && report.summary.outputPath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase) && File.Exists(report.summary.outputPath)) File.Delete(report.summary.outputPath);
        if(report.summary.platform!=BuildTarget.StandaloneWindows64 || PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone)!=ScriptingImplementation.Mono2x) throw new BuildFailedException("This game-build SDK currently supports Windows x64 Mono Players only; IL2CPP and Web are not verified.");
        var settings=ReadSettings();ValidateProvider(Mode);
        if(!File.Exists(GameDirectory+"/GameIntegrityBootstrap.cs")||!File.Exists(GameDirectory+"/Plugins/x86_64/GameSecurity_"+settings.id+".dll")) throw new BuildFailedException("Missing game-owned verifier; run Gua/Game build/Prepare game verification before building.");
    }
    public void OnPostprocessBuild(BuildReport report) {
        string executable=report.summary.outputPath;
        string pending=executable+".gua-pending-"+Guid.NewGuid().ToString("N");
        try {File.Move(executable,pending);CompletePlayer(report);File.Move(pending,executable);}
        catch(Exception e) {
            try {if(File.Exists(pending)) File.Delete(pending);if(File.Exists(executable)) File.Delete(executable);}
            finally {throw new BuildFailedException("Gua game-build finalization failed; do not use any previous output: "+e.Message);}
        }
    }
    private static void CompletePlayer(BuildReport report) {
        string mode=Mode;var manifest=ValidateProvider(mode);var settings=ReadSettings();
        string data=Path.Combine(Path.GetDirectoryName(report.summary.outputPath),Path.GetFileNameWithoutExtension(report.summary.outputPath)+"_Data");
        string plugins=Path.Combine(data,"Plugins/x86_64");
        Directory.CreateDirectory(plugins);
        var files=new List<NativePolicyWriter.FileEntry>();
        string native=Path.Combine(PackageRoot,"Provider~/windows",mode);
        foreach(string name in new[]{"gua.dll","gua_runtime.dll"}) {
            string file=Path.Combine(plugins,name);
            // Native variants are ignored provider inputs, not auto-imported
            // plugins with colliding names. Only this selected pair is emitted.
            File.Copy(Path.Combine(native,name),file,true);
            if(Hash(file)!=Hash(Path.Combine(native,name))) throw new BuildFailedException("Unity copied a native DLL from another Gua mode.");
            files.Add(new NativePolicyWriter.FileEntry("Plugins/x86_64/"+name,file,true));
        }
        if(Directory.GetFiles(Path.Combine(data,"Plugins"),"*.dll",SearchOption.AllDirectories).Count(f=>Path.GetFileName(f)=="gua.dll"||Path.GetFileName(f)=="gua_runtime.dll")!=2) throw new BuildFailedException("Unity emitted extra Gua native variants.");
        var expectedNames=Directory.GetFiles(Path.Combine(PackageRoot,"Runtime/Plugins/Managed"),"*.dll").Select(Path.GetFileName).Concat(new[]{"Gua.Unity.dll","Gua.Unity.Bootstrap.dll","Gua.Unity.TMP.dll"});
        if(Hash(Path.Combine(data,"Managed/Gua.Unity.dll"))!=Hash(Path.Combine(PackageRoot,"Runtime/Plugins/Managed",mode,"Gua.Unity.dll"))) throw new BuildFailedException("Unity selected another prebuilt adapter or rewrote it; this unverified rewrite/stripping path is not supported.");
        foreach(string name in expectedNames) {
            string file=Path.Combine(data,"Managed",name);
            if(File.Exists(file)) files.Add(new NativePolicyWriter.FileEntry("Managed/"+name,file,false));
        }
        if(files.Count(f=>!f.Native&&f.RelativePath.EndsWith("/Gua.Unity.dll",StringComparison.Ordinal))!=1) throw new BuildFailedException("The emitted game is missing the selected Gua Unity adapter.");
        NativePolicyWriter.Write(Path.Combine(PackageRoot,"Provider~/GamePolicyTemplate.dll"),Path.Combine(plugins,"GameSecurity_"+settings.id+".dll"),1,ModeValue(mode),"",manifest.build.sourceCommit,manifest.build.buildId,files);
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
}
}
