using System.Text.Json;
using CSweet.Isolation.Security;
using CSweet.WebHost.Contracts;
using CSweet.WebHost.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed record PreviewClientEvent(string Code, string Summary);
public sealed partial class WebPreviewGatewayService
{
    public async Task ValidateSessionAsync(Guid previewId, string cookie, CancellationToken token)
    {
        ValidateSecret(cookie); var hash = WorkloadAuthorizationEnvelope.Digest(cookie);
        var id = await db.WebPreviewBrowserSessions.AsNoTracking().Where(x => x.PreviewId == previewId && x.SessionHash == hash).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token)
            ?? throw new UnauthorizedAccessException("A current preview session is required.");
        await execution.RequireBrowserSessionAsync(id, previewId, token);
    }
    public async Task RecordClientEventAsync(Guid previewId, string cookie, PreviewClientEvent input, CancellationToken token)
    {
        await ValidateSessionAsync(previewId, cookie, token);
        if (input.Code is not ("BrowserError" or "UnhandledRejection") || input.Summary is not { Length: > 0 and <= 2048 })
            throw new ArgumentException("A bounded browser error is required.");
        var hash = WorkloadAuthorizationEnvelope.Digest(cookie);
        var session = await db.WebPreviewBrowserSessions.SingleAsync(x => x.PreviewId == previewId && x.SessionHash == hash, token);
        if (session.ClientEventCount >= 20) return;
        session.ClientEventCount++; session.Revision++;
        var job = await db.WebPreviewJobs.AsNoTracking().SingleAsync(x => x.Id == previewId, token);
        var manifest = JsonSerializer.Deserialize<PreviewManifest>(job.ManifestJson, PreviewJson.Options)!;
        var diagnostic = new PreviewDiagnostic(Guid.NewGuid(), job.Id, job.WorkstreamId, job.BuildId, manifest.SourceRevision,
            "browser", "browser", input.Code, DiagnosticSanitizer.Sanitize(input.Summary, []), clock.GetUtcNow(), false);
        var fingerprint = DiagnosticSanitizer.Fingerprint(diagnostic);
        if (!await db.WebPreviewFindings.AnyAsync(x => x.OrganizationId == job.OrganizationId && x.ProjectId == job.WorkstreamId && x.BuildId == job.BuildId && x.Fingerprint == fingerprint, token) &&
            await db.WebPreviewFindings.CountAsync(x => x.OrganizationId == job.OrganizationId && x.BuildId == job.BuildId, token) < 128)
            db.WebPreviewFindings.Add(new()
            {
                Id = Guid.NewGuid(), OrganizationId = job.OrganizationId, ProjectId = job.WorkstreamId, PreviewId = job.Id,
                BuildId = job.BuildId, Fingerprint = fingerprint, EvidenceJson = JsonSerializer.Serialize(diagnostic, PreviewJson.Options),
                CreatedAt = clock.GetUtcNow(), RetainUntil = diagnostic.OccurredAt.AddDays(7)
            });
        await db.SaveChangesAsync(token);
    }
}
