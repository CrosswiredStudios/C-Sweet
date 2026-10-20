using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using CSweet.Contracts.Agents;
using CSweet.Contracts.WorkManagement;
using CSweet.UI.Pages;
using Microsoft.AspNetCore.Components;

namespace CSweet.UnitTests;

public sealed class DeveloperAssignmentSelectionTests
{
    [Theory]
    [InlineData("com.csweet.software-developer.openhands", true, true, "Active", true)]
    [InlineData("independent.publisher.developer", true, true, "Active", true)]
    [InlineData("com.csweet.software-developer", true, true, "Active", true)]
    [InlineData("com.csweet.video-game-engineer", true, true, "Active", true)]
    [InlineData("com.csweet.software-developer", false, true, "Active", false)]
    [InlineData("com.csweet.software-developer.openhands", true, false, "Active", false)]
    [InlineData("com.csweet.software-developer.openhands", true, true, "Superseded", false)]
    public async Task Assignment_picker_uses_reviewed_implementation_capability_instead_of_identity(
        string agentId, bool implements, bool enabled, string revisionStatus, bool expected)
    {
        var id = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var installation = JsonSerializer.Deserialize<AgentInstallationResponse>(JsonSerializer.Serialize(new
        {
            id, businessId = organization.ToString("D"), agentId, agentName = "Developer", agentVersion = "0.1.5",
            isEnabled = enabled, revisionStatus,
            grantedCapabilities = implements ? new[] { "software-development.implement.v1" } : []
        }), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        using var http = new HttpClient(new SelectionApi(installation)) { BaseAddress = new Uri("http://localhost/") };
        var component = new WorkBoards();
        ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(WorkBoards.OrganizationId)] = organization })
            .SetParameterProperties(component);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(WorkBoards).GetProperty("Http", flags)!.SetValue(component, http);
        var item = new WorkBoardItemResponse(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null,
            "Task", "Pilot", "", "Backlog", "Medium", null, 1, 1, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await (Task)typeof(WorkBoards).GetMethod("OpenDeveloperAssignmentAsync", flags)!.Invoke(component, [item])!;
        Assert.Null(typeof(WorkBoards).GetField("_developerAssignmentError", flags)!.GetValue(component));
        var choices = (IReadOnlyList<AgentInstallationResponse>)typeof(WorkBoards)
            .GetField("_developerInstallations", flags)!.GetValue(component)!;
        Assert.Equal(expected ? new[] { id } : [], choices.Select(x => x.Id).ToArray());
        var selected = (Guid?)typeof(WorkBoards).GetField("_developerInstallationId", flags)!.GetValue(component);
        Assert.Equal(expected ? id : (Guid?)null, selected);
    }

    private sealed class SelectionApi(AgentInstallationResponse installation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("Selection must remain read-only.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath == "/api/agents/installations"
                    ? JsonContent.Create(new[] { installation })
                    : JsonContent.Create(Array.Empty<SourceControlRepositoryOptionResponse>())
            });
        }
    }
}
