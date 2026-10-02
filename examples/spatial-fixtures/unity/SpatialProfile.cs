using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Gua.Core;
using Gua.Unity;
using UnityEngine;

// Runtime component: samples real FixedUpdate callbacks, never Simulate.
public sealed class SpatialProfile : MonoBehaviour
{
    public Action<List<object>,Exception> Completed;
    GuaSpatialHost host;
    GuaUnitySpatial reader;
    ulong owner;
    int frame;
    long last;
    double sink,phaseCpu;
    long phaseWall;
    float oldFixedDelta;
    readonly List<object> records = new List<object>();
    [StructLayout(LayoutKind.Sequential)] struct FileTime { public uint Low,High; public ulong Value => ((ulong)High<<32)|Low; }
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool GetThreadTimes(IntPtr thread,out FileTime created,out FileTime exited,out FileTime kernel,out FileTime user);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryThreadCycleTime(IntPtr thread,out ulong cycles);
    public static ulong CpuCycles()
    {
        if(!QueryThreadCycleTime(GetCurrentThread(),out var cycles)) throw new InvalidOperationException("Windows thread cycles unavailable");
        return cycles;
    }
    public static double CpuUs()
    {
        if(!GetThreadTimes(GetCurrentThread(),out var created,out var exited,out var kernel,out var user)) throw new InvalidOperationException("Windows thread CPU unavailable");
        return (kernel.Value+user.Value)/10.0;
    }
    static double Us(long ticks) => ticks*1000000.0/Stopwatch.Frequency;
    static GuaSpatialVector V(double x,double y,double z) => new GuaSpatialVector(x,y,z);
    void Start()
    {
        try
        {
            oldFixedDelta=Time.fixedDeltaTime; Time.fixedDeltaTime=1f/60f;
            var region=new GuaSpatialRegion(V(-20,-20,-20),V(20,20,20));
            var basis=new GuaSpatialBasis(V(1,0,0),V(0,1,0),V(0,0,1));
            host=new GuaSpatialHost(new GuaSpatialHostOptions {MaxProviders=1,MaxOwners=1,MaxQueueDepth=1,MaxQueriesPerBatch=64,MaxHitsPerQuery=2,QueryDeadlineMs=1000,MaxBatchWorkMs=1000},"profile-unity");
            var registration=new GuaSpatialRegistration {LoadedRegion=region,Policies=new[]{new GuaSpatialHostPolicy("solid",1,region)},Provider=new GuaSpatialProvider
            {ProviderId="profile",SpaceId="profile",SpaceEpoch=1,Basis=basis,Up=basis.Y,Unit=new GuaSpatialUnit {Label="fixture-unit"},Precision=new GuaSpatialPrecision {Representation="binary32",Reason="backend_error_unmeasured"},Operations=new[]{"raycast","overlap","sweep"},Shapes=new[]{"sphere","capsule","box"},Policies=new[]{"solid"},Consistencies=new[]{"samePhysicsSample"},Engine=new GuaSpatialEngine("Unity",Application.unityVersion,"PhysX","unknown"),Limits=new GuaSpatialLimits(64,2,1000)}};
            reader=new GuaUnitySpatial(host,Physics.defaultPhysicsScene,registration,new[]{new GuaUnitySpatialPolicy("solid",1<<8,QueryTriggerInteraction.Ignore,false)});
            using(var grants=GuaSpatialDocument.FromOwner(new GuaSpatialOwnerGrants {SessionEpoch=1,Profile="Testing",Enabled=true,Policies=new[]{"solid"},Region=region})) owner=host.OpenOwner(grants);
            Physics.SyncTransforms(); // One approved host setup boundary.
        }
        catch(Exception e) { Finish(e); }
    }
    void FixedUpdate()
    {
        if(host==null || reader==null) return;
        try
        {
            var start=Stopwatch.GetTimestamp(); var cpu=CpuUs(); var cycles=CpuCycles();
            if(frame%150==30) {phaseCpu=cpu;phaseWall=start;}
            int count=frame<150?0:frame<300?1:16;
            for(int n=0;n<2000;++n) sink+=Math.Sin(n*.01)*.000001;
            double pumpWall=0,queueWall=0;
            if(count>0)
            {
                var queries=new GuaSpatialRequest[count];
                for(int n=0;n<count;++n) queries[n]=new GuaSpatialRequest {RequestId=100000+frame*16+n,QueryId="profile:"+frame+":"+n,SessionEpoch=1,SpaceId="profile",SpaceEpoch=1,QueryPolicyId="solid",DeadlineMs=1000,Consistency="samePhysicsSample",MaxHits=2,Kind="sweep",Shape=new GuaSpatialShape {Type="sphere",Center=V(0,0,-2),Radius=.25},Delta=V(0,0,4)};
                var queued=Stopwatch.GetTimestamp();
                using(var batch=GuaSpatialDocument.FromBatch(new GuaSpatialBatch {BatchId=100000+frame,Consistency="samePhysicsSample",Queries=queries})) host.Enqueue(owner,batch);
                var pumpStart=Stopwatch.GetTimestamp();
                if(!reader.Pump()) throw new Exception("profile boundary not pumped");
                pumpWall=Us(Stopwatch.GetTimestamp()-pumpStart); queueWall=Us(pumpStart-queued);
                using(var result=host.Poll(owner,(ulong)(100000+frame)))
                {
                    if(result==null) throw new Exception("profile result missing");
                    foreach(var item in result.ReadBatchResult().Items) if(item.State!="completed"||item.Result.Outcome!="clear") throw new Exception("profile fixed geometry failed");
                }
            }
            var end=Stopwatch.GetTimestamp(); var cpuEnd=CpuUs();
            if(frame%150>=30) records.Add(new {batchSize=count,callbackWallUs=Us(end-start),threadCpuUs=cpuEnd-cpu,threadCpuCycles=CpuCycles()-cycles,pumpWallUs=pumpWall,queueWallUs=queueWall,callbackIntervalUs=Us(start-last),fixedUpdateCallback=frame,phaseMainThreadCpuUs=frame%150==149?(double?)(cpuEnd-phaseCpu):null,phaseElapsedUs=frame%150==149?(double?)Us(end-phaseWall):null});
            last=start; ++frame;
            if(frame==450) Finish(null);
        }
        catch(Exception e) { Finish(e); }
    }
    void Finish(Exception error)
    {
        enabled=false;
        try { reader?.Dispose(); if(host!=null) {host.CloseOwner(owner);host.Dispose();} }
        catch(Exception e) { if(error==null) error=e; }
        host=null;reader=null; Time.fixedDeltaTime=oldFixedDelta;
        Completed?.Invoke(records,error);
    }
}
