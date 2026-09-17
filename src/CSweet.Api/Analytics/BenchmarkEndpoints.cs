using System.Text;
using System.Text.Json;
using CSweet.Api.Auth;
using CSweet.Application.Analytics;
using CSweet.Contracts.Analytics;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Analytics;

public static class BenchmarkEndpoints
{
    public static IEndpointRouteBuilder MapBenchmarkEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/benchmarks").RequireAuthorization("HostAdministration");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "State changed; refresh and retry." }); }
        });
        group.MapGet("", async (IBenchmarkService service, CancellationToken ct) => Results.Ok(await service.GetAsync(ct)));
        group.MapGet("/options", async (CSweetDbContext db, CancellationToken ct) => Results.Ok(new
        {
            agents = await db.AgentDefinitions.AsNoTracking().Where(x => x.IsAvailableForHire)
                .Select(x => new { x.Id, x.AgentId, x.PackageVersionId, Name = x.PackageVersion!.AgentName, x.PackageVersion.Version }).ToListAsync(ct),
            providers = await db.LlmProviderProfiles.AsNoTracking()
                .Select(x => new { x.Id, x.Name, Model = x.DefaultChatModel }).ToListAsync(ct)
        }));
        group.MapPost("/definitions", async (CreateBenchmarkDefinitionRequest request, HttpContext http,
            IBenchmarkService service, CancellationToken ct) => Results.Ok(await service.CreateDefinitionAsync(request,
                http.User.GetApplicationUserId()!.Value, ct)));
        group.MapPost("/campaigns", async (LaunchBenchmarkRequest request, HttpContext http,
            IBenchmarkService service, CancellationToken ct) => Results.Ok(await service.LaunchAsync(request,
                http.User.GetApplicationUserId()!.Value, ct)));
        group.MapGet("/campaigns/{id:guid}", async (Guid id, IBenchmarkService service, CancellationToken ct) =>
            await service.GetCampaignAsync(id, ct) is { } response ? Results.Ok(response) : Results.NotFound());
        group.MapPost("/trials/{id:guid}/cancel", async (Guid id, IBenchmarkService service, CancellationToken ct) =>
        { await service.CancelAsync(id, ct); return Results.NoContent(); });
        group.MapPost("/trials/{id:guid}/assessments", async (Guid id, BenchmarkAssessmentRequest request,
            HttpContext http, IBenchmarkService service, CancellationToken ct) => Results.Ok(await service.AssessAsync(id,
                request, http.User.GetApplicationUserId()!.Value, ct)));
        group.MapGet("/campaigns/{id:guid}/export", async (Guid id, string? format, IBenchmarkService service,
            CSweetDbContext db, CancellationToken ct) =>
        {
            var campaign = await service.GetCampaignAsync(id, ct);
            if (campaign is null) return Results.NotFound();
            var definition = await db.BenchmarkDefinitions.AsNoTracking().SingleAsync(x => x.Id == campaign.DefinitionId, ct);
            if (format == "csv")
            {
                var blueprint = JsonSerializer.Deserialize<BenchmarkBlueprint>(definition.BlueprintJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var csv = new StringBuilder("Variant,Repetition,Delivery,Evaluation,Tokens,Calls,Elapsed ms,Reported calls,Legacy calls,Human messages,Agent requests,Tool attempts,Setup tokens,Evaluation tokens,Trailing tokens,Checks passed,Checks recorded,Human score,Judge score\r\n");
                foreach (var t in campaign.Trials) csv.AppendLine($"{WorkEfficiencyEndpoints.Csv(t.VariantName)},{t.Repetition},{t.Status},{t.EvaluationStatus},{t.DeliveryUsage.TotalTokens},{t.DeliveryUsage.ModelCalls},{t.DeliveryTimeMs},{t.DeliveryUsage.FullyReportedCalls},{t.DeliveryUsage.LegacyCalls},{t.HumanInterventions},{t.AgentHandoffs},{t.ToolOperations},{t.SetupUsage.TotalTokens},{t.EvaluationUsage.TotalTokens},{t.TrailingUsage.TotalTokens},{t.Assessments.Count(a => a.Kind == "Check" && a.Passed == true)},{t.Assessments.Count(a => a.Kind == "Check")},{Score(t, blueprint, "Human")},{Score(t, blueprint, "Judge")}");
                return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "benchmark.csv");
            }
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(new { manifest = JsonSerializer.Deserialize<JsonElement>(definition.ManifestJson),
                definition.Digest, results = campaign }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "application/json", "benchmark.json");
        });
        return routes;
    }

    private static string Score(BenchmarkTrialResponse trial, BenchmarkBlueprint blueprint, string kind)
    {
        var rubric = blueprint.Criteria.Where(x => x.Kind == "Rubric").ToList();
        var scores = trial.Assessments.Where(x => x.Kind == kind && x.Score.HasValue).GroupBy(x => x.CriterionKey)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id).First().Score!.Value);
        return rubric.Count == 0 || rubric.Any(x => !scores.ContainsKey(x.Key)) ? "" :
            (rubric.Sum(x => scores[x.Key] * x.Weight) / rubric.Sum(x => x.Weight)).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}
