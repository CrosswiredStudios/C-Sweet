namespace CSweet.Domain.Setup;

public sealed class PluginSetupObligation
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid InstallationId { get; set; }
    public Guid AgentOrganizationUserId { get; set; }
    public Guid HumanOrganizationUserId { get; set; }
    public Guid ConversationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? IntroductionWorkId { get; set; }
    public DateTimeOffset? IntroducedAt { get; set; }
    public Guid? ReminderWorkId { get; set; }
    public DateTimeOffset? ReminderQueuedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
}
