using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Gua.Core;
using Gua.Unity;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif
using UnityEngine;
using UnityEngine.SceneManagement;

public static class SpatialFixture
{
    static GuaSpatialVector V(double x, double y, double z) => new GuaSpatialVector(x,y,z);
    static readonly GuaSpatialRegion Region = new GuaSpatialRegion(V(-20,-20,-20), V(20,20,20));
    static Vector3 Vector(JsonElement v) => new Vector3(v.GetProperty("x").GetSingle(),v.GetProperty("y").GetSingle(),v.GetProperty("z").GetSingle());
    static void Check(bool condition, string label) { if (!condition) throw new Exception("SPATIAL FAIL: " + label); }
    static void Obstacle(Scene scene, JsonElement obstacle)
    {
        var go = new GameObject("anonymous-obstacle");
        SceneManager.MoveGameObjectToScene(go,scene);
        go.transform.position = Vector(obstacle.GetProperty("center"));
        var category = obstacle.GetProperty("category").GetString();
        go.layer = category == "self" ? 9 : 8;
        if(obstacle.TryGetProperty("mesh",out var meshFlag) && meshFlag.GetBoolean())
        {
            var mesh=new Mesh {vertices=new[]{new Vector3(-2,-2,0),new Vector3(0,2,0),new Vector3(2,-2,0)},triangles=new[]{0,1,2}};
            go.AddComponent<MeshCollider>().sharedMesh=mesh;
        }
        else { var collider = go.AddComponent<BoxCollider>(); collider.size=Vector(obstacle.GetProperty("size")); collider.isTrigger=category=="trigger"; }
    }
    static GuaSpatialRegistration Registration(bool loaded, long epoch)
    {
        var basis = new GuaSpatialBasis(V(1,0,0),V(0,1,0),V(0,0,1));
        return new GuaSpatialRegistration { LoadedRegion = loaded ? Region : null,
            Policies = new[] { new GuaSpatialHostPolicy("solid",1,Region),new GuaSpatialHostPolicy("triggers",1,Region) },
            Provider = new GuaSpatialProvider { ProviderId="unity-fixture", SpaceId="fixture", SpaceEpoch=epoch,
                Basis=basis,Up=basis.Y,Unit=new GuaSpatialUnit { Label="fixture-unit" },
                Precision=new GuaSpatialPrecision { Representation="binary32",Reason="backend_error_unmeasured" },
                Operations=new[]{"raycast","overlap","sweep"},Shapes=new[]{"sphere","capsule","box"},
                Policies=new[]{"solid","triggers"},Consistencies=new[]{"bestEffort","samePhysicsSample"},
                Engine=new GuaSpatialEngine("Unity",Application.unityVersion,"PhysX","unknown"),
                Limits=new GuaSpatialLimits(64,2,1000) }};
    }
#if UNITY_EDITOR
    public static void Run()
    {
        SessionState.SetBool("GuaSpatialFixturePending", true);
        EditorApplication.EnterPlaymode();
    }
    [InitializeOnLoadMethod]
    static void ResumeAfterReload() { EditorApplication.update += RunWhenPlaying; }
    static void RunWhenPlaying()
    {
        if (!SessionState.GetBool("GuaSpatialFixturePending", false) || !EditorApplication.isPlaying || !Application.isPlaying) return;
        SessionState.SetBool("GuaSpatialFixturePending", false);
        EditorApplication.update -= RunWhenPlaying;
        RunCore();
    }
#endif
    public static void RunCore()
    {
        var exit = 1;
        try
        {
            using (var fixture = JsonDocument.Parse(File.ReadAllText("spatial-engine-r1.json")))
            using (var host = new GuaSpatialHost(new GuaSpatialHostOptions { MaxProviders=2,MaxOwners=2,MaxQueueDepth=4,
                MaxQueriesPerBatch=64,MaxHitsPerQuery=2,QueryDeadlineMs=1000,MaxBatchWorkMs=1000 },"fixture-unity"))
            {
                Check(Application.unityVersion == fixture.RootElement.GetProperty("configuration").GetProperty("unity").GetString(),"pinned Unity patch");
                // Global state is a host precondition; the reader never changes it.
                Check(!Physics.queriesHitBackfaces,"fixture requires explicit backfaces=false host state");
                var evidence = new List<object>();
                var leaseRaces = new List<object>();
                var other = SceneManager.CreateScene("spatial-other",new CreateSceneParameters(LocalPhysicsMode.Physics3D));
                var sentinel = new GameObject("other-scene-wall"); SceneManager.MoveGameObjectToScene(sentinel,other);
                sentinel.layer=8;
                sentinel.AddComponent<BoxCollider>().size = Vector3.one * 10;
                long batchId = 0;
                foreach (var test in fixture.RootElement.GetProperty("cases").EnumerateArray())
                {
                    ++batchId;
                    var id = test.GetProperty("id").GetString();
                    var scene = SceneManager.CreateScene("spatial-"+id,new CreateSceneParameters(LocalPhysicsMode.Physics3D));
                    foreach (var obstacle in test.GetProperty("obstacles").EnumerateArray()) Obstacle(scene,obstacle);
                    using (var adapter = new GuaUnitySpatial(host,scene.GetPhysicsScene(),Registration(test.GetProperty("loaded").GetBoolean(),batchId),
                        new[] { new GuaUnitySpatialPolicy("solid",1<<8,QueryTriggerInteraction.Ignore,false),
                            new GuaUnitySpatialPolicy("triggers",1<<8,QueryTriggerInteraction.Collide,false) }))
                    {
                        // Approved host boundary: sync once after setup, never per query.
                        Physics.SyncTransforms();
                        using (var grants = GuaSpatialDocument.FromOwner(new GuaSpatialOwnerGrants {SessionEpoch=1,Profile="Testing",Enabled=true,
                            Policies=new[]{"solid","triggers"},Region=Region}))
                        using (var request = GuaSpatialDocument.FromJson(GuaSpatialDocumentType.Request,test.GetProperty("query").GetRawText()))
                        {
                            var owner = host.OpenOwner(grants);
                            var deniedQuery = request.ReadRequest(); deniedQuery.SpaceEpoch=batchId; deniedQuery.Kind="overlap"; deniedQuery.Segment=null; deniedQuery.Delta=null;
                            deniedQuery.Shape = new GuaSpatialShape {Type="sphere",Center=V(19.75,0,0),Radius=0.5};
                            using (var denied = GuaSpatialDocument.FromBatch(new GuaSpatialBatch {BatchId=9999,Consistency="samePhysicsSample",Queries=new[]{deniedQuery}}))
                            {
                                try { host.Enqueue(owner,denied); throw new Exception("region unexpectedly accepted"); }
                                catch (GuaSpatialException e) { Check(e.Code==GuaSpatialErrorCode.NotAuthorized,"whole-shape region denied"); }
                            }
                            var player = grants.ReadOwner(); player.Profile="Player";
                            using (var playerDoc = GuaSpatialDocument.FromOwner(player)) host.SetOwner(owner,playerDoc);
                            var currentQuery = request.ReadRequest(); currentQuery.SpaceEpoch=batchId;
                            using (var denied = GuaSpatialDocument.FromBatch(new GuaSpatialBatch {BatchId=9999,Consistency="samePhysicsSample",Queries=new[]{currentQuery}}))
                            {
                                try { host.Enqueue(owner,denied); throw new Exception("Player unexpectedly accepted"); }
                                catch (GuaSpatialException e) { Check(e.Code==GuaSpatialErrorCode.NotAuthorized,"Player denied before physics"); }
                            }
                            host.SetOwner(owner,grants);
                            var narrow=grants.ReadOwner(); narrow.Region=new GuaSpatialRegion(V(-20,0.74999998,-20),V(20,20,20));
                            using (var narrowDoc=GuaSpatialDocument.FromOwner(narrow)) host.SetOwner(owner,narrowDoc);
                            var roundingQuery=request.ReadRequest(); roundingQuery.SpaceEpoch=batchId; roundingQuery.RequestId=10001; roundingQuery.QueryId="capsule-derived-rounding";
                            roundingQuery.Kind="overlap"; roundingQuery.Segment=null; roundingQuery.Delta=null;
                            roundingQuery.Shape=new GuaSpatialShape {Type="capsule",PointA=V(0,1,0),PointB=V(0,1.0000001192092896,0),Radius=0.25};
                            using (var rounding=GuaSpatialDocument.FromBatch(new GuaSpatialBatch {BatchId=10001,Consistency="samePhysicsSample",Queries=new[]{roundingQuery}})) host.Enqueue(owner,rounding);
                            Check(adapter.Pump(),"derived geometry refusal pump");
                            using (var rounding=host.Poll(owner,10001))
                            { var item=rounding.ReadBatchResult().Items[0]; Check(item.State=="failed" && item.Reason=="unsupported_shape" && item.Result==null,"no expanded physics geometry"); }
                            host.SetOwner(owner,grants);
                            roundingQuery.RequestId=10002;roundingQuery.QueryId="capsule-axis-underflow";
                            roundingQuery.Shape.PointA=V(0,0,0);roundingQuery.Shape.PointB=V(1e-25,0,0);
                            using(var underflow=GuaSpatialDocument.FromBatch(new GuaSpatialBatch {BatchId=10002,Consistency="samePhysicsSample",Queries=new[]{roundingQuery}})) host.Enqueue(owner,underflow);
                            Check(adapter.Pump(),"underflow refusal pump");
                            using(var underflow=host.Poll(owner,10002)) {var item=underflow.ReadBatchResult().Items[0];Check(item.State=="failed"&&item.Reason=="unsupported_shape"&&item.Result==null,"no collapsed capsule axis");}
                            roundingQuery.RequestId=10003;roundingQuery.QueryId="capsule-axis-subnormal";roundingQuery.Shape.PointB=V(1e-22,0,0);
                            using(var subnormal=GuaSpatialDocument.FromBatch(new GuaSpatialBatch {BatchId=10003,Consistency="samePhysicsSample",Queries=new[]{roundingQuery}})) host.Enqueue(owner,subnormal);
                            Check(adapter.Pump(),"subnormal refusal pump");
                            using(var subnormal=host.Poll(owner,10003)) {var item=subnormal.ReadBatchResult().Items[0];Check(item.State=="failed"&&item.Reason=="unsupported_shape"&&item.Result==null,"no subnormal capsule axis");}
                            using (var batch = GuaSpatialDocument.FromBatch(new GuaSpatialBatch { BatchId=batchId,Consistency="samePhysicsSample",Queries=new[]{currentQuery} }))
                            {
                                var queued = Stopwatch.GetTimestamp(); host.Enqueue(owner,batch);
                                var start = Stopwatch.GetTimestamp(); var cpu = SpatialProfile.CpuUs();
                                Check(adapter.Pump(),id+" pump");
                                var wallUs = (Stopwatch.GetTimestamp()-start)*1000000.0/Stopwatch.Frequency;
                                var cpuUs = SpatialProfile.CpuUs()-cpu;
                                using (var result = host.Poll(owner,(ulong)batchId))
                                {
                                    Check(result != null,id+" poll");
                                    var item = result.ReadBatchResult().Items[0];
                                    Check(item.State == "completed",id+" completion "+result.ToJson());
                                    var expected = test.GetProperty("outcome").GetString();
                                    Check(expected == "engine-specific" || item.Result.Outcome == expected,id+" outcome "+result.ToJson());
                                    Check(item.Result.Truncated == test.GetProperty("truncated").GetBoolean(),id+" truncation");
                                    Check(item.Result.Sample.Tick == null,id+" no invented edit-mode physics tick");
                                    foreach (var hit in item.Result.Hits) Check(hit.Missing.ContainsKey("normal") && hit.Relation=="unknown",id+" unknown normal/relation");
                                    evidence.Add(new { id, result=result.ToJson(),pumpWallUs=wallUs,threadCpuUs=cpuUs,
                                        queueWallUs=(start-queued)*1000000.0/Stopwatch.Frequency });
                                    if (id == "door-transition")
                                    {
                                        var oldSample = item.Result.Sample;
                                        scene.GetRootGameObjects()[0].transform.position = new Vector3(0,0,10);
                                        // A second approved boundary after host door mutation.
                                        Physics.SyncTransforms();
                                        var openQuery = request.ReadRequest(); openQuery.SpaceEpoch=batchId; openQuery.RequestId=5000; openQuery.QueryId="door-transition-open";
                                        using (var openBatch = GuaSpatialDocument.FromBatch(new GuaSpatialBatch {BatchId=5000,Consistency="samePhysicsSample",Queries=new[]{openQuery}})) host.Enqueue(owner,openBatch);
                                        Check(adapter.Pump(),"door open pump");
                                        using (var openResult = host.Poll(owner,5000))
                                        {
                                            var openItem=openResult.ReadBatchResult().Items[0];
                                            Check(openItem.State=="completed" && openItem.Result.Outcome==test.GetProperty("doorOpenOutcome").GetString(),"door open physics state");
                                            Check(openItem.Result.Sample.PhysicsSampleId!=oldSample.PhysicsSampleId && openItem.Result.Sample.ObservedFromMs>=oldSample.ObservedToMs,"door new boundary sample");
                                            evidence.Add(new {id="door-transition-open",result=openResult.ToJson()});
                                        }
                                    }
                                }
                            }
                            var queries = new GuaSpatialRequest[16];
                            for (int n=0;n<queries.Length;++n) { queries[n]=request.ReadRequest(); queries[n].SpaceEpoch=batchId; queries[n].RequestId=6000+n; queries[n].QueryId="batch:"+n; }
                            using (var batch = GuaSpatialDocument.FromBatch(new GuaSpatialBatch {BatchId=6000,Consistency="samePhysicsSample",Queries=queries}))
                            {
                                var queued=Stopwatch.GetTimestamp(); host.Enqueue(owner,batch);
                                var start=Stopwatch.GetTimestamp(); var cpu=SpatialProfile.CpuUs();
                                Check(adapter.Pump(),"batch pump");
                                var wallUs=(Stopwatch.GetTimestamp()-start)*1000000.0/Stopwatch.Frequency;
                                var cpuUs=SpatialProfile.CpuUs()-cpu;
                                using (var result=host.Poll(owner,6000))
                                {
                                    var items=result.ReadBatchResult().Items;
                                    var sample=items[0].Result.Sample.PhysicsSampleId;
                                    var expected=id=="door-transition" ? test.GetProperty("doorOpenOutcome").GetString() : test.GetProperty("outcome").GetString();
                                    foreach (var item in items)
                                    {
                                        Check(item.State=="completed" && item.Result.Sample.PhysicsSampleId==sample,"one held physics sample");
                                        Check(expected=="engine-specific" || item.Result.Outcome==expected,"batch fixed expectation");
                                        Check(item.Result.Truncated==test.GetProperty("truncated").GetBoolean(),"batch truncation");
                                    }
                                    evidence.Add(new {id,batchSize=16,result=result.ToJson(),pumpWallUs=wallUs,threadCpuUs=cpuUs,queueWallUs=(start-queued)*1000000.0/Stopwatch.Frequency});
                                }
                            }
                            LeaseRace(host,adapter,owner,grants,currentQuery,id=="door-transition"?test.GetProperty("doorOpenOutcome").GetString():test.GetProperty("outcome").GetString(),leaseRaces,id);
                            if(batchId==1) LeaseRace(host,adapter,owner,grants,currentQuery,test.GetProperty("outcome").GetString(),leaseRaces,id,true);
                            adapter.Dispose();
                            foreach (var obstacle in scene.GetRootGameObjects()) obstacle.transform.position+=new Vector3(100,0,0);
                            Physics.SyncTransforms();
                            using (var replacement = new GuaUnitySpatial(host,scene.GetPhysicsScene(),Registration(false,batchId+1000),
                                new[] {new GuaUnitySpatialPolicy("solid",1<<8,QueryTriggerInteraction.Ignore,false),new GuaUnitySpatialPolicy("triggers",1<<8,QueryTriggerInteraction.Collide,false)}))
                            using (var stale=GuaSpatialDocument.FromBatch(new GuaSpatialBatch {BatchId=9999,Consistency="samePhysicsSample",Queries=new[]{currentQuery}}))
                            {
                                try {host.Enqueue(owner,stale); throw new Exception("old epoch unexpectedly accepted");}
                                catch (GuaSpatialException e) {Check(e.Code==GuaSpatialErrorCode.Context,"old scene/origin epoch rejected");}
                            }
                            host.CloseOwner(owner);
                        }
                    }
                    SceneManager.UnloadSceneAsync(scene);
                }
                SceneManager.UnloadSceneAsync(other);
                var configuration=fixture.RootElement.GetProperty("configuration").Clone();
                var profile=new GameObject("spatial-profile").AddComponent<SpatialProfile>();
                profile.Completed=(records,error)=>
                {
                    if(error!=null) {UnityEngine.Debug.LogException(error);Exit(1);return;}
                    File.WriteAllText("evidence.json",JsonSerializer.Serialize(new {configuration,results=evidence,profile=records,leaseRaces}));
                    UnityEngine.Debug.Log("SPATIAL PROFILE PASS: "+records.Count+" real FixedUpdate samples");
                    Exit(0);
                };
                UnityEngine.Debug.Log("SPATIAL PASS: "+evidence.Count+" real Unity cases");
            }
            return; // Runtime profile completes across normal FixedUpdate callbacks.
        }
        catch (Exception e) { UnityEngine.Debug.LogException(e); }
        Exit(exit);
    }
    static void Exit(int code) {
#if UNITY_EDITOR
        EditorApplication.Exit(code);
#else
        Application.Quit(code);
#endif
    }
    static void LeaseRace(GuaSpatialHost host,GuaUnitySpatial adapter,ulong owner,GuaSpatialDocument grants,GuaSpatialRequest query,string expected,List<object> evidence,string id,bool deadline=false)
    {
        // Reflection is fixture-only access to the exact private pump helpers;
        // no production test hook or physics override is installed.
        var execute=typeof(GuaUnitySpatial).GetMethod("ExecuteOrTerminal",BindingFlags.Instance|BindingFlags.NonPublic);
        var complete=typeof(GuaUnitySpatial).GetMethod("CompleteItem",BindingFlags.Instance|BindingFlags.NonPublic);
        GuaSpatialRequest Copy() {using(var d=GuaSpatialDocument.FromRequest(query)) return d.ReadRequest();}
        var first=Copy();first.RequestId=11000;first.QueryId="race-revoked";first.Kind="overlap";first.Segment=null;first.Delta=null;first.Shape=new GuaSpatialShape {Type="sphere",Center=V(9,0,0),Radius=.25};if(deadline) first.DeadlineMs=100;
        var second=Copy();second.RequestId=11001;second.QueryId="race-eligible";
        using(var batch=GuaSpatialDocument.FromBatch(new GuaSpatialBatch {BatchId=11000,Consistency="samePhysicsSample",Queries=new[]{first,second}})) host.Enqueue(owner,batch);
        var sample="race:"+id+":"+deadline;
        using(var boundary=GuaSpatialDocument.FromBoundary(new GuaSpatialBoundary {PhysicsSampleId=sample}))
        {
            var lease=host.Begin(adapter.Provider,boundary).Value;
            try
            {
                using(var consumed=host.Take(lease))
                {
                    Check(consumed!=null && consumed.ReadRequest().RequestId==11000,"race first consumed before terminal change");
                    if(deadline) System.Threading.Thread.Sleep(120);
                    else {var narrow=grants.ReadOwner();narrow.Region=new GuaSpatialRegion(V(-20,-20,-20),V(8,20,20));using(var updated=GuaSpatialDocument.FromOwner(narrow)) host.SetOwner(owner,updated);}
                    var execution=(GuaSpatialHostQueryResult)execute.Invoke(adapter,new object[]{consumed.ReadRequest(),lease});
                    Check((bool)complete.Invoke(adapter,new object[]{lease,execution}),"race correlation released");
                }
                using(var eligible=host.Take(lease))
                {
                    Check(eligible!=null,"race next eligible taken");
                    var execution=(GuaSpatialHostQueryResult)execute.Invoke(adapter,new object[]{eligible.ReadRequest(),lease});
                    Check((bool)complete.Invoke(adapter,new object[]{lease,execution}),"race actual physics completed");
                }
                using(var result=host.Poll(owner,11000))
                {
                    var items=result.ReadBatchResult().Items;
                    Check(items[0].State=="failed"&&items[0].Reason==(deadline?"deadline_exceeded":"not_authorized")&&items[0].Result==null,"race revoked geometry withheld");
                    Check(items[1].State=="completed"&&items[1].Result.Sample.PhysicsSampleId==sample,"race same held sample");
                    Check(expected=="engine-specific"||items[1].Result.Outcome==expected,"race independent geometry");
                    evidence.Add(new {id,deadline,result=result.ToJson()});
                }
            }
            finally {host.End(lease);host.SetOwner(owner,grants);}
        }
    }

}
