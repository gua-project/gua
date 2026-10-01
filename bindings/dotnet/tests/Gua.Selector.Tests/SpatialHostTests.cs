using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Core;
using NUnit.Framework;
namespace Gua.Selector.Tests;
[TestFixture]
public sealed class SpatialHostTests
{
    private static JsonNode Fixture => JsonNode.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory,"spatial-host-r1.json")))!;
    private static GuaSpatialDocument Doc(int index) { var c=Fixture["valid"]![index]!; return GuaSpatialDocument.FromJson((GuaSpatialDocumentType)c["type"]!.GetValue<int>(),c["json"]!.ToJsonString()); }
    private static GuaSpatialHost Host() => new(new GuaSpatialHostOptions { MaxProviders=2, MaxOwners=2, MaxQueueDepth=2, MaxQueriesPerBatch=64, MaxHitsPerQuery=2, QueryDeadlineMs=1000, MaxBatchWorkMs=1000 },"managed-clock");
    [Test]
    public void SharedContractsAndLegacyVersionRemainStrict()
    {
        Assert.That(Marshal.SizeOf<GuaSpatialHostOptions>(),Is.EqualTo(40));
        foreach(var c in Fixture["valid"]!.AsArray()) { using var d=GuaSpatialDocument.FromJson((GuaSpatialDocumentType)c!["type"]!.GetValue<int>(),c["json"]!.ToJsonString()); using var copy=GuaSpatialDocument.FromJson(d.Type,d.ToJson()); Assert.That(copy.ToJson(),Is.EqualTo(d.ToJson())); }
        foreach(var c in Fixture["invalid"]!.AsArray()) Assert.Throws<GuaSpatialException>(()=> { using var d=GuaSpatialDocument.FromJson((GuaSpatialDocumentType)c!["type"]!.GetValue<int>(),c["json"]!.ToJsonString()); });
    }
    [Test]
    public void OrderedBoundaryHasOptionalTickAndPartialEnd()
    {
        using var h=Host(); using var registration=Doc(0); using var grants=Doc(1); using var batch=Doc(2); using var boundary=Doc(3); using var execution=Doc(4);
        var p=h.Register(registration); var o=h.OpenOwner(grants); h.Enqueue(o,batch);
        Assert.That(h.Poll(o,1),Is.Null); var l=h.Begin(p,boundary)!.Value;
        using var q=h.Take(l)!; Assert.That(q.ReadRequest().MaxHits,Is.EqualTo(2)); h.Complete(l,execution); h.End(l);
        using var result=h.Poll(o,1)!; var r=JsonNode.Parse(result.ToJson())!;
        var typed=result.ReadBatchResult(); Assert.That(typed.Items[0].Result!.Sample!.Tick,Is.Null);
        Assert.That(r["items"]![0]!["state"]!.GetValue<string>(),Is.EqualTo("completed"));
        Assert.That(r["items"]![0]!["result"]!["sample"]!["tick"],Is.Null);
        Assert.That(r["items"]![0]!["result"]!["sample"]!["clockId"]!.GetValue<string>(),Is.EqualTo("managed-clock"));
        Assert.That(r["items"]![1]!["state"]!.GetValue<string>(),Is.EqualTo("notExecuted"));
        Assert.That(Assert.Throws<GuaSpatialException>(()=>h.Poll(o,1))!.Code,Is.EqualTo(GuaSpatialErrorCode.Stale));
    }
    [Test]
    public void RevocationAndLateCompletionCannotPublishGeometry()
    {
        using var h=Host(); using var registration=Doc(0); using var grants=Doc(1); using var batch=Doc(2); using var boundary=Doc(3); using var execution=Doc(4);
        var p=h.Register(registration); var o=h.OpenOwner(grants); h.Enqueue(o,batch); var l=h.Begin(p,boundary)!.Value;
        using var q=h.Take(l)!; var g=JsonNode.Parse(grants.ToJson())!; g["profile"]="Player";
        using var revoked=GuaSpatialDocument.FromJson(GuaSpatialDocumentType.Owner,g.ToJsonString());
        Task.Run(()=>h.SetOwner(o,revoked)).GetAwaiter().GetResult();
        Assert.That(Assert.Throws<GuaSpatialException>(()=>h.Complete(l,execution))!.Code,Is.EqualTo(GuaSpatialErrorCode.Stale));
        using var result=h.Poll(o,1)!; Assert.That(result.ToJson(),Does.Not.Contain("\"result\":").And.Not.Contain("sample")); h.End(l);
        Assert.That(Assert.Throws<GuaSpatialException>(()=>h.Enqueue(o,batch))!.Code,Is.EqualTo(GuaSpatialErrorCode.NotAuthorized));
    }
    [Test]
    public void DisposedHostAndDocumentsCannotCrossAbi()
    {
        using var registration=Doc(0); var h=Host(); h.Dispose();
        Assert.Throws<ObjectDisposedException>(()=>h.Register(registration));
        using var active=Host(); registration.Dispose(); Assert.Throws<ObjectDisposedException>(()=>active.Register(registration));
    }
    [Test]
    public void DisposeRacesSafeNativeCalls()
    {
        for(int i=0;i<30;i++) {
            var h=Host(); using var registration=Doc(0); using var grants=Doc(1); var p=h.Register(registration); var o=h.OpenOwner(grants);
            var task=Task.Run(()=> { try { h.CloseOwner(o); } catch(ObjectDisposedException) {} }); h.Dispose(); task.GetAwaiter().GetResult();
        }
    }
    [Test]
    public void TypedAuthoringAndAdvertisementUseNativeContracts()
    {
        using var h=Host(); using var registration=Doc(0); using var owner=Doc(1); using var batch=Doc(2); using var boundary=Doc(3); using var execution=Doc(4);
        using var regCopy=GuaSpatialDocument.FromRegistration(registration.ReadRegistration());
        using var ownerCopy=GuaSpatialDocument.FromOwner(owner.ReadOwner());
        using var batchCopy=GuaSpatialDocument.FromBatch(batch.ReadBatch());
        using var boundaryCopy=GuaSpatialDocument.FromBoundary(boundary.ReadBoundary());
        using var executionCopy=GuaSpatialDocument.FromExecution(execution.ReadExecution());
        var p=h.Register(regCopy); var o=h.OpenOwner(ownerCopy); using var ad=h.Describe(o,p);
        Assert.That(ad.ReadAdvertisement().Provider.Limits.MaxHitsPerQuery,Is.EqualTo(2));
        Assert.That(ad.ReadAdvertisement().Budgets.MaxQueueDepth,Is.EqualTo(2));
        h.Enqueue(o,batchCopy); var l=h.Begin(p,boundaryCopy)!.Value; using var q=h.Take(l); h.Complete(l,executionCopy); h.End(l); using var r=h.Poll(o,1); Assert.That(r!.ReadBatchResult().Items[1].State,Is.EqualTo("notExecuted"));
    }
    [Test]
    public void NormalManagedPumpEndsAtNullAfterLastCompletion()
    {
        using var h=Host(); using var registration=Doc(0); using var grants=Doc(1); using var batch=Doc(2); using var boundary=Doc(3); using var execution=Doc(4);
        var p=h.Register(registration); var o=h.OpenOwner(grants); h.Enqueue(o,batch); var l=h.Begin(p,boundary)!.Value; int count=0;
        try {
            while(true) {
                using var q=h.Take(l); if(q is null) break;
                var request=q.ReadRequest(); var completed=execution.ReadExecution(); completed.RequestId=request.RequestId; completed.QueryId=request.QueryId;
                using var owned=GuaSpatialDocument.FromExecution(completed); h.Complete(l,owned); ++count;
            }
            using var result=h.Poll(o,1); Assert.That(result!.ReadBatchResult().Items.All(i=>i.State=="completed"),Is.True);
            Assert.That(h.Take(l),Is.Null); Assert.That(count,Is.EqualTo(2));
        } finally { h.End(l); }
    }
    [TestCase(false)]
    [TestCase(true)]
    public void LateMiscorrelatedCompletionPreservesElapsedBudgetReason(bool deadline)
    {
        using var h=new GuaSpatialHost(new GuaSpatialHostOptions { MaxProviders=2,MaxOwners=2,MaxQueueDepth=2,MaxQueriesPerBatch=64,MaxHitsPerQuery=2,QueryDeadlineMs=deadline?50:1000,MaxBatchWorkMs=deadline?1000:50 },"managed-clock");
        using var registration=Doc(0); using var grants=Doc(1); using var original=Doc(2); using var boundary=Doc(3); using var execution=Doc(4);
        var batch=original.ReadBatch(); if(deadline) foreach(var query in batch.Queries) query.DeadlineMs=50;
        using var input=GuaSpatialDocument.FromBatch(batch); var bad=execution.ReadExecution(); bad.QueryId="wrong"; using var payload=GuaSpatialDocument.FromExecution(bad);
        var p=h.Register(registration); var o=h.OpenOwner(grants); h.Enqueue(o,input); var l=h.Begin(p,boundary)!.Value; using var q=h.Take(l);
        Thread.Sleep(100);
        Assert.That(Assert.Throws<GuaSpatialException>(()=>h.Complete(l,payload))!.Code,Is.EqualTo(GuaSpatialErrorCode.NotReady));
        using var result=h.Poll(o,1)!; var items=result.ReadBatchResult().Items;
        Assert.That(items[0].State,Is.EqualTo("failed")); Assert.That(items[1].State,Is.EqualTo("notExecuted"));
        Assert.That(items.All(i=>i.Reason==(deadline?"deadline_exceeded":"work_budget")&&i.Result is null),Is.True); h.End(l);
    }
    [Test]
    public void UnregisterPreservesTerminationReasonsAndRedactsCompletedGeometry()
    {
        using var h=Host(); using var registration=Doc(0); using var grants=Doc(1); using var batch=Doc(2); using var boundary=Doc(3); using var execution=Doc(4);
        var p=h.Register(registration); var o=h.OpenOwner(grants); h.Enqueue(o,batch); var l=h.Begin(p,boundary)!.Value;
        using var q=h.Take(l); h.Complete(l,execution); h.Unregister(p);
        using var result=h.Poll(o,1)!; var items=result.ReadBatchResult().Items;
        Assert.That(items[0].State,Is.EqualTo("failed")); Assert.That(items[1].State,Is.EqualTo("notExecuted"));
        Assert.That(items.All(i=>i.Reason=="provider_unregistered"&&i.Result is null),Is.True);
        Assert.That(Assert.Throws<GuaSpatialException>(()=>h.End(l))!.Code,Is.EqualTo(GuaSpatialErrorCode.Stale));
    }
    [Test]
    public void ClosedOwnerRetainsConsumedCompletionButCannotPoll()
    {
        using var h=Host(); using var registration=Doc(0); using var grants=Doc(1); using var batch=Doc(2); using var boundary=Doc(3); using var execution=Doc(4);
        var p=h.Register(registration); var o=h.OpenOwner(grants); h.Enqueue(o,batch); var l=h.Begin(p,boundary)!.Value; using var q=h.Take(l);
        h.CloseOwner(o); Assert.That(Assert.Throws<GuaSpatialException>(()=>h.Poll(o,1))!.Code,Is.EqualTo(GuaSpatialErrorCode.Stale));
        Assert.DoesNotThrow(()=>h.Complete(l,execution)); Assert.That(h.Take(l),Is.Null); h.End(l);
        Assert.That(Assert.Throws<GuaSpatialException>(()=>h.Complete(l,execution))!.Code,Is.EqualTo(GuaSpatialErrorCode.Stale));
    }
}
