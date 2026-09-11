using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.WebHost.Contracts;
using CSweet.WebHost.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class WebPreviewExecutionService
{
    private async Task IngestEvidenceAsync(WebPreviewJobRecord job, WebHostCommandRecord command, PreviewDiagnosticPage page, CancellationToken token)
    {
        var request = JsonSerializer.Deserialize<ProductGuestRequest>(command.BodyJson, PreviewJson.Options)!;
        if (page.PreviewId != job.Id || page.Items is not { Count: <= 256 } ||
            page.NextSequence != (page.Items.Count == 0 ? request.DiagnosticAfterSequence : page.Items[^1].Sequence) ||
            page.HasMore && page.Items.Count == 0) throw new InvalidDataException("Invalid diagnostic export page.");
        var manifest = JsonSerializer.Deserialize<PreviewManifest>(job.ManifestJson, PreviewJson.Options)!;
        long priorSequence = request.DiagnosticAfterSequence;
        foreach (var item in page.Items)
        {
            var diagnostic = item.Diagnostic;
            if (item.Sequence <= priorSequence || diagnostic.Id == Guid.Empty || diagnostic.PreviewId != job.Id ||
                diagnostic.ProjectId != job.WorkstreamId || diagnostic.BuildId != job.BuildId || diagnostic.SourceRevision != manifest.SourceRevision ||
                diagnostic.Source is not ("runtime" or "build" or "browser") || diagnostic.Summary is not { Length: <= 8192 } ||
                diagnostic.Service is not { Length: <= 128 } || diagnostic.Code is not { Length: <= 128 } ||
                diagnostic.OccurredAt < job.CreatedAt || diagnostic.OccurredAt > job.ExpiresAt.AddMinutes(1) || diagnostic.OccurredAt > clock.GetUtcNow().AddSeconds(30) ||
                item.RetainUntil != diagnostic.OccurredAt + DiagnosticStore.Retention)
                throw new InvalidDataException("Diagnostic evidence does not match the canonical preview and build.");
            priorSequence = item.Sequence;
            if (item.RetainUntil <= clock.GetUtcNow()) continue;
            var clean = diagnostic with
            {
                Summary = DiagnosticSanitizer.Sanitize(diagnostic.Summary, []),
                Service = DiagnosticSanitizer.Sanitize(diagnostic.Service, [], 128),
                Code = DiagnosticSanitizer.Sanitize(diagnostic.Code, [], 128)
            };
            var json = JsonSerializer.Serialize(clean, PreviewJson.Options);
            var existing = await db.WebPreviewEvidence.SingleOrDefaultAsync(x => x.Id == clean.Id ||
                (x.PreviewId == job.Id && x.HostSequence == item.Sequence), token);
            if (existing is not null)
            {
                if (existing.OrganizationId != job.OrganizationId || existing.PreviewId != job.Id || existing.DiagnosticJson != json || existing.HostSequence != item.Sequence)
                    throw new InvalidDataException("Retained evidence identity was reused with different content.");
                continue;
            }
            var fingerprint = DiagnosticSanitizer.Fingerprint(clean);
            if (clean.Code is not ("Ready" or "CommandOutput") && !db.WebPreviewFindings.Local.Any(x => x.OrganizationId == job.OrganizationId && x.ProjectId == job.WorkstreamId && x.BuildId == job.BuildId && x.Fingerprint == fingerprint) &&
                !await db.WebPreviewFindings.AnyAsync(x => x.OrganizationId == job.OrganizationId && x.ProjectId == job.WorkstreamId && x.BuildId == job.BuildId && x.Fingerprint == fingerprint, token) &&
                await db.WebPreviewFindings.CountAsync(x => x.BuildId == job.BuildId, token) + db.WebPreviewFindings.Local.Count(x => db.Entry(x).State == EntityState.Added && x.BuildId == job.BuildId) < 128)
                db.WebPreviewFindings.Add(new()
                {
                    Id = Guid.NewGuid(), OrganizationId = job.OrganizationId, ProjectId = job.WorkstreamId, PreviewId = job.Id,
                    BuildId = job.BuildId, Fingerprint = fingerprint, EvidenceJson = json, CreatedAt = clock.GetUtcNow(), RetainUntil = item.RetainUntil
                });
            db.WebPreviewEvidence.Add(new()
            {
                Id = clean.Id, PreviewId = job.Id, OrganizationId = job.OrganizationId, HostSequence = item.Sequence,
                DiagnosticJson = json, Fingerprint = DiagnosticSanitizer.Fingerprint(clean), RetainUntil = item.RetainUntil
            });
        }
        job.LastEvidenceSequence = Math.Max(job.LastEvidenceSequence, page.NextSequence);
    }

    public async Task<PreviewDiagnosticPage> DiagnosticsAsync(Guid organizationId, Guid installationId,
        PreviewDiagnosticRequest request, CancellationToken token)
    {
        var job = await RequireJobAsync(organizationId, installationId, request.PreviewId, WebPreviewCapabilities.Diagnostics, token);
        if (request.AfterSequence < 0 || request.Limit is < 1 or > 256) throw new ArgumentException("Choose a bounded evidence page.");
        var now = clock.GetUtcNow();
        var records = await db.WebPreviewEvidence.AsNoTracking().Where(x => x.PreviewId == job.Id && x.OrganizationId == organizationId &&
            x.HostSequence > request.AfterSequence && x.RetainUntil > now).OrderBy(x => x.HostSequence).Take(request.Limit + 1).ToListAsync(token);
        var items = records.Take(request.Limit).Select(x => new RetainedPreviewDiagnostic(x.HostSequence,
            JsonSerializer.Deserialize<PreviewDiagnostic>(x.DiagnosticJson, PreviewJson.Options)!, x.RetainUntil)).ToArray();
        var findingIds = await db.WebPreviewFindings.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.BuildId == job.BuildId && x.ProjectId == job.WorkstreamId && x.RetainUntil > now).Select(x => x.Id).Take(128).ToListAsync(token);
        return new(job.Id, items.Length == 0 ? request.AfterSequence : items[^1].Sequence, items, records.Count > request.Limit, findingIds);
    }
}
