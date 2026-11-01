using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Gua.Core;
using Gua.Runtime;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Gua.Unity.Editor
{
/// <summary>Checks the selected artifacts; build intent cannot rewrite native capabilities.</summary>
public sealed class GuaVariantBuildGuard : IPreprocessBuildWithReport
{
    public int callbackOrder => -10000;

    public void OnPreprocessBuild(BuildReport report)
    {
        string expected = Environment.GetEnvironmentVariable("GUA_BUILD_VARIANT") ?? "shipping";
        if (expected != "shipping" && expected != "development" && expected != "qa" && expected != "ai-player")
            throw new BuildFailedException("Unknown GUA_BUILD_VARIANT build purpose.");
        bool release = (report.summary.options & BuildOptions.Development) == 0;
        if (expected != "development" && !release)
            throw new BuildFailedException("Shipping, QA and AI Player require a Release Player build.");
        try
        {
            // WebGL archives cannot execute in the Editor; validate their emitted build identity and bytes.
            bool web = report.summary.platform == BuildTarget.WebGL;
            using var loaded = web ? null : JsonDocument.Parse(GuaBuildInfo.GetJson());
            if (!web)
            {
            ValidateIdentity(loaded!.RootElement, expected);
            using var runtime = new GuaRuntime();
            if (runtime.GetBuildInfoJson() != GuaBuildInfo.GetJson()) throw new InvalidDataException("Loaded core/runtime build mismatch.");
            }
            string? sourceCommit = null, buildId = null;
            int selectedCore = 0, selectedRuntime = 0;
            foreach (var importer in PluginImporter.GetAllImporters())
            {
                if (!importer.GetCompatibleWithPlatform(report.summary.platform)) continue;
                string name = Path.GetFileName(importer.assetPath);
                if (web ? (name != "libgua-core.a" && name != "libgua_runtime.a") :
                    (name != "gua.dll" && name != "gua_runtime.dll" && name != "libgua.so" && name != "libgua_runtime.so" && name != "libgua.dylib" && name != "libgua_runtime.dylib")) continue;
                string file = ResolveAsset(importer.assetPath);
                string[] pair = web ? new[] {"libgua-core.a","libgua_runtime.a"} :
                    name.EndsWith(".dll",StringComparison.Ordinal) ? new[] {"gua.dll","gua_runtime.dll"} :
                    name.EndsWith(".so",StringComparison.Ordinal) ? new[] {"libgua.so","libgua_runtime.so"} : new[] {"libgua.dylib","libgua_runtime.dylib"};
                var allowed = pair.Concat(pair.Select(n=>n+".meta")).Concat(new[]{"gua-build-manifest.json","gua-build-manifest.json.meta"});
                if(web) allowed=allowed.Concat(new[]{"Managed.meta"});
                if(web && GuaUnityCompiledBuild.IsRemoteEnabled()) allowed=allowed.Concat(new[]{"GuaWebMcp.jslib","GuaWebMcp.jslib.meta"});
                foreach(var candidate in Directory.GetFiles(Path.GetDirectoryName(file)!))
                    if(!allowed.Contains(Path.GetFileName(candidate),StringComparer.Ordinal)) throw new InvalidDataException("Unexpected Unity native package file: "+Path.GetFileName(candidate));
                string manifestPath = Path.Combine(Path.GetDirectoryName(file)!, "gua-build-manifest.json");
                using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
                ValidateIdentity(manifest.RootElement.GetProperty("build"), expected);
                var fileBuild = manifest.RootElement.GetProperty("build");
                sourceCommit ??= web ? fileBuild.GetProperty("sourceCommit").GetString() : loaded!.RootElement.GetProperty("sourceCommit").GetString();
                buildId ??= web ? fileBuild.GetProperty("buildId").GetString() : loaded!.RootElement.GetProperty("buildId").GetString();
                if (string.IsNullOrEmpty(sourceCommit) || string.IsNullOrEmpty(buildId) ||
                    fileBuild.GetProperty("sourceCommit").GetString() != sourceCommit ||
                    fileBuild.GetProperty("buildId").GetString() != buildId)
                    throw new InvalidDataException("Imported native assets came from different builds.");
                var entries = manifest.RootElement.GetProperty("files").EnumerateArray().Where(e => e.GetProperty("path").GetString() == name).ToArray();
                if (entries.Length != 1) throw new InvalidDataException("Missing or duplicate native file identity.");
                using var hash = SHA256.Create();
                using var stream = File.OpenRead(file);
                string actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (actual != entries[0].GetProperty("sha256").GetString()) throw new InvalidDataException("Native file differs from its verified build.");
                if (name.Contains("runtime")) selectedRuntime++; else selectedCore++;
            }
            if (selectedCore != 1 || selectedRuntime != 1) throw new InvalidDataException("The Player needs exactly one verified Gua core/runtime pair.");
        }
        catch (Exception error)
        {
            throw new BuildFailedException("Gua build variant validation failed before Player generation: " + error.Message);
        }
    }

    private static void ValidateIdentity(JsonElement info, string expected)
    {
        string ceiling = info.GetProperty("remotePermissionCeiling").GetString()!;
        bool remote = info.GetProperty("remoteEnabled").GetBoolean();
        if (info.GetProperty("flavor").GetString() != expected ||
            (expected != "development" && info.GetProperty("configuration").GetString() != "Release") ||
            (expected == "shipping" && remote) || (expected != "shipping" && expected != "development" && !remote) ||
            ceiling != (!remote ? "none" : expected == "ai-player" ? "player" : "debug") ||
            remote != GuaUnityCompiledBuild.IsRemoteEnabled() || (ceiling == "player") != GuaUnityCompiledBuild.IsPlayerOnly())
            throw new InvalidDataException("Native/managed build purpose or compiled ceiling mismatch.");
    }

    private static string ResolveAsset(string asset)
    {
        if (asset.StartsWith("Packages/", StringComparison.Ordinal))
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(asset) ?? throw new InvalidDataException("Cannot resolve Gua package.");
            return Path.Combine(package.resolvedPath, asset.Substring(("Packages/" + package.name + "/").Length));
        }
        return Path.GetFullPath(asset);
    }
}
}
