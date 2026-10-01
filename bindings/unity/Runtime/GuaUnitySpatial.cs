using System;
using System.Collections.Generic;
using System.Threading;
using Gua.Core;
using UnityEngine;

namespace Gua.Unity
{
    /// <summary>Trusted collision policy. Self exclusion must use excluded layers;
    /// post-query predicates cannot satisfy the spatial host prefilter contract.</summary>
    public sealed class GuaUnitySpatialPolicy
    {
        public readonly string Id;
        public readonly int LayerMask;
        public readonly QueryTriggerInteraction Triggers;
        public readonly bool Backfaces;
        public GuaUnitySpatialPolicy(string id, int layerMask, QueryTriggerInteraction triggers, bool backfaces)
        {
            if (triggers == QueryTriggerInteraction.UseGlobal) throw new ArgumentException("Explicit trigger policy required");
            Id = id; LayerMask = layerMask; Triggers = triggers; Backfaces = backfaces;
        }
    }

    /// <summary>Opt-in, main-thread PhysicsScene reader. The host must call Pump
    /// at its approved synchronized physics boundary, with no mutations during
    /// the call. This reader never SyncTransforms, Simulate or changes globals.
    /// Dispose before scene destruction/origin changes, then register a new epoch.</summary>
    public sealed class GuaUnitySpatial : IDisposable
    {
        readonly GuaSpatialHost host;
        readonly PhysicsScene scene;
        readonly GuaSpatialRegistration registration;
        readonly Dictionary<string, GuaUnitySpatialPolicy> policies = new Dictionary<string, GuaUnitySpatialPolicy>();
        readonly RaycastHit[] casts;
        readonly Collider[] overlaps;
        readonly int thread = Thread.CurrentThread.ManagedThreadId;
        readonly string samplePrefix = Guid.NewGuid().ToString("N");
        long sample;
        bool disposed, pumping;
        public ulong Provider { get; private set; }

        public GuaUnitySpatial(GuaSpatialHost host, PhysicsScene scene, GuaSpatialRegistration registration,
            GuaUnitySpatialPolicy[] policies, int bufferCapacity = 64)
        {
            if (!scene.IsValid()) throw new ArgumentException("Registered scene must be valid");
            if (bufferCapacity < 33 || bufferCapacity > 1024) throw new ArgumentOutOfRangeException(nameof(bufferCapacity));
            this.host = host; this.scene = scene;
            // Freeze mutable authoring DTOs through the native validator.
            using (var doc = GuaSpatialDocument.FromRegistration(registration)) this.registration = doc.ReadRegistration();
            foreach (var policy in policies) this.policies.Add(policy.Id, policy);
            foreach (var policy in this.registration.Policies)
                if (!this.policies.ContainsKey(policy.Id)) throw new ArgumentException("Missing host collision policy");
            if (this.registration.Provider.Engine.Name != "Unity" || this.registration.Provider.Engine.Version != Application.unityVersion
                || this.registration.Provider.Engine.Backend != "PhysX" || this.registration.Provider.Precision.Representation != "binary32")
                throw new ArgumentException("Registration must identify the running Unity patch version");
            casts = new RaycastHit[bufferCapacity]; overlaps = new Collider[bufferCapacity];
            using (var doc = GuaSpatialDocument.FromRegistration(this.registration)) Provider = host.Register(doc);
        }

        void CheckThread()
        {
            if (thread != Thread.CurrentThread.ManagedThreadId) throw new InvalidOperationException("Physics requires the registering main thread");
            if (disposed) throw new ObjectDisposedException(nameof(GuaUnitySpatial));
        }

