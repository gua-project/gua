using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;
using NUnit.Framework;
namespace Gua.Selector.Tests;
[TestFixture]
public sealed class SpatialTransportTests
{
    static GuaSpatialDocument Doc(int index)
    {
        var item=JsonNode.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory,"spatial-host-r1.json")))!["valid"]![index]!;
        return GuaSpatialDocument.FromJson((GuaSpatialDocumentType)item["type"]!.GetValue<int>(),item["json"]!.ToJsonString());
    }
    sealed class Host : IDisposable
    {
        internal readonly GuaSpatialHost Spatial=new(new GuaSpatialHostOptions {MaxProviders=2,MaxOwners=8,MaxQueueDepth=4,MaxQueriesPerBatch=64,MaxHitsPerQuery=2,QueryDeadlineMs=1000,MaxBatchWorkMs=1000},"transport-clock");
        internal readonly GuaRuntime Runtime=new();
        internal readonly ulong Provider;
        internal Host() { using var registration=Doc(0); Provider=Spatial.Register(registration); Bind(); }
        internal void Bind() { using var grants=Doc(1); Runtime.BindSpatial(Spatial,Provider,grants.ReadOwner()); }
        internal void Pump()
        {
            using var boundary=Doc(3); var lease=Spatial.Begin(Provider,boundary);
            if(lease is null) return;
            try
            {
                using var execution=Doc(4); var result=execution.ReadExecution();
                while(true)
                {
                    using var query=Spatial.Take(lease.Value); if(query is null) break;
                    var q=query.ReadRequest(); result.RequestId=q.RequestId; result.QueryId=q.QueryId;
                    using var completion=GuaSpatialDocument.FromExecution(result); Spatial.Complete(lease.Value,completion);
                }
            }
            finally { Spatial.End(lease.Value); }
        }
        public void Dispose() { Runtime.Dispose(); Spatial.Dispose(); }
    }
    [Test]
    public void RuntimeBindingsReuseHostLimitsAndOneShotOwnerResults()
    {
        using var h=new Host(); using var client=h.Runtime.CreateSpatialClient(); using var other=h.Runtime.CreateSpatialClient();
        using var ad=client.Describe(); Assert.That(ad.ReadAdvertisement().Provider.Limits.MaxHitsPerQuery,Is.EqualTo(2));
        using var batch=Doc(2); client.Enqueue(batch.ReadBatch());
        Assert.That(client.Poll(1),Is.Null);
        Assert.That(Assert.Throws<GuaSpatialException>(()=>other.Poll(1))!.Code,Is.EqualTo(GuaSpatialErrorCode.Stale));
        h.Pump(); using var result=client.Poll(1)!;
        Assert.That(result.ReadBatchResult().Items.All(i=>i.State=="completed"),Is.True);
        Assert.That(Assert.Throws<GuaSpatialException>(()=>client.Poll(1))!.Code,Is.EqualTo(GuaSpatialErrorCode.Stale));
        Assert.That(h.Runtime.GetVersionJson(),Does.Contain("spatial_read_r1"));
    }
    [Test]
    public void ExplicitTestingOnlyAndRevocationPreventsPublication()
    {
        using var h=new Host(); using var client=h.Runtime.CreateSpatialClient(); using var batch=Doc(2);
        client.Enqueue(batch.ReadBatch()); h.Runtime.DisableSpatial(); h.Pump();
        Assert.That(Assert.Throws<GuaSpatialException>(()=>client.Poll(1))!.Code,Is.EqualTo(GuaSpatialErrorCode.NotAuthorized));
        Assert.That(h.Runtime.GetVersionJson(),Does.Not.Contain("spatial_read_r1"));
        foreach(var profile in new[]{"Player","PublicAgent"})
        {
            using var grants=Doc(1); var denied=grants.ReadOwner(); denied.Profile=profile;
            Assert.That(Assert.Throws<GuaSpatialException>(()=>h.Runtime.BindSpatial(h.Spatial,h.Provider,denied))!.Code,Is.EqualTo(GuaSpatialErrorCode.NotAuthorized));
        }
    }
    [Test]
    public void ProviderRemovalMakesCapabilityDisappearAndRedactsRetainedGeometry()
    {
        using var h=new Host(); using var client=h.Runtime.CreateSpatialClient(); using var batch=Doc(2);
        client.Enqueue(batch.ReadBatch()); h.Pump(); h.Spatial.Unregister(h.Provider);
        using var result=client.Poll(1)!;
        Assert.That(result.ReadBatchResult().Items.All(i=>i.Reason=="provider_unregistered" && i.Result is null),Is.True);
        Assert.That(h.Runtime.GetVersionJson(),Does.Not.Contain("spatial_read_r1"));
    }
    [Test]
    public void RebindingCannotRestorePreviousConnectionOrEnlargeItsGrants()
    {
        using var h=new Host(); using var client=h.Runtime.CreateSpatialClient(); h.Bind();
        Assert.That(Assert.Throws<GuaSpatialException>(()=>client.Describe())!.Code,Is.EqualTo(GuaSpatialErrorCode.Stale));
        using var fresh=h.Runtime.CreateSpatialClient(); using var batch=Doc(2); var input=batch.ReadBatch();
        input.Queries[0].QueryPolicyId="secret-policy";
        Assert.That(Assert.Throws<GuaSpatialException>(()=>fresh.Enqueue(input))!.Code,Is.EqualTo(GuaSpatialErrorCode.NotAuthorized));
    }
    [Test]
    public void RealWebSocketDispatchRetainsBatchCorrelationAndExistingUiRead()
    {
        using var h=new Host(); var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        Assert.That(h.Runtime.StartInspectorBridge(port),Is.True);
        using var remote=new GuaWebSocketContext($"ws://127.0.0.1:{port}");
        Assert.That(remote.GetUiTreeJson(),Does.Contain("nodes"));
        Assert.That(remote.GetSpatialInfo().Provider.Engine.Backend,Is.EqualTo("fake"));
        using var batch=Doc(2); var request=Task.Run(()=>remote.QuerySpatialBatch(batch.ReadBatch(),TimeSpan.FromSeconds(2)));
        var deadline=DateTime.UtcNow.AddSeconds(2);
        while(!request.IsCompleted && DateTime.UtcNow<deadline) { h.Pump(); Thread.Sleep(5); }
        var result=request.GetAwaiter().GetResult(); Assert.That(result.BatchId,Is.EqualTo(1));
        Assert.That(result.Items.Select(i=>i.RequestId),Is.EqualTo(batch.ReadBatch().Queries.Select(q=>q.RequestId)));
    }
    [TestCase(1, false)]
    [TestCase(1048576, true)]
    public async Task SpatialAttachmentSeparatesStorageOmissionAndPreservesCallerFailure(int limit,bool expectedStored)
    {
        using var h=new Host(); using var client=h.Runtime.CreateSpatialClient(); using var batch=Doc(2);
        client.Enqueue(batch.ReadBatch()); h.Pump(); using var result=client.Poll(1)!; using var ad=client.Describe();
        using var trace=new GuaTraceSession(new GuaTraceOptions {OutputDirectory=Path.Combine(TestContext.CurrentContext.WorkDirectory,"gua-spatial-transport",Guid.NewGuid().ToString("N")),
            SavePolicy=GuaTraceSavePolicy.Always,MaxAttachmentBytes=limit});
        var step=trace.BeginStep(GuaTraceStepKind.Lifecycle,"spatial batch");
        Assert.That(GuaTraceCapture.SpatialBatch(trace,step,result,ad),Is.EqualTo(expectedStored));
        await trace.CompleteAsync(GuaTraceOutcome.Failed);
        var saved=GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(saved.Manifest.PrimaryOutcome,Is.EqualTo("failed"));
        var receipt=saved.Events.Single(e=>e.Type=="spatial.received");
        Assert.That(receipt.Data.GetProperty("storageStatus").GetString(),Is.EqualTo("unconfirmed"));
        if(expectedStored) Assert.That(saved.Events.Single(e=>e.Type=="spatial.storage").Data.GetProperty("storageOmitted").GetBoolean(),Is.False);
        else Assert.That(saved.Events.Any(e=>e.Type=="capture.failure" && e.StepId==step && e.Data.GetProperty("reason").GetString()=="spatial-storage-omitted") ||
            saved.Manifest.Quality.Issues.Contains("capture-failure:"+step+":gua.spatial.batch.r1:spatial-storage-omitted"),Is.True);
        Assert.That(receipt.Data.GetProperty("queryTruncated").GetBoolean(),Is.False);
        Assert.That(saved.Events.Any(e=>e.Type=="step.begin" && e.Data.GetProperty("kind").GetString()=="action"),Is.False);
        if(expectedStored) Assert.That(saved.Blobs.Values.Single().GetProperty("advertisement").GetProperty("provider").GetProperty("engine").GetProperty("backend").GetString(),Is.EqualTo("fake"));
        else Assert.That(saved.Manifest.Quality.Issues,Does.Contain("attachment-limit"));
    }
    [TestCase(GuaTraceCaptureMode.Recent,"memory","retained-byte-limit")]
    [TestCase(GuaTraceCaptureMode.Recent,"artifact","retained-byte-limit")]
    [TestCase(GuaTraceCaptureMode.Streaming,"memory","queue-byte-limit")]
    [TestCase(GuaTraceCaptureMode.Streaming,"artifact","artifact-limit")]
    [TestCase(GuaTraceCaptureMode.Streaming,"items","queue-limit")]
    public async Task StorageCapacityFailureKeepsTruthfulCorrelatedOmission(GuaTraceCaptureMode mode,string limit,string issue)
    {
        using var h=new Host(); using var client=h.Runtime.CreateSpatialClient(); using var batch=Doc(2);
        client.Enqueue(batch.ReadBatch()); h.Pump(); using var result=client.Poll(1)!; using var ad=client.Describe();
        using var trace=new GuaTraceSession(new GuaTraceOptions {OutputDirectory=Path.Combine(TestContext.CurrentContext.WorkDirectory,"gua-spatial-transport",Guid.NewGuid().ToString("N")),
            SavePolicy=GuaTraceSavePolicy.Always,CaptureMode=mode,MaxAttachmentBytes=1048576,
            MaxMemoryBytes=limit=="memory" ? 2048 : 16384,MaxArtifactBytes=limit=="artifact" ? 2048 : 16384,
            MaxQueueItems=limit=="items" ? 1 : 256});
        var gate=typeof(GuaTraceSession).GetField("_gate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(trace)!;
        string step;
        lock(gate)
        {
            step=trace.BeginStep(GuaTraceStepKind.Lifecycle,"spatial batch");
            if(limit=="items") for(var i=0;i<10 && !trace.Status.DetailStopped;i++) trace.Record(step,"detail",System.Text.Json.JsonSerializer.SerializeToElement(new {index=i}));
            Assert.That(GuaTraceCapture.SpatialBatch(trace,step,result,ad),Is.False);
            Assert.That(trace.Status.Issues,Does.Contain(issue),"The selected capacity failure must actually occur.");
        }
        await trace.CompleteAsync(GuaTraceOutcome.Failed);
        var saved=GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(saved.Manifest.PrimaryOutcome,Is.EqualTo("failed")); Assert.That(saved.Blobs,Is.Empty);
        Assert.That(saved.Events.Any(e=>e.Type=="capture.failure" && e.StepId==step && e.Data.GetProperty("channel").GetString()=="gua.spatial.batch.r1") ||
            saved.Manifest.Quality.Issues.Contains("capture-failure:"+step+":gua.spatial.batch.r1:spatial-storage-omitted"),Is.True);
        Assert.That(saved.Events.Any(e=>e.Type=="spatial.storage" && !e.Data.GetProperty("storageOmitted").GetBoolean()),Is.False);
        foreach(var receipt in saved.Events.Where(e=>e.Type=="spatial.received")) {
            Assert.That(receipt.Data.GetProperty("storageStatus").GetString(),Is.EqualTo("unconfirmed"));
            Assert.That(receipt.Data.GetProperty("queryTruncated").GetBoolean(),Is.False);
        }
        var payloadBytes=Directory.GetFiles(trace.ArtifactPath,"*",SearchOption.AllDirectories).Where(p=>Path.GetFileName(p)!="manifest.json").Sum(p=>new FileInfo(p).Length);
        Assert.That(payloadBytes,Is.LessThanOrEqualTo(limit=="artifact" ? 2048 : 16384));
    }
}
