namespace Gua.Unity
{
/// <summary>Compile-time managed variant, independently checked against native identity.</summary>
public static class GuaUnityCompiledBuild
{
    // Keep Editor binaries reusable: referencing const fields would bake the producer's variant into them.
    public static bool IsRemoteEnabled() => RemoteEnabled;
    public static bool IsPlayerOnly() => PlayerOnly;
#if GUA_REMOTE_AUTOMATION
    public const bool RemoteEnabled = true;
#else
    public const bool RemoteEnabled = false;
#endif
#if GUA_PLAYER_ONLY
    public const bool PlayerOnly = true;
#if !GUA_REMOTE_AUTOMATION
#error GUA_PLAYER_ONLY requires GUA_REMOTE_AUTOMATION
#endif
#else
    public const bool PlayerOnly = false;
#endif
}
}
