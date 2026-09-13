using CSweet.Compute.Contracts;
using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Application.Setup;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

/// <summary>Operator-managed enrollment and catalog. Mutation methods are exposed only by HostAdministration routes.</summary>
public sealed class ComputeRegistry(CSweetDbContext db, TimeProvider clock, IAuditExecutionContextAccessor context)
    : IComputeNodeTrust, IComputeTemplateCatalog
{
    public async Task<ComputeNodeVerificationKey?> ResolveAsync(Guid organizationId, Guid nodeId, string keyId, CancellationToken token)
    {
        var node = await db.ComputeNodes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == nodeId &&
            x.OrganizationId == organizationId && x.KeyId == keyId && x.Enabled, token);
        return node is null ? null : new(node.KeyId, organizationId, node.Id, node.ProviderId, node.VerificationPublicKeyBase64);
    }

    public async Task<RegisteredComputeTemplate?> ResolveAsync(Guid organizationId, string templateId, CancellationToken token)
    {
        var template = await db.ComputeTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.TemplateId == templateId && x.Enabled, token);
        if (template is null) return null;
        var node = await (from placement in db.ComputeTemplatePlacements.AsNoTracking()
            join candidate in db.ComputeNodes.AsNoTracking() on placement.NodeId equals candidate.Id
            where placement.TemplateRegistrationId == template.Id && placement.OrganizationId == organizationId &&
                placement.Enabled && candidate.Enabled && candidate.OrganizationId == organizationId
            orderby candidate.Id
            select candidate).FirstOrDefaultAsync(token);
        if (node is null) return null;
        var definition = JsonSerializer.Deserialize<ComputeTemplate>(template.TemplateJson, ComputeBroker.Json);
        return definition is { Enabled: true } ? new(definition, node.ProviderId, node.Id) : null;
    }

    public async Task<ComputeNodeRegistration> PutNodeAsync(Guid organizationId, Guid nodeId, long expectedRevision,
        string name, string providerId, string keyId, string publicKey, bool enabled, CancellationToken token)
    {
        if (nodeId == Guid.Empty || !ComputeSpecification.Identifier(providerId) || !ComputeSpecification.Identifier(keyId) ||
            name is not { Length: > 0 and <= 160 } || string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) ||
            publicKey is not { Length: > 0 and <= 1024 }) throw new ArgumentException("Valid node identity and verification key are required.");
        try
        {
            using var key = ECDsa.Create(); var encoded = Convert.FromBase64String(publicKey);
            key.ImportSubjectPublicKeyInfo(encoded, out var read);
            if (read != encoded.Length || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                throw new ArgumentException("Compute nodes require a P-256 public verification key.");
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        { throw new ArgumentException("The compute node verification key is invalid.", error); }
        await RequireOrganizationAsync(organizationId, token);
        var node = await db.ComputeNodes.SingleOrDefaultAsync(x => x.Id == nodeId, token);
        if (node is null)
        {
            if (expectedRevision != 0) throw new InvalidOperationException("The node revision changed.");
            db.ComputeNodes.Add(node = new() { Id = nodeId, OrganizationId = organizationId, ProviderId = providerId });
        }
        else
        {
            if (node.OrganizationId != organizationId || node.ProviderId != providerId)
                throw new InvalidOperationException("Node organization and provider identity cannot be changed.");
            if (node.Revision != expectedRevision) throw new InvalidOperationException("The node revision changed.");
            node.Revision++;
        }
        node.Name = name.Trim(); node.KeyId = keyId; node.VerificationPublicKeyBase64 = publicKey;
        node.Enabled = enabled; node.UpdatedAt = clock.GetUtcNow();
        QueueAudit(organizationId, node.Id, "compute.node.updated.v1", node.Revision, enabled,
            new { node.ProviderId, node.KeyId, publicKeyFingerprint = Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(publicKey))) });
        await db.SaveChangesAsync(token);
        return node;
    }

    public async Task<ComputeTemplateRegistration> PutTemplateAsync(Guid organizationId, ComputeTemplate definition,
        long expectedRevision, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!ComputeSpecification.Identifier(definition.Id) || !ComputeSpecification.Identifier(definition.OperatingSystem) ||
            !ComputeSpecification.Identifier(definition.Architecture) || definition.Features is null || definition.Features.Count > 64 ||
            definition.Features.Any(x => !ComputeSpecification.Identifier(x)) ||
            definition.ImageDigest is not { Length: 71 } || !definition.ImageDigest.StartsWith("sha256:", StringComparison.Ordinal) ||
            definition.ImageDigest.AsSpan(7).ContainsAnyExcept("0123456789abcdef".AsSpan()))
            throw new ArgumentException("An immutable image digest and valid template metadata are required.");
        await RequireOrganizationAsync(organizationId, token);
        var template = await db.ComputeTemplates.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.TemplateId == definition.Id, token);
        var normalized = JsonSerializer.Serialize(definition with { Enabled = true, Features = definition.Features.Order().ToHashSet() }, ComputeBroker.Json);
        if (template is null)
        {
            if (expectedRevision != 0) throw new InvalidOperationException("The template revision changed.");
            db.ComputeTemplates.Add(template = new() { Id = Guid.NewGuid(), OrganizationId = organizationId, TemplateId = definition.Id, TemplateJson = normalized });
        }
        else
        {
            if (template.Revision != expectedRevision) throw new InvalidOperationException("The template revision changed.");
            if (template.TemplateJson != normalized) throw new InvalidOperationException("Publish changed image contents under a new template ID.");
            template.Revision++;
        }
        template.Enabled = definition.Enabled; template.UpdatedAt = clock.GetUtcNow();
        QueueAudit(organizationId, template.Id, "compute.template.updated.v1", template.Revision, template.Enabled,
            new { definition.Id, definition.ImageDigest, definition.OperatingSystem, definition.Architecture, definition.Features });
        await db.SaveChangesAsync(token);
        return template;
    }

    public async Task<ComputeTemplatePlacement> PutPlacementAsync(Guid organizationId, Guid templateRegistrationId,
        Guid nodeId, long expectedRevision, bool enabled, CancellationToken token)
    {
        if (!await db.ComputeNodes.AnyAsync(x => x.Id == nodeId && x.OrganizationId == organizationId, token) ||
            !await db.ComputeTemplates.AnyAsync(x => x.Id == templateRegistrationId && x.OrganizationId == organizationId, token))
            throw new InvalidOperationException("The template and node must belong to this organization.");
        var placement = await db.ComputeTemplatePlacements.SingleOrDefaultAsync(x => x.TemplateRegistrationId == templateRegistrationId && x.NodeId == nodeId, token);
        if (placement is null)
        {
            if (expectedRevision != 0) throw new InvalidOperationException("The placement revision changed.");
            db.ComputeTemplatePlacements.Add(placement = new() { Id = Guid.NewGuid(), OrganizationId = organizationId,
                TemplateRegistrationId = templateRegistrationId, NodeId = nodeId });
        }
        else
        {
            if (placement.OrganizationId != organizationId || placement.Revision != expectedRevision)
                throw new InvalidOperationException("The placement revision changed.");
            placement.Revision++;
        }
        placement.Enabled = enabled; placement.UpdatedAt = clock.GetUtcNow();
        QueueAudit(organizationId, placement.Id, "compute.placement.updated.v1", placement.Revision, enabled,
            new { nodeId, templateRegistrationId });
        await db.SaveChangesAsync(token);
        return placement;
    }

    private async Task RequireOrganizationAsync(Guid organizationId, CancellationToken token)
    {
        if (!await db.CoreOrganizations.AnyAsync(x => x.Id == organizationId, token))
            throw new InvalidOperationException("The organization is unavailable.");
    }

    private void QueueAudit(Guid organizationId, Guid entityId, string action, long revision, bool enabled, object details)
    {
        var now = clock.GetUtcNow(); var id = Guid.NewGuid();
        var request = new AuditEventWriteRequest(action, Category: "Infrastructure", OrganizationId: organizationId,
            EntityType: "ComputeRegistry", EntityId: entityId, Actor: context.Current?.Actor ?? new("Platform"),
            OccurredAt: now, EventId: id, UseAmbientOrganization: false,
            MetadataJson: JsonSerializer.Serialize(new { revision, enabled, details }));
        db.ComputeAuditOutbox.Add(new() { Id = id, CreatedAt = now, RequestJson = JsonSerializer.Serialize(request) });
    }
}