        /// <summary>One bounded batch per boundary. Tick is optional actual host
        /// tick, never inferred from frameCount. No World snapshot is fabricated.</summary>
        public bool Pump(long? tick = null)
        {
            CheckThread();
            if (pumping) throw new InvalidOperationException("Reentrant physics boundary");
            if (!scene.IsValid()) { Dispose(); return false; }
            pumping = true;
            ulong? lease = null;
            try
            {
                using (var boundary = GuaSpatialDocument.FromBoundary(new GuaSpatialBoundary
                    { PhysicsSampleId = samplePrefix + ":" + (++sample), Tick = tick }))
                    lease = host.Begin(Provider, boundary);
                if (!lease.HasValue) return false;
                while (true)
                {
                    using (var request = host.Take(lease.Value))
                    {
                        if (request == null) break;
                        var query = request.ReadRequest();
                        var result = Execute(query, lease.Value);
                        using (var execution = GuaSpatialDocument.FromExecution(result)) host.Complete(lease.Value, execution);
                    }
                }
                return true;
            }
            finally
            {
                try { if (lease.HasValue) host.End(lease.Value); }
                finally { pumping = false; Array.Clear(casts, 0, casts.Length); Array.Clear(overlaps, 0, overlaps.Length); }
            }
        }

        static float F(double value)
        {
            var converted = (float)value;
            if (float.IsInfinity(converted) || float.IsNaN(converted) || Math.Abs(value) > 8192)
                throw new ArgumentOutOfRangeException(nameof(value));
            return converted;
        }
        static Vector3 V(GuaSpatialVector v) => new Vector3(F(v.X), F(v.Y), F(v.Z));
        static GuaSpatialVector V(Vector3 v) => new GuaSpatialVector(v.x, v.y, v.z);
        static Quaternion Orientation(GuaSpatialBasis basis)
        {
            // Either handedness is legal. Flipping one box axis describes the
            // identical occupied box and gives Unity a proper rotation basis.
            return Quaternion.LookRotation(V(basis.Z), V(basis.Y));
        }
        int Overlap(GuaSpatialShape shape, GuaUnitySpatialPolicy policy)
        {
            switch (shape.Type)
            {
                case "sphere": return scene.OverlapSphere(V(shape.Center), F(shape.Radius.Value), overlaps, policy.LayerMask, policy.Triggers);
                case "capsule":
                    if (shape.PointA == shape.PointB) return scene.OverlapSphere(V(shape.PointA), F(shape.Radius.Value), overlaps, policy.LayerMask, policy.Triggers);
                    return scene.OverlapCapsule(V(shape.PointA), V(shape.PointB), F(shape.Radius.Value), overlaps, policy.LayerMask, policy.Triggers);
                case "box": return scene.OverlapBox(V(shape.Center), V(shape.HalfExtents), overlaps, Orientation(shape.Basis), policy.LayerMask, policy.Triggers);
                default: throw new ArgumentException("Unsupported shape");
            }
        }
        int Cast(GuaSpatialShape shape, Vector3 direction, float distance, GuaUnitySpatialPolicy policy)
        {
            switch (shape.Type)
            {
                case "sphere": return scene.SphereCast(V(shape.Center), F(shape.Radius.Value), direction, casts, distance, policy.LayerMask, policy.Triggers);
                case "capsule":
                    if (shape.PointA == shape.PointB) return scene.SphereCast(V(shape.PointA), F(shape.Radius.Value), direction, casts, distance, policy.LayerMask, policy.Triggers);
                    return scene.CapsuleCast(V(shape.PointA), V(shape.PointB), F(shape.Radius.Value), direction, casts, distance, policy.LayerMask, policy.Triggers);
                case "box": return scene.BoxCast(V(shape.Center), V(shape.HalfExtents), direction, casts, Orientation(shape.Basis), distance, policy.LayerMask, policy.Triggers);
                default: throw new ArgumentException("Unsupported shape");
            }
        }

