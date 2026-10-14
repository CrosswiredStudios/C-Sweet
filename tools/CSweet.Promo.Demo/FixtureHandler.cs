using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Auth;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Compute;
using CSweet.Contracts.Core;

namespace CSweet.Promo.Demo;

public sealed class FixtureHandler(DemoData data) : HttpMessageHandler
{
    public static ConcurrentQueue<string> Failures { get; } = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
        var org = data.OrganizationId.ToString();
        var root = $"/api/organizations/{org}";
        var core = $"/api/core/organizations/{org}";
        var hub = $"{root}/communications/hub";
        object? payload = null;
        if (request.Method == HttpMethod.Get)
        {
            if (path == "/api/auth/session") payload = new AuthStatusResponse(false,true,"founder@example.test",true,false);
            else if (path == "/api/core/organizations") payload = new[] { data.Organization };
            else if (path == core) payload = data.Organization;
            else if (path == $"{core}/users") payload = data.People;
            else if (path == "/api/agents/installations") payload = data.Installations;
            else if (path == $"{core}/approvals") payload = new ApprovalDashboardResponse(data.OwnerId,0,[]);
            else if (path == $"{core}/dashboard/hiring") payload = new HiringBacklogResponse([]);
            else if (path == $"{root}/communications/questions/pending") payload = Array.Empty<PendingAgentQuestionResponse>();
            else if (path == $"{root}/compute") payload = new ComputeDashboard(DemoData.Epoch,null,[],false);
            else if (path == "/api/core/hiring/operations" || path == "/api/business-onboarding/operations") payload = Array.Empty<object>();
            else if (path == "/api/setup/status") payload = new { isFirstRunComplete = false };
            else if (path == hub) payload = data.Hub;
            else if (path == $"{hub}/unread-summary") payload = new CommunicationUnreadSummaryResponse(0,new Dictionary<Guid,int>());
            else if (path == $"{hub}/chats/{data.ChatId}/messages") payload = data.Messages;
            else if (path == $"{hub}/chats/{data.ChatId}/coordination-sessions" || path == $"{hub}/chats/{data.ChatId}/turns" || path == $"{root}/communications/notifications") payload = Array.Empty<object>();
            else if (path == $"{root}/documents/{data.DocumentId}") payload = data.Document;
            else if (path == $"{root}/documents/folders") payload = Array.Empty<ArtifactFolderResponse>();
            else if (path == $"{root}/calendar") payload = data.Calendar;
            else if (path == $"{root}/calendar/reminders") payload = Array.Empty<object>();
        }
        // Mark-read is the only UI write accepted, and changes no persisted state.
        else if (request.Method == HttpMethod.Post && path == $"{hub}/chats/{data.ChatId}/read") payload = new CommunicationUnreadSummaryResponse(0,new Dictionary<Guid,int>());
        if (payload is not null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload,payload.GetType()) });
        var failure = $"Unmapped demo request: {request.Method} {path}";
        Failures.Enqueue(failure);
        Console.Error.WriteLine(failure);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotImplemented) { Content = new StringContent(failure) });
    }
}
