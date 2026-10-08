using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Contracts.Communications;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;

namespace CSweet.Infrastructure.Communications;

public sealed class ApprovalUserActionWorkflowResolver(CSweetDbContext db) : IUserActionWorkflowResolver
{
    public string WorkflowType => SuggestedUserActionWorkflows.ReviewApproval;
    public UserActionWorkflowResolution Resolve(Guid organizationId, JsonElement parameters) =>
        throw new UnauthorizedAccessException("Approval cards require the originating installation.");
    public UserActionWorkflowResolution Resolve(Guid organizationId, Guid originatingInstallationId, JsonElement parameters)
    {
        var id = ReadId(parameters.GetRawText()) ?? throw new ArgumentException("approvalId is required.");
        if (!db.ActionProposals.Any(x => x.Id == id && x.OrganizationId == organizationId &&
            x.AgentInstallationId == originatingInstallationId && x.ActionType == ProjectApprovalReader.ActionType))
            throw new UnauthorizedAccessException("The project approval does not belong to this installation.");
        return new($"/organizations/{organizationId:D}/approvals?approvalId={id:D}",
            JsonSerializer.Serialize(new { approvalId = id }));
    }
    public static Guid? ReadId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("approvalId", out var id) && id.ValueKind == JsonValueKind.String &&
                id.TryGetGuid(out var result) ? result : null;
        }
        catch (JsonException) { return null; }
    }
}
