using CSweet.Api.Auth;
using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CSweet.Api.Setup;

internal static class HostedOfficeEndpoints
{
    private const string Review = "hosted-offices";
    internal static Task<bool> ReviewedAsync(CSweetDbContext db, CancellationToken ct) =>
        db.OnboardingSteps.AnyAsync(x => x.Key == Review && x.IsComplete, ct);

    internal static async Task<IResult> EnrollDeviceAsync(HttpContext http, CSweet.Application.Setup.IExecutionFleetService fleet,
        HostedWebTenant tenant, CancellationToken ct)
    {
        var services = http.RequestServices;
        using var response = await HostedWebSignIn.GetOfficesAsync(http, tenant, services.GetRequiredService<IMemoryCache>(),
            services.GetRequiredService<IHttpClientFactory>(), ct);
        if (response is null || !response.IsSuccessStatusCode) return Results.StatusCode(503);
        var plan = await response.Content.ReadFromJsonAsync<HostedOfficePlan>(ct);
        if (plan?.CanManage != true) return Results.StatusCode(403);
        var db = services.GetRequiredService<CSweetDbContext>();
        // One enrollment reservation per unassigned slot, including concurrent browser sessions.
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var occupied = await db.ExecutionNodes.CountAsync(x => x.Status != ExecutionNodeStatus.Revoked, ct);
        var now = DateTimeOffset.UtcNow;
        var reserved = await db.ExecutionNodeEnrollments.CountAsync(x => x.Status == ExecutionEnrollmentStatus.Available && x.ExpiresAt > now, ct);
        if (occupied + reserved >= plan.Linked) return Results.Conflict(new { message = "All paid device slots are in use." });
        var enrollment = await fleet.CreateEnrollmentAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(enrollment);
    }

    internal static void MapHostedOffices(this WebApplication app)
    {
        if (app.Services.GetService<HostedWebTenant>() is null) return;
        var group = app.MapGroup("/api/setup/hosted-offices").RequireAuthorization("HostAdministration");
        group.MapGet("", async (HttpContext http, HostedWebTenant tenant, CSweetDbContext db, IMemoryCache cache,
            IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            HostedOfficePlan? plan = null;
            string? error = null;
            try
            {
                using var response = await HostedWebSignIn.GetOfficesAsync(http, tenant, cache, clients, ct);
                if (response?.IsSuccessStatusCode == true) plan = await response.Content.ReadFromJsonAsync<HostedOfficePlan>(ct);
                else error = response is null || (int)response.StatusCode is 401 or 403 ? "signin" : "unavailable";
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) && !ct.IsCancellationRequested)
            {
                error = "unavailable";
            }
            return Results.Ok(new HostedOfficeOverview(plan, error,
                new Uri(tenant.Issuer, $"account/hosting/orders/{tenant.Order:D}/offices").AbsoluteUri, await ReviewedAsync(db, ct)));
        });
        group.MapPost("/continue", async (CSweetDbContext db, CancellationToken ct) =>
        {
            if (await ReviewedAsync(db, ct)) return Results.NoContent();
            var now = DateTimeOffset.UtcNow;
            db.OnboardingSteps.Add(new OnboardingStep { Id = Guid.NewGuid(), Key = Review, DisplayName = "Your offices",
                IsComplete = true, CreatedAt = now, UpdatedAt = now, CompletedAt = now });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                if (!await ReviewedAsync(db, ct)) throw;
            }
            return Results.NoContent();
        });
    }
}
