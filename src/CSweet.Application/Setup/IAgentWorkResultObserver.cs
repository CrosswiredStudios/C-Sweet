using System.Text.Json;
using CSweet.Domain.Setup;

namespace CSweet.Application.Setup;

/// <summary>Projects retained agent results in the same transaction as work completion.</summary>
public interface IAgentWorkResultObserver
{
    Task ObserveAsync(AgentWorkItem work, bool succeeded, JsonElement? value, CancellationToken token);
}
