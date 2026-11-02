using Gua.Unity;
using UnityEngine;

// This repository fixture explicitly compiles GUA_REMOTE_AUTOMATION in csc.rsp.
// Remote startup belongs to its game code, separately from local SDK startup.
internal static class GuaFixtureRemoteBootstrap
{
#if GUA_REMOTE_AUTOMATION
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void StartRemote() => GuaUnityRuntime.StartRemoteForGame();
#endif
}
