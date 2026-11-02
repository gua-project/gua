using System.Security.Cryptography;
using System.Text.Json;
using Gua.GameBuild;
using System.Runtime.InteropServices;

if(args.Length>=2 && args[0] is "check-native" or "check-native-preloaded") {
    if(args[0]=="check-native-preloaded") NativeLibrary.Load(Path.GetFullPath(args[2]));
    nint library=NativeLibrary.Load(Path.GetFullPath(args[1]));
    var status=Marshal.GetDelegateForFunctionPointer<ReadPolicyUInt>(NativeLibrary.GetExport(library,"GamePolicyStatus"));
    var getMode=Marshal.GetDelegateForFunctionPointer<ReadPolicyUInt>(NativeLibrary.GetExport(library,"GamePolicyMode"));
    Console.WriteLine(JsonSerializer.Serialize(new { status=status(), mode=getMode() }));
    if(args.Length==3 && args[0]=="check-native") Thread.Sleep(int.Parse(args[2]));
    return;
}

if(args.Length==4 && args[0]=="embed-native") {
    using var input=JsonDocument.Parse(File.ReadAllBytes(args[2]));
    var policy=input.RootElement;
    var files=policy.GetProperty("files").EnumerateArray().Select(f=>new NativePolicyWriter.FileEntry(
        f.GetProperty("path").GetString()!,f.GetProperty("source").GetString()!,f.GetProperty("native").GetBoolean()));
    Console.WriteLine(NativePolicyWriter.Write(args[1],args[3],policy.GetProperty("kind").GetUInt32(),policy.GetProperty("mode").GetUInt32(),
        policy.GetProperty("root").GetString()!,policy.GetProperty("sourceCommit").GetString()!,policy.GetProperty("buildId").GetString()!,files));
    return;
}

// This runs on the developer/build machine. Never consult a runtime DLL to decide
// what the game should trust, and never regenerate the policy on game startup.
if (args.Length < 4 || args[0] != "generate" || args[2] is not ("off" or "testing" or "player"))
    throw new ArgumentException("generate <verified-provider-package> <off|testing|player> <game-owned-source.cs> [managed-assembly ...]");
string package = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
string output = Path.GetFullPath(args[3]);
string mode = args[2];
using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(package, "gua-build-manifest.json")));
var root = manifest.RootElement;
var build = root.GetProperty("build");
string flavor = build.GetProperty("flavor").GetString()!;
string ceiling = build.GetProperty("remotePermissionCeiling").GetString()!;
bool remote = build.GetProperty("remoteEnabled").GetBoolean();
if (root.GetProperty("schemaVersion").GetInt32() != 1 || build.GetProperty("schemaVersion").GetInt32() != 1 ||
    build.GetProperty("configuration").GetString() != "Release" ||
    (mode == "off" ? remote || flavor != "shipping" || ceiling != "none" :
     mode == "player" ? !remote || flavor != "ai-player" || ceiling != "player" :
     !remote || flavor is not ("development" or "qa") || ceiling != "debug"))
    throw new InvalidDataException("Provider package does not match the game's remote setting.");
string source = build.GetProperty("sourceCommit").GetString()!;
string buildId = build.GetProperty("buildId").GetString()!;
if (source.Length != 40 || source.Any(c => !char.IsAsciiHexDigit(c)) || string.IsNullOrWhiteSpace(buildId))
    throw new InvalidDataException("Missing provider source/build identity.");

var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var entry in root.GetProperty("files").EnumerateArray())
{
    string path = entry.GetProperty("path").GetString()!;
    string expected = entry.GetProperty("sha256").GetString()!;
    if (path.Contains('\\') || path.Split('/').Any(p => p is "" or "." or "..") || Path.IsPathRooted(path) ||
        expected.Length != 64 || expected.Any(c => !char.IsAsciiHexDigit(c)) || !entries.TryAdd(path, expected))
        throw new InvalidDataException("Invalid or duplicate provider file entry.");
    string absolute = Path.GetFullPath(Path.Combine(package, path));
    if (!absolute.StartsWith(package + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Provider file escapes package.");
    for (string? current = absolute; current != package; current = Path.GetDirectoryName(current))
        if (current == null || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Provider files must not traverse links/reparse points.");
    if (!Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(absolute))).Equals(expected, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Provider file hash mismatch: " + path);
}
// Initial supported loader: Windows x64 native pair. Other targets must not silently
// get a weaker policy; their host-specific loader is a separate acceptance boundary.
foreach (string name in new[] { "gua.dll", "gua_runtime.dll" })
    if (!entries.ContainsKey(name)) throw new InvalidDataException("Missing Windows native pair: " + name);
if (entries.Keys.Any(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && p is not ("gua.dll" or "gua_runtime.dll")))
    throw new InvalidDataException("This generator requires a native-pair package; managed/engine closure is not yet supported.");
string template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GameGuard.cs.txt"));
var expectedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (string name in new[] { "gua.dll", "gua_runtime.dll" }) expectedFiles.Add(name, entries[name].ToLowerInvariant());
foreach (string managed in args.Skip(4))
{
    string name = Path.GetFileName(managed);
    if (!name.StartsWith("Gua.", StringComparison.Ordinal) || !name.EndsWith(".dll", StringComparison.Ordinal) ||
        !expectedFiles.TryAdd(name, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(managed)))))
        throw new InvalidDataException("Invalid or duplicate managed assembly input.");
}
string generated = template.Replace("/*EXPECTED_FILES*/", string.Join(",\n", expectedFiles
    .Select(e => "        new ExpectedFile(" + JsonSerializer.Serialize(e.Key) + ", " + JsonSerializer.Serialize(e.Value) + ")")))
    .Replace("/*MODE*/", JsonSerializer.Serialize(mode))
    .Replace("/*SOURCE*/", JsonSerializer.Serialize(source))
    .Replace("/*BUILD*/", JsonSerializer.Serialize(buildId));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, generated);
Console.WriteLine("Generated game-owned Windows native policy: " + mode + " / " + source);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate uint ReadPolicyUInt();
