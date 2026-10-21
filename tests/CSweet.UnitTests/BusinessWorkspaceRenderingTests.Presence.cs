using System.Reflection;
using System.Text.Json;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Realtime;
using CSweet.UI.Services;
using Microsoft.JSInterop;

namespace CSweet.UnitTests;

public sealed partial class BusinessWorkspaceRenderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommunicationsRefreshesAgentDotsOnPresenceEventsAndReconnect(bool reconnect)
    {
        var person = new CommunicationPersonResponse(ItemId, "Naomi Chen", "Agent", null,
            "Creative Director", CommunicationPresenceStatuses.Starting, "Agent runtime is Stopping.");
        var participant = new CommunicationParticipantResponse(ItemId, person.DisplayName, "Agent", "Member",
            person.PresenceStatus, person.PresenceDetail);
        var chat = new CommunicationChatResponse(Guid.NewGuid(), person.DisplayName, null, true, true, true, false,
            DateTimeOffset.UtcNow, [participant], null, null, 0);
        var api = new BusinessApi { Hub = new(Guid.NewGuid(), Guid.NewGuid(), false, true, [chat], [person], []) };
        var html = await Render(typeof(CSweet.UI.Pages.Communications), "presence", api: api, exercise: async page =>
        {
            Set(page, "_communicationsModule", new PresenceJavaScript());
            Assert.Equal(CommunicationPresenceStatuses.Starting,
                Assert.Single(((CommunicationHubResponse)Get(page, "_hub")!).People).PresenceStatus);
            var banner = typeof(CSweet.UI.Pages.Communications).GetProperty("SelectedUnavailableAgent",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.NotNull(banner.GetValue(page));
            var realtime = (AppRealtimeState)typeof(CSweet.UI.Pages.Communications)
                .GetProperty("Realtime", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
            void Publish(Guid organization) => typeof(AppRealtimeState)
                .GetMethod("Receive", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(realtime,
                    [new AppRealtimeEventEnvelope(Guid.NewGuid(), 1, AppRealtimeEvents.AgentPresenceChanged,
                        organization, "agent-presence", DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { }))]);
            Publish(Guid.NewGuid());
            Assert.Equal(1, api.HubReads);
            api.Hub = api.Hub! with
            {
                People = [person with { PresenceStatus = CommunicationPresenceStatuses.Available, PresenceDetail = null }],
                Chats = [chat with { Participants = [participant with { PresenceStatus = CommunicationPresenceStatuses.Available, PresenceDetail = null }] }]
            };
            if (reconnect)
                ((Action?)typeof(AppRealtimeState).GetField("Reconnected", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(realtime))?.Invoke();
            else Publish(BusinessId);
            await Task.Yield();
            Assert.Equal(2, api.HubReads);
            Assert.Equal(CommunicationPresenceStatuses.Available,
                Assert.Single(((CommunicationHubResponse)Get(page, "_hub")!).People).PresenceStatus);
            Assert.Null(banner.GetValue(page));
            // A late/duplicate hint must still read current state, not restore Starting.
            Publish(BusinessId);
            await Task.Yield();
            Assert.Equal(3, api.HubReads);
        });
        Assert.Contains("presence-dot available", html);
        Assert.DoesNotContain("presence-dot starting", html);
        Assert.DoesNotContain("Agent runtime is Stopping.", html);
    }

    private sealed class PresenceJavaScript : IJSObjectReference
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(identifier == "isDocumentVisible" ? (TValue)(object)true : default!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}
