using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

internal static class WorkDeliveryToolSchemas
{
    internal static JsonElement Input(string capability)
    {
        var type = capability switch
        {
            WorkDeliveryCapabilities.Read => typeof(ReadWorkDeliveryPlansRequest),
            WorkDeliveryCapabilities.Configure => typeof(ConfigureWorkDeliveryPlanRequest),
            WorkDeliveryCapabilities.Control => typeof(ControlWorkDeliveryPlanRequest),
            WorkDeliveryCapabilities.Accept => typeof(DecideWorkDeliveryAcceptanceRequest),
            WorkDeliveryCapabilities.Recover => typeof(RecoverWorkDeliveryRequest),
            WorkDeliveryCapabilities.Correct => typeof(CoordinateWorkDeliveryCorrectionRequest),
            WorkDeliveryCapabilities.Review => typeof(CompleteWorkDeliveryReviewRequest),
            WorkDeliveryCapabilities.Evidence => typeof(ReadWorkDeliveryEvidenceRequest),
            _ => throw new ArgumentException("Unknown delivery capability.")
        };
        return ContractToolSchema.Create(type);
    }
}
