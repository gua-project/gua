using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gua.GameBuild;
internal static class CppGameBuild
{
    static readonly string[] SdkNames=["gua.dll","gua_runtime.dll"];
    internal record Import(string Name,bool Delayed);
    internal static List<Import> Imports(string path)
    {
        using var input=File.OpenRead(path);using var pe=new PEReader(input);
        var header=pe.PEHeaders.PEHeader??throw new InvalidDataException("Missing PE header.");
        if(header.Magic!=PEMagic.PE32Plus) throw new InvalidDataException("Protected modules must be PE64.");
        string Text(int rva) {
            var bytes=pe.GetSectionData(rva).GetContent();int end=bytes.IndexOf((byte)0);
            if(end<1||end>512) throw new InvalidDataException("Invalid imported library name.");
            string name=Encoding.ASCII.GetString(bytes.AsSpan(0,end));
            if(name.Contains('/')||name.Contains('\\')||name.Contains(':')) throw new InvalidDataException("Import names must be library basenames.");
            return name;
        }
        var result=new List<Import>();
        foreach(bool delayed in new[]{false,true}) {
            var directory=delayed?header.DelayImportTableDirectory:header.ImportTableDirectory;
            if(directory.RelativeVirtualAddress==0) continue;
            int stride=delayed?32:20;
            if(directory.Size<stride||directory.Size>1024*1024)throw new InvalidDataException("Invalid import directory size.");
            bool terminated=false;
            for(int offset=0;offset+stride<=directory.Size;offset+=stride) {
                var bytes=pe.GetSectionData(directory.RelativeVirtualAddress+offset).GetContent(0,stride).AsSpan();
                if(bytes.IndexOfAnyExcept((byte)0)<0){terminated=true;break;}
                uint raw=BitConverter.ToUInt32(bytes.Slice(delayed?4:12,4));
                ulong rva=raw;
                if(delayed && (BitConverter.ToUInt32(bytes[..4])&1)==0) rva=checked(rva-header.ImageBase);
                if(rva>int.MaxValue) throw new InvalidDataException("Invalid import RVA.");
                result.Add(new(Text((int)rva),delayed));
            }
            if(!terminated)throw new InvalidDataException("Unterminated import directory.");
        }
        return result;
    }
    internal static object Check(string main,IEnumerable<string> plugins)
    {
        var seeds=new[]{Path.GetFullPath(main)}.Concat(plugins.Select(Path.GetFullPath)).ToArray();
        var dirs=seeds.Select(p=>Path.GetDirectoryName(p)!).Concat((Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator)).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var queue=new Queue<string>(seeds);var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var evidence=new List<object>();
        while(queue.TryDequeue(out string? file)) {
            if(!seen.Add(file))continue;
            var imports=Imports(file);
            foreach(var item in imports) {
                if(SdkNames.Contains(item.Name,StringComparer.OrdinalIgnoreCase)) {
                    if(!item.Delayed) throw new InvalidDataException($"Eager Gua import before verification: {Path.GetFileName(file)} -> {item.Name}");
                    continue;
                }
                // Trust OS/API-set dependencies, not arbitrary SDK self reports.
                if(item.Name.StartsWith("api-ms-",StringComparison.OrdinalIgnoreCase)||item.Name.StartsWith("ext-ms-",StringComparison.OrdinalIgnoreCase)||File.Exists(Path.Combine(Environment.SystemDirectory,item.Name))) continue;
                string? dependency=new[]{Path.GetDirectoryName(file)!}.Concat(dirs).Select(d=>Path.Combine(d,item.Name)).FirstOrDefault(File.Exists);
                if(dependency!=null) queue.Enqueue(Path.GetFullPath(dependency));
                else if(!item.Delayed) throw new InvalidDataException($"Unresolved startup import; supply its deployed/declared module path: {item.Name}");
            }
            evidence.Add(new{module=file,imports});
        }
        return new {stage="PE import closure before deployment",modules=evidence};
    }
    internal static void Prepare(string sdk,string mode,string executable,string policyName,string[] plugins)
    {
        sdk=Path.GetFullPath(sdk);executable=Path.GetFullPath(executable);
        if(mode is not("off" or "testing" or "player")||Path.GetFileName(policyName)!=policyName||!policyName.StartsWith("GameSecurity_",StringComparison.Ordinal)||!policyName.EndsWith(".dll",StringComparison.Ordinal)) throw new InvalidDataException("Invalid game mode/policy name.");
        string pending=executable+".gua-unverified";
        File.Move(executable,pending,true);
        try {
            var closure=Check(pending,plugins);
            string package=Path.Combine(sdk,mode);
            using var manifest=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(package,"gua-build-manifest.json")));
            var build=manifest.RootElement.GetProperty("build");
            string ceiling=mode=="off"?"none":mode=="player"?"player":"debug";
            if(build.GetProperty("configuration").GetString()!="Release"||build.GetProperty("remoteEnabled").GetBoolean()!=(mode!="off")||build.GetProperty("remotePermissionCeiling").GetString()!=ceiling)throw new InvalidDataException("Provider mode mismatch.");
            string root=Path.GetDirectoryName(executable)!;
            var files=new List<NativePolicyWriter.FileEntry>();
            foreach(string name in SdkNames) {
                var entries=manifest.RootElement.GetProperty("files").EnumerateArray().Where(e=>e.GetProperty("path").GetString()==name).ToArray();
                string source=Path.Combine(package,name);
                if(entries.Length!=1||Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant()!=entries[0].GetProperty("sha256").GetString())throw new InvalidDataException("Modified provider native input.");
                string output=Path.Combine(root,name);File.Copy(source,output,true);
                files.Add(new(name,output,true));
            }
            NativePolicyWriter.Write(Path.Combine(sdk,"GamePolicyTemplate.dll"),Path.Combine(root,policyName),2,mode=="off"?0u:mode=="player"?2u:1u,"",build.GetProperty("sourceCommit").GetString()!,build.GetProperty("buildId").GetString()!,files);
            File.WriteAllText(executable+".gua-imports.json",JsonSerializer.Serialize(closure,new JsonSerializerOptions{WriteIndented=true}));
            File.Move(pending,executable,true);
        } catch {if(File.Exists(pending))File.Delete(pending);throw;}
    }
}
