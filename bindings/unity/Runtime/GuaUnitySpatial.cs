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
                        var result = Execute(query);
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
            if (float.IsInfinity(converted) || float.IsNaN(converted) || (double)converted != value || Math.Abs(value) > 8192)
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

        GuaSpatialCoverage Coverage(GuaSpatialRequest q)
        {
            var region = registration.LoadedRegion;
            if (region != null && Contained(q, region))
                return new GuaSpatialCoverage { State = "complete", LoadedRegion = region };
            return new GuaSpatialCoverage { State = "unknown", Reason = "engine_coverage_unverified" };
        }
        static bool Cardinal(GuaSpatialVector v)
            => (v.X == 0 ? 0 : 1) + (v.Y == 0 ? 0 : 1) + (v.Z == 0 ? 0 : 1) <= 1;
        static bool Contained(GuaSpatialRequest q, GuaSpatialRegion region)
        {
            var lo = q.Kind == "raycast" ? V(q.Segment.From) : q.Shape.Type == "capsule" ? Vector3.Min(V(q.Shape.PointA), V(q.Shape.PointB)) : V(q.Shape.Center);
            var hi = q.Kind == "raycast" ? V(q.Segment.To) : q.Shape.Type == "capsule" ? Vector3.Max(V(q.Shape.PointA), V(q.Shape.PointB)) : lo;
            var min = Vector3.Min(lo, hi); var max = Vector3.Max(lo, hi);
            if (q.Kind != "raycast")
            {
                Vector3 extents;
                if (q.Shape.Type == "box")
                {
                    var b = q.Shape.Basis; var h = V(q.Shape.HalfExtents);
                    var x = V(b.X); var y = V(b.Y); var z = V(b.Z);
                    extents = new Vector3(Mathf.Abs(x.x)*h.x+Mathf.Abs(y.x)*h.y+Mathf.Abs(z.x)*h.z,
                        Mathf.Abs(x.y)*h.x+Mathf.Abs(y.y)*h.y+Mathf.Abs(z.y)*h.z,
                        Mathf.Abs(x.z)*h.x+Mathf.Abs(y.z)*h.y+Mathf.Abs(z.z)*h.z);
                }
                else extents = Vector3.one * F(q.Shape.Radius.Value);
                min -= extents; max += extents;
                if (q.Kind == "sweep") { var d = V(q.Delta); min = Vector3.Min(min, min + d); max = Vector3.Max(max, max + d); }
            }
            // Stay away from a rounded region boundary. Native host repeats the
            // authoritative outward-rounded containment check at completion.
            const double slack = 0.001;
            return min.x > region.Min.X + slack && min.y > region.Min.Y + slack && min.z > region.Min.Z + slack
                && max.x < region.Max.X - slack && max.y < region.Max.Y - slack && max.z < region.Max.Z - slack;
        }
        static bool ExactCardinal(GuaSpatialRequest q)
        {
            // An initial bounded subset avoids expanding an authorized binary64
            // shape by silently rounding it into engine floats. Arbitrary
            // orientations/diagonal motion require an explicit precision contract.
            if (q.Kind == "raycast") return Cardinal(new GuaSpatialVector(q.Segment.To.X-q.Segment.From.X, q.Segment.To.Y-q.Segment.From.Y, q.Segment.To.Z-q.Segment.From.Z));
            if (q.Kind == "sweep" && !Cardinal(q.Delta)) return false;
            if (q.Shape.Type == "capsule" && !Cardinal(new GuaSpatialVector(q.Shape.PointB.X-q.Shape.PointA.X, q.Shape.PointB.Y-q.Shape.PointA.Y, q.Shape.PointB.Z-q.Shape.PointA.Z))) return false;
            if (q.Shape.Type == "capsule")
            {
                var a=q.Shape.PointA; var b=q.Shape.PointB;
                Func<double,bool> exact = n => Math.Abs(n)<=8192 && (double)(float)n==n;
                if (!exact(a.X)||!exact(a.Y)||!exact(a.Z)||!exact(b.X)||!exact(b.Y)||!exact(b.Z)) return false;
                var mid = new GuaSpatialVector((a.X+b.X)*0.5,(a.Y+b.Y)*0.5,(a.Z+b.Z)*0.5);
                if (!exact(mid.X)||!exact(mid.Y)||!exact(mid.Z)) return false;
                var engineMid=(V(a)+V(b))*0.5f;
                if (engineMid.x!=mid.X || engineMid.y!=mid.Y || engineMid.z!=mid.Z) return false;
                var length=Math.Abs(b.X-a.X)+Math.Abs(b.Y-a.Y)+Math.Abs(b.Z-a.Z);
                if (Vector3.Distance(V(a),V(b))!=length || !exact(length+2*q.Shape.Radius.Value)) return false;
            }
            if (q.Shape.Type == "box")
            {
                var b = q.Shape.Basis;
                return Cardinal(b.X) && Cardinal(b.Y) && Cardinal(b.Z)
                    && b.X.X*b.X.X+b.X.Y*b.X.Y+b.X.Z*b.X.Z == 1
                    && b.Y.X*b.Y.X+b.Y.Y*b.Y.Y+b.Y.Z*b.Y.Z == 1
                    && b.Z.X*b.Z.X+b.Z.Y*b.Z.Y+b.Z.Z*b.Z.Z == 1;
            }
            return true;
        }
        static GuaSpatialHit Anonymous()
        {
            return new GuaSpatialHit { Missing = new Dictionary<string, string>
            {
                ["position"] = "not_observed", ["distance"] = "not_observed", ["normal"] = "not_observed",
                ["collisionRef"] = "not_published", ["worldObjectId"] = "not_published"
            }};
        }
        GuaSpatialHostQueryResult Execute(GuaSpatialRequest q)
        {
            var r = new GuaSpatialHostQueryResult { RequestId = q.RequestId, SessionEpoch = q.SessionEpoch,
                QueryId = q.QueryId, SpaceId = q.SpaceId, SpaceEpoch = q.SpaceEpoch, Kind = q.Kind };
            var policy = policies[q.QueryPolicyId];
            if (!ExactCardinal(q))
            { r.Status = "unsupported"; r.Error = new GuaSpatialFailure(q.Kind == "raycast" ? "unsupported_operation" : "unsupported_shape"); return r; }
            // Unity has no per-query backface flag. Refuse mismatched globals,
            // never silently inherit them or mutate game settings.
            if (Physics.queriesHitBackfaces != policy.Backfaces)
            { r.Status = "unsupported"; r.Error = new GuaSpatialFailure("unsupported_policy"); return r; }
            try
            {
                r.Status = "completed"; r.Coverage = Coverage(q); r.Truncated = false; r.Hits = Array.Empty<GuaSpatialHit>();
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
            catch (ArgumentException)
            {
                return new GuaSpatialHostQueryResult { RequestId = q.RequestId, SessionEpoch = q.SessionEpoch,
                    QueryId = q.QueryId, SpaceId = q.SpaceId, SpaceEpoch = q.SpaceEpoch, Kind = q.Kind,
                    Status = "unsupported", Error = new GuaSpatialFailure(q.Kind == "raycast" ? "unsupported_operation" : "unsupported_shape") };
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            CheckThread();
            if (pumping) throw new InvalidOperationException("Cannot dispose a live physics boundary");
            host.Unregister(Provider); disposed = true; Provider = 0;
        }
    }
}
