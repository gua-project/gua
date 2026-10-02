using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Net.WebSockets;
using System.Text;
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
        internal readonly GuaSpatialHost Spatial;
        internal readonly GuaRuntime Runtime=new();
        internal readonly ulong Provider;
        internal Host(uint maxOwners=8) { Spatial=new(new GuaSpatialHostOptions {MaxProviders=2,MaxOwners=maxOwners,MaxQueueDepth=4,MaxQueriesPerBatch=64,MaxHitsPerQuery=2,QueryDeadlineMs=1000,MaxBatchWorkMs=1000},"transport-clock"); using var registration=Doc(0); Provider=Spatial.Register(registration); Bind(); }
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
    [TestCase(false,1048576)] [TestCase(true,1048576)]
    [TestCase(false,1)] [TestCase(true,1)]
    public async Task PartialBoundaryResultSurvivesWebSocketTraceReaderAndReport(bool truncated,int storageLimit)
    {
        using var h=new Host(); var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); Assert.That(h.Runtime.StartInspectorBridge(port),Is.True);
        using var remote=new GuaWebSocketContext($"ws://127.0.0.1:{port}"); using var original=Doc(2);
        var input=original.ReadBatch();
        // Three ordered queries: one completed, one consumed but unfinished,
        // one never consumed. End must preserve all three distinctions.
        var third=JsonNode.Parse(original.ToJson())!["queries"]![0]!.DeepClone(); third["requestId"]=3; third["queryId"]="q3";
        using var thirdDoc=GuaSpatialDocument.FromJson(GuaSpatialDocumentType.Request,third.ToJsonString());
        input.Queries=[..input.Queries,thirdDoc.ReadRequest()];
        var directory=Path.Combine(TestContext.CurrentContext.WorkDirectory,"gua-spatial-partial",Guid.NewGuid().ToString("N"));
        using var trace=new GuaTraceSession(new GuaTraceOptions {OutputDirectory=directory,SavePolicy=GuaTraceSavePolicy.Always,MaxAttachmentBytes=storageLimit});
        var call=Task.Run(()=>remote.QuerySpatialBatch(input,TimeSpan.FromSeconds(3),trace:trace));
        using var boundary=Doc(3); ulong? lease=null; var deadline=DateTime.UtcNow.AddSeconds(2);
        while(lease is null && DateTime.UtcNow<deadline) { lease=h.Spatial.Begin(h.Provider,boundary); if(lease is null) await Task.Delay(5); }
        Assert.That(lease,Is.Not.Null,"The submitted batch must reach the real native host.");
        using(var first=h.Spatial.Take(lease!.Value)) {
            Assert.That(first!.ReadRequest().RequestId,Is.EqualTo(1));
            var execution=JsonNode.Parse(DocJson(4))!;
            execution["outcome"]="hit"; execution["nearest"]="returnedHits"; execution["truncated"]=truncated;
            execution["hits"]=JsonNode.Parse("[{\"relation\":\"unknown\",\"distance\":0.5,\"missing\":{\"position\":\"fixture unavailable\",\"normal\":\"fixture unavailable\",\"collisionRef\":\"anonymous\",\"worldObjectId\":\"unregistered\"}}]");
            using var completion=GuaSpatialDocument.FromJson(GuaSpatialDocumentType.Execution,execution.ToJsonString()); h.Spatial.Complete(lease.Value,completion);
        }
        using(var second=h.Spatial.Take(lease.Value)) Assert.That(second!.ReadRequest().RequestId,Is.EqualTo(2));
        h.Spatial.End(lease.Value);
        var received=await call;
        Assert.That(received.Items,Has.Length.EqualTo(3));
        Assert.That(received.Items.Select(i=>i.RequestId),Is.EqualTo(new long[]{1,2,3}));
        Assert.That(received.Items.Select(i=>i.QueryId),Is.EqualTo(new[]{"q1","q2","q3"}));
        Assert.That(received.Items.Select(i=>i.State),Is.EqualTo(new[]{"completed","failed","notExecuted"}));
        Assert.That(received.Items.Skip(1).Select(i=>i.Reason),Is.EqualTo(new[]{"boundary_ended","boundary_ended"}));
        Assert.That(received.Items.Skip(1).All(i=>i.Result is null),Is.True);
        Assert.That(received.Items[0].Result!.Truncated,Is.EqualTo(truncated));
        await trace.CompleteAsync(GuaTraceOutcome.Failed); var saved=GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(saved.Manifest.PrimaryOutcome,Is.EqualTo("failed"));
        Assert.That(saved.Events.Count(e=>e.Type=="step.begin" && e.Data.GetProperty("kind").GetString()=="lifecycle"),Is.EqualTo(1));
        Assert.That(saved.Events.Any(e=>e.Type=="step.begin" && e.Data.GetProperty("kind").GetString()=="action"),Is.False);
        var receipt=saved.Events.Single(e=>e.Type=="spatial.received");
        Assert.That(receipt.Data.GetProperty("batchId").GetInt64(),Is.EqualTo(input.BatchId));
        Assert.That(receipt.Data.GetProperty("queryTruncated").GetBoolean(),Is.EqualTo(truncated));
        var states=receipt.Data.GetProperty("states").EnumerateArray().ToArray();
        Assert.That(states.Select(s=>s.GetProperty("state").GetString()),Is.EqualTo(new[]{"completed","failed","notExecuted"}));
        Assert.That(states.Select(s=>s.GetProperty("requestId").GetInt64()),Is.EqualTo(new long[]{1,2,3}));
        Assert.That(states.Skip(1).Select(s=>s.GetProperty("reason").GetString()),Is.EqualTo(new[]{"boundary_ended","boundary_ended"}));
        if(storageLimit==1) {
            Assert.That(saved.Blobs,Is.Empty); Assert.That(saved.Manifest.Quality.Issues,Does.Contain("attachment-limit"));
            Assert.That(saved.Events.Any(e=>e.Type=="spatial.storage"),Is.False);
        } else {
            var items=saved.Blobs.Values.Single().GetProperty("result").GetProperty("items").EnumerateArray().ToArray();
            Assert.That(items,Has.Length.EqualTo(3)); Assert.That(items[0].GetProperty("result").GetProperty("truncated").GetBoolean(),Is.EqualTo(truncated));
            Assert.That(items.Skip(1).All(i=>!i.TryGetProperty("result",out _)),Is.True);
            Assert.That(items.Select(i=>i.GetProperty("state").GetString()),Is.EqualTo(new[]{"completed","failed","notExecuted"}));
        }
        var report=GuaTraceReport.WriteHtml(trace.ArtifactPath,Path.Combine(directory,"partial.html"));
        Assert.That(report.Succeeded,Is.True,report.Error); var html=File.ReadAllText(report.Path!);
        Assert.That(html,Does.Contain("boundary_ended").And.Contain("notExecuted"));
    }
    static string DocJson(int index) { using var document=Doc(index); return document.ToJson(); }
    [TestCase("typescript","cancel")] [TestCase("typescript","timeout")]
    [TestCase("typescript","disconnect")] [TestCase("typescript","correlation")]
    [TestCase("mcp","cancel")] [TestCase("mcp","timeout")]
    [TestCase("mcp","disconnect")] [TestCase("mcp","correlation")]
    public async Task TypeScriptAndBuiltMcpInvalidateActualNativeOwner(string route,string fault)
    {
        using var h=new Host(1); var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); Assert.That(h.Runtime.StartInspectorBridge(port),Is.True);
        var root=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"AGENTS.md"))) root=root.Parent;
        Assert.That(root,Is.Not.Null,"This integration test requires the source checkout and built MCP driver.");
        var start=new System.Diagnostics.ProcessStartInfo("bun") {WorkingDirectory=root!.FullName,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
        foreach(var argument in new[]{"scripts/verify-spatial-owner.ts",$"ws://127.0.0.1:{port}",route,fault}) start.ArgumentList.Add(argument);
        using var driver=System.Diagnostics.Process.Start(start)!;
        var errors=driver.StandardError.ReadToEndAsync();
        ulong? lease=null;
        try {
            Assert.That(await driver.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)),Is.EqualTo("poll"),"The fault must fire after actual native acceptance.");
            using var boundary=Doc(3); lease=h.Spatial.Begin(h.Provider,boundary);
            Assert.That(lease,Is.Not.Null,"Real host has the original pending batch.");
            using var first=h.Spatial.Take(lease!.Value); Assert.That(first!.ReadRequest().RequestId,Is.EqualTo(1));
            driver.StandardInput.WriteLine("fire"); driver.StandardInput.Flush();
            await driver.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var output=await driver.StandardOutput.ReadToEndAsync();
            Assert.That(driver.ExitCode,Is.EqualTo(0),await errors);
            Assert.That(output,Does.Contain("\"submissions\":1").And.Contain("\"connections\":1"));
            // Closing the TS/MCP connection must invalidate the native owner.
            // Its consumed completion is acknowledged, discarded and cannot be
            // resumed by a later connection. Capacity one exposes leaked owners.
            using var completion=Doc(4); Assert.DoesNotThrow(()=>h.Spatial.Complete(lease.Value,completion));
            Assert.That(h.Spatial.Take(lease.Value),Is.Null); h.Spatial.End(lease.Value); lease=null;
            GuaRuntimeSpatialClient? fresh=null; var deadline=DateTime.UtcNow.AddSeconds(2);
            while(fresh is null && DateTime.UtcNow<deadline) {
                try { fresh=h.Runtime.CreateSpatialClient(); }
                catch(InvalidOperationException) { await Task.Delay(10); }
            }
            using(fresh) {
                Assert.That(fresh,Is.Not.Null,"The disconnected native owner must release its bounded slot.");
                using var info=fresh!.Describe(); Assert.That(info.ReadAdvertisement().Provider.ProviderId,Is.EqualTo("test"));
                Assert.That(Assert.Throws<GuaSpatialException>(()=>fresh.Poll(1))!.Code,Is.EqualTo(GuaSpatialErrorCode.Stale));
            }
            Assert.That(h.Spatial.Begin(h.Provider,boundary),Is.Null,"No retry or remaining batch may be leased.");
        } finally {
            if(!driver.HasExited) driver.Kill(entireProcessTree:true);
            if(lease is not null) h.Spatial.End(lease.Value);
        }
    }
    [Test]
    public void RebindAtFullOwnerCapacityReplacesOldOwner()
    {
        using var h=new Host(1); using var old=h.Runtime.CreateSpatialClient();
        h.Bind();
        Assert.Throws<GuaSpatialException>(()=>old.Describe());
        using var current=h.Runtime.CreateSpatialClient(); using var info=current.Describe();
        Assert.That(info.ReadAdvertisement().Provider.ProviderId,Is.Not.Empty);
    }
    [Test]
    public void OwnerlessConnectionNeverAdvertisesSpatialSupport()
    {
        using var h=new Host(1); var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        var port=((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); Assert.That(h.Runtime.StartInspectorBridge(port),Is.True);
        using var first=new GuaWebSocketContext($"ws://127.0.0.1:{port}"); Assert.That(first.GetSpatialInfo(),Is.Not.Null);
        using var second=new GuaWebSocketContext($"ws://127.0.0.1:{port}");
        var version=second.GetVersion();
        Assert.That(version.Capabilities,Does.Not.Contain("spatial_read_r1"));
        using var diagnostics=System.Text.Json.JsonDocument.Parse(second.GetDiagnosticsJson());
        Assert.That(diagnostics.RootElement.GetProperty("version").GetProperty("capabilities").EnumerateArray().Select(x=>x.GetString()),Does.Not.Contain("spatial_read_r1"));
        Assert.That(Assert.Catch<InvalidOperationException>(()=>second.GetSpatialInfo())!.Message,Is.EqualTo("unsupported"));
        Assert.That(first.GetVersion().Capabilities,Does.Contain("spatial_read_r1"));
        using var authorizedDiagnostics=System.Text.Json.JsonDocument.Parse(first.GetDiagnosticsJson());
        Assert.That(authorizedDiagnostics.RootElement.GetProperty("version").GetProperty("capabilities").EnumerateArray().Select(x=>x.GetString()),Does.Contain("spatial_read_r1"));
    }
    sealed class StalledPeer : IDisposable
    {
        readonly HttpListener listener=new(); readonly CancellationTokenSource stop=new(); readonly Task worker;
        internal readonly TaskCompletionSource<bool> Stalled=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Closed=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly string Url;
        internal StalledPeer(string stall,string advertisement)
        {
            var portSource=new TcpListener(IPAddress.Loopback,0); portSource.Start(); var port=((IPEndPoint)portSource.LocalEndpoint).Port; portSource.Stop();
            Url=$"ws://127.0.0.1:{port}/"; listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
            worker=Task.Run(async()=> {
                WebSocket? socket=null;
                try {
                    var context=await listener.GetContextAsync(); socket=(await context.AcceptWebSocketAsync(null)).WebSocket;
                    var buffer=new byte[65536];
                    while(!stop.IsCancellationRequested) {
                        using var message=new MemoryStream(); WebSocketReceiveResult received;
                        do { received=await socket.ReceiveAsync(new ArraySegment<byte>(buffer),stop.Token); if(received.MessageType==WebSocketMessageType.Close) return; message.Write(buffer,0,received.Count); } while(!received.EndOfMessage);
                        var request=JsonNode.Parse(Encoding.UTF8.GetString(message.ToArray()))!; var type=request["type"]!.GetValue<string>();
                        if(type==stall) { Stalled.TrySetResult(true); continue; }
                        var payload=Encoding.UTF8.GetBytes("{\"id\":"+request["id"]!.ToJsonString()+",\"ok\":true,\"result\":"+(type=="get_spatial_info" ? advertisement : "null")+"}");
                        await socket.SendAsync(new ArraySegment<byte>(payload),WebSocketMessageType.Text,true,stop.Token);
                    }
                } catch(Exception) when(stop.IsCancellationRequested||socket is not null) { }
                finally { socket?.Dispose(); Closed.TrySetResult(true); }
            });
        }
        public void Dispose() { stop.Cancel(); listener.Close(); worker.Wait(TimeSpan.FromSeconds(2)); stop.Dispose(); }
    }
    [TestCase("get_spatial_info",true)] [TestCase("query_spatial_batch",true)] [TestCase("poll_spatial_batch",true)]
    [TestCase("get_spatial_info",false)] [TestCase("query_spatial_batch",false)] [TestCase("poll_spatial_batch",false)]
    public async Task CancellationAndDeadlineInterruptEachWireWait(string stalled,bool cancel)
    {
        using var h=new Host(); using var client=h.Runtime.CreateSpatialClient(); using var ad=client.Describe(); using var batch=Doc(2);
        using var peer=new StalledPeer(stalled,ad.ToJson()); using var remote=new GuaWebSocketContext(peer.Url); using var cancellation=new CancellationTokenSource();
        var call=Task.Factory.StartNew(()=>remote.QuerySpatialBatch(batch.ReadBatch(),cancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(300),cancellation.Token),
            CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        await peer.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(2)); var clock=System.Diagnostics.Stopwatch.StartNew(); if(cancel) cancellation.Cancel();
        try { await call.WaitAsync(TimeSpan.FromSeconds(1)); Assert.Fail("Stalled read must fail."); }
        catch(OperationCanceledException) when(cancel) { }
        catch(TimeoutException) when(!cancel && call.IsCompleted) { }
        Assert.That(clock.Elapsed,Is.LessThan(TimeSpan.FromSeconds(1)));
        await peer.Closed.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }
    [TestCase(true)] [TestCase(false)]
    public async Task SpatialDeadlineIncludesSharedGateAcquisition(bool cancel)
    {
        using var remote=new GuaWebSocketContext("ws://127.0.0.1:1/"); using var batch=Doc(2); using var cancellation=new CancellationTokenSource();
        var gate=(SemaphoreSlim)typeof(GuaWebSocketContext).GetField("requestGate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(remote)!;
        gate.Wait();
        try {
            var call=Task.Factory.StartNew(()=>remote.QuerySpatialBatch(batch.ReadBatch(),cancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(100),cancellation.Token),
                CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            if(cancel) cancellation.Cancel();
            try { await call.WaitAsync(TimeSpan.FromSeconds(1)); Assert.Fail("Held gate must fail within deadline."); }
            catch(OperationCanceledException) when(cancel) { }
            catch(TimeoutException) when(!cancel && call.IsCompleted) { }
        } finally { gate.Release(); }
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
