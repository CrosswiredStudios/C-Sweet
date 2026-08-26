using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed class ManagementReportCapabilityHandler(CSweetDbContext db, IAuditEventWriter audit)
    : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public const string StatusReportCapability = "platform.management.status-report.v1";
    public const string ResourceNeedCapability = "platform.management.resource-need-report.v1";

    public bool CanHandle(string capability) =>
        capability is StatusReportCapability or ResourceNeedCapability;

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(
        AgentSession session,
        RequestCapability request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await ProcessAsync(session, request, cancellationToken);
        yield return new CapabilityResult
        {
            RequestId = request.RequestId,
            Succeeded = true,
            Payload = JsonPayload.FromUtf8("{\"accepted\":true}")
        };
    }

    private async Task ProcessAsync(
        AgentSession session,
        RequestCapability publishedEvent,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(session.BusinessId, out var organizationId) || !Guid.TryParse(session.InstallationId, out var installationId)) return;
        var reporterId = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.AgentInstallationId == installationId && x.IsActive)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
        if (reporterId is null) return;

        if (publishedEvent.Capability == StatusReportCapability)
        {
            var report = JsonSerializer.Deserialize<ManagementStatusReport>(publishedEvent.Payload.Span, JsonOptions);
            if (report is null) return;
            var reporterIdentity = await db.CoreOrganizationUsers.AsNoTracking()
                .Include(x => x.Role)
                .SingleAsync(x => x.Id == reporterId.Value && x.OrganizationId == organizationId,
                    cancellationToken);
            report = report with
            {
                ReporterOrganizationUserId = reporterId.Value,
                ReporterDisplayName = reporterIdentity.DisplayName,
                ReporterRole = reporterIdentity.Role?.Name ?? reporterIdentity.EmployeeType.ToString()
            };
            var request = report.RequestId.HasValue
                ? await db.ManagementCheckInRequests.SingleOrDefaultAsync(x => x.Id == report.RequestId.Value &&
                    x.OrganizationId == organizationId && x.RequestedFromOrganizationUserId == reporterId &&
                    (x.Status == "Pending" || x.Status == "AwaitingReport"), cancellationToken)
                : await db.ManagementCheckInRequests
                    .Where(x => x.OrganizationId == organizationId && x.RequestedFromOrganizationUserId == reporterId &&
                                x.CheckInType != "ExecutiveBriefing" && x.Status == "Pending")
                    .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            if (request is null) return;
            if (request.CheckInType == "ExecutiveBriefing" && !await db.LeadershipAssignments.AsNoTracking().AnyAsync(
                    x => x.OrganizationId == organizationId && x.OrganizationUserId == reporterId.Value &&
                         x.PositionKey == "chief-of-staff" && x.EndsAt == null, cancellationToken))
            {
                request.Status = "Failed"; request.FailureCode = "chief_replaced";
                request.FailureMessage = "The report publisher is no longer the active Chief of Staff.";
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            if (request.CheckInType == "ExecutiveBriefing" && !IsSafeMarkdown(report.Markdown))
            {
                request.Status = "Failed"; request.FailureCode = "invalid_markdown";
                request.FailureMessage = "The Chief returned empty, oversized, or unsafe Markdown.";
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            var exists = await db.ManagementStatusReports.AnyAsync(x => x.ManagementCheckInRequestId == request.Id, cancellationToken);
            if (exists) return;
            var completed = report.CompletedOutcomes ?? [];
            var inProgress = report.InProgress ?? [];
            var blockers = report.Blockers ?? [];
            var risks = report.Risks ?? [];
            var decisions = report.DecisionsNeeded ?? [];
            var resourceNeeds = report.ResourceNeeds ?? [];
            var immediateActions = report.ImmediateActions ?? [];
            var conversationTopics = report.ConversationTopics ?? [];
            var markdown = request.CheckInType == "ExecutiveBriefing" ? report.Markdown! : report.Markdown;
            var record = new ManagementStatusReportRecord
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, ManagementCheckInRequestId = request.Id,
                ReporterOrganizationUserId = reporterId.Value, Summary = report.Summary,
                OutcomesJson = JsonSerializer.Serialize(completed.Concat(inProgress), JsonOptions),
                BlockersJson = JsonSerializer.Serialize(blockers, JsonOptions), RisksJson = JsonSerializer.Serialize(risks, JsonOptions),
                DecisionsJson = JsonSerializer.Serialize(decisions, JsonOptions), Markdown = markdown,
                ImmediateActionsJson = JsonSerializer.Serialize(immediateActions, JsonOptions),
                ConversationTopicsJson = JsonSerializer.Serialize(conversationTopics, JsonOptions),
                Severity = NormalizeSeverity(report.Severity), ReportedAt = report.ReportedAt
            };
            db.ManagementStatusReports.Add(record);
            foreach (var need in resourceNeeds)
                db.ResourceNeedReports.Add(ToRecord(organizationId, reporterId.Value, record.Id, need, report.ReportedAt));
            request.Status = "Reported"; request.RespondedAt = report.ReportedAt;
            var usedOwnerFallback = false;
            if (request.CheckInType == "ExecutiveBriefing")
            {
                var reporter = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == reporterId.Value, cancellationToken);
                var recipient = reporter.ReportsToOrganizationUserId is { } managerId
                    ? await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.Id == managerId && x.OrganizationId == organizationId && x.IsActive, cancellationToken)
                    : null;
                if (recipient is null)
                {
                    recipient = await db.CoreOrganizationUsers.Where(x => x.OrganizationId == organizationId && x.IsActive &&
                            x.PermissionLevel == OrganizationPermissionLevel.Owner && x.Id != reporter.Id)
                        .OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
                    usedOwnerFallback = recipient is not null;
                }
                if (recipient is null)
                {
                    request.Status = "Failed"; request.FailureCode = "recipient_unavailable";
                    request.FailureMessage = "No active managing entity or owner can receive the briefing.";
                }
                else if (recipient.EmployeeType == EmployeeType.Human)
                {
                    await AddHumanDeliveryAsync(organizationId, request, record, reporter, recipient, markdown!,
                        report, cancellationToken);
                    request.Status = "Delivered";
                }
                else
                {
                    db.ExecutiveBriefingDeliveries.Add(new ExecutiveBriefingDeliveryRecord
                    {
                        Id = Guid.NewGuid(), OrganizationId = organizationId, ManagementCheckInRequestId = request.Id,
                        ManagementStatusReportId = record.Id, RecipientOrganizationUserId = recipient.Id,
                        Channel = "AgentRuntime", Status = "Pending",
                        PayloadJson = JsonSerializer.Serialize(report, JsonOptions), CreatedAt = DateTimeOffset.UtcNow
                    });
                }
            }
            else if (reporterIdentity.ReportsToOrganizationUserId is { } reportingManagerId)
            {
                var agentManager = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.Id == reportingManagerId && x.OrganizationId == organizationId && x.IsActive &&
                    x.EmployeeType == EmployeeType.Agent && x.AgentInstallationId != null, cancellationToken);
                if (agentManager is not null)
                    db.ExecutiveBriefingDeliveries.Add(new ExecutiveBriefingDeliveryRecord
                    {
                        Id = Guid.NewGuid(), OrganizationId = organizationId,
                        ManagementCheckInRequestId = request.Id, ManagementStatusReportId = record.Id,
                        RecipientOrganizationUserId = agentManager.Id, Channel = "AgentRuntime",
                        Status = "Pending", PayloadJson = JsonSerializer.Serialize(report, JsonOptions),
                        CreatedAt = DateTimeOffset.UtcNow
                    });
            }
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync("management.status-reported", nameof(ManagementStatusReportRecord), record.Id,
                "A structured management status report was recorded.", cancellationToken: cancellationToken);
            if (usedOwnerFallback)
                await audit.WriteAsync("executive-briefing.owner-fallback", nameof(ManagementCheckInRequestRecord), request.Id,
                    "The configured managing entity was unavailable, so the briefing was delivered to the active owner.", cancellationToken: cancellationToken);
            return;
        }

        var resourceNeed = JsonSerializer.Deserialize<ResourceNeedReport>(publishedEvent.Payload.Span, JsonOptions);
        if (resourceNeed is null) return;
        var needRecord = ToRecord(organizationId, reporterId.Value, null, resourceNeed, DateTimeOffset.UtcNow);
        db.ResourceNeedReports.Add(needRecord);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("management.resource-need-reported", nameof(ResourceNeedReportRecord), needRecord.Id,
            $"Resource need reported for capability {needRecord.Capability}.", cancellationToken: cancellationToken);
    }

    private static ResourceNeedReportRecord ToRecord(Guid organizationId, Guid reporterId, Guid? statusReportId, ResourceNeedReport need, DateTimeOffset reportedAt) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = organizationId, ManagementStatusReportId = statusReportId,
        ReporterOrganizationUserId = reporterId, Capability = need.Capability, BusinessOutcome = need.BusinessOutcome,
        Urgency = need.Urgency, Evidence = need.Evidence, ReportedAt = reportedAt
    };

    private async Task AddHumanDeliveryAsync(Guid organizationId, ManagementCheckInRequestRecord request,
        ManagementStatusReportRecord record, OrganizationUser reporter, OrganizationUser recipient, string markdown,
        ManagementStatusReport report, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = await db.CoreConversations.Include(x => x.Participants).FirstOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.Kind == ConversationKind.DirectHumanAgent &&
            x.AgentOrganizationUserId == reporter.Id && x.InitiatedByOrganizationUserId == recipient.Id, token);
        if (conversation is null)
        {
            conversation = new Conversation
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, AgentOrganizationUserId = reporter.Id,
                InitiatedByOrganizationUserId = recipient.Id, Kind = ConversationKind.DirectHumanAgent,
                Title = "Chief of Staff briefings", IsPrivate = true, IsDeletionProtected = true,
                CreatedAt = now, UpdatedAt = now
            };
            conversation.Participants.Add(new ConversationParticipant
            {
                Id = Guid.NewGuid(), OrganizationUserId = recipient.Id, Role = ConversationParticipantRole.Member, JoinedAt = now
            });
            conversation.Participants.Add(new ConversationParticipant
            {
                Id = Guid.NewGuid(), OrganizationUserId = reporter.Id, Role = ConversationParticipantRole.Member, JoinedAt = now
            });
            db.CoreConversations.Add(conversation);
        }
        else conversation.UpdatedAt = now;
        var message = new ConversationMessage
        {
            Id = Guid.NewGuid(), ConversationId = conversation.Id, Role = ConversationRole.Assistant, Content = markdown,
            CreatedAt = now, SenderOrganizationUserId = reporter.Id, CorrelationId = record.Id, CausationId = request.Id,
            DeliveryIntent = (report.ConversationTopics?.Count ?? 0) > 0 ? CommunicationDeliveryIntent.RequestResponse : CommunicationDeliveryIntent.Inform,
            SourceProvider = "InApp", IdempotencyKey = $"executive-briefing:{request.Id:D}"
        };
        var notification = new UserNotification
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, RecipientOrganizationUserId = recipient.Id,
            OriginatingAgentOrganizationUserId = reporter.Id,
            Severity = NormalizeSeverity(report.Severity) == "Urgent" ? NotificationSeverity.Urgent : NotificationSeverity.Important,
            Category = "ExecutiveBriefing", Title = report.Severity == "Urgent" ? "Chief of Staff: immediate attention" : "Chief of Staff briefing",
            Body = report.Summary.Length <= 1024 ? report.Summary : report.Summary[..1024],
            ActionUri = $"/organizations/{organizationId:D}/communications/{conversation.Id:D}",
            DeduplicationKey = $"executive-briefing:{request.Id:D}", CreatedAt = now
        };
        db.CoreConversationMessages.Add(message);
        db.MemoryCaptureOutbox.Add(new MemoryCaptureOutboxItem
        {
            Id = Guid.NewGuid(), ConversationMessageId = message.Id, Status = MemoryCaptureStatus.Pending,
            CreatedAt = now, NextAttemptAt = now
        });
        db.UserNotifications.Add(notification);
        db.ExecutiveBriefingDeliveries.Add(new ExecutiveBriefingDeliveryRecord
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ManagementCheckInRequestId = request.Id,
            ManagementStatusReportId = record.Id, RecipientOrganizationUserId = recipient.Id,
            Channel = "InApp", Status = "Delivered", PayloadJson = JsonSerializer.Serialize(report, JsonOptions),
            ConversationId = conversation.Id, ConversationMessageId = message.Id, NotificationId = notification.Id,
            Attempts = 1, LastAttemptAt = now, DeliveredAt = now, CreatedAt = now
        });
    }

    private static bool IsSafeMarkdown(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown) || markdown.Length > 8192) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(markdown, "<[A-Za-z!/][^>]*>", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) return false;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(markdown, @"\]\(([^)]+)\)"))
        {
            var target = match.Groups[1].Value.Trim();
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is not ("http" or "https")) return false;
        }
        return true;
    }

    private static string NormalizeSeverity(string? severity) => severity?.Trim().Equals("Urgent", StringComparison.OrdinalIgnoreCase) == true
        ? "Urgent" : "Important";
}
