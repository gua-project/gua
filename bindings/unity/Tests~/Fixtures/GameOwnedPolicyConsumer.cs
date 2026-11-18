using System;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Gua.Core;
using Gua.Runtime;
using Gua.Unity;
using UnityEngine;
using UnityEngine.UI;

// Copied into the GAME's Assets by the test builder, outside any Gua DLL.
public sealed class GameOwnedPolicyConsumer : MonoBehaviour
{
    [DllImport("GameSecurity_fixture",CallingConvention=CallingConvention.Cdecl)] private static extern uint GamePolicyStatus();
    [DllImport("GameSecurity_fixture",CallingConvention=CallingConvention.Cdecl)] private static extern uint GamePolicyMode();
    [DllImport("GameSecurity_fixture",CallingConvention=CallingConvention.Cdecl)] private static extern uint GamePolicyWasPreloaded();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern void OutputDebugString(string text);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    private static int clicked;
    private static bool readyForRemote;
    private static int framesAfterClick;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void RequireGamePolicy()
    {
#if !UNITY_EDITOR
        OutputDebugString("GameSource: before first policy call.");
        if(GetModuleHandle("GameSecurity_fixture.dll")==IntPtr.Zero) Environment.Exit(83);
        Debug.Log("GAME_POLICY_MODULE_PRESENT_BEFORE_CALL");
        RequirePreloadedPolicy();
#endif
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RequirePreloadedPolicy()
    {
#if !UNITY_EDITOR
        if(GamePolicyWasPreloaded()!=1) Environment.Exit(83);
        if(GamePolicyStatus()!=1) Environment.Exit(78);
#endif
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void StartGameAdapter()
    {
#if !UNITY_EDITOR
        RequireGamePolicy();
        var driver=new GameObject("Game owned Gua driver");
        DontDestroyOnLoad(driver); driver.AddComponent<GameOwnedPolicyConsumer>();
#if GUA_REMOTE_AUTOMATION
        GuaUnityRuntime.StartRemoteForGame();
#endif
#endif
    }
    private void LateUpdate()
    {
        GuaUnityRuntime.RunFrame();
        if(readyForRemote && clicked!=0 && ++framesAfterClick>=5) {
            if(clicked!=1) {Debug.LogError("UI action was not consumed exactly once."); Application.Quit(1); return;}
            Debug.Log("GAME_OWNED_UNITY_SUCCESS"); Application.Quit(0);
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static async void Run()
    {
#if !UNITY_EDITOR
        try {
#if GUA_REMOTE_AUTOMATION
            const bool remote=true;
#if GUA_PLAYER_ONLY
            const bool player=true;
#else
            const bool player=false;
#endif
#else
            const bool remote=false,player=false;
#endif
            if(GamePolicyStatus()!=1 || GamePolicyMode()!=(player?2u:remote?1u:0u)) throw new Exception("Game-owned policy/mode mismatch.");
            using(var local=new GuaRuntime()) {
                local.BeginFrame("local");
                local.RegisterNode(new GuaNodeDescriptor("local","button","Local API",new GuaBounds(0,0,10,10)));
                local.EndFrame();
                if(!local.GetUiTreeJson().Contains("Local API")) throw new Exception("Local API unavailable.");
                if(!remote && local.StartInspectorBridge(19474)) throw new Exception("OFF native runtime started a listener.");
                if(player) {
                    bool denied=false;
                    try {local.SetObservationProfile(GuaObservationProfile.Debug);} catch(InvalidOperationException){denied=true;}
                    if(!denied) throw new Exception("Player permission ceiling escaped.");
                }
            }
            if(remote) {
                var canvas=new GameObject("Canvas",typeof(Canvas)); canvas.GetComponent<Canvas>().renderMode=RenderMode.ScreenSpaceOverlay;
                var widget=new GameObject("Guarded Button",typeof(RectTransform),typeof(Button));
                widget.transform.SetParent(canvas.transform,false);
                widget.GetComponent<RectTransform>().sizeDelta=new Vector2(100,40);
                widget.GetComponent<Button>().onClick.AddListener(()=>clicked++);
                await Task.Yield(); await Task.Yield();
                readyForRemote=true;
                Debug.Log("GAME_OWNED_UNITY_READY");
                return;
            }
            Debug.Log("GAME_OWNED_UNITY_SUCCESS"); Application.Quit(0);
        } catch(Exception error) {Debug.LogException(error); Application.Quit(1);}
#endif
    }

}

