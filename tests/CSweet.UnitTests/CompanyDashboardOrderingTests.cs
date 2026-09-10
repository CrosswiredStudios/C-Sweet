using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using CSweet.Contracts.Core;
using CSweet.UI.Pages;
namespace CSweet.UnitTests;

public sealed class CompanyDashboardOrderingTests
{
    [Fact]
    public void OlderLayoutsPreserveTheirOrderAndGainDecisions()
    {
        var restored = DashboardWidgets.Restore(new[] { "legal", "projects", "finance", "approvals" });
        Assert.Equal(new[] { "legal", "projects", "finance", "approvals", "decisions" }, restored);
        Assert.True(DashboardWidgets.IsValid(restored));
        Assert.Equal(DashboardWidgets.DefaultOrder, DashboardWidgets.Restore(new[] { "legal", "legal", "finance", "approvals" }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MoveSavesTheNewOrderAndRestoresPreviousOrderWhenSaveFails(bool succeeds)
    {
        var handler = new SaveHandler(succeeds);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var component = new CommandCenter();
        typeof(CommandCenter).GetProperty("Http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, http);
        Field("_layoutLoaded").SetValue(component, true);
        await (Task)typeof(CommandCenter).GetMethod("MoveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, ["projects", 0])!;
        Assert.Equal(new[] { "projects", "decisions", "approvals", "finance", "legal" }, handler.Saved!.Order);
        var order = (List<string>)Field("_order").GetValue(component)!;
        Assert.Equal(succeeds ? handler.Saved.Order : DashboardWidgets.DefaultOrder, order);
        Assert.False((bool)Field("_saving").GetValue(component)!);
        if (!succeeds) Assert.Contains("restored", (string)Field("_layoutMessage").GetValue(component)!);
    }
    private static FieldInfo Field(string name) => typeof(CommandCenter).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private sealed class SaveHandler(bool succeeds) : HttpMessageHandler
    {
        public DashboardLayoutRequest? Saved { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Saved = await request.Content!.ReadFromJsonAsync<DashboardLayoutRequest>(token);
            return new HttpResponseMessage(succeeds ? HttpStatusCode.NoContent : HttpStatusCode.ServiceUnavailable);
        }
    }
}
