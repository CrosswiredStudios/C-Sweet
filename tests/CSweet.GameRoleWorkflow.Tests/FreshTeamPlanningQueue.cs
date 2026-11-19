using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.GameRoleWorkflow.Tests;

// Script the personal board clock so ordinary scheduled wakes run immediately in the test.
// Retry dispositions, human requeues and blocked cards fail the scenario.
internal sealed class FreshTeamPlanningQueue(Guid owner, Guid? existingBoard = null)
{
    private readonly Guid board = existingBoard ?? Guid.NewGuid();
    private readonly Dictionary<Guid, PersonalTodoItem> cards = [];
    private readonly Dictionary<Guid, Guid> claims = [];
    public Func<DeferPersonalTodoItemRequest, PersonalTodoItem, Task<PersonalTodoItem>>? OnDefer { get; set; }
    public void Track(PersonalTodoItem item) => cards[item.Id] = item;

    public PersonalTodoItem Add(AddPersonalTodoItemRequest request)
    {
        var prior = cards.Values.SingleOrDefault(c => c.CorrelationId == request.CorrelationId);
        if (prior is not null) return prior;
        var now = DateTimeOffset.UtcNow;
        var card = new PersonalTodoItem(Guid.NewGuid(), board, owner, owner, "Gabriel", request.Title,
            request.Description ?? "", PersonalTodoStatuses.Ready, request.Priority, cards.Count, 1, null, null, null, [], null, null, now, now)
            { CorrelationId = request.CorrelationId, WorkContext = request.WorkContext };
        return cards[card.Id] = card;
    }

    public PersonalTodoAvailableEvent NextWake(Func<PersonalTodoItem, bool>? inScope = null)
    {
        var scheduled = cards.Values.Where(c => inScope?.Invoke(c) != false);
        var ready = scheduled.FirstOrDefault(c => c.Status == PersonalTodoStatuses.Ready);
        if (ready is null)
        {
            var due = scheduled.Where(c => c.Wait is not null).MinBy(c => c.Wait!.NextReviewAt)
                ?? throw new InvalidOperationException("No ready or scheduled planning work.");
            Assert.Null(due.Wait!.Dependency); // An exact dependency must be woken by its source.
            ready = cards[due.Id] = due with { Status = PersonalTodoStatuses.Ready, Wait = null, Revision = due.Revision + 1 };
        }
        return new(owner, board, ready.Id);
    }

    public string Describe() => string.Join("\n", cards.Values.Select(c => $"{c.Title}: {c.Wait?.Reason ?? c.Status}"));
    public void Connect(AgentTestRuntime runtime, Guid employee)
    {
        runtime.RegisterCapability<object, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) =>
            Task.FromResult(new PersonalTodoDirectory([new(board, owner, "Gabriel", null, null, 1, cards.Values.ToArray())], employee)));
        if (employee != owner) return;
        runtime.RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add, (r, _) => Task.FromResult(Add(r)));
        runtime.RegisterCapability<ClaimPersonalTodoItemRequest, PersonalTodoClaim>(PersonalTodoCapabilities.Claim, (r, _) =>
        {
            var card = cards.Values.FirstOrDefault(c => c.Status == PersonalTodoStatuses.Ready && (!r.ItemId.HasValue || c.Id == r.ItemId));
            if (card is null) return Task.FromResult(new PersonalTodoClaim(null));
            claims.Add(card.Id, r.EventId);
            return Task.FromResult(new PersonalTodoClaim(cards[card.Id] = card with { Status = PersonalTodoStatuses.Running, Revision = card.Revision + 1 }));
        });
        runtime.RegisterCapability<UpdatePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Update, (r, _) => {
            var card = cards[r.ItemId];
            Assert.Equal(card.Revision, r.ExpectedRevision);
            return Task.FromResult(cards[r.ItemId] = card with { WorkContext = r.WorkContext, Revision = card.Revision + 1 });
        });
        runtime.RegisterCapability<CompletePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Complete, (r, _) =>
            Task.FromResult(cards[r.ItemId] = Finish(r.ItemId, r.EventId, r.ExpectedRevision) with { Status = PersonalTodoStatuses.Completed, ResultSummary = r.Summary }));
        runtime.RegisterCapability<DeferPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Defer, async (r, _) =>
        {
            Assert.False(r.Reason.StartsWith("Retrying automatically", StringComparison.Ordinal), r.Reason);
            var deferred = Finish(r.ItemId, r.EventId, r.ExpectedRevision) with {
                Status = PersonalTodoStatuses.Running, Wait = new(r.NextReviewAt, r.Reason, r.WaitingOnOrganizationUserId) { Dependency = r.Dependency } };
            return cards[r.ItemId] = OnDefer is null ? deferred : await OnDefer(r, deferred);
        });
        runtime.RegisterCapability<BlockPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Block, (r, _) =>
            throw new InvalidOperationException("Unexpected blocked planning card: " + r.Reason));
    }

    private PersonalTodoItem Finish(Guid id, Guid delivery, long revision)
    {
        Assert.Equal(delivery, claims[id]); claims.Remove(id);
        var card = cards[id];
        Assert.Equal(revision, card.Revision);
        return card with { Revision = revision + 1 };
    }
}

