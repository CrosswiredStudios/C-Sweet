using CSweet.AI.Providers;
using CSweet.Application.Llm;
using CSweet.Contracts.Llm;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Llm;

namespace CSweet.Api.Llm;

public static class LlmProviderProfileEndpoints
{
    public static IEndpointRouteBuilder MapLlmProviderProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/llm-provider-profiles");

        group.MapGet("/usage/summary", async (
            ILlmTokenUsageService usageService,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await usageService.GetSummaryAsync(cancellationToken));
        });

        group.MapPost("", async (
            CreateLlmProviderProfileRequest request,
            ILlmProviderProfileService providerService,
            CancellationToken cancellationToken) =>
        {
            var result = await providerService.CreateAsync(request, cancellationToken);
            return result.Succeeded
                ? Results.Created($"/api/llm-provider-profiles/{result.Profile!.Id}", result.Profile)
                : Results.BadRequest(result);
        });

        group.MapPost("/discover-local", async (
            ILocalLlmProviderDiscoveryService discoveryService,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await discoveryService.DiscoverAsync(cancellationToken));
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateLlmProviderProfileRequest request,
            ILlmProviderProfileService providerService,
            CancellationToken cancellationToken) =>
        {
            var result = await providerService.UpdateAsync(id, request, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result)
                : ToProviderActionError(result);
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            ILlmProviderProfileService providerService,
            CancellationToken cancellationToken) =>
        {
            var result = await providerService.DeleteAsync(id, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result)
                : ToProviderActionError(result);
        });

        group.MapPost("/model-catalog/preview", async (
            PreviewModelCatalogRequest request,
            OpenAiCompatibleProviderClient providerClient,
            CancellationToken cancellationToken) =>
        {
            if (!TryCreateProviderPreview(request, out var profile, out var validationResponse))
            {
                return Results.Ok(validationResponse);
            }

            try
            {
                var apiKey = request.ApiKey ?? string.Empty;
                var models = await providerClient.ListModelsAsync(profile, apiKey, cancellationToken);
                return Results.Ok(new PreviewModelCatalogResponse(
                    true,
                    null,
                    models.Count == 0
                        ? "Connected, but the provider did not return any models."
                        : $"Found {models.Count} model(s).",
                    models));
            }
            catch (LlmProviderHttpException ex)
            {
                return Results.Ok(new PreviewModelCatalogResponse(
                    false,
                    "provider_http_error",
                    ex.Message,
                    []));
            }
            catch (UriFormatException)
            {
                return Results.Ok(new PreviewModelCatalogResponse(
                    false,
                    "invalid_base_url",
                    "Enter a valid HTTP or HTTPS base URL.",
                    []));
            }
            catch (HttpRequestException)
            {
                return Results.Ok(new PreviewModelCatalogResponse(
                    false,
                    "provider_unreachable",
                    "Could not connect to the provider at this base URL.",
                    []));
            }
            catch (TaskCanceledException)
            {
                return Results.Ok(new PreviewModelCatalogResponse(
                    false,
                    "provider_timeout",
                    "Timed out while connecting to the provider.",
                    []));
            }
        });

        group.MapGet("", async (
            ILlmProviderProfileService providerService,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await providerService.ListAsync(cancellationToken));
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            ILlmProviderProfileService providerService,
            CancellationToken cancellationToken) =>
        {
            var profile = await providerService.GetAsync(id, cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        });

        group.MapGet("/{id:guid}/models", async (
            Guid id,
            ILlmProviderProfileService providerService,
            IModelCatalogClient modelCatalogClient,
            CancellationToken cancellationToken) =>
        {
            var profile = await providerService.GetAsync(id, cancellationToken);
            if (profile is null)
            {
                return Results.NotFound();
            }

            try
            {
                var models = await modelCatalogClient.ListModelsAsync(id, cancellationToken);
                return Results.Ok(new PreviewModelCatalogResponse(
                    true,
                    null,
                    models.Count == 0
                        ? "Connected, but the provider did not return any models."
                        : $"Found {models.Count} model(s).",
                    models));
            }
            catch (LlmProviderHttpException ex)
            {
                return Results.Ok(new PreviewModelCatalogResponse(false, "provider_http_error", ex.Message, []));
            }
            catch (HttpRequestException)
            {
                return Results.Ok(new PreviewModelCatalogResponse(false, "provider_unreachable", "Could not connect to this provider.", []));
            }
            catch (TaskCanceledException)
            {
                return Results.Ok(new PreviewModelCatalogResponse(false, "provider_timeout", "Timed out while loading models.", []));
            }
        });

        group.MapPost("/{id:guid}/test", async (
            Guid id,
            ILlmProviderProfileService providerService,
            CancellationToken cancellationToken) =>
        {
            var result = await providerService.TestAsync(id, cancellationToken);
            return result.FailureMessage == "Provider profile was not found."
                ? Results.NotFound(result)
                : Results.Ok(result);
        });

        return endpoints;
    }

    private static IResult ToProviderActionError(LlmProviderProfileActionResponse result)
    {
        return result.ErrorCode == "provider_profile_not_found"
            ? Results.NotFound(result)
            : Results.BadRequest(result);
    }

    private static bool TryCreateProviderPreview(
        PreviewModelCatalogRequest request,
        out LlmProviderProfile profile,
        out PreviewModelCatalogResponse response)
    {
        profile = new LlmProviderProfile();

        if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            response = new PreviewModelCatalogResponse(
                false,
                "invalid_base_url",
                "Enter a valid HTTP or HTTPS base URL.",
                []);
            return false;
        }

        if (!request.ProviderType.UsesOpenAiCompatibleApi())
        {
            response = new PreviewModelCatalogResponse(
                false,
                "unsupported_provider_type",
                "Model discovery is available for OpenAI-compatible providers.",
                []);
            return false;
        }

        profile = new LlmProviderProfile
        {
            Id = Guid.NewGuid(),
            Name = "Provider preview",
            ProviderType = request.ProviderType,
            BaseUrl = request.BaseUrl.Trim(),
            DefaultChatModel = string.Empty,
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        response = new PreviewModelCatalogResponse(true, null, null, []);
        return true;
    }

}
