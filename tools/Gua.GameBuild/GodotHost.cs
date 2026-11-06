using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text.Json;
namespace Gua.GameBuild;
internal static class GodotHost
{
    internal static void SynchronizeNative(string package,string mode,string output)
    {
        if(mode is not ("off" or "testing" or "player"))throw new InvalidDataException("Invalid game mode.");
        using var manifest=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(package,"gua-build-manifest.json")));
        var build=manifest.RootElement.GetProperty("build");
        string ceiling=mode=="off"?"none":mode=="player"?"player":"debug";
        string flavor=build.GetProperty("flavor").GetString()!;
        if(build.GetProperty("configuration").GetString()!="Release" || build.GetProperty("remoteEnabled").GetBoolean()!=(mode!="off") || build.GetProperty("remotePermissionCeiling").GetString()!=ceiling ||
            (mode=="off"?flavor!="shipping":mode=="player"?flavor!="ai-player":flavor is not("qa" or "development")))
            throw new InvalidDataException("Provider mode mismatch.");
        // Validate both inputs before touching the output. Identical files must
        // not be rewritten: a live Editor deliberately retains deny-write leases.
        var files=new List<(string Source,string Target,string Hash)>();
        foreach(string name in new[]{"gua.dll","gua_runtime.dll"}) {
            var entries=manifest.RootElement.GetProperty("files").EnumerateArray().Where(e=>e.GetProperty("path").GetString()==name).ToArray();
            string source=Path.Combine(package,name);
            if(entries.Length!=1)throw new InvalidDataException("Missing/duplicate native member.");
            string hash=entries[0].GetProperty("sha256").GetString()!;
            if(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source)))!=hash)throw new InvalidDataException("Modified provider native input.");
            files.Add((source,Path.Combine(output,name),hash));
        }
        Directory.CreateDirectory(output);
        foreach(var file in files) {
            if(File.Exists(file.Target)&&Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file.Target)))==file.Hash)continue;
            try {File.Copy(file.Source,file.Target,true);}
            catch(IOException error){throw new IOException("The selected Gua SDK changed or is in use. Close/restart the Godot Editor before changing SDK/mode; no verification lease was released.",error);}
        }
    }
    internal static void Check(string project)
    {
        project=Path.GetFullPath(project);
        if(!File.Exists(Path.Combine(project,"project.godot")))throw new InvalidDataException("Missing Godot game project.");
        string config=File.ReadAllText(Path.Combine(project,"project.godot"));
        var autoload=Regex.Match(config,@"(?ms)^\[autoload\][ \t]*\r?$\n?(.*?)(?=^\[|\z)");
        if(autoload.Success && Regex.IsMatch(autoload.Groups[1].Value,@"(?m)^\s*GuaGamePolicy\s*="))throw new InvalidDataException("Remove the separate GDScript GuaGamePolicy autoload before using the protected managed Godot host.");
        if(File.Exists(Path.Combine(project,"addons","gua","GuaGodotRuntime.cs")))throw new InvalidDataException("The protected SDK supplies GuaGodotRuntime as game source. Remove the legacy addons/gua/GuaGodotRuntime.cs copy to avoid duplicate/unprotected adapter code.");
        bool GuaDescriptor(string file) {
            string text=File.ReadAllText(file);
            return Regex.IsMatch(text,@"(?m)^\s*entry_symbol\s*=\s*""(?:gua_godot_library_init|game_policy_godot_init)""") ||
                Regex.IsMatch(text,@"(?i)(?:gua\.dll|gua_runtime\.dll|gua_godot[^""\r\n/\\]*\.dll)");
        }
        void Inspect(string path) {
            if(File.Exists(path)&&GuaDescriptor(path))throw new InvalidDataException("Protected Godot .NET uses the managed adapter, not a preloaded Gua GDExtension. Remove the legacy Gua descriptor/addon or choose the separate GDScript host.");
        }
        // Godot's cached preload list is relevant even for otherwise hidden
        // directories. Project code/data and unrelated extensions stay intact.
        string list=Path.Combine(project,".godot","extension_list.cfg");
        if(File.Exists(list))foreach(string raw in File.ReadAllLines(list)) {
            string entry=raw.Trim();
            if(!entry.StartsWith("res://",StringComparison.Ordinal))continue;
            string file=Path.GetFullPath(Path.Combine(project,entry[6..]));
            if(!file.StartsWith(project+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Invalid project extension path.");
            Inspect(file);
        }
        void Walk(string directory) {
            if(File.Exists(Path.Combine(directory,".gdignore")))return;
            foreach(string file in Directory.EnumerateFiles(directory,"*.gdextension"))Inspect(file);
            foreach(string child in Directory.EnumerateDirectories(directory)) {
                if(Path.GetFileName(child) is ".godot" or ".git" or "obj" or "bin")continue;
                if((File.GetAttributes(child)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Godot project extensions must not traverse reparse points.");
                Walk(child);
            }
        }
        Walk(project);
    }
}
