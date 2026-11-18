using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Gua.GameBuild
{
// Build-tool source, shared with Unity's Editor compiler. The resulting verifier
// is a game-specific module, not an SDK runtime DLL or a runtime manifest.
public static class NativePolicyWriter
{
    public sealed class FileEntry
    {
        public string RelativePath;
        public string SourcePath;
        public bool Native;
        public FileEntry(string relativePath, string sourcePath, bool native)
        { RelativePath=relativePath; SourcePath=sourcePath; Native=native; }
    }

    public static string Write(string template, string output, uint kind, uint mode,
        string explicitEditorRoot, string source, string build, IEnumerable<FileEntry> expected)
    {
        if(Environment.OSVersion.Platform!=PlatformID.Win32NT || IntPtr.Size!=8)
            throw new PlatformNotSupportedException("The current native game policy writer requires Windows64.");
        if(kind<1 || kind>3 || mode>2 || source.Length!=40 || source.Any(c=>!Uri.IsHexDigit(c)) ||
            string.IsNullOrWhiteSpace(build) || build.Length>512 || (kind==3 ? !Path.IsPathRooted(explicitEditorRoot) : explicitEditorRoot!=""))
            throw new InvalidDataException("Invalid game policy identity or host scope.");
        template=Path.GetFullPath(template); output=Path.GetFullPath(output);
        if(string.Equals(template,output,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Never patch the provider template in place.");
        // The Editor template has no process-termination path, even for malformed
        // resources. Verify that the build tool cannot accidentally choose Player.
        IntPtr library=LoadLibraryEx(template,IntPtr.Zero,0x800);
        if(library==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try {
            IntPtr query=GetProcAddress(library,"GamePolicyTemplateKind");
            if(query==IntPtr.Zero || Marshal.GetDelegateForFunctionPointer<TemplateKind>(query)()!=(kind==3?3u:0u))
                throw new InvalidDataException("Player/Editor policy template mismatch.");
        } finally { FreeLibrary(library); }
        var files=expected.ToArray();
        if(files.Length==0 || files.Length>256 || !files.Any(f=>f.Native)) throw new InvalidDataException("Incomplete game policy file set.");
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte[] payload;
        using(var stream=new MemoryStream()) {
            using(var writer=new BinaryWriter(stream,new UTF8Encoding(false,true),true)) {
                writer.Write(0x50415547u); writer.Write(1u); writer.Write(kind); writer.Write(mode);
                Text(writer,explicitEditorRoot); Text(writer,source); Text(writer,build); writer.Write((uint)files.Length);
                foreach(var file in files) {
                    string path=file.RelativePath.Replace('\\','/');
                    if(Path.IsPathRooted(path) || path.Contains(':') || path.Split('/').Any(p=>p=="" || p=="." || p=="..") || !seen.Add(path))
                        throw new InvalidDataException("Invalid or duplicate policy-relative file path.");
                    writer.Write(file.Native?0u:1u); Text(writer,path);
                    using(var algorithm=SHA256.Create()) using(var input=File.OpenRead(file.SourcePath)) writer.Write(algorithm.ComputeHash(input));
                }
            }
            payload=stream.ToArray();
        }
        if(payload.Length>1024*1024) throw new InvalidDataException("Game policy exceeds its bounded resource size.");
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? throw new InvalidDataException("Missing policy output directory."));
        File.Copy(template,output,true);
        IntPtr update=BeginUpdateResource(output,false);
        if(update==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        bool complete=false;
        try {
            if(!UpdateResource(update,new IntPtr(10),new IntPtr(179),0,payload,(uint)payload.Length))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            complete=EndUpdateResource(update,false);
            update=IntPtr.Zero;
            if(!complete) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        } finally { if(update!=IntPtr.Zero) EndUpdateResource(update,true); }
        using(var algorithm=SHA256.Create()) return BitConverter.ToString(algorithm.ComputeHash(payload)).Replace("-","").ToLowerInvariant();
    }

    private static void Text(BinaryWriter writer,string text) { byte[] bytes=new UTF8Encoding(false,true).GetBytes(text); if(bytes.Length>32768)throw new InvalidDataException("Policy text too long."); writer.Write((uint)bytes.Length); writer.Write(bytes); }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint TemplateKind();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)] private static extern IntPtr GetProcAddress(IntPtr module,string name);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr BeginUpdateResource(string file,bool deleteExisting);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool UpdateResource(IntPtr update,IntPtr type,IntPtr name,ushort language,byte[] data,uint size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool EndUpdateResource(IntPtr update,bool discard);
}
}
