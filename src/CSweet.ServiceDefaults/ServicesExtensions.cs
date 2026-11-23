using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

public static class ServicesExtensions
{
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        // The extension activates only when the Service Control Manager starts the process.
        builder.Services.AddWindowsService();
        if (OperatingSystem.IsWindows() && WindowsServices.WindowsServiceHelpers.IsWindowsService())
        {
            var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "CSweet", "Provisioning", builder.Environment.ApplicationName + ".json");
            builder.Configuration.AddJsonFile(settings, optional: true, reloadOnChange: false);
            if (builder.Environment.ApplicationName == "CSweet.ExecutionGateway")
                builder.Configuration.AddJsonFile(Path.Combine(Path.GetDirectoryName(settings)!, "combined-office-compute.json"),
                    optional: true, reloadOnChange: false);
            if ((builder.Environment.ApplicationName is "CSweet.Api" or "CSweet.ExecutionGateway") &&
                string.IsNullOrWhiteSpace(builder.Configuration["CSweet:ExecutionNodeCertificates:AuthorityPfxPath"]) &&
                string.IsNullOrWhiteSpace(builder.Configuration["CSweet:ExecutionNodeCertificates:AuthorityCertificateThumbprint"]))
                builder.Configuration.AddJsonFile(Path.Combine(Path.GetDirectoryName(settings)!, "office-issuer.json"),
                    optional: true, reloadOnChange: false);
        }

        // Configure health checks
        builder.Services.AddDefaultHealthChecks();

        // Configure open telemetry
        builder.AddOpenTelemetry();

        // Resolve Aspire resource names such as https+http://agenthost.
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddServiceDiscovery();
        });

        // Configure HTTP client with resilience and service discovery
        builder.Services.AddHttpClient("healthcheck", (httpClient) =>
        {
            httpClient.Timeout = TimeSpan.FromSeconds(30);
        });

        return builder;
    }

    public static IHostApplicationBuilder AddOpenTelemetry(this IHostApplicationBuilder builder)
    {
        // Configure logging with OpenTelemetry
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("CSweet.AgentRuntime");
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static IHostApplicationBuilder AddOpenTelemetryExporters(this IHostApplicationBuilder builder)
    {
        bool useOtlpExporter = false;

        if (string.Equals(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"], "http://plus", StringComparison.OrdinalIgnoreCase))
        {
            useOtlpExporter = true;
        }

        if (useOtlpExporter)
        {
            builder.Services.Configure<OpenTelemetryLoggerOptions>(logging => logging.AddOtlpExporter());
            builder.Services.Configure<MeterProviderBuilder>(metrics => metrics.AddOtlpExporter());
            builder.Services.Configure<TracerProviderBuilder>(tracing => tracing.AddOtlpExporter());
        }

        return builder;
    }

    public static IHttpClientBuilder AddDefaultRetriesAndCircuitBreaker(this IHttpClientBuilder builder)
    {
        builder.AddStandardResilienceHandler();
        return builder;
    }

    private static void AddDefaultHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy());
    }
}
