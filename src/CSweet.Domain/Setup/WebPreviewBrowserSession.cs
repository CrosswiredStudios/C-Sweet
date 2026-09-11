namespace CSweet.Domain.Setup;

public sealed class WebPreviewBrowserSession
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid PreviewId { get; set; }
    public Guid OrganizationUserId { get; set; }
    public string TicketHash { get; set; } = "";
    public string? SessionHash { get; set; }
    public DateTimeOffset TicketExpiresAt { get; set; }
    public DateTimeOffset? TicketConsumedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int ClientEventCount { get; set; }
    public long Revision { get; set; } = 1;
}
