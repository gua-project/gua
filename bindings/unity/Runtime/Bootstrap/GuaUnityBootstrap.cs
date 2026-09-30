using UnityEngine;
using UnityEngine.Scripting;

// The driver assembly may be referenced only by its runtime initialization hook.
[assembly: AlwaysLinkAssembly]

namespace Gua.Unity
{

[DefaultExecutionOrder(32000)]
internal sealed class GuaUnityBootstrap : MonoBehaviour
{
    private static GuaUnityBootstrap activeDriver;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void StartDriver()
    {
        GuaUnityRuntime.EnsureStarted();
        if (activeDriver != null) return;
        var host = new GameObject("Gua Runtime Driver") { hideFlags = HideFlags.HideAndDontSave };
        DontDestroyOnLoad(host);
        activeDriver = host.AddComponent<GuaUnityBootstrap>();
    }

    private void LateUpdate()
    {
        GuaUnityRuntime.RunFrame();
    }

    private void OnDestroy()
    {
        if (activeDriver == this) activeDriver = null;
    }
}

}