        GuaSpatialEngineBounds PreparedBounds(GuaSpatialRequest q)
        {
            var lo=new[]{double.PositiveInfinity,double.PositiveInfinity,double.PositiveInfinity};
            var hi=new[]{double.NegativeInfinity,double.NegativeInfinity,double.NegativeInfinity};
            Func<GuaSpatialVector,double[]> original=v=>new[]{v.X,v.Y,v.Z};
            Func<Vector3,double[]> converted=v=>new[]{(double)v.x,(double)v.y,(double)v.z};
            var delta=q.Kind=="raycast"?V(q.Segment.To)-V(q.Segment.From):q.Kind=="sweep"?V(q.Delta):Vector3.zero;
            var length=delta.magnitude;
            if((q.Kind=="raycast"||(q.Kind=="sweep"&&(q.Delta.X!=0||q.Delta.Y!=0||q.Delta.Z!=0)))&&(length==0||float.IsInfinity(length))) throw new ArgumentException("Collapsed motion");
            var travel=length==0?Vector3.zero:(delta/length).normalized*length;
            for(int pass=0;pass<2;++pass)
            {
                bool engine=pass==1;
                Func<GuaSpatialVector,double[]> v=n=>engine?converted(V(n)):original(n);
                double[] a,b;
                if(q.Kind=="raycast") { a=v(q.Segment.From); b=engine?converted(V(q.Segment.From)+travel):v(q.Segment.To); }
                else
                {
                    var s=q.Shape; a=v(s.Type=="capsule"?s.PointA:s.Center); b=v(s.Type=="capsule"?s.PointB:s.Center);
                    if(engine && s.Type=="capsule" && (a[0]!=b[0] || a[1]!=b[1] || a[2]!=b[2]) && (V(s.PointB)-V(s.PointA)).sqrMagnitude==0) throw new ArgumentException("Collapsed capsule axis");
                    var ext=new double[3];
                    if(s.Type=="box")
                    {
                        var h=v(s.HalfExtents); if(h[0]<=0||h[1]<=0||h[2]<=0) throw new ArgumentException("Collapsed extents");
                        var rotation=engine?Orientation(s.Basis):Quaternion.identity;
                        var x=engine?converted(rotation*Vector3.right):original(s.Basis.X);
                        var y=engine?converted(rotation*Vector3.up):original(s.Basis.Y);
                        var z=engine?converted(rotation*Vector3.forward):original(s.Basis.Z);
                        for(int i=0;i<3;++i) ext[i]=Math.Abs(x[i])*h[0]+Math.Abs(y[i])*h[1]+Math.Abs(z[i])*h[2];
                    }
                    else { var r=engine?F(s.Radius.Value):s.Radius.Value; if(r<=0) throw new ArgumentException("Collapsed radius"); for(int i=0;i<3;++i) ext[i]=r; }
                    var d=q.Kind=="sweep"?(engine?converted(travel):original(q.Delta)):new double[3];
                    for(int i=0;i<3;++i) { var min=Math.Min(a[i],b[i])-ext[i]; var max=Math.Max(a[i],b[i])+ext[i]; a[i]=min+Math.Min(0,d[i]); b[i]=max+Math.Max(0,d[i]); }
                }
                for(int i=0;i<3;++i) { lo[i]=Math.Min(lo[i],Math.Min(a[i],b[i])); hi[i]=Math.Max(hi[i],Math.Max(a[i],b[i])); }
            }
            double scale=1; for(int i=0;i<3;++i) scale=Math.Max(scale,Math.Max(Math.Abs(lo[i]),Math.Abs(hi[i])));
            // Guard prepared parameter rounding; backend collision error remains unknown.
            double guard=scale*64.0/8388608.0;
            return new GuaSpatialEngineBounds {MinX=lo[0]-guard,MinY=lo[1]-guard,MinZ=lo[2]-guard,MaxX=hi[0]+guard,MaxY=hi[1]+guard,MaxZ=hi[2]+guard};
        }
        static GuaSpatialHit Anonymous()
        {
            return new GuaSpatialHit { Missing = new Dictionary<string, string>
            {
                ["position"] = "not_observed", ["distance"] = "not_observed", ["normal"] = "not_observed",
                ["collisionRef"] = "not_published", ["worldObjectId"] = "not_published"
            }};
        }
        GuaSpatialHostQueryResult Execute(GuaSpatialRequest q, ulong lease)
        {
            var r = new GuaSpatialHostQueryResult { RequestId = q.RequestId, SessionEpoch = q.SessionEpoch,
                QueryId = q.QueryId, SpaceId = q.SpaceId, SpaceEpoch = q.SpaceEpoch, Kind = q.Kind };
            var policy = policies[q.QueryPolicyId];
            // Unity has no per-query backface flag. Refuse mismatched globals,
            // never silently inherit them or mutate game settings.
            if (Physics.queriesHitBackfaces != policy.Backfaces)
            { r.Status = "unsupported"; r.Error = new GuaSpatialFailure("unsupported_policy"); return r; }
            try
            {
                var loaded = host.CheckEngineBounds(lease, PreparedBounds(q));
                r.Status = "completed"; r.Coverage = loaded ? new GuaSpatialCoverage { State="complete", LoadedRegion=registration.LoadedRegion } : new GuaSpatialCoverage { State="unknown", Reason="engine_coverage_unverified" }; r.Truncated = false; r.Hits = Array.Empty<GuaSpatialHit>();
                int count;
                if (q.Kind != "raycast")
                {
                    count = Overlap(q.Shape, policy);
                    if (q.Kind == "sweep") r.InitialOverlap = count > 0 ? "detected" : "notDetected";
                    if (count > 0)
                    {
                        r.Outcome = q.Kind == "sweep" ? "initialOverlap" : "detected";
                        r.Truncated = count == overlaps.Length || count > (q.MaxHits ?? registration.Provider.Limits.MaxHitsPerQuery);
                        var hitCount = Math.Min(count, q.MaxHits ?? registration.Provider.Limits.MaxHitsPerQuery);
                        r.Hits = new GuaSpatialHit[hitCount];
                        for (int i = 0; i < hitCount; ++i) r.Hits[i] = Anonymous();
                        if (q.Kind == "sweep" && q.Delta.X == 0 && q.Delta.Y == 0 && q.Delta.Z == 0)
                            r.Motion = new GuaSpatialMotion { Type = "zeroLength", Distance = 0 };
                        return r;
                    }
                    if (q.Kind == "overlap") { r.Outcome = "notDetected"; return r; }
                    if (q.Delta.X == 0 && q.Delta.Y == 0 && q.Delta.Z == 0)
                    { r.Outcome = r.Coverage.State == "complete" ? "clear" : "indeterminate"; r.Motion = new GuaSpatialMotion { Type = "zeroLength", Distance = 0 }; return r; }
                }
                Vector3 delta = q.Kind == "raycast" ? V(q.Segment.To) - V(q.Segment.From) : V(q.Delta);
                var distance = delta.magnitude;
                if (distance == 0 || float.IsInfinity(distance)) throw new ArgumentOutOfRangeException(nameof(distance));
                count = q.Kind == "raycast"
                    ? scene.Raycast(V(q.Segment.From), delta / distance, casts, distance, policy.LayerMask, policy.Triggers)
                    : Cast(q.Shape, delta / distance, distance, policy);
                if (q.Kind == "raycast") { r.Nearest = count > 0 ? "returnedHits" : "none"; r.OriginInside = "unknown"; }
                r.Outcome = count > 0 ? (q.Kind == "raycast" ? "hit" : "blocked") : (q.Kind == "raycast" ? "noHit" : r.Coverage.State == "complete" ? "clear" : "indeterminate");
                Array.Sort(casts, 0, count, Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance)));
                var limit = q.MaxHits ?? registration.Provider.Limits.MaxHitsPerQuery;
                r.Truncated = count == casts.Length || count > limit;
                r.Hits = new GuaSpatialHit[Math.Min(count, limit)];
                for (int i = 0; i < r.Hits.Length; ++i)
                {
                    // Internal-start and mesh normals are not certified. Publish
                    // detection without inferring penetration or support.
                    var hit = Anonymous();
                    r.Hits[i] = hit;
                }
                return r;
            }
            catch (GuaSpatialException e) when (e.Code == GuaSpatialErrorCode.NotAuthorized) { return Unsupported(q); }
            catch (ArgumentException)
            {
                return Unsupported(q);
            }
        }
        static GuaSpatialHostQueryResult Unsupported(GuaSpatialRequest q) => new GuaSpatialHostQueryResult
        { RequestId=q.RequestId,SessionEpoch=q.SessionEpoch,QueryId=q.QueryId,SpaceId=q.SpaceId,SpaceEpoch=q.SpaceEpoch,Kind=q.Kind,
            Status="unsupported",Error=new GuaSpatialFailure(q.Kind=="raycast"?"unsupported_operation":"unsupported_shape") };

        public void Dispose()
        {
            if (disposed) return;
            CheckThread();
            if (pumping) throw new InvalidOperationException("Cannot dispose a live physics boundary");
            host.Unregister(Provider); disposed = true; Provider = 0;
        }
    }
}
