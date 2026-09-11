using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Isolation.Security;
using CSweet.WebHost.Core;
using CSweet.WebHost.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

public sealed class WebPreviewGatewayOptions
{
    public const string SectionName = "CSweet:WebHost:Gateway";
    public string HeadquartersOrigin { get; set; } = "";
    public string PreviewHostSuffix { get; set; } = "";
}
public sealed record WebPreviewOpenTicket(string Origin, string Ticket);
public sealed record WebPreviewSessionCookie(string Value, DateTimeOffset ExpiresAt);

public sealed class WebPreviewOrigins(IOptions<WebPreviewGatewayOptions> options)
{
    public const string CookieName = "__Host-CSweetPreview";
    public bool IsPreviewHost(string host) => options.Value.PreviewHostSuffix.Length > 0 &&
        (host.Equals(options.Value.PreviewHostSuffix, StringComparison.OrdinalIgnoreCase) ||
         host.EndsWith("." + options.Value.PreviewHostSuffix, StringComparison.OrdinalIgnoreCase));
    public string Origin(Guid previewId)
    {
        var suffix = options.Value.PreviewHostSuffix;
        if (!Uri.TryCreate(options.Value.HeadquartersOrigin, UriKind.Absolute, out var headquarters) ||
            headquarters.Scheme != "https" || headquarters.AbsolutePath != "/" || headquarters.UserInfo.Length > 0 ||
            headquarters.Query.Length > 0 || headquarters.Fragment.Length > 0 || suffix.Length is < 4 or > 200 ||
            suffix.Split('.').Length < 2 || suffix.Split('.').Any(x => x.Length == 0 || x.StartsWith('-') || x.EndsWith('-') ||
                x.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) || Uri.CheckHostName(suffix) != UriHostNameType.Dns)
            throw new InvalidOperationException("Configure an HTTPS Headquarters origin and a dedicated preview DNS suffix.");
        // Conservative cookie/site isolation check. Requiring different final two DNS labels also
        // rejects some safe deployments; it never treats a sibling under the Headquarters site as isolated.
        if (string.Join('.', headquarters.Host.Split('.').TakeLast(2)).Equals(string.Join('.', suffix.Split('.').TakeLast(2)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Previews require a separate site from Headquarters.");
        if (previewId == Guid.Empty) throw new ArgumentException("An exact preview is required.");
        return "https://" + previewId.ToString("N") + "." + suffix.ToLowerInvariant();
    }
    public Guid PreviewId(string host)
    {
        var prefix = host.Split('.')[0];
        if (!Guid.TryParseExact(prefix, "N", out var id) || !new Uri(Origin(id)).Host.Equals(host, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The preview origin is invalid.");
        return id;
    }
}

public interface IWebPreviewGateway
{
    Task<WebPreviewSessionCookie> RedeemAsync(Guid previewId, string ticket, CancellationToken token);
    Task<GuestHttpResponse> SendAsync(Guid previewId, string cookie, GuestHttpRequest request, CancellationToken token);
    Task ValidateSessionAsync(Guid previewId, string cookie, CancellationToken token);
    Task RecordClientEventAsync(Guid previewId, string cookie, PreviewClientEvent input, CancellationToken token);
}

public sealed partial class WebPreviewGatewayService(CSweetDbContext db, WebPreviewExecutionService execution,
    WebPreviewOrigins origins, TimeProvider clock) : IWebPreviewGateway
{
    public async Task<WebPreviewOpenTicket> OpenAsync(Guid organizationId, Guid applicationUserId, Guid previewId, CancellationToken token)
    {
        var job = await db.WebPreviewJobs.SingleOrDefaultAsync(x => x.Id == previewId && x.OrganizationId == organizationId, token)
            ?? throw new UnauthorizedAccessException("The preview is unavailable.");
        var member = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId && x.IsActive && x.ArchivedAt == null && x.EmployeeType == EmployeeType.Human, token)
            ?? throw new UnauthorizedAccessException("Current team membership is required.");
        await execution.RequireBrowserAccessAsync(job, member.Id, token);
        var origin = origins.Origin(job.Id);
        if (await db.WebPreviewBrowserSessions.CountAsync(x => x.OrganizationUserId == member.Id && x.ExpiresAt > clock.GetUtcNow(), token) >= 20)
            throw new InvalidOperationException("Close or let existing preview sessions expire before opening more.");
        var ticket = Secret();
        var expiry = clock.GetUtcNow().AddMinutes(30);
        if (expiry > job.ExpiresAt) expiry = job.ExpiresAt;
        db.WebPreviewBrowserSessions.Add(new()
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, PreviewId = previewId, OrganizationUserId = member.Id,
            TicketHash = WorkloadAuthorizationEnvelope.Digest(ticket), TicketExpiresAt = clock.GetUtcNow().AddMinutes(1), ExpiresAt = expiry
        });
        await db.SaveChangesAsync(token);
        return new(origin, ticket);
    }
    public async Task<WebPreviewSessionCookie> RedeemAsync(Guid previewId, string ticket, CancellationToken token)
    {
        ValidateSecret(ticket);
        var hash = WorkloadAuthorizationEnvelope.Digest(ticket);
        var session = await db.WebPreviewBrowserSessions.SingleOrDefaultAsync(x => x.TicketHash == hash && x.PreviewId == previewId &&
            x.TicketConsumedAt == null && x.TicketExpiresAt > clock.GetUtcNow(), token)
            ?? throw new UnauthorizedAccessException("The preview opening ticket expired or was consumed.");
        await execution.RequireBrowserSessionAsync(session.Id, previewId, token, requireRedeemed: false);
        var secret = Secret();
        session.SessionHash = WorkloadAuthorizationEnvelope.Digest(secret); session.TicketConsumedAt = clock.GetUtcNow(); session.Revision++;
        await db.SaveChangesAsync(token);
        return new(secret, session.ExpiresAt);
    }
    public async Task<GuestHttpResponse> SendAsync(Guid previewId, string cookie, GuestHttpRequest request, CancellationToken token)
    {
        ValidateSecret(cookie); ProductGuestProtocol.ValidateHttp(request);
        var hash = WorkloadAuthorizationEnvelope.Digest(cookie);
        var session = await db.WebPreviewBrowserSessions.AsNoTracking().SingleOrDefaultAsync(x => x.PreviewId == previewId && x.SessionHash == hash, token)
            ?? throw new UnauthorizedAccessException("A preview session is required.");
        await execution.RequireBrowserSessionAsync(session.Id, previewId, token);
        if (await db.WebHostCommands.CountAsync(x => x.PreviewId == previewId && (x.Status == "Pending" || x.Status == "Dispatched"), token) >= 64)
            throw new InvalidOperationException("The preview has too many queued requests.");
        var job = await db.WebPreviewJobs.SingleAsync(x => x.Id == previewId, token);
        var id = Guid.NewGuid();
        var command = new WebHostCommandRecord
        {
            Id = id, OrganizationId = job.OrganizationId, WebHostId = job.WebHostId!.Value, PreviewId = job.Id,
            BrowserSessionId = session.Id, Action = "http", CreatedAt = clock.GetUtcNow(),
            BodyJson = JsonSerializer.Serialize(new ProductGuestRequest(id, "http", request), PreviewJson.Options)
        };
        db.WebHostCommands.Add(command); await db.SaveChangesAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try { while (true)
        {
            var current = await db.WebHostCommands.AsNoTracking().SingleAsync(x => x.Id == id, timeout.Token);
            if (current.Status == "Completed")
            {
                await execution.RequireBrowserSessionAsync(session.Id, previewId, timeout.Token);
                var response = JsonSerializer.Deserialize<ProductRuntimeResponse>(current.ResponseJson!, PreviewJson.Options);
                var http = response?.Guest?.Http;
                if (response?.Code != "Completed" || http is null || http.StatusCode is < 100 or > 599 ||
                    http.Body is null || http.Headers is null || http.Body.Length > ProductGuestProtocol.MaximumHttpBodyBytes || http.Headers.Count > 64)
                    throw new InvalidDataException("The preview did not return a valid HTTP response.");
                if (http.Headers.Any(x => string.IsNullOrEmpty(x.Key) || x.Key.Length > 128 || x.Value is null || x.Value.Length > 8192 ||
                    x.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || x.Value.Any(char.IsControl)) ||
                    http.Headers.Sum(x => (long)x.Key.Length + x.Value.Length) > 32768)
                    throw new InvalidDataException("The preview returned invalid headers.");
                return http with { Headers = http.Headers.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase) };
            }
            if (current.Status is "Cancelled" or "Unknown") throw new InvalidOperationException("The preview request is no longer available.");
            await Task.Delay(100, timeout.Token);
        } }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await db.Entry(command).ReloadAsync(cleanup.Token);
                if (command.Status == "Pending") command.Status = "Cancelled";
                command.BodyJson = "{}"; command.ResponseJson = null; command.Revision++;
                await db.SaveChangesAsync(cleanup.Token);
            }
            catch (Exception error) when (error is DbUpdateException or OperationCanceledException or InvalidOperationException)
            { db.ChangeTracker.Clear(); } // Independent maintenance repeats bounded payload cleanup.
        }
    }
    private static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static void ValidateSecret(string secret)
    {
        if (secret is not { Length: 43 } || secret.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new UnauthorizedAccessException("A valid preview session is required.");
    }
}

public sealed partial class WebPreviewExecutionService
{
    internal async Task RequireBrowserAccessAsync(WebPreviewJobRecord job, Guid organizationUserId, CancellationToken token)
    {
        if (job.Phase != "Ready") throw new UnauthorizedAccessException("The preview is not ready.");
        var member = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == organizationUserId &&
            x.OrganizationId == job.OrganizationId && x.IsActive && x.ArchivedAt == null && x.EmployeeType == EmployeeType.Human, token)
            ?? throw new UnauthorizedAccessException("The preview membership is no longer active.");
        if (member.PermissionLevel != OrganizationPermissionLevel.Owner)
            await grants.RequireWorkstreamAsync(job.OrganizationId, member.Id, job.WorkstreamId, token);
        await RequireLiveExecutionAsync(job, token);
    }
    internal async Task RequireBrowserSessionAsync(Guid sessionId, Guid previewId, CancellationToken token, bool requireRedeemed = true)
    {
        var session = await db.WebPreviewBrowserSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sessionId &&
            x.PreviewId == previewId && x.ExpiresAt > clock.GetUtcNow(), token)
            ?? throw new UnauthorizedAccessException("The browser session expired.");
        if (requireRedeemed && (session.TicketConsumedAt is null || session.SessionHash is null))
            throw new UnauthorizedAccessException("The browser session has not been redeemed.");
        var job = await db.WebPreviewJobs.AsNoTracking().SingleAsync(x => x.Id == previewId && x.OrganizationId == session.OrganizationId, token);
        await RequireBrowserAccessAsync(job, session.OrganizationUserId, token);
    }
}
