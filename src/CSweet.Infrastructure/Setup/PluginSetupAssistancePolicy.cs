using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Host-owned authority for the fixed, conversation-only setup profile.</summary>
public sealed class PluginSetupAssistancePolicy(CSweetDbContext db)
{
    public const string RequestedEvent = "com.csweet.plugin.setup.requested.v1";
    public const string MessageEvent = "com.csweet.user.message.received.v1";
    public const string AssistantCapability = "assistant.converse.v1";
    public static readonly IReadOnlySet<string> Capabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        "platform.llm.chat-stream.v1", "communication.chat.read.v1",
        "communication.message.send.v1", "platform.user-action.suggest.v1"
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool Enabled(PluginManifest manifest) => manifest.Kind == "agent" &&
        manifest.Setup is { Required: true, Assistance.Profile: "conversation.v1" };

    public async Task ValidateCapabilityAsync(string organizationId, Guid installationId,
        string capability, JsonElement input, CancellationToken ct)
    {
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion)
            .SingleOrDefaultAsync(x => x.Id == installationId && x.BusinessId == organizationId, ct)
            ?? throw Denied();
        if (!installation.IsEnabled || installation.RevisionStatus != PluginRevisionStatus.Active) throw Denied();
        if (installation.SetupState == PluginSetupState.Ready) return;
        var manifest = JsonSerializer.Deserialize<PluginManifest>(installation.PackageVersion?.ManifestJson ?? "{}", JsonOptions);
        // Legacy bootstrap keeps its existing restricted grant. This profile never expands it.
        if (manifest?.Setup?.Assistance is null) return;
        if (!Enabled(manifest) || !Capabilities.Contains(capability)) throw Denied();
        var obligation = await RequireObligationAsync(organizationId, installationId, ct);
        ValidateJson(input);
        if (input.ValueKind != JsonValueKind.Object) throw Denied();
        switch (capability)
        {
            case "communication.chat.read.v1":
                RequireId(input, "chatId", obligation.ConversationId);
                break;
            case "communication.message.send.v1":
                RequireId(input, "chatId", obligation.ConversationId);
                RequireEmpty(input, "mentions");
                RequireEmpty(input, "attachmentMediaAssetIds");
                break;
            case "platform.llm.chat-stream.v1":
                if (!input.TryGetProperty("telemetry", out var telemetry)) throw Denied();
                RequireId(telemetry, "conversationId", obligation.ConversationId);
                RequireEmpty(input, "tools");
                if (!input.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                    throw Denied();
                foreach (var message in messages.EnumerateArray())
                    if (message.TryGetProperty("contents", out var contents) && contents.ValueKind != JsonValueKind.Null)
                    {
                        if (contents.ValueKind != JsonValueKind.Array) throw Denied();
                        foreach (var content in contents.EnumerateArray())
                            if (content.ValueKind != JsonValueKind.Object ||
                                !content.TryGetProperty("kind", out var kind) || kind.GetString() != "text" ||
                                content.EnumerateObject().Any(x => x.Name is not ("kind" or "text") && x.Value.ValueKind != JsonValueKind.Null))
                                throw Denied();
                    }
                break;
            case "platform.user-action.suggest.v1":
                if (!input.TryGetProperty("workflowType", out var workflow) || workflow.GetString() != "plugin.setup.open.v1" ||
                    !input.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
                    parameters.EnumerateObject().Any()) throw Denied();
                var messageId = ReadId(input, "messageId");
                var turnId = ReadId(input, "chatTurnId");
                if (messageId.HasValue == turnId.HasValue) throw Denied();
                var belongs = messageId.HasValue
                    ? await db.CoreConversationMessages.AsNoTracking().AnyAsync(x => x.Id == messageId &&
                        x.ConversationId == obligation.ConversationId && x.SenderOrganizationUserId == obligation.AgentOrganizationUserId, ct)
                    : await db.ChatTurns.AsNoTracking().AnyAsync(x => x.Id == turnId &&
                        x.ConversationId == obligation.ConversationId && x.TargetAgentOrganizationUserId == obligation.AgentOrganizationUserId, ct);
                if (!belongs) throw Denied();
                break;
        }
    }

    public async Task<bool> AllowsWorkAsync(string organizationId, Guid installationId,
        AgentWorkKind kind, string name, JsonElement payload, string? sourceType, CancellationToken ct)
    {
        if (!(kind == AgentWorkKind.Event && name is RequestedEvent or MessageEvent) &&
            !(kind == AgentWorkKind.Capability && name == AssistantCapability)) return false;
        try
        {
            var obligation = await RequireObligationAsync(organizationId, installationId, ct);
            ValidateJson(payload);
            RequireId(payload, "conversationId", obligation.ConversationId);
            if (name == RequestedEvent)
            {
                RequireId(payload, "installationId", installationId);
                RequireId(payload, "organizationId", obligation.OrganizationId);
                RequireId(payload, "agentOrganizationUserId", obligation.AgentOrganizationUserId);
                RequireId(payload, "humanOrganizationUserId", obligation.HumanOrganizationUserId);
                return sourceType == "plugin-setup-assistance";
            }
            // Only a persisted human message in the protected conversation may start a setup turn.
            var messageId = ReadId(payload, "messageId");
            return messageId.HasValue && await db.CoreConversationMessages.AsNoTracking().AnyAsync(x =>
                x.Id == messageId && x.ConversationId == obligation.ConversationId &&
                x.SenderOrganizationUserId == obligation.HumanOrganizationUserId, ct);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return false; }
    }

    private async Task<PluginSetupObligation> RequireObligationAsync(string organizationId, Guid installationId, CancellationToken ct)
    {
        if (!Guid.TryParse(organizationId, out var organization)) throw Denied();
        return await db.PluginSetupObligations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organization && x.InstallationId == installationId &&
            x.CompletedAt == null && x.CancelledAt == null, ct) ?? throw Denied();
    }

    private static void RequireId(JsonElement value, string property, Guid expected)
    { if (ReadId(value, property) != expected) throw Denied(); }

    private static Guid? ReadId(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out var id) && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var result)
        ? result : null;

    private static void RequireEmpty(JsonElement value, string property)
    {
        if (value.TryGetProperty(property, out var list) && list.ValueKind != JsonValueKind.Null &&
            (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() != 0)) throw Denied();
    }

    private static void ValidateJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name.Length == 0 || char.IsUpper(property.Name[0]) || !names.Add(property.Name)) throw Denied();
                ValidateJson(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateJson(item);
    }

    private static UnauthorizedAccessException Denied() => new("Setup assistance is restricted to its protected conversation and native setup action.");
}
