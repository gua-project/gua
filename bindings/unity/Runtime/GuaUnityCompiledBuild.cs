namespace Gua.Unity
{
/// <summary>Compile-time managed variant, independently checked against native identity.</summary>
public static class GuaUnityCompiledBuild
{
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
