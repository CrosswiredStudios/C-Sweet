using System.Text.Json;
using System.Text.RegularExpressions;
using CSweet.Contracts.Security;
using CSweet.Domain.Setup;

namespace CSweet.Infrastructure.Security;

/// <summary>Presentation only: never changes the original summary or integrity-sealed evidence.</summary>
internal static class AuditActivityDescriptions
{
    private static readonly IReadOnlyDictionary<string, string> Events = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["security.timeline.viewed"] = "Opened security audit history",
        ["security.event.viewed"] = "Opened security event details",
        ["employee.timeline.viewed"] = "Opened employee activity history",
        ["employee.timeline.event.viewed"] = "Opened employee activity details",
        ["communication.message.sent"] = "Sent a chat message",
        ["communication.message.edited"] = "Edited a chat message",
        ["communication.message.deleted"] = "Deleted a chat message",
        ["communication.chat.created"] = "Created a chat",
        ["communication.chat.modified"] = "Updated a chat",
        ["communication.chat.archived"] = "Archived a chat",
        ["communication.participant.joined"] = "Joined a chat",
        ["communication.participant.left"] = "Left a chat",
        ["communication.participant.updated"] = "Updated chat membership",
        ["chat.trace.recorded"] = "Recorded chat processing details",
        ["model.call.started"] = "Requested a model response",
        ["model.call.completed"] = "Finished a model request",
        ["model.response"] = "Model response",
        ["model.response.chunk"] = "Received part of a model response",
        ["model.request"] = "Requested a model response",
        ["agent.work.progress"] = "Reported work progress",
        ["agent.work.attempt.started"] = "Started a work attempt",
        ["agent.work.attempt.stopped"] = "Stopped a work attempt",
        ["work.execution.started"] = "Started working",
        ["work.execution.paused"] = "Paused working",
        ["work.lifecycle.recorded"] = "Recorded work status",
        ["mcp.session.established"] = "Connected to platform tools",
        ["mcp.session.renewed"] = "Renewed the platform tools connection",
        ["mcp.runtime.operation"] = "Performed a runtime operation",
        ["artifact.created"] = "Created a document",
        ["artifact.read"] = "Read a document",
        ["artifact.listed"] = "Browsed documents",
        ["artifact.searched"] = "Searched documents",
        ["artifact.moved"] = "Moved a document",
        ["artifact.steward.reassigned"] = "Changed document steward",
        ["artifact.archived"] = "Archived a document",
        ["artifact.restored"] = "Restored a document",
        ["artifact.folder.created"] = "Created a document folder",
        ["artifact.folder.updated"] = "Updated a document folder",
        ["artifact.folder.archived"] = "Archived a document folder",
        ["artifact.folder.restored"] = "Restored a document folder",
        ["artifact.access.granted"] = "Granted document access",
        ["artifact.access.revoked"] = "Revoked document access",
        ["artifact.access.grants-updated"] = "Updated document permissions",
        ["artifact.access.already-granted"] = "Checked existing document access",
        ["artifact.access.requested"] = "Requested document access",
        ["artifact.access.rejected"] = "Rejected a document access request",
        ["artifact.revision.created"] = "Created a document revision",
        ["artifact.revision.submitted"] = "Submitted a document for review",
        ["artifact.revision.accepted"] = "Accepted a document revision",
        ["artifact.revision.changes-requested"] = "Requested document changes",
        ["artifact.revision.structured-decided"] = "Reviewed a document revision",
        ["artifact.package.created"] = "Created a document package",
        ["artifact.package.read"] = "Read a document package",
        ["artifact.package.archived"] = "Archived a document package",
        ["artifact.package.restored"] = "Restored a document package",
        ["artifact.package.changes-requested"] = "Requested document package changes",
        ["artifact.package.submitted"] = "Submitted a document package",
        ["artifact.package.accepted"] = "Accepted a document package",
        ["artifact.access.denied"] = "Attempted to access a document",
        ["agent-runtime.interactive.queued"] = "Queued an interactive session",
        ["agent-runtime.schedule.tick"] = "Triggered a scheduled run",
        ["agent-runtime.workload.started"] = "Started an agent run",
        ["agent-runtime.workload.stopped"] = "Stopped an agent run",
        ["agent-runtime.completed"] = "Finished an agent run",
        ["agent-runtime.timeout"] = "Agent run timed out",
        ["agent-runtime.policy-denied"] = "Attempted to start an agent run",
        ["agent-runtime.failed"] = "Agent run failed",
        ["agent-runtime.cancelled"] = "Cancelled an agent run",
        ["agent-runtime.settings.updated"] = "Updated agent runtime settings",
        ["audit.source.snapshot"] = "Recorded a historical activity snapshot"
    };

    public static string Describe(AuditEvent item, bool groupedModelResponse)
    {
        if (groupedModelResponse) return "Model response";
        if (item.EventType is "security.timeline.viewed" or "security.event.viewed" && item.Outcome == "Denied")
            return "Attempted to open security audit data";
        if (item.EventType.StartsWith("agent.capability.", StringComparison.Ordinal))
            return DescribeAction(item);
        if (Events.TryGetValue(item.EventType, out var description)) return description;
        foreach (var (prefix, subject) in new[] { ("chat.turn.", "chat processing"), ("agent.work.", "work"),
                     ("agent.runtime.", "agent run"), ("agent.event.", "platform notification"), ("work.item.", "work item") })
        {
            if (item.EventType.StartsWith(prefix, StringComparison.Ordinal))
                return DescribeState(subject, item.Outcome);
        }

        var summary = item.Summary?.Trim();
        if (!string.IsNullOrWhiteSpace(summary) && summary != item.EventType && summary != item.EventType.Replace('.', ' '))
        {
            // Remove only an exact recorded identity at the start of a sentence.
            foreach (var actor in new[] { item.ActorDisplayName, item.ActorAgentId })
                if (!string.IsNullOrWhiteSpace(actor) && summary.StartsWith(actor + " ", StringComparison.Ordinal))
                { summary = summary[(actor.Length + 1)..]; break; }
            return char.ToUpperInvariant(summary[0]) + summary[1..];
        }
        return AuditActionDescriptions.Humanize(item.EventType);
    }

    private static string DescribeState(string subject, string status) => status.ToLowerInvariant() switch
    {
        "pending" or "queued" => $"Queued {subject}",
        "leased" => $"Claimed {subject}",
        "running" or "started" => $"Started {subject}",
        "completed" => $"Completed {subject}",
        "cancelled" => $"Cancelled {subject}",
        "paused" => $"Paused {subject}",
        "delivered" => $"Delivered {subject}",
        "failed" => $"{AuditActionDescriptions.Humanize(subject)} failed",
        "deadletter" => $"{AuditActionDescriptions.Humanize(subject)} exhausted retries",
        _ => $"{AuditActionDescriptions.Humanize(subject)}: {AuditActionDescriptions.Humanize(status)}"
    };

    private static string DescribeAction(AuditEvent item)
    {
        try
        {
            using var metadata = JsonDocument.Parse(item.MetadataJson ?? "{}");
            if (metadata.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (ReadString(metadata.RootElement, "activityDescription") is { Length: > 0 } label) return label;
                if (ReadString(metadata.RootElement, "toolName") is { Length: > 0 } tool) return AuditActionDescriptions.ForTool(tool);
            }
        }
        catch (JsonException) { }
        // Older starts stored the tool name only in the summary. Parse that known format,
        // never the actor name or capability identifier, into the display label.
        var match = Regex.Match(item.Summary ?? "", @" invoked (?<tool>[A-Za-z0-9_]+) \(");
        return match.Success ? AuditActionDescriptions.ForTool(match.Groups["tool"].Value) : "Used a platform action";
    }

    private static string? ReadString(JsonElement value, string key) =>
        value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
