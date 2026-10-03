using System;
using System.IO;
using Gua.Core;
using Gua.Runtime;
using Gua.Unity;
using UnityEngine;

// Standalone Testing fixture. This explicit host opt-in is not a Player policy.
public sealed class SpatialTransportHost : MonoBehaviour
{
    GuaSpatialHost host;
    GuaRuntime runtime;
    GuaUnitySpatial reader;
    int pumped;
    float started;
    static string spatialPort;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void SeparateFixtureBridges() {
        // macOS Players reset cwd to the app bundle. Both fixture scenes use the
        // caller's fresh evidence directory, independently of engine launch cwd.
        var directory=Environment.GetEnvironmentVariable("GUA_FIXTURE_DIRECTORY");
        if(!string.IsNullOrEmpty(directory)) Directory.SetCurrentDirectory(directory);
        spatialPort=Environment.GetEnvironmentVariable("GUA_BRIDGE_PORT") ?? "8875";
        var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);
        listener.Start();
        try { Environment.SetEnvironmentVariable("GUA_BRIDGE_PORT",((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString()); }
        finally { listener.Stop(); }
    }
    static GuaSpatialVector V(double x,double y,double z) => new GuaSpatialVector(x,y,z);
    void Start()
    {
        started=Time.realtimeSinceStartup;
        var wall=new GameObject("anonymous-solid"); wall.layer=8;
        wall.transform.position=new Vector3(0,0,2); wall.AddComponent<BoxCollider>().size=new Vector3(2,2,1);
        var region=new GuaSpatialRegion(V(-20,-20,-20),V(20,20,20));
        var basis=new GuaSpatialBasis(V(1,0,0),V(0,1,0),V(0,0,1));
        host=new GuaSpatialHost(new GuaSpatialHostOptions {MaxProviders=2,MaxOwners=8,MaxQueueDepth=4,
            MaxQueriesPerBatch=64,MaxHitsPerQuery=2,QueryDeadlineMs=1000,MaxBatchWorkMs=1000},"unity-transport");
        var registration=new GuaSpatialRegistration {LoadedRegion=region,Policies=new[]{new GuaSpatialHostPolicy("solid",1,region)},
            Provider=new GuaSpatialProvider {ProviderId="unity-transport",SpaceId="fixture",SpaceEpoch=1,
                Basis=basis,Up=basis.Y,Unit=new GuaSpatialUnit {Label="fixture-unit"},
                Precision=new GuaSpatialPrecision {Representation="binary32",Reason="backend_error_unmeasured"},
                Operations=new[]{"raycast","overlap","sweep"},Shapes=new[]{"sphere","capsule","box"},
                Policies=new[]{"solid"},Consistencies=new[]{"bestEffort","samePhysicsSample"},
                Engine=new GuaSpatialEngine("Unity",Application.unityVersion,"PhysX","unknown"),Limits=new GuaSpatialLimits(64,2,1000)}};
        reader=new GuaUnitySpatial(host,gameObject.scene.GetPhysicsScene(),registration,
            new[]{new GuaUnitySpatialPolicy("solid",1<<8,QueryTriggerInteraction.Ignore,false)});
        // Host synchronizes once after setup. Reader never calls SyncTransforms.
        Physics.SyncTransforms();
        runtime=new GuaRuntime(); runtime.BindSpatial(host,reader.Provider,new GuaSpatialOwnerGrants {
            SessionEpoch=1,Profile="Testing",Enabled=true,Policies=new[]{"solid"},Region=region});
        runtime.BeginFrame("spatial-route"); runtime.EndFrame();
        int port=int.Parse(spatialPort);
        if(!runtime.StartInspectorBridge(port)) throw new Exception("Native spatial bridge failed.");
        File.WriteAllText("transport-ready.tmp",runtime.GetVersionJson());
        File.Move("transport-ready.tmp","transport-ready.json");
    }
    void FixedUpdate()
    {
        if(reader!=null && reader.Pump()) ++pumped;
        if(File.Exists("transport-done")) {
            File.WriteAllText("transport-host-evidence.json","{\"physicsBatches\":"+pumped+",\"backend\":\"PhysX\",\"version\":\""+Application.unityVersion+"\"}");
            Cleanup(); Application.Quit(0);
        }
        if(Time.realtimeSinceStartup-started>180) { Cleanup(); Application.Quit(1); }
    }
    void Cleanup() { runtime?.Dispose(); runtime=null; reader?.Dispose(); reader=null; host?.Dispose(); host=null; }
    void OnDestroy() { Cleanup(); }
}
