using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using CSweet.Application.Setup;
using CSweet.Contracts.Setup;
using CSweet.ExecutionGateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CSweet.UnitTests;

public sealed class OfficeLocalSetupEndpointTests
{
    [Theory]
    [InlineData(true, HttpStatusCode.NoContent)]
    [InlineData(false, HttpStatusCode.Unauthorized)]
    public async Task GatewayEnrollmentReadyRoutesReceiptToFleetService(bool accepted, HttpStatusCode expected)
    {
        var fleet = DispatchProxy.Create<IExecutionFleetService, FleetProxy>();
        var proxy = (FleetProxy)(object)fleet;
        proxy.Accepted = accepted;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath(), Args = []
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(fleet);
        await using var app = builder.Build();
        app.MapOfficeLocalSetupEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            var request = new RefreshLocalOfficeEnrollmentRequest(
                Guid.NewGuid(), "test-receipt", "test-host", "windows", "x64");
            using var response = await client.PostAsJsonAsync("/api/offices/local-sessions/enrollment-ready", request);
            Assert.Equal(expected, response.StatusCode);
            Assert.Equal(request, proxy.Request);
            Assert.Equal(1, proxy.CallCount);
            Assert.Empty(await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    public class FleetProxy : DispatchProxy
    {
        public bool Accepted { get; set; }
        public RefreshLocalOfficeEnrollmentRequest? Request { get; private set; }
        public int CallCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal(nameof(IExecutionFleetService.RefreshLocalSetupEnrollmentAsync), targetMethod?.Name);
            Request = Assert.IsType<RefreshLocalOfficeEnrollmentRequest>(args![0]);
            Assert.IsType<CancellationToken>(args[1]);
            CallCount++;
            return Task.FromResult(Accepted);
        }
    }
}
