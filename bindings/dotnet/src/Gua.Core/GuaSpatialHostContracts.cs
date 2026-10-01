namespace Gua.Core;
public sealed record GuaSpatialHostPolicy(string Id, long Revision, GuaSpatialRegion Region);
public sealed class GuaSpatialRegistration
{
    public string SchemaVersion { get; set; } = "spatial-host-r1";
    public string DocumentType { get; set; } = "registration";
    public GuaSpatialProvider Provider { get; set; } = null!;
    public GuaSpatialHostPolicy[] Policies { get; set; } = [];
    public GuaSpatialRegion? LoadedRegion { get; set; }
}
public sealed class GuaSpatialOwnerGrants
{
    public string SchemaVersion { get; set; } = "spatial-host-r1";
    public string DocumentType { get; set; } = "owner";
    public long SessionEpoch { get; set; }
    public string Profile { get; set; } = "Debug";
    public bool Enabled { get; set; }
    public string[] Policies { get; set; } = [];
    public GuaSpatialRegion Region { get; set; } = null!;
}
public sealed class GuaSpatialBatch
{
    public string SchemaVersion { get; set; } = "spatial-host-r1";
    public string DocumentType { get; set; } = "batch";
    public long BatchId { get; set; }
    public string Consistency { get; set; } = "bestEffort";
    public GuaSpatialRequest[] Queries { get; set; } = [];
}
public sealed class GuaSpatialBoundary
{
    public string SchemaVersion { get; set; } = "spatial-host-r1";
    public string DocumentType { get; set; } = "boundary";
    public string PhysicsSampleId { get; set; } = "";
    public long? Tick { get; set; }
    public GuaSpatialWorldSnapshot? WorldSnapshot { get; set; }
}
public sealed class GuaSpatialHostSample
{
    public string PhysicsSampleId { get; set; } = "";
    public string ClockId { get; set; } = "";
    public double ObservedFromMs { get; set; }
    public double ObservedToMs { get; set; }
    public long QueryPolicyRevision { get; set; }
    public long? Tick { get; set; }
    public GuaSpatialWorldSnapshot? WorldSnapshot { get; set; }
}
/// <summary>Execution authoring defaults to no sample; the core stamps HostResult
/// samples. Legacy GuaSpatialResult remains a separate tick-required contract.</summary>
public sealed class GuaSpatialHostQueryResult
{
    public string SchemaVersion { get; set; } = "spatial-host-r1";
    public string DocumentType { get; set; } = "execution";
    public long RequestId { get; set; }
    public long SessionEpoch { get; set; }
    public string QueryId { get; set; } = "";
    public string SpaceId { get; set; } = "";
    public long SpaceEpoch { get; set; }
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "";
    public GuaSpatialFailure? Error { get; set; }
    public string? Outcome { get; set; }
    public GuaSpatialCoverage? Coverage { get; set; }
    public bool? Truncated { get; set; }
    public GuaSpatialHostSample? Sample { get; set; }
    public GuaSpatialHit[]? Hits { get; set; }
    public string? Nearest { get; set; }
    public string? OriginInside { get; set; }
    public string? InitialOverlap { get; set; }
    public GuaSpatialMotion? Motion { get; set; }
}
public sealed class GuaSpatialBatchItem
{
    public long RequestId { get; set; }
    public string QueryId { get; set; } = "";
    public string State { get; set; } = "";
    public string? Reason { get; set; }
    public GuaSpatialHostQueryResult? Result { get; set; }
}
public sealed class GuaSpatialBatchResult
{
    public string SchemaVersion { get; set; } = "spatial-host-r1";
    public string DocumentType { get; set; } = "batchResult";
    public long BatchId { get; set; }
    public GuaSpatialBatchItem[] Items { get; set; } = [];
}
public sealed record GuaSpatialHostBudgets(int MaxQueueDepth, double QueryDeadlineMs, double MaxBatchWorkTimeMs);
public sealed class GuaSpatialAdvertisement
{
    public string SchemaVersion { get; set; } = "spatial-host-r1";
    public string DocumentType { get; set; } = "advertisement";
    public GuaSpatialProvider Provider { get; set; } = null!;
    public GuaSpatialHostBudgets Budgets { get; set; } = null!;
}
