using CSweet.WorkManagement.Contracts;

namespace CSweet.Contracts.WorkManagement;

/// <summary>Explicit manager-approved task scope amendment; never an automatic criteria translation.</summary>
public sealed record AmendWorkDeliveryTaskRequest(Guid PlanId, Guid ItemId, long ExpectedPlanRevision,
    long ExpectedItemRevision, long ExpectedPlanningRevision, WorkItemPlanningSpecification Planning,
    string Reason, string IdempotencyKey, bool UsePlatformPreview);
