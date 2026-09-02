using CSweet.Contracts.Agents;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;

namespace CSweet.Infrastructure.Core;

internal static class CoreMappers
{
    #region Organization

    public static OrganizationResponse ToResponse(this Organization org)
    {
        return new OrganizationResponse(
            org.Id,
            org.Name,
            org.Industry,
            org.Mission,
            org.Stage,
            org.PrimaryGoal,
            org.ConstraintsJson,
            org.CreatedAt,
            org.UpdatedAt)
        {
            Status = org.Status.ToString()
        };
    }

    #endregion

    #region OrganizationUser

    public static OrganizationUserResponse ToResponse(this OrganizationUser user)
    {
        return new OrganizationUserResponse(
            user.Id,
            user.OrganizationId,
            user.ReportsToOrganizationUserId,
            user.RoleId,
            user.WorkerId,
            user.DisplayName,
            user.Email,
            (int)user.EmployeeType,
            (int)user.PermissionLevel,
            user.CreatedAt)
        {
            ApplicationUserId = user.ApplicationUserId,
            AgentInstallationId = user.AgentInstallationId,
            SupportsAgentConfiguration = user.AgentInstallation?.PackageVersion?.ManifestJson.Contains(
                "\"configuration\"", StringComparison.OrdinalIgnoreCase) == true,
            IsActive = user.IsActive,
            ArchivedAt = user.ArchivedAt,
            Revision = user.Revision
        };
    }

    #endregion

    #region Role

    public static RoleResponse ToResponse(this Role role)
    {
        return new RoleResponse(
            role.Id,
            role.OrganizationId,
            role.Name,
            role.Description,
            role.ResponsibilitiesJson,
            (int)role.AuthorityLevel,
            role.CreatedAt,
            role.UpdatedAt);
    }

    #endregion

    #region StrategicObjective

    public static StrategicObjectiveResponse ToResponse(this StrategicObjective obj)
    {
        return new StrategicObjectiveResponse(
            obj.Id,
            obj.OrganizationId,
            obj.Title,
            obj.Description,
            (int)obj.Status,
            obj.TargetDate,
            obj.CreatedAt,
            obj.UpdatedAt);
    }

    #endregion

    #region Conversation

    public static ConversationMessageResponse ToResponse(this ConversationMessage message)
    {
        return new ConversationMessageResponse(
            message.Id,
            message.ConversationId,
            (int)message.Role,
            message.Content,
            message.CreatedAt,
            message.ChatTurnId)
        {
            Sequence = message.Sequence,
            SenderOrganizationUserId = message.SenderOrganizationUserId,
            ReplyToMessageId = message.ReplyToMessageId,
            CorrelationId = message.CorrelationId,
            DeliveryIntent = message.DeliveryIntent.ToString(),
            SourceProvider = message.SourceProvider,
            SourceChannelExternalId = message.SourceChannelExternalId,
            Attachments = message.Attachments.Select(x =>
                new Contracts.Communications.CommunicationMessageAttachmentResponse(
                    x.Id, x.MessageId, x.FileName, x.ContentType, x.SizeBytes, x.Sha256)).ToList(),
            Artifacts = message.Artifacts.Select(x =>
                new Contracts.Communications.CommunicationMessageArtifactResponse(
                    x.Id, x.MessageId, x.ArtifactId, x.RevisionId)
                {
                    Title = x.Artifact?.Title,
                    Status = x.Artifact?.DocumentStatus.ToString(),
                    SubmittedRevisionId = x.Artifact?.SubmittedRevisionId,
                    AcceptedRevisionId = x.Artifact?.AcceptedRevisionId
                }).ToList()
        };
    }

    #endregion

    #region Worker

    public static WorkerResponse ToResponse(this Worker worker)
    {
        return new WorkerResponse(
            worker.Id,
            worker.OrganizationId,
            worker.Name,
            worker.Description,
            (int)worker.WorkerType,
            (int)worker.ExecutionMode,
            worker.CapabilitiesJson,
            worker.CostModelJson,
            worker.EndpointConfigurationJson,
            worker.IsEnabled,
            worker.RequiresHumanApproval,
            worker.CreatedAt,
            worker.UpdatedAt);
    }

    #endregion

    #region WorkTask

    public static WorkTaskResponse ToResponse(this WorkTask task)
    {
        return new WorkTaskResponse(
            task.Id,
            task.OrganizationId,
            task.StrategicObjectiveId,
            task.AssignedRoleId,
            task.AssignedWorkerId,
            task.Title,
            task.Description,
            (int)task.Status,
            (int)task.Priority,
            task.DueDate,
            task.RequiresApproval,
              task.CreatedAt,
              task.UpdatedAt,
              task.BoardId,
              task.BoardColumnId,
              task.BoardRank,
              task.Revision,
              task.Kind.ToString(),
              task.ParentWorkTaskId,
              task.SprintId,
              task.EstimatePoints)
        {
            Mentions = CSweet.Infrastructure.WorkManagement.WorkItemMentionCodec.Deserialize(
                task.StructuredMentionsJson)
        };
    }

    #endregion

    #region TaskRun

    public static TaskRunResponse ToResponse(this TaskRun run)
    {
        return new TaskRunResponse(
            run.Id,
            run.TaskId,
            run.WorkerId,
            (int)run.Status,
            run.StartedAt,
            run.CompletedAt,
            run.InputJson,
            run.OutputJson,
            run.FailureMessage,
            run.CostAmount,
            run.CostCurrency);
    }

    #endregion

    #region Artifact

    public static ArtifactResponse ToResponse(this Artifact artifact)
    {
        return new ArtifactResponse(
            artifact.Id,
            artifact.OrganizationId,
            artifact.TaskId,
            artifact.TaskRunId,
            (int)artifact.Type,
            artifact.Title,
            artifact.Content,
            artifact.Version,
            (int)artifact.ApprovalStatus,
            artifact.CreatedAt,
            artifact.UpdatedAt);
    }

    #endregion

    #region Approval

    public static ApprovalResponse ToResponse(this Approval approval)
    {
        return new ApprovalResponse(
            approval.Id,
            approval.ArtifactId,
            (int)approval.Status,
            approval.Comment,
            approval.DecidedAt,
            approval.CreatedAt);
    }

    #endregion
}
