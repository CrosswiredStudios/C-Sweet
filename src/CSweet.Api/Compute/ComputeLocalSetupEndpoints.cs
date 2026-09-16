using System.Security.Cryptography;
using System.Text;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

#if COMPUTE_GATEWAY
namespace CSweet.ExecutionGateway.Compute;
#else
namespace CSweet.Api.Compute;
#endif

public static class ComputeLocalSetupEndpoints
{
    public sealed record Enrollment(string Secret, string PublicKey, ComputeTemplate Template, Guid? NodeId = null);
    public sealed record Completion(string Secret, bool Succeeded);

    public static IEndpointRouteBuilder MapComputeLocalSetupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/compute/local-setup/{id:guid}").AllowAnonymous()
            .RequireRateLimiting(ComputeProviderEndpoints.MaintenanceRatePolicy);
        group.MapPost("/enroll", async (Guid id, Enrollment request, HttpContext http, CSweetDbContext db,
            ComputeRegistry registry, CancellationToken token) =>
        {
            if (!http.Request.IsHttps) return Results.Forbid();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var setup = await AuthenticateAsync(db, id, request.Secret, token);
            if (setup is null) return Results.Unauthorized();
            if (request.Template is not { OperatingSystem: "linux", Architecture: "x64", Enabled: true } ||
                !request.Template.Id.StartsWith("linux-local-", StringComparison.Ordinal)) return Results.BadRequest();
            var nodeId = request.NodeId ?? id;
            if (nodeId == Guid.Empty) return Results.BadRequest();
            var node = await db.ComputeNodes.SingleOrDefaultAsync(x => x.Id == nodeId, token);
            if (node is not null && (node.OrganizationId != setup.OrganizationId ||
                node.VerificationPublicKeyBase64 != request.PublicKey)) return Results.Conflict();
            if (node is null) await registry.PutNodeAsync(setup.OrganizationId, nodeId, 0, "Local Linux compute", "hyperv",
                "local-node", request.PublicKey, true, token);
            var existing = await db.ComputeTemplates.SingleOrDefaultAsync(x => x.OrganizationId == setup.OrganizationId &&
                x.TemplateId == request.Template.Id, token);
            var template = existing ?? await registry.PutTemplateAsync(setup.OrganizationId, request.Template, 0, token);
            if (!await db.ComputeTemplatePlacements.AnyAsync(x => x.TemplateRegistrationId == template.Id && x.NodeId == nodeId, token))
                await registry.PutPlacementAsync(setup.OrganizationId, template.Id, nodeId, 0, true, token);
            setup.TemplateId = template.TemplateId; setup.Revision++; setup.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
            return Results.Ok(new { registered = true });
        });
        group.MapPost("/complete", async (Guid id, Completion request, HttpContext http, CSweetDbContext db,
            ComputeDefaultsService defaults, CancellationToken token) =>
        {
            if (!http.Request.IsHttps) return Results.Forbid();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var setup = await AuthenticateAsync(db, id, request.Secret, token);
            if (setup is null) return Results.Unauthorized();
            if (request.Succeeded && setup.TemplateId is null) return Results.Conflict();
            setup.State = request.Succeeded ? "Ready" : "Failed";
            setup.ErrorCode = request.Succeeded ? null : "local_setup_failed";
            setup.HandoffHash = null; setup.HandoffExpiresAt = null; setup.Revision++; setup.UpdatedAt = DateTimeOffset.UtcNow;
            if (request.Succeeded) await defaults.ActivateAccessAsync(setup, token);
            await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
            return Results.Ok(new { completed = true });
        });
        return endpoints;
    }

    internal static async Task<ComputeLocalSetup?> AuthenticateAsync(CSweetDbContext db, Guid id, string? secret, CancellationToken token)
    {
        if (secret is not { Length: 64 } || !secret.All(Uri.IsHexDigit)) return null;
        var setup = await db.Set<ComputeLocalSetup>().SingleOrDefaultAsync(x => x.Id == id, token);
        if (setup is not { State: "Running", HandoffHash.Length: 64 } || setup.HandoffExpiresAt <= DateTimeOffset.UtcNow) return null;
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(digest), Encoding.ASCII.GetBytes(setup.HandoffHash)) ? setup : null;
    }
}
