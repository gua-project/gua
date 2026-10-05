using System.Text.Json;
using Gua.Core;
using Gua.Runtime;
using NUnit.Framework;

namespace Gua.Selector.Tests;

[TestFixture, NonParallelizable]
public sealed class ContextLifetimeTests
{
    [TestCase(false, 0)]
    [TestCase(false, 1)]
    [TestCase(false, 2)]
    [TestCase(true, 0)]
    public async Task DiagnosticCopiesRemainSafeDuringDisposal(bool borrowed, int profile)
    {
        // A sizeable native snapshot keeps a real copy in flight while its owner
        // disposes. A successful read must be complete; a later read must throw
        // ObjectDisposedException, never use a freed C ABI context.
        for (var iteration = 0; iteration < 10; iteration++)
        {
            using var runtime = borrowed ? new GuaRuntime() : null;
            using var context = borrowed ? null : new GuaContext();
            if (borrowed) runtime!.BeginFrame("lifetime");
            else context!.BeginFrame("lifetime");
            for (var i = 0; i < 2000; i++)
            {
                var node = new GuaNodeDescriptor("node" + i, "button", new string('x', 96), new(0, 0, 10, 10));
                if (borrowed) runtime!.RegisterNode(node);
                else context!.RegisterNode(node);
            }
            if (borrowed) runtime!.EndFrame();
            else context!.EndFrame();

            string Read() => borrowed ? runtime!.GetDiagnosticsJson() : profile switch
            {
                1 => context!.GetDiagnosticsJson(GuaObservationProfile.Debug),
                2 => context!.GetDiagnosticsJson(GuaObservationProfile.Player),
                _ => context!.GetDiagnosticsJson(),
            };
            void Validate(string json)
            {
                using var document = JsonDocument.Parse(json);
                Assert.That(document.RootElement.GetProperty("uiTree").GetProperty("nodes").GetArrayLength(), Is.EqualTo(2000));
            }
            Validate(Read());
            using var entered = new ManualResetEventSlim();
            var reading = Task.Factory.StartNew(() =>
            {
                entered.Set();
                try { for (var read = 0; read < 20; read++) Validate(Read()); }
                catch (ObjectDisposedException) { }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Thread.Sleep(1);
            if (borrowed) runtime!.Dispose();
            else context!.Dispose();
            await reading.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Throws<ObjectDisposedException>(() => Read());
        }
    }
}
